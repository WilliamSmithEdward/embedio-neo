using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Adapted workload from bdurrer/embedio-websocket-example (public-domain dedication):
    // JSON subprotocol, 20ms inputs, 50-150ms work and 1000 rows of ten 20-character columns.
    public class Issue502_LargeMessages
    {
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        public async Task OriginalDelayedLargeReplyPatternSurvivesConcurrentBroadcasts(HttpListenerMode mode, int clientCount)
        {
            const int count = 20;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new LargeModule();
            server.WithModule(module);
            var running = server.RunAsync(stop.Token);
            var clients = new List<ClientWebSocket>();
            try
            {
                for (var i = 0; i < clientCount; i++)
                {
                    var client = new ClientWebSocket();
                    clients.Add(client);
                    client.Options.AddSubProtocol("json");
                    await client.ConnectAsync(new Uri(url.Replace("http://", "ws://", StringComparison.Ordinal) + "event"), timeout.Token);
                    Assert.That(client.SubProtocol, Is.EqualTo("json"));
                }
                var readers = clients.Select(async client =>
                {
                    var replies = new HashSet<int>();
                    var broadcasts = new HashSet<int>();
                    var connected = 0;
                    for (var i = 0; i < count + 3; i++)
                    {
                        using var data = new MemoryStream();
                        var buffer = new byte[713];
                        WebSocketReceiveResult part;
                        do
                        {
                            part = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                            Assert.That(part.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                            data.Write(buffer, 0, part.Count);
                        } while (!part.EndOfMessage);
                        using var document = JsonDocument.Parse(data.ToArray());
                        var root = document.RootElement;
                        var type = root.GetProperty("type").GetString();
                        if (type == "connected") { connected++; continue; }
                        var sequence = root.GetProperty("sequence").GetInt32();
                        Assert.That((type == "spam-back" ? replies : broadcasts).Add(sequence), Is.True);
                        var table = root.GetProperty("data");
                        Assert.That(table.GetArrayLength(), Is.EqualTo(1000));
                        foreach (var row in table.EnumerateArray())
                        {
                            var columns = row.GetProperty("columns");
                            Assert.That(columns.GetArrayLength(), Is.EqualTo(10));
                            foreach (var column in columns.EnumerateArray()) Assert.That(column.GetString(), Is.EqualTo(new string('X', 20)));
                        }
                    }
                    Assert.That(connected, Is.EqualTo(1));
                    Assert.That(replies, Is.EquivalentTo(Enumerable.Range(0, count)));
                    Assert.That(broadcasts, Is.EquivalentTo(new[] { 0, 1 }));
                }).ToArray();
                var sending = Task.WhenAll(clients.Select(async client =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "spam", sequence = i, data = new string('G', 1000) }));
                        await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                        await Task.Delay(20, timeout.Token);
                    }
                }));
                await Task.WhenAll(sending, module.BroadcastLarge(0), module.BroadcastLarge(1));
                await Task.WhenAll(readers);
                foreach (var client in clients)
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                Assert.That(module.Fault.Task.IsCompleted, Is.False);
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class LargeModule : WebSocketModule
        {
            private static readonly object[] Table = Enumerable.Range(0, 1000)
                .Select(_ => (object)new { columns = Enumerable.Repeat(new string('X', 20), 10).ToArray() }).ToArray();
            public LargeModule() : base("/event", true) { AddProtocol("json"); }
            public TaskCompletionSource<Exception> Fault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
                => SendAsync(context, "{\"type\":\"connected\",\"data\":{}}");
            protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                try
                {
                    using var request = JsonDocument.Parse(buffer);
                    var sequence = request.RootElement.GetProperty("sequence").GetInt32();
                    await Task.Delay(50 + sequence * 47 % 101, context.CancellationToken);
                    await SendAsync(context, JsonSerializer.Serialize(new { type = "spam-back", sequence, data = Table }));
                }
                catch (Exception error) { Fault.TrySetResult(error); throw; }
            }
            public Task BroadcastLarge(int sequence)
                => BroadcastAsync(JsonSerializer.Serialize(new { type = "broadcast", sequence, data = Table }));
        }
    }
}
