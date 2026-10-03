using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

internal sealed class MockMeld : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _send = new(1);
    private WebSocket _socket;
    private Task _server;
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Calls { get; } = new();
    public int Connections;
    public int SnapshotRequests;
    public TaskCompletionSource SnapshotResponseGate = null;
    public bool IgnoreCameraToggle = false;
    public bool Streaming, Recording, VirtualCamera, Muted;
    public bool CameraVisible = true, IncludeTargets = true, OmitVirtualCameraStatus = false;
    public string IdPrefix = "scene-";
    public int CurrentScene, CameraScene = 0, CameraIndex = 4;
    public string CameraName = "Camera", CameraId = "cam";
    public bool GroupedCamera, DuplicateCamera = false;
    public bool IncludeCamera = true, IncludeOtherCaptures = false;
    public bool MixedCaseSession = false;
    public bool PublishAfterCommand = true;
    public string MicName = "Microphone", MicId = "mic", MicParent = null;
    public Dictionary<string,object> ExtraItems = new();
    public void Start() {
        _listener.Prefixes.Add("http://127.0.0.1:13376/");
        _listener.Start();
        _server = ServeAsync();
    }
    private object Session() {
        var items = new Dictionary<string, object>();
        for (var i = 0; i < 14; i++) items[IdPrefix+i] = new {
            type = "scene", name = i == 0 ? "VALORANT" : i == 1 ? "SYNAPSE // STARTING SOON" : i >= 12 ? "Duplicate" : "Scene " + (i+1),
            index = IdPrefix == "scene-" ? i : 13-i, current = i == CurrentScene
        };
        if (IncludeTargets) {
            items[MicId] = new {type = "track", name = MicName, parent = MicParent, muted = Muted};
            if (GroupedCamera) items["group"] = new {type = "group", name = "Facecam group", parent = IdPrefix+CameraScene};
            if (IncludeCamera) items[CameraId] = new {type = "layer", name = CameraName, index = CameraIndex,
                parent = GroupedCamera ? "group" : IdPrefix+CameraScene, visible = CameraVisible};
            items["camera-frame"] = new {type = "layer", name = "Camera Frame", source="file:///frame.png", index = 9, parent = IdPrefix+CameraScene, visible = true};
            items["browser"] = new {type = "layer", name = "Browser", url="https://example.test/overlay", index = 11, parent = IdPrefix+CameraScene, visible = true};
            items["media"] = new {type = "layer", name = "Video", mediaSource="file:///video.mp4", index = 12, parent = IdPrefix+CameraScene, visible = true};
            items["effect"] = new {type = "effect", name = "Camera effect", parent = CameraId, enabled=true};
            if (IncludeOtherCaptures) {
                items["capture-card"] = new {type="layer",name="HD60 X",index=5,parent=IdPrefix+CameraScene,visible=false};
                // The documented API does not distinguish these from cameras.
                items["screen"] = new {type="layer",name="Screen Capture",index=6,parent=IdPrefix+CameraScene,visible=true};
            }
            if (DuplicateCamera) items["second-camera"] = new {type = "layer", name = CameraName, index = 10,
                parent = IdPrefix+CameraScene, visible = true};
        }
        foreach (var extra in ExtraItems) items[extra.Key] = extra.Value;
        if (MixedCaseSession) {
            // Exercise type values, property keys and parent references with
            // different casing while leaving canonical outgoing IDs intact.
            foreach (var key in items.Keys.ToArray()) {
                var item = JsonSerializer.SerializeToElement(items[key]);
                items[key] = item.EnumerateObject().ToDictionary(x=>x.Name.ToUpperInvariant(), x=>(object)(
                    x.Name=="type" ? JsonSerializer.SerializeToElement(" " + x.Value.GetString().ToUpperInvariant() + " ") :
                    x.Name=="parent" && x.Value.ValueKind==JsonValueKind.String ? JsonSerializer.SerializeToElement(x.Value.GetString().ToUpperInvariant()) : x.Value));
            }
            return new Dictionary<string,object>{{"ITEMS",items}};
        }
        return new {items};
    }
    private async Task SendAsync(object payload) {
        await _send.WaitAsync(_cancel.Token);
        try { await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, _cancel.Token); }
        finally { _send.Release(); }
    }
    public Task PublishAsync() => SendAsync(new {
        type=2, data=new[]{new{@object="meld",properties=new Dictionary<string,object> {
            ["0"] = Session(), ["1"] = Streaming, ["2"] = Recording,
            ["3"] = OmitVirtualCameraStatus ? null : VirtualCamera
        }}}
    });
    public async Task DisconnectAsync() {
        await _send.WaitAsync(_cancel.Token);
        try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"test disconnect",_cancel.Token); }
        finally { _send.Release(); }
    }
    private async Task ServeAsync() {
        try {
            while (!_cancel.IsCancellationRequested) {
                var context=await _listener.GetContextAsync().WaitAsync(_cancel.Token);
                using var socket=(await context.AcceptWebSocketAsync(null)).WebSocket;
                _socket=socket;
                var initialized=false;
                try {
                    var buffer=new byte[65536];
                    using var message=new MemoryStream();
                    while(socket.State==WebSocketState.Open) {
                        var result=await socket.ReceiveAsync(buffer.AsMemory(),_cancel.Token);
                        if(result.MessageType==WebSocketMessageType.Close) break;
                        message.Write(buffer,0,result.Count);
                        if(!result.EndOfMessage) continue;
                        using var doc=JsonDocument.Parse(message.ToArray());
                        message.SetLength(0);
                        var data=doc.RootElement;
                        var type=data.GetProperty("type").GetInt32();
                        if(type==3) {
                            var wasInitialized=initialized;
                            if(wasInitialized) {
                                Interlocked.Increment(ref SnapshotRequests);
                                if(SnapshotResponseGate is { } gate) await gate.Task.WaitAsync(_cancel.Token);
                            }
                            var methods=new[]{"toggleStream","toggleRecord","toggleMute","toggleLayer","showScene","sendCommand"};
                            await SendAsync(new {type=10,id=data.GetProperty("id").GetInt32(),data=new {meld=new {
                                methods=methods.Select((x,i)=>new object[]{x,i}).ToArray(),
                                properties=new object[][] {
                                    new object[]{0,"session",Array.Empty<object>(),wasInitialized?Session():null},
                                    new object[]{1,"isStreaming",Array.Empty<object>(),Streaming},
                                    new object[]{2,"isRecording",Array.Empty<object>(),Recording},
                                    new object[]{3,"isVirtualCameraRunning",Array.Empty<object>(),OmitVirtualCameraStatus?null:VirtualCamera}
                                }
                            }}});
                            if(!wasInitialized) {
                                initialized=true;
                                Interlocked.Increment(ref Connections);
                                Connected.TrySetResult();
                            }
                        } else if(type==6) {
                            var method=data.GetProperty("method").GetString();
                            var arguments=data.GetProperty("args");
                            Calls.Enqueue(method+":"+arguments.GetRawText());
                            switch(method) {
                                case "toggleStream": Streaming=!Streaming; break;
                                case "toggleRecord": Recording=!Recording; break;
                                case "toggleMute" when arguments.GetArrayLength()==1 && arguments[0].GetString()==MicId:
                                    Muted=!Muted; break;
                                case "toggleLayer" when arguments[0].GetString()==IdPrefix+CameraScene && arguments[1].GetString()==CameraId:
                                    if(!IgnoreCameraToggle) CameraVisible=!CameraVisible; break;
                                case "showScene":
                                    if (int.TryParse(arguments[0].GetString()?.Replace(IdPrefix,""),out var target)) CurrentScene=target;
                                    break;
                                case "sendCommand" when arguments[0].GetString()=="meld.toggleVirtualCameraAction": VirtualCamera=!VirtualCamera; break;
                            }
                            await SendAsync(new {type=10,id=data.GetProperty("id").GetInt32(),data=(object)null});
                            if (PublishAfterCommand) await PublishAsync();
                        }
                    }
                } catch (WebSocketException) when (!_cancel.IsCancellationRequested) {
                    // A client may close without a full close handshake. Keep
                    // accepting reconnects instead of ending the mock server.
                }
            }
        } catch(Exception e) when (_cancel.IsCancellationRequested || e is WebSocketException) { }
    }
    public async ValueTask DisposeAsync() {
        _cancel.Cancel(); _socket?.Abort();
        // Start() can dispose its listener when binding fails. Preserve that
        // original error instead of masking it while unwinding the test.
        try { _listener.Stop(); } catch(ObjectDisposedException) { }
        if(_server!=null) await _server;
        _cancel.Dispose(); _send.Dispose();
    }
}
