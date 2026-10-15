using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue105_NativeWebSocketShutdown
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task SilentPeerCanBeCanceledAndTheServerRemainsHealthy(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var module = new ClosingModule("echo");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                var context = await module.Connected.Task.WaitAsync(timeout.Token);
                using var cancellation = new CancellationTokenSource();
                var close = context.WebSocket.CloseAsync(cancellation.Token);
                var frame = await client.ReceiveAsync(new ArraySegment<byte>(new byte[32]), timeout.Token);
                Assert.That(frame.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                cancellation.Cancel();
                try { await close.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) { }
                await module.Disconnected.Task.WaitAsync(timeout.Token);
                Assert.That(module.ActiveCount, Is.Zero);
                module.Reset();
                using var healthy = new ClientWebSocket();
                await healthy.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                await healthy.SendAsync(new ArraySegment<byte>(new byte[] { 7 }), WebSocketMessageType.Binary, true, timeout.Token);
                var buffer = new byte[32];
                var response = await healthy.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.That(response.Count, Is.EqualTo(1));
                Assert.That(buffer[0], Is.EqualTo(7));
                await healthy.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                await module.Disconnected.Task.WaitAsync(timeout.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, "simultaneous")]
        [TestCase(HttpListenerMode.EmbedIO, "connected")]
        [TestCase(HttpListenerMode.EmbedIO, "message")]
        [TestCase(HttpListenerMode.EmbedIO, "protected")]
        [TestCase(HttpListenerMode.EmbedIO, "reset")]
        public async Task RealConnectionsCloseAndSubsequentConnectionsRemainHealthy(HttpListenerMode mode, string scenario)
        {
            var url = Resources.GetServerAddress();
            using var module = new ClosingModule(scenario);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            try
            {
                var iterations = scenario == "simultaneous" ? 10 : 3;
                var random = new Random(42);
                for (var i = 0; i < iterations; i++)
                {
                    module.Reset();
                    using var client = new ClientWebSocket();
                    await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                    var context = await module.Connected.Task.WaitAsync(timeout.Token);
                    if (scenario == "reset")
                    {
                        client.Abort();
                    }
                    else if (scenario == "simultaneous")
                    {
                        var clientClose = client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer", timeout.Token);
                        Thread.SpinWait(random.Next(200_000));
                        var serverClose = Task.Run(async () =>
                        {
                            await context.WebSocket.CloseAsync(timeout.Token);
                        });
                        var reply = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), timeout.Token);
                        Assert.That(reply.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                        await Task.WhenAll(clientClose, serverClose).WaitAsync(timeout.Token);
                    }
                    else
                    {
                        if (scenario == "message" || scenario == "protected")
                            await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("close")), WebSocketMessageType.Text, true, timeout.Token);
                        var reply = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), timeout.Token);
                        Assert.That(reply.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                        Assert.That(reply.CloseStatus, Is.EqualTo(scenario == "protected" ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation));
                        Assert.That(reply.CloseStatusDescription, Is.EqualTo(scenario == "protected" ? string.Empty : "test close"));
                        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", timeout.Token);
                    }
                    await module.Disconnected.Task.WaitAsync(timeout.Token);
                    Assert.That(module.DisconnectCount, Is.EqualTo(1));
                    Assert.That(module.ActiveCount, Is.Zero);
                }
                module.Scenario = "echo";
                module.Reset();
                using var healthy = new ClientWebSocket();
                await healthy.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                await healthy.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("healthy")), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[32];
                var echo = await healthy.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, echo.Count), Is.EqualTo("healthy"));
                await healthy.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                await module.Disconnected.Task.WaitAsync(timeout.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task ModuleDisposalCanRacePeerClose(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var module = new ClosingModule("echo");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                await module.Connected.Task.WaitAsync(timeout.Token);
                var peerClose = client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer", timeout.Token);
                var dispose = Task.Run(module.Dispose);
                var reply = await client.ReceiveAsync(new ArraySegment<byte>(new byte[32]), timeout.Token);
                Assert.That(reply.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                await Task.WhenAll(peerClose, dispose).WaitAsync(timeout.Token);
                await module.Disconnected.Task.WaitAsync(timeout.Token);
                Assert.That(module.DisconnectCount, Is.EqualTo(1));
                Assert.That(module.ActiveCount, Is.Zero);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private sealed class ClosingModule : WebSocketModule
        {
            public ClosingModule(string scenario) : base("/socket", false) => Scenario = scenario;
            public string Scenario { get; set; }
            public TaskCompletionSource<IWebSocketContext> Connected { get; private set; } = NewSource<IWebSocketContext>();
            public TaskCompletionSource<bool> Disconnected { get; private set; } = NewSource<bool>();
            public int DisconnectCount;
            public int ActiveCount => ActiveContexts.Count;
            public void Reset() { Connected = NewSource<IWebSocketContext>(); Disconnected = NewSource<bool>(); DisconnectCount = 0; }
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(context);
                if (Scenario == "connected") await context.WebSocket.CloseAsync(CloseStatusCode.PolicyViolation, "test close", context.CancellationToken);
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Scenario == "protected" ? CloseAsync(context) : Scenario == "message" ? context.WebSocket.CloseAsync(CloseStatusCode.PolicyViolation, "test close", context.CancellationToken) : SendAsync(context, buffer);
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref DisconnectCount);
                Disconnected.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
