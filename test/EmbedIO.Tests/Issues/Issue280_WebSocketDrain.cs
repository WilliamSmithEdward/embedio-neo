using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Graceful drain and open WebSockets: by default drain waits for them until its deadline; with
    // WebSocketModule.CloseOnDrain it closes them with 1001 Going Away so the drain can end early.
    [TestFixture]
    public sealed class Issue280_WebSocketDrain
    {
        private const int Large = 4 * 1024 * 1024;

        private sealed class Hub : WebSocketModule
        {
            private int _connected;
            private int _disconnected;

            internal Hub(string route, bool closeOnDrain) : base(route, false) => CloseOnDrain = closeOnDrain;

            internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int ConnectedCount => Volatile.Read(ref _connected);

            internal int DisconnectedCount => Volatile.Read(ref _disconnected);

            internal void TrySetCloseOnDrain(bool value) => CloseOnDrain = value;

            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref _connected);
                Connected.TrySetResult();
                return Task.CompletedTask;
            }

            protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                var text = Encoding.UTF8.GetString(buffer);
                if (text == "large")
                {
                    Sending.TrySetResult();
                    await context.WebSocket.SendAsync(new byte[Large], false, CancellationToken.None).ConfigureAwait(false);
                }
                else if (text == "bye")
                {
                    await context.WebSocket.CloseAsync(CloseStatusCode.Normal, "bye", CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await context.WebSocket.SendAsync(buffer, true, CancellationToken.None).ConfigureAwait(false);
                }
            }

            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref _disconnected);
                Disconnected.TrySetResult();
                return Task.CompletedTask;
            }
        }

        // Holds requests to a path until released, so an upgrade can be accepted after a drain started.
        private sealed class Gate : WebModuleBase
        {
            internal Gate(string route) : base(route) { }

            public override bool IsFinalHandler => false;

            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task OnRequestAsync(IHttpContext context)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            internal Host(Hub hub, IWebModule? before = null)
            {
                Url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
                Server = new WebServer(o => o.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO));
                if (before != null)
                    Server.WithModule(before);
                Server.WithModule(hub);
                Server.WithModule(new ActionModule("/slow", HttpVerbs.Get, async context =>
                {
                    SlowEntered.TrySetResult();
                    await SlowRelease.Task.ConfigureAwait(false);
                    await context.SendStringAsync("slow", "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
                }));
                Running = Server.RunAsync(_stop.Token);
            }

            internal string Url { get; }

            internal WebServer Server { get; }

            internal Task Running { get; }

            internal TaskCompletionSource SlowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource SlowRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Uri WebSocketUri(string path) => new Uri(Url.Replace("http:", "ws:", StringComparison.Ordinal) + path.TrimStart('/'));

            public async ValueTask DisposeAsync()
            {
                SlowRelease.TrySetResult();
                _stop.Cancel();
                try { await Running.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                finally
                {
                    Server.Dispose();
                    _stop.Dispose();
                }
            }
        }

        private sealed class Client : IDisposable
        {
            private readonly SocketsHttpHandler _handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };

            internal Client(bool http2)
            {
                Http2 = http2;
                Invoker = new HttpMessageInvoker(_handler, false);
                Http = new HttpClient(_handler, false) { Timeout = TimeSpan.FromSeconds(30) };
                if (http2)
                {
                    Http.DefaultRequestVersion = HttpVersion.Version20;
                    Http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                }
            }

            internal bool Http2 { get; }

            internal HttpMessageInvoker Invoker { get; }

            internal HttpClient Http { get; }

            internal async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken token)
            {
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                if (Http2)
                {
                    socket.Options.HttpVersion = HttpVersion.Version20;
                    socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                }

                try
                {
                    await socket.ConnectAsync(uri, Http2 ? Invoker : null, token).ConfigureAwait(false);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                Http.Dispose();
                Invoker.Dispose();
                _handler.Dispose();
            }
        }

        // Reads until the peer's close frame; returns its status, or null if the connection ended without one.
        private static async Task<(WebSocketCloseStatus? Status, string? Reason, long Bytes)> ReadToCloseAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            long bytes = 0;
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return (result.CloseStatus, result.CloseStatusDescription, bytes);
                    bytes += result.Count;
                }
            }
            catch (Exception error) when (error is System.Net.WebSockets.WebSocketException or IOException or HttpRequestException)
            {
                return (null, null, bytes);
            }
        }

        // Answers the server's close so the closing handshake completes, as a well-behaved client does.
        private static async Task AnswerCloseAsync(ClientWebSocket socket, CancellationToken token)
        {
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", token).ConfigureAwait(false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ByDefaultDrainWaitsForAnOpenWebSocketUntilItsDeadline(bool http2)
        {
            var hub = new Hub("/ws", false);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            var reading = ReadToCloseAsync(socket, stop.Token);
            var clock = Stopwatch.StartNew();
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(1.5), stop.Token);
            // Nothing closes the socket before the deadline. What the client sees at the deadline, a cut or a
            // close sent while the server abandons the socket, depends on timing and is not part of the contract.
            await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            Assert.That(reading.IsCompleted, Is.False, "The WebSocket stayed open before the deadline.");
            await drain;
            clock.Stop();

            Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.3)), "Drain waited for the WebSocket until its deadline.");
            _ = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(host.Server.DrainStarted.IsCancellationRequested, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CloseOnDrainSendsGoingAwayAndTheDrainEndsEarly(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            var reading = ReadToCloseAsync(socket, stop.Token);
            var clock = Stopwatch.StartNew();
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
            var closed = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(closed.Status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            Assert.That(closed.Reason, Is.EqualTo("Server is draining"));
            await AnswerCloseAsync(socket, stop.Token);

            await drain.WaitAsync(TimeSpan.FromSeconds(15));
            clock.Stop();
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "The drain ended without waiting for its deadline.");
            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // A message the server is sending when the drain starts arrives whole, before the close.
        [TestCase(false)]
        [TestCase(true)]
        public async Task AMessageBeingSentArrivesBeforeTheClose(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("large"), WebSocketMessageType.Text, true, stop.Token);
            await hub.Sending.Task.WaitAsync(stop.Token);
            // The client is not reading yet, so the 4 MiB send is under way, held by flow control, when the drain starts.
            await Task.Delay(300, stop.Token);

            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(30), stop.Token);
            var closed = await ReadToCloseAsync(socket, stop.Token);
            Assert.That(closed.Bytes, Is.EqualTo(Large));
            Assert.That(closed.Status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            await AnswerCloseAsync(socket, stop.Token);
            await drain.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // The application closes the socket itself while the drain closes it: one disconnection, no failure.
        [TestCase(false)]
        [TestCase(true)]
        public async Task ACallbackCloseRacingTheDrainCleansUpOnce(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            var reading = ReadToCloseAsync(socket, stop.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("bye"), WebSocketMessageType.Text, true, stop.Token);
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
            var closed = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(closed.Status, Is.EqualTo(WebSocketCloseStatus.NormalClosure).Or.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            await AnswerCloseAsync(socket, stop.Token);
            await drain.WaitAsync(TimeSpan.FromSeconds(15));
            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200, stop.Token);
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // The client closes at the moment the drain starts.
        [TestCase(false)]
        [TestCase(true)]
        public async Task ASimultaneousClientCloseCleansUpOnce(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            var closing = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client", stop.Token);
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
            try { await closing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (System.Net.WebSockets.WebSocketException)
            {
                // The server's 1001 may cross the client's close; either side may see the other end first.
            }

            await drain.WaitAsync(TimeSpan.FromSeconds(15));
            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200, stop.Token);
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // A client that never answers the close cannot hold the drain past its deadline.
        [TestCase(false)]
        [TestCase(true)]
        public async Task AnUnresponsiveClientIsBoundedByTheDeadline(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            // The client never reads, so it never sees or answers the close.
            var clock = Stopwatch.StartNew();
            await host.Server.DrainAsync(TimeSpan.FromSeconds(3), stop.Token).WaitAsync(TimeSpan.FromSeconds(15));
            clock.Stop();
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancelingTheDrainEndsItAndCleansUpOnce(bool http2)
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(http2);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);

            using var cancel = new CancellationTokenSource();
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(30), cancel.Token);
            cancel.Cancel();
            try { await drain.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException)
            {
                // Cancellation aborts the remaining connections.
            }

            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // On HTTP/2, closing the WebSocket stream leaves a sibling request on the same connection unharmed.
        [Test]
        public async Task Http2SiblingRequestsFinishDuringTheDrain()
        {
            var hub = new Hub("/ws", true);
            await using var host = new Host(hub);
            using var client = new Client(true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await hub.Connected.Task.WaitAsync(stop.Token);
            var slow = client.Http.GetStringAsync(new Uri(host.Url + "slow"), stop.Token);
            await host.SlowEntered.Task.WaitAsync(stop.Token);

            var reading = ReadToCloseAsync(socket, stop.Token);
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
            Assert.That((await reading.WaitAsync(TimeSpan.FromSeconds(10))).Status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            await AnswerCloseAsync(socket, stop.Token);
            host.SlowRelease.SetResult();
            Assert.That(await slow.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo("slow"));
            await drain.WaitAsync(TimeSpan.FromSeconds(15));
        }

        // An upgrade still in flight when the drain starts is accepted, then closed with 1001 straight away.
        [Test]
        public async Task AWebSocketAcceptedAfterTheDrainStartedIsClosed()
        {
            var hub = new Hub("/ws", true);
            var gate = new Gate("/ws");
            await using var host = new Host(hub, gate);
            using var client = new Client(false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var connecting = client.ConnectAsync(host.WebSocketUri("/ws"), stop.Token);
            await gate.Entered.Task.WaitAsync(stop.Token);

            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
            gate.Release.SetResult();
            using var socket = await connecting;
            var closed = await ReadToCloseAsync(socket, stop.Token).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(closed.Status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            await AnswerCloseAsync(socket, stop.Token);
            await drain.WaitAsync(TimeSpan.FromSeconds(15));
            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(hub.DisconnectedCount, Is.EqualTo(1));
        }

        // Modules inside a module group are found too.
        [Test]
        public async Task NestedModulesCloseOnDrain()
        {
            var hub = new Hub("/ws", true);
            var group = new ModuleGroup("/chat", true).WithModule(hub);
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(group);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new Client(false);
                using var socket = await client.ConnectAsync(new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "chat/ws"), stop.Token);
                await hub.Connected.Task.WaitAsync(stop.Token);
                var reading = ReadToCloseAsync(socket, stop.Token);
                var drain = server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
                Assert.That((await reading.WaitAsync(TimeSpan.FromSeconds(10))).Status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
                await AnswerCloseAsync(socket, stop.Token);
                await drain.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        [Test]
        public async Task DrainStartedFiresOnceAndAThrowingCallbackDoesNotStopTheDrain()
        {
            var hub = new Hub("/ws", false);
            await using var host = new Host(hub);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var calls = 0;
            Assert.That(host.Server.DrainStarted.IsCancellationRequested, Is.False);
            using (host.Server.DrainStarted.Register(() => Interlocked.Increment(ref calls)))
            using (host.Server.DrainStarted.Register(() => throw new InvalidOperationException("callback failure")))
            {
                var first = host.Server.DrainAsync(TimeSpan.FromSeconds(5), stop.Token);
                var second = host.Server.DrainAsync(TimeSpan.FromSeconds(5), stop.Token);
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(host.Server.DrainStarted.IsCancellationRequested, Is.True);
        }

        [Test]
        public async Task CloseOnDrainIsLockedOnceTheServerStarts()
        {
            var hub = new Hub("/ws", false);
            await using var host = new Host(hub);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (host.Server.State != WebServerState.Listening)
                await Task.Delay(20, stop.Token);
            Assert.Throws<InvalidOperationException>(() => hub.TrySetCloseOnDrain(true));
            Assert.That(hub.CloseOnDrain, Is.False);
        }
    }
}
