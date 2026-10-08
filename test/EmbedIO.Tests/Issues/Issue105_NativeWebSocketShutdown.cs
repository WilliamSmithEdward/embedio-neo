using System;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
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
        private static IWebSocket Wrap(System.Net.WebSockets.WebSocket socket)
            => (IWebSocket)(Activator.CreateInstance((typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.SystemWebSocket", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), socket) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private static Task<WebSocketReceiveResult> Receive(IWebSocket socket, CancellationToken token)
            => (Task<WebSocketReceiveResult>)((((socket).GetType().GetMethod("ReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(socket, new object[] { new ArraySegment<byte>(new byte[16]), token })) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        [TestCase(false)]
        [TestCase(true)]
        public async Task FullCloseWaitsForPeerWithoutCallingNativeCloseAsync(bool pendingReceive)
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            var receive = pendingReceive ? Receive(socket, CancellationToken.None) : null;
            var close = socket.CloseAsync(CloseStatusCode.PolicyViolation, "policy");
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(close.IsCompleted, Is.False, "Sending a close frame alone must not complete full close.");
            native.PeerClose();
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            if (receive != null) await receive.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(native.OutputCount, Is.EqualTo(1));
            Assert.That(native.MaximumReaders, Is.EqualTo(1));
            Assert.That(native.SentStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
            Assert.That(native.SentReason, Is.EqualTo("policy"));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposalReleasesGatesAfterPendingOperationsFinish(bool pendingReceive)
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            var receive = pendingReceive ? Receive(socket, CancellationToken.None) : null;
            var close = socket.CloseAsync();
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            socket.Dispose();
            await FinishInterrupted(close);
            if (receive != null) await FinishInterrupted(receive);
            foreach (var name in new[] { "_receiveGate", "_closeGate" })
            {
                var gate = (SemaphoreSlim)((((socket).GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(socket)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
                Assert.That(() => (gate).Wait(0), Throws.InstanceOf<ObjectDisposedException>());
            }
        }

        private static async Task FinishInterrupted(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        [Test]
        public async Task UnexpectedDataDuringFullCloseAbortsTheConnection()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket { ResultType = WebSocketMessageType.Text };
            using var socket = Wrap(native);
            var close = socket.CloseAsync();
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            native.PeerClose();
            await Assert.ThatAsync(async () => await close.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<System.Net.WebSockets.WebSocketException>());
            Assert.That(native.AbortCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ConcurrentCloseCallsShareOneCompletedHandshake()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            var closes = Enumerable.Range(0, 8).Select(_ => socket.CloseAsync()).ToArray();
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(closes.All(t => !t.IsCompleted), Is.True);
            native.PeerClose();
            await Task.WhenAll(closes).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(native.OutputCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CancellationAbortsAnUnansweredHandshake()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            using var cancellation = new CancellationTokenSource();
            var close = socket.CloseAsync(cancellation.Token);
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThatAsync(async () => await close.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(native.AbortCount, Is.EqualTo(1));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
        }

        [Test]
        public async Task CancelingAQueuedCloseDoesNotAbortTheExistingClose()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            var first = socket.CloseAsync();
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var second = socket.CloseAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThatAsync(async () => await second, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(native.AbortCount, Is.Zero);
            native.PeerClose();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationReleasesAPendingReceiver(bool pendingReceive)
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            var receive = pendingReceive ? Receive(socket, CancellationToken.None) : null;
            using var cancellation = new CancellationTokenSource();
            var close = socket.CloseAsync(cancellation.Token);
            await native.Output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThatAsync(async () => await close.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>());
            if (receive != null)
                await Assert.ThatAsync(async () => await receive.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(native.MaximumReaders, Is.EqualTo(1));
        }

        [Test]
        public void InvalidCloseReasonThrowsSynchronously()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            Assert.That(() => socket.CloseAsync(CloseStatusCode.Normal, new string('é', 62)), Throws.InstanceOf<ArgumentException>());
            Assert.That(native.AbortCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InvalidCloseReasonIsRejectedBeforeAborting(bool terminal)
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows native workaround");
            using var native = new ControlledSocket();
            using var socket = Wrap(native);
            if (terminal) native.Abort();
            var before = native.AbortCount;
            await Assert.ThatAsync(async () => await socket.CloseAsync(CloseStatusCode.Normal, new string('é', 62)), Throws.InstanceOf<ArgumentException>());
            Assert.That(native.AbortCount, Is.EqualTo(before));
            Assert.That(native.OutputCount, Is.Zero);
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
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
                if (mode == HttpListenerMode.Microsoft)
                {
                    if (OperatingSystem.IsWindows())
                        Assert.That(context.WebSocket.State, Is.EqualTo(WebSocketState.Aborted));
                    else
                        // Unix's unchanged canceled close does not promise to abort its separate receive.
                        context.WebSocket.Dispose();
                }
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
        [TestCase(HttpListenerMode.Microsoft, "simultaneous")]
        [TestCase(HttpListenerMode.EmbedIO, "connected")]
        [TestCase(HttpListenerMode.Microsoft, "connected")]
        [TestCase(HttpListenerMode.EmbedIO, "message")]
        [TestCase(HttpListenerMode.Microsoft, "message")]
        [TestCase(HttpListenerMode.EmbedIO, "protected")]
        [TestCase(HttpListenerMode.Microsoft, "protected")]
        [TestCase(HttpListenerMode.EmbedIO, "reset")]
        [TestCase(HttpListenerMode.Microsoft, "reset")]
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
                var iterations = scenario == "simultaneous" ? (mode == HttpListenerMode.Microsoft ? 100 : 10) : 3;
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
                            try { await context.WebSocket.CloseAsync(timeout.Token); }
                            catch (System.Net.WebSockets.WebSocketException) when (mode == HttpListenerMode.Microsoft && !OperatingSystem.IsWindows() && context.WebSocket.State == WebSocketState.Closed)
                            {
                                // Unix's unchanged BCL close can lose the race to a completed peer close.
                            }
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
        [TestCase(HttpListenerMode.Microsoft)]
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

        private sealed class ControlledSocket : System.Net.WebSockets.WebSocket
        {
            private int _state = (int)WebSocketState.Open;
            private int _readers;
            private readonly TaskCompletionSource<bool> _peer = NewSource<bool>();
            public TaskCompletionSource<bool> Output { get; } = NewSource<bool>();
            public WebSocketMessageType ResultType = WebSocketMessageType.Close;
            public int OutputCount;
            public int MaximumReaders;
            public int AbortCount;
            public WebSocketCloseStatus SentStatus;
            public string? SentReason;
            public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
            public override string? CloseStatusDescription => "peer";
            public override string? SubProtocol => null;
            public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
            public override void Abort() { Interlocked.Increment(ref AbortCount); Volatile.Write(ref _state, (int)WebSocketState.Aborted); _peer.TrySetCanceled(); }
            public override void Dispose()
            {
                if (State != WebSocketState.Closed) Volatile.Write(ref _state, (int)WebSocketState.Aborted);
                _peer.TrySetCanceled();
            }
            public void PeerClose() => _peer.TrySetResult(true);
            public override Task CloseAsync(WebSocketCloseStatus code, string? reason, CancellationToken token) => throw new InvalidOperationException("The defective native full-close path must not be called.");
            public override Task CloseOutputAsync(WebSocketCloseStatus code, string? reason, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref OutputCount);
                SentStatus = code; SentReason = reason;
                Volatile.Write(ref _state, (int)WebSocketState.CloseSent);
                Output.TrySetResult(true);
                return Task.CompletedTask;
            }
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                var readers = Interlocked.Increment(ref _readers);
                MaximumReaders = Math.Max(MaximumReaders, readers);
                try
                {
                    Assert.That(readers, Is.EqualTo(1), "Native receives must not overlap.");
                    await _peer.Task.WaitAsync(token);
                    Volatile.Write(ref _state, (int)(ResultType == WebSocketMessageType.Close ? WebSocketState.Closed : WebSocketState.CloseSent));
                    return new WebSocketReceiveResult(0, ResultType, true);
                }
                finally { Interlocked.Decrement(ref _readers); }
            }
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
        }
    }
}
