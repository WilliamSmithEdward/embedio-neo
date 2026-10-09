using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private const string ClientHeader = "X-Issue502-Client";

        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        public Task OriginalDelayedLargeReplyPatternSurvivesConcurrentBroadcasts(HttpListenerMode mode, int clientCount)
            => RunWorkloadAsync(mode, clientCount, null);

        // WebSocketModule adds a connection to the contexts used by BroadcastAsync after the
        // opening-handshake response has been written, so a client can observe an open connection
        // first. This holds the server between those steps for every client and checks that the
        // workload's broadcasts still reach them all.
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        public async Task OriginalPatternSurvivesRegistrationAfterTheClientHandshake(HttpListenerMode mode, int clientCount)
        {
            var source = EmbedIO.Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            using var delay = new RegistrationDelay(clientCount);
            source.Listeners.Add(delay);
            source.Switch.Level = SourceLevels.Verbose;
            try
            {
                await RunWorkloadAsync(mode, clientCount, delay);
            }
            finally
            {
                source.Switch.Level = previousLevel;
                source.Listeners.Remove(delay);
                delay.ReleaseAll();
            }
        }

        private static async Task RunWorkloadAsync(HttpListenerMode mode, int clientCount, RegistrationDelay? delay)
        {
            const int count = 20;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new LargeModule();
            delay?.Watch(module.BaseRoute);
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
                    client.Options.SetRequestHeader(ClientHeader, i.ToString(CultureInfo.InvariantCulture));
                    await client.ConnectAsync(new Uri(url.Replace("http://", "ws://", StringComparison.Ordinal) + "event"), timeout.Token);
                    Assert.That(client.SubProtocol, Is.EqualTo("json"));
                    if (delay != null)
                    {
                        Assert.That(await delay.WaitUntilHeldAsync(i, TimeSpan.FromSeconds(10)), Is.True, "The server did not reach the held registration step.");
                        Assert.That(module.IsRegistered(i), Is.False, "The connection was registered before the held step.");
                        delay.Release(i);
                    }
                }
                var registered = clients.Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                var readers = clients.Select(async (client, index) =>
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
                        if (type == "connected") { connected++; registered[index].TrySetResult(true); continue; }
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
                // A completed ConnectAsync does not mean the server can broadcast to the connection
                // yet. The module sends "connected" only after registering it, so wait for that.
                // A reader can only finish before then by failing; awaiting it reports the failure.
                var ready = Task.WhenAll(registered.Select(r => r.Task));
                await await Task.WhenAny(readers.Append(ready)).WaitAsync(timeout.Token);
                await Task.WhenAll(sending, module.BroadcastLarge(0), module.BroadcastLarge(1));
                await Task.WhenAll(readers);
                foreach (var client in clients)
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                Assert.That(module.Fault.Task.IsCompleted, Is.False);
            }
            finally
            {
                // A failed assertion must not leave the server held during shutdown.
                delay?.ReleaseAll();
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
            public bool IsRegistered(int client)
                => ActiveContexts.Any(c => c.Headers[ClientHeader] == client.ToString(CultureInfo.InvariantCulture));
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

        // WebSocketModule logs "<route> - Purged ..." at Verbose level after writing the
        // opening-handshake response and before registering the connection. Blocking that call
        // holds the server inside the window until the test has observed it, then for at least
        // MinimumHold so a broadcast issued immediately afterwards could not reach the connection.
        private sealed class RegistrationDelay : TraceListener
        {
            private static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(250);
            private static readonly TimeSpan MaximumHold = TimeSpan.FromSeconds(10);
            private readonly TaskCompletionSource<bool>[] _held;
            private readonly TaskCompletionSource<bool>[] _released;
            private string? _purgePrefix;
            private int _next = -1;

            public RegistrationDelay(int registrations)
            {
                _held = Enumerable.Range(0, registrations).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                _released = Enumerable.Range(0, registrations).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            }

            public override bool IsThreadSafe => true;

            public void Watch(string baseRoute) => Volatile.Write(ref _purgePrefix, baseRoute + " - Purged ");

            public async Task<bool> WaitUntilHeldAsync(int registration, TimeSpan timeout)
            {
                try
                {
                    await _held[registration].Task.WaitAsync(timeout);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public void Release(int registration) => _released[registration].TrySetResult(true);

            public void ReleaseAll()
            {
                foreach (var released in _released) released.TrySetResult(true);
            }

            public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
            {
                if (args is not { Length: 2 } || !Equals(args[0], nameof(WebSocketModule)) || args[1] is not string message
                    || Volatile.Read(ref _purgePrefix) is not { } prefix || !message.StartsWith(prefix, StringComparison.Ordinal))
                    return;
                var registration = Interlocked.Increment(ref _next);
                if (registration >= _held.Length)
                    return;
                var held = Stopwatch.StartNew();
                _held[registration].TrySetResult(true);
                _released[registration].Task.Wait(MaximumHold);
                var remaining = MinimumHold - held.Elapsed;
                if (remaining > TimeSpan.Zero)
                    Thread.Sleep(remaining);
            }

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
            }
        }
    }
}
