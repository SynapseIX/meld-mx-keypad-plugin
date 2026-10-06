using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Loupedeck.MeldStudioControlsPlugin;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:13376/");
listener.Start();
var calls = new List<string>();
var session = new { items = new Dictionary<string, object> {
    ["SCENE-A"] = new { type = "scene", name = "Gameplay", index = 0, current = true },
    ["SCENE-B"] = new { type = "scene", name = "Starting Soon", index = 1, current = false }
} };
var server = Task.Run(async () => {
    var context = await listener.GetContextAsync();
    var accepted = await context.AcceptWebSocketAsync(null);
    using var ws = accepted.WebSocket;
    var bytes = new byte[65536];
    var sentScenes = false;
    while (ws.State == WebSocketState.Open) {
        var received = await ws.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
        if (received.MessageType == WebSocketMessageType.Close) break;
        using var doc = JsonDocument.Parse(bytes.AsMemory(0, received.Count));
        var message = doc.RootElement;
        var type = message.GetProperty("type").GetInt32();
        if (type is 3 or 6) {
            if (type == 6) calls.Add(message.GetProperty("method").GetString());
            var id = message.GetProperty("id").GetInt32();
            // Meld may connect before its session has finished loading.
            var data = type == 3 ? JsonSerializer.SerializeToElement(new { meld = new {
                methods = new object[][] { new object[] { "toggleStream", 0 }, new object[] { "showScene", 1 } },
                properties = new object[][] {
                    new object[] { 0, "session", new object[0], new { items = new Dictionary<string, object>() } },
                    new object[] { 1, "isStreaming", new object[0], false }
                }, signals = new object[0]
            } }) : JsonSerializer.SerializeToElement((object)null);
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = 10, id, data }),
                WebSocketMessageType.Text, true, CancellationToken.None);
            if (type == 6 && calls.Count == 1)
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new {
                    type = 2, data = new[] { new { @object = "meld", properties = new Dictionary<string, object> { ["1"] = true } } }
                }), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else if (type == 4 && !sentScenes) {
            sentScenes = true;
            await Task.Delay(250);
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new {
                type = 2, data = new[] { new { @object = "meld", properties = new Dictionary<string, object> { ["0"] = session } } }
            }), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
});
var client = MeldClient.Instance;
var changes = 0;
client.Changed += () => Interlocked.Increment(ref changes);
client.Start();
for (var i = 0; i < 60 && client.Scenes().Count != 2; i++) await Task.Delay(100);
if (client.Scenes().Select(x => x.Name).SequenceEqual(new[] { "Gameplay", "Starting Soon" }) == false ||
    !client.IsConnected || changes < 2) throw new Exception("Delayed scene discovery or update notification failed.");
await client.InvokeAsync("toggleStream");
await client.InvokeAsync("showScene", client.Scenes().Last().Id);
for (var i = 0; i < 40 && client.State("isStreaming") != true; i++) await Task.Delay(100);
if (client.State("isStreaming") != true || !calls.SequenceEqual(new[] { "toggleStream", "showScene" }))
    throw new Exception("Calls or live status update failed.");
Console.WriteLine("Delayed scenes, change notification, streaming status, and calls passed.");
client.Stop();
listener.Stop();

namespace Loupedeck.MeldStudioControlsPlugin {
    internal static class PluginLog { public static void Warning(string message) => Console.Error.WriteLine(message); }
}
