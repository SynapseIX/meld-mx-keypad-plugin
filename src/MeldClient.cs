namespace Loupedeck.MeldStudioControlsPlugin;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed record MeldItem(String Id, String Name, String Type, String Parent, Int32 Index,
    Boolean? Current, Boolean? Muted, Boolean? Visible, Boolean HasNonCaptureSource);

// Qt WebChannel message subset used by Meld. One connection supplies all button actions.
internal sealed class MeldClient
{
    public static MeldClient Instance { get; } = new();
    public event Action Changed;
    public Boolean IsConnected => _ready.Task.IsCompletedSuccessfully;

    private readonly Object _sync = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Int32, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Dictionary<String, (Int32 Index, JsonElement Value)> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<String> _methods = new();
    private CancellationTokenSource _lifetime;
    private ClientWebSocket _socket;
    private TaskCompletionSource<Boolean> _ready = NewReady();
    private Int32 _nextId;
    private Int64 _sessionRevision;

    private static TaskCompletionSource<Boolean> NewReady() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void NotifyChanged()
    {
        foreach (Action callback in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { callback(); }
            catch (Exception error) { PluginLog.Warning($"Meld button refresh: {error.Message}"); }
    }

    public void Start()
    {
        if (_lifetime != null) return;
        _lifetime = new CancellationTokenSource();
        var cancellation = _lifetime.Token;
        _ = Task.Run(() => RunAsync(cancellation));
    }

    public void Stop()
    {
        _lifetime?.Cancel();
        _socket?.Abort();
        _lifetime = null;
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:13376"), cancellation);
                _socket = socket;
                var receiving = ReceiveAsync(socket, cancellation);
                var init = await RequestAsync(new { type = 3 }, cancellation);
                if (!init.TryGetProperty("meld", out var meld))
                    throw new InvalidDataException("Meld did not expose its WebChannel object.");
                lock (_sync)
                {
                    _methods.Clear();
                    foreach (var method in meld.GetProperty("methods").EnumerateArray())
                        _methods.Add(method[0].GetString());
                    _properties.Clear();
                    foreach (var property in meld.GetProperty("properties").EnumerateArray())
                        _properties[property[1].GetString()] = (property[0].GetInt32(), property[3].Clone());
                    _sessionRevision++;
                }
                await SendAsync(new { type = 4 }, cancellation);
                _ready.TrySetResult(true);
                NotifyChanged();
                await receiving;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { PluginLog.Warning($"Meld connection: {error.Message}"); }
            finally
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                lock (_sync) { _properties.Clear(); _methods.Clear(); _sessionRevision++; }
                foreach (var pending in _pending)
                    if (_pending.TryRemove(pending.Key, out var completion))
                        completion.TrySetException(new IOException("Meld connection closed."));
                _ready = NewReady();
                NotifyChanged();
            }
            try { await Task.Delay(2500, cancellation); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new Byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("Meld message is too large.");
            using var json = JsonDocument.Parse(message.ToArray());
            message.SetLength(0);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var type)) continue;
            if (type.GetInt32() == 10 && root.TryGetProperty("id", out var id))
            {
                if (_pending.TryRemove(id.GetInt32(), out var completion))
                    completion.TrySetResult(root.GetProperty("data").Clone());
            }
            else if (type.GetInt32() == 2 && root.TryGetProperty("data", out var updates))
            {
                lock (_sync)
                {
                    foreach (var update in updates.EnumerateArray())
                    {
                        if (update.GetProperty("object").GetString() != "meld" ||
                            !update.TryGetProperty("properties", out var values)) continue;
                        foreach (var value in values.EnumerateObject())
                        {
                            var index = Int32.Parse(value.Name);
                            foreach (var name in _properties.Keys.ToArray())
                                if (_properties[name].Index == index)
                                {
                                    _properties[name] = (index, value.Value.Clone());
                                    if (String.Equals(name, "session", StringComparison.OrdinalIgnoreCase)) _sessionRevision++;
                                }
                        }
                    }
                }
                await SendAsync(new { type = 4 }, cancellation);
                NotifyChanged();
            }
        }
    }

    private async Task SendAsync(Object data, CancellationToken cancellation)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) throw new IOException("Meld is disconnected.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        await _sendLock.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellation); }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonElement> RequestAsync(Object data, CancellationToken cancellation)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            // Merge the protocol request with its response ID.
            using var baseJson = JsonDocument.Parse(JsonSerializer.Serialize(data));
            var request = new Dictionary<String, Object> { ["id"] = id };
            foreach (var field in baseJson.RootElement.EnumerateObject()) request[field.Name] = field.Value.Clone();
            await SendAsync(request, cancellation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task InvokeAsync(String method, params Object[] arguments)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (_sync)
            if (!_methods.Contains(method)) throw new InvalidOperationException($"Meld does not support {method}.");
        await RequestAsync(new { type = 6, @object = "meld", method, args = arguments },
            _lifetime?.Token ?? CancellationToken.None);
    }

    public async Task RefreshSessionAsync(CancellationToken cancellation)
    {
        // Qt WebChannel's init response reads the current property values.
        // Reuse the transport to fetch a fresh snapshot; leave subscriptions intact.
        ClientWebSocket socket;
        Int64 revision;
        lock (_sync) { socket = _socket; revision = _sessionRevision; }
        if (!IsConnected) throw new IOException("Meld is disconnected.");
        var snapshot = await RequestAsync(new { type = 3 }, cancellation);
        if (!snapshot.TryGetProperty("meld", out var meld) || !meld.TryGetProperty("properties", out var properties))
            throw new InvalidDataException("Meld did not return a session snapshot.");
        foreach (var property in properties.EnumerateArray())
        {
            if (!String.Equals(property[1].GetString(), "session", StringComparison.OrdinalIgnoreCase)) continue;
            var updated = false;
            lock (_sync)
            {
                // A push received while awaiting the response takes precedence.
                // Also prevent an old connection's response from reviving stale state.
                if (ReferenceEquals(socket, _socket) && IsConnected && revision == _sessionRevision)
                {
                    _properties["session"] = (property[0].GetInt32(), property[3].Clone());
                    _sessionRevision++;
                    updated = true;
                }
            }
            if (updated) NotifyChanged();
            return;
        }
        throw new InvalidDataException("Meld's snapshot did not contain the session property.");
    }

    public Boolean? State(String name)
    {
        lock (_sync)
            return _properties.TryGetValue(name, out var property) &&
                   property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.Value.GetBoolean() : null;
    }

    public IReadOnlyList<MeldItem> Items()
    {
        lock (_sync)
        {
            if (!_properties.TryGetValue("session", out var property) ||
                property.Value.ValueKind != JsonValueKind.Object ||
                !TryProperty(property.Value, "items", out var items) ||
                items.ValueKind != JsonValueKind.Object) return Array.Empty<MeldItem>();
            var output = new List<MeldItem>();
            foreach (var entry in items.EnumerateObject())
            {
                var item = entry.Value;
                if (item.ValueKind != JsonValueKind.Object) continue;
                String text(String name) => TryProperty(item, name, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                Boolean? flag(String name) => TryProperty(item, name, out var value) &&
                    value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
                output.Add(new MeldItem(entry.Name, text("name"), text("type")?.Trim().ToLowerInvariant(), text("parent"),
                    TryProperty(item, "index", out var index) && index.ValueKind == JsonValueKind.Number &&
                        index.TryGetInt32(out var position) ? position : 0,
                    flag("current"), flag("muted"), flag("visible"),
                    text("source") != null || text("url") != null || text("mediaSource") != null));
            }
            return output;
        }
    }

    private static Boolean TryProperty(JsonElement item, String name, out JsonElement value)
    {
        if (item.TryGetProperty(name, out value)) return true;
        foreach (var property in item.EnumerateObject())
            if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        return false;
    }

    public IReadOnlyList<MeldItem> Scenes() => Items().Where(x => x.Type == "scene")
        .OrderBy(x => x.Index).ToArray();
}
