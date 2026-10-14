using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Closes that must end in time (failure, disposal, finalization), the 1001 close a server stop
    // sends, keep-alive pings, close argument checks, and sends that a close cuts short.
    [TestFixture]
    [NonParallelizable]
    public sealed class WebSocketCloseAndKeepAliveTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private static readonly Type SocketType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)
            ?? throw new AssertionException("Missing the managed WebSocket type.");

        // Payload bytes per outgoing data frame.
        private static readonly int Fragment = (int)(SocketType.GetField("SendFragmentLength", PrivateStatic)?.GetValue(null)
            ?? throw new AssertionException("Missing the send fragment length."));

        // A socket gives up a close after one second; this leaves room for a slow machine.
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);

        [Test]
        public async Task DisposeEndsWithinTheCloseBoundWhenTheTransportStalls()
        {
            using var transport = new Transport { StallWrites = true };
            var socket = CreateSocket(transport);
            try
            {
                await Task.Run(socket.Dispose).WaitAsync(Bound);
                Assert.That(transport.Headers, Is.EqualTo(new byte[] { 0x88 }), "The 1001 close was attempted.");
                Assert.That(transport.CloseCount, Is.EqualTo(1), "The connection was released.");
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            }
            finally { transport.Release(); }
        }

        [Test]
        public async Task TheFinalizerPathReleasesTheConnectionWithoutWritingOrWaiting()
        {
            using var transport = new Transport { StallWrites = true };
            var socket = CreateSocket(transport);
            var dispose = SocketType.GetMethod("Dispose", PrivateInstance, null, new[] { typeof(bool) }, null)
                ?? throw new AssertionException("Missing Dispose(bool).");
            try
            {
                await Task.Run(() => dispose.Invoke(socket, new object[] { false })).WaitAsync(Bound);
                Assert.That(transport.Headers, Is.Empty, "A finalizer must not write to the network.");
                Assert.That(transport.CloseCount, Is.EqualTo(1));
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            }
            finally
            {
                transport.Release();
                GC.SuppressFinalize(socket);
            }
        }

        [Test]
        public async Task ARejectedMessageReleasesTheConnectionWhenItsCloseCannotBeWritten()
        {
            using var transport = new Transport { StallWrites = true };
            var limit = SocketType.GetMethod("SetAcceptedMaxMessageSize", PrivateStatic)
                ?? throw new AssertionException("Missing SetAcceptedMaxMessageSize.");
            IWebSocket socket;
            limit.Invoke(null, new object[] { 16 });
            try { socket = OpenSocket(transport); }
            finally { limit.Invoke(null, new object[] { 0 }); }

            try
            {
                transport.Feed(ClientFrame(0x81, new byte[32]));
                await transport.Closed.WaitAsync(Bound);
                Assert.That(transport.FirstCloseStatus, Is.EqualTo(1009), "The 1009 close was attempted.");
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            }
            finally
            {
                transport.Release();
                socket.Dispose();
            }
        }

        [Test]
        public async Task AProtocolErrorReleasesTheConnectionWhenItsCloseCannotBeWritten()
        {
            using var transport = new Transport { StallWrites = true };
            var socket = OpenSocket(transport);
            try
            {
                // Opcode 3 is reserved, so the frame is a protocol error.
                transport.Feed(ClientFrame(0x83, Array.Empty<byte>()));
                await transport.Closed.WaitAsync(Bound);
                Assert.That(transport.FirstCloseStatus, Is.EqualTo(1002), "The 1002 close was attempted.");
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            }
            finally
            {
                transport.Release();
                socket.Dispose();
            }
        }

        [Test]
        public async Task DisposingTheServerSendsGoingAwayBeforeTheConnectionCloses()
        {
            var hub = new Hub(Timeout.InfiniteTimeSpan);
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var disposing = Task.Run(host.Server.Dispose);
            await ExpectGoingAwayAsync(client, limit.Token);
            await disposing.WaitAsync(limit.Token);
            Assert.That(await client.ReadFrameAsync(limit.Token), Is.Null, "The connection ends after the closing handshake.");
        }

        // The module has not finished OnClientConnectedAsync, so only the server's stop can close this socket.
        [Test]
        public async Task CancelingRunAsyncSendsGoingAwayWhileTheModuleIsStillConnecting()
        {
            var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hub = new Hub(Timeout.InfiniteTimeSpan, connecting.Task);
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
                _ = await hub.Connected.Task.WaitAsync(limit.Token);

                var stopping = Task.Run(host.Stop.Cancel);
                await ExpectGoingAwayAsync(client, limit.Token);
                await stopping.WaitAsync(limit.Token);
                await host.Running.WaitAsync(limit.Token);
            }
            finally { connecting.TrySetResult(); }
        }

        // The idle socket's handler sees the canceled token and closes it while the server's stop closes
        // it too; either way the client gets one 1001 close before the connection ends.
        [Test]
        [Repeat(5)]
        public async Task CancelingRunAsyncSendsGoingAwayToAnIdleWebSocket()
        {
            var hub = new Hub(Timeout.InfiniteTimeSpan);
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var stopping = Task.Run(host.Stop.Cancel);
            await ExpectGoingAwayAsync(client, limit.Token);
            await stopping.WaitAsync(limit.Token);
            await host.Running.WaitAsync(limit.Token);
            await hub.Disconnected.Task.WaitAsync(limit.Token);
        }

        [Test]
        public async Task AClientThatNeverAnswersTheCloseDelaysTheStopByAtMostItsBudget()
        {
            var hub = new Hub(Timeout.InfiniteTimeSpan);
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var clock = Stopwatch.StartNew();
            await Task.Run(host.Stop.Cancel).WaitAsync(limit.Token);
            await host.Running.WaitAsync(limit.Token);
            clock.Stop();
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "The one-second budget bounds the stop.");

            // The close waited in the client's receive buffer.
            var close = await client.ReadFrameAsync(limit.Token) ?? throw new AssertionException("The connection ended without a close frame.");
            Assert.That(close.Header, Is.EqualTo(0x88));
            Assert.That(close.Payload, Is.EqualTo(new byte[] { 0x03, 0xE9 }));
        }

        [Test]
        public async Task CancelingRunAsyncSendsGoingAwayOverHttp2()
        {
            var hub = new Hub(Timeout.InfiniteTimeSpan);
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var invoker = new HttpMessageInvoker(handler, false);
            using var socket = await ConnectHttp2Async(host, invoker, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var receiving = socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), limit.Token);
            var stopping = Task.Run(host.Stop.Cancel);
            var result = await receiving;
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", limit.Token);
            await stopping.WaitAsync(limit.Token);
        }

        [Test]
        public async Task KeepAlivePingsTheClientAtItsInterval()
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(200));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);

            var ping = await client.ReadFrameAsync(limit.Token).WaitAsync(TimeSpan.FromSeconds(3))
                ?? throw new AssertionException("The connection ended without a ping.");
            Assert.That(ping.Header, Is.EqualTo(0x89));
            Assert.That(ping.Payload, Is.Empty);
        }

        [Test]
        public async Task KeepAliveClosesAClientThatDoesNotAnswer()
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(200));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pings = 0;
            while (await client.ReadFrameAsync(window.Token) is { } frame)
            {
                Assert.That(frame.Header, Is.EqualTo(0x89), "Only unanswered pings arrive before the connection ends.");
                pings++;
            }

            Assert.That(pings, Is.GreaterThanOrEqualTo(1));
            await hub.Disconnected.Task.WaitAsync(limit.Token);
        }

        [Test]
        public async Task KeepAliveKeepsAClientThatAnswersConnected()
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(400));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var clock = Stopwatch.StartNew();
            var pings = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(1.6))
            {
                var ping = await client.ReadFrameAsync(limit.Token) ?? throw new AssertionException("The server closed a client that answered.");
                Assert.That(ping.Header, Is.EqualTo(0x89));
                pings++;
                await client.SendAsync(0x0A, ping.Payload, limit.Token);
            }

            Assert.That(pings, Is.GreaterThanOrEqualTo(2));
            await client.SendAsync(0x01, Encoding.UTF8.GetBytes("still here"), limit.Token);
            Assert.That(await ReadTextSkippingPingsAsync(client, limit.Token), Is.EqualTo("still here"));
            Assert.That(hub.Disconnected.Task.IsCompleted, Is.False);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public async Task KeepAliveIsOffForZeroOrAnInfiniteInterval(int milliseconds)
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(milliseconds));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await RawClient.ConnectAsync(host.Port, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var reading = client.ReadFrameAsync(limit.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(600), limit.Token);
            Assert.That(reading.IsCompleted, Is.False, "No ping arrived.");
            await client.SendAsync(0x01, Encoding.UTF8.GetBytes("hello"), limit.Token);
            var echo = await reading ?? throw new AssertionException("The connection ended.");
            Assert.That(echo.Header, Is.EqualTo(0x81));
            Assert.That(Encoding.UTF8.GetString(echo.Payload), Is.EqualTo("hello"));
        }

        // ClientWebSocket answers pings while a receive is pending.
        [Test]
        public async Task KeepAliveKeepsAReceivingClientWebSocketConnected()
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(400));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            socket.Options.Proxy = null;
            await socket.ConnectAsync(host.WebSocketUri, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            var buffer = new byte[64];
            var receiving = socket.ReceiveAsync(new ArraySegment<byte>(buffer), limit.Token);
            await Task.Delay(TimeSpan.FromSeconds(1.6), limit.Token);
            Assert.That(receiving.IsCompleted, Is.False);
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));

            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("answered")), WebSocketMessageType.Text, true, limit.Token);
            var echo = await receiving;
            Assert.That(Encoding.UTF8.GetString(buffer, 0, echo.Count), Is.EqualTo("answered"));
            Assert.That(hub.Disconnected.Task.IsCompleted, Is.False);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", limit.Token);
        }

        // Over HTTP/2 the pings travel on the extended CONNECT stream; a client that never reads cannot answer.
        [Test]
        public async Task KeepAliveClosesAnHttp2ClientThatDoesNotAnswer()
        {
            var hub = new Hub(TimeSpan.FromMilliseconds(200));
            using var host = new Host(hub);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var invoker = new HttpMessageInvoker(handler, false);
            using var socket = await ConnectHttp2Async(host, invoker, limit.Token);
            _ = await hub.Connected.Task.WaitAsync(limit.Token);

            await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), limit.Token);
        }

        [TestCase(0)]
        [TestCase(999)]
        [TestCase(1006)]
        [TestCase(1010)]
        [TestCase(1015)]
        [TestCase(1016)]
        [TestCase(2000)]
        [TestCase(2999)]
        [TestCase(5000)]
        [TestCase(65535)]
        public async Task CloseRejectsAStatusAServerCannotSend(int code)
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await Assert.ThatAsync(async () => await socket.CloseAsync((CloseStatusCode)code), Throws.ArgumentException);
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(transport.Headers, Is.Empty);
            socket.Dispose();
        }

        [Test]
        public async Task CloseRejectsACommentWithoutAStatus()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await Assert.ThatAsync(async () => await socket.CloseAsync(CloseStatusCode.NoStatus, "comment"), Throws.ArgumentException);
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(transport.Headers, Is.Empty);
            socket.Dispose();
        }

        // 62 two-byte characters make 124 UTF-8 bytes, one more than a close frame can carry with its status.
        [Test]
        public async Task CloseRejectsACommentLongerThan123Bytes()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await Assert.ThatAsync(async () => await socket.CloseAsync(CloseStatusCode.Normal, new string('\u00e9', 62)), Throws.ArgumentException);
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(transport.Headers, Is.Empty);
            socket.Dispose();
        }

        [Test]
        public async Task CloseSendsACommentOf123Bytes()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await socket.CloseAsync(CloseStatusCode.Normal, new string('a', 123));
            var frame = transport.Frames.Single();
            Assert.That(frame[0], Is.EqualTo(0x88));
            Assert.That(frame[1], Is.EqualTo(125));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
        }

        [TestCase(1000)]
        [TestCase(1001)]
        [TestCase(1011)]
        [TestCase(1012)]
        [TestCase(1014)]
        [TestCase(3000)]
        [TestCase(4999)]
        public async Task CloseSendsAStatusAServerCanSend(int code)
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await socket.CloseAsync((CloseStatusCode)code, "bye");
            Assert.That(transport.FirstCloseStatus, Is.EqualTo(code));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            Assert.That(transport.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CloseWithoutAStatusSendsAnEmptyCloseFrame()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await socket.CloseAsync(CloseStatusCode.NoStatus);
            Assert.That(transport.Frames.Single(), Is.EqualTo(new byte[] { 0x88, 0x00 }));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
        }

        [Test]
        public async Task CloseWithUndefinedClosesWithoutAFrame()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await socket.CloseAsync(CloseStatusCode.Undefined);
            Assert.That(transport.Headers, Is.Empty);
            Assert.That(transport.CloseCount, Is.EqualTo(1));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
        }

        [Test]
        public async Task CloseChecksItsArgumentsAfterTheSocketClosed()
        {
            using var transport = new Transport();
            var socket = CreateSocket(transport);
            await socket.CloseAsync(CloseStatusCode.Normal);
            await Assert.ThatAsync(async () => await socket.CloseAsync((CloseStatusCode)5000), Throws.ArgumentException);
            await socket.CloseAsync(CloseStatusCode.Normal);
            Assert.That(transport.Headers, Is.EqualTo(new byte[] { 0x88 }), "A second close on a closed socket writes nothing.");
        }

        [Test]
        public async Task APeerCloseBetweenFragmentsFailsTheSend()
        {
            using var transport = new Transport { HoldFirstWrite = true };
            var socket = OpenSocket(transport);
            try
            {
                var sending = socket.SendAsync(new byte[(2 * Fragment) + 1], false);
                await transport.FirstWriteStarted.WaitAsync(Bound);
                transport.Feed(ClientFrame(0x88, new byte[] { 0x03, 0xE8 }));
                using (var wait = new CancellationTokenSource(Bound))
                {
                    while (socket.State == WebSocketState.Open)
                        await Task.Delay(1, wait.Token);
                }

                transport.Release();
                await Assert.ThatAsync(async () => await sending.WaitAsync(Bound), Throws.TypeOf<EmbedIO.WebSockets.WebSocketException>());
                Assert.That(transport.Headers, Is.EqualTo(new byte[] { 0x02, 0x88 }), "The echoed close follows the first frame; no continuation frame is written.");
            }
            finally
            {
                transport.Release();
                socket.Dispose();
            }
        }

        private static IWebSocket CreateSocket(Transport transport)
            => (IWebSocket)(Activator.CreateInstance(SocketType, PrivateInstance, null, new object[] { transport, (Action)transport.CloseConnection }, null)
                ?? throw new AssertionException("Could not create the socket."));

        // A socket whose receive loop runs, as after an accepted upgrade.
        private static IWebSocket OpenSocket(Transport transport)
            => (IWebSocket)((SocketType.GetMethod("FromStream", PrivateStatic) ?? throw new AssertionException("Missing FromStream."))
                .Invoke(null, new object[] { transport, (Action)transport.CloseConnection })
                ?? throw new AssertionException("Could not open the socket."));

        // A masked client frame with a payload shorter than 126 bytes.
        private static byte[] ClientFrame(byte first, byte[] payload)
        {
            var frame = new byte[6 + payload.Length];
            frame[0] = first;
            frame[1] = (byte)(0x80 | payload.Length);
            for (var i = 0; i < 4; i++)
                frame[2 + i] = (byte)(i + 1);
            for (var i = 0; i < payload.Length; i++)
                frame[6 + i] = (byte)(payload[i] ^ frame[2 + (i % 4)]);
            return frame;
        }

        private static async Task ExpectGoingAwayAsync(RawClient client, CancellationToken token)
        {
            var close = await client.ReadFrameAsync(token) ?? throw new AssertionException("The connection ended without a close frame.");
            Assert.That(close.Header, Is.EqualTo(0x88));
            Assert.That(close.Payload, Is.EqualTo(new byte[] { 0x03, 0xE9 }), "1001, without a reason.");
            try { await client.SendAsync(0x08, new byte[] { 0x03, 0xE9 }, token); }
            catch (Exception error) when (error is IOException or SocketException)
            {
                // When the request handler closed the socket first, the stop waits only for the close
                // frame, so the connection may already be gone when the client answers.
            }
        }

        private static async Task<string> ReadTextSkippingPingsAsync(RawClient client, CancellationToken token)
        {
            while (true)
            {
                var frame = await client.ReadFrameAsync(token) ?? throw new AssertionException("The connection ended.");
                if (frame.Header == 0x89)
                {
                    await client.SendAsync(0x0A, frame.Payload, token);
                    continue;
                }

                Assert.That(frame.Header, Is.EqualTo(0x81));
                return Encoding.UTF8.GetString(frame.Payload);
            }
        }

        private static async Task<ClientWebSocket> ConnectHttp2Async(Host host, HttpMessageInvoker invoker, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            try
            {
                await socket.ConnectAsync(host.WebSocketUri, invoker, token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private sealed class Hub : WebSocketModule
        {
            private readonly Task _connecting;

            internal Hub(TimeSpan keepAliveInterval, Task? connecting = null)
                : base("/ws", false)
            {
                KeepAliveInterval = keepAliveInterval;
                _connecting = connecting ?? Task.CompletedTask;
            }

            internal TaskCompletionSource<IWebSocketContext> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(context);
                await _connecting.ConfigureAwait(false);
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => context.WebSocket.SendAsync(buffer, true, CancellationToken.None);

            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Disconnected.TrySetResult();
                return Task.CompletedTask;
            }
        }

        private sealed class Host : IDisposable
        {
            internal Host(WebSocketModule module)
            {
                Url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
                Server = new WebServer(o => o.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module);
                Running = Server.RunAsync(Stop.Token);
            }

            internal string Url { get; }

            internal int Port => new Uri(Url).Port;

            internal Uri WebSocketUri => new(Url.Replace("http:", "ws:", StringComparison.Ordinal) + "ws");

            internal WebServer Server { get; }

            internal CancellationTokenSource Stop { get; } = new();

            internal Task Running { get; }

            public void Dispose()
            {
                Stop.Cancel();
                try { _ = Running.Wait(TimeSpan.FromSeconds(15)); }
                finally
                {
                    Server.Dispose();
                    Stop.Dispose();
                }
            }
        }

        private sealed class Frame
        {
            internal Frame(byte header, byte[] payload)
            {
                Header = header;
                Payload = payload;
            }

            internal byte Header { get; }

            internal byte[] Payload { get; }
        }

        // A WebSocket client over a raw TCP connection, to see exactly which frames the server sends.
        private sealed class RawClient : IDisposable
        {
            private readonly TcpClient _tcp = new();

            private NetworkStream Stream => _tcp.GetStream();

            internal static async Task<RawClient> ConnectAsync(int port, CancellationToken token)
            {
                var client = new RawClient();
                try
                {
                    await client._tcp.ConnectAsync(IPAddress.Loopback, port, token);
                    var request = $"GET /ws HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
                    await client.Stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
                    using var head = new MemoryStream();
                    var one = new byte[1];
                    while (head.Length < 8192 && !Encoding.ASCII.GetString(head.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        await client.Stream.ReadExactlyAsync(one, token);
                        head.WriteByte(one[0]);
                    }

                    Assert.That(Encoding.ASCII.GetString(head.ToArray()), Does.StartWith("HTTP/1.1 101"));
                    return client;
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            // Reads one server frame, or returns null when the connection ends first.
            internal async Task<Frame?> ReadFrameAsync(CancellationToken token)
            {
                try
                {
                    var start = new byte[2];
                    if (await Stream.ReadAtLeastAsync(start, 2, false, token) < 2)
                        return null;
                    Assert.That(start[1] & 0x80, Is.Zero, "Server frames are not masked.");
                    long length = start[1] & 0x7F;
                    if (length >= 126)
                    {
                        var extended = new byte[length == 126 ? 2 : 8];
                        await Stream.ReadExactlyAsync(extended, token);
                        length = 0;
                        foreach (var b in extended)
                            length = (length << 8) | b;
                    }

                    var payload = new byte[length];
                    await Stream.ReadExactlyAsync(payload, token);
                    return new Frame(start[0], payload);
                }
                catch (Exception error) when (error is IOException or SocketException)
                {
                    return null;
                }
            }

            internal async Task SendAsync(byte opcode, byte[] payload, CancellationToken token)
                => await Stream.WriteAsync(ClientFrame((byte)(0x80 | opcode), payload), token);

            public void Dispose() => _tcp.Dispose();
        }

        // An in-memory transport for a socket built without a server: the test supplies what the socket
        // reads and sees each frame it writes. Writes can stall, as on a connection whose peer stopped
        // reading, until the test releases them or the socket releases the connection.
        private sealed class Transport : Stream
        {
            private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[] _pending = Array.Empty<byte>();
            private int _pendingOffset;
            private int _writes;
            private int _closeCount;

            internal bool StallWrites { get; init; }

            internal bool HoldFirstWrite { get; init; }

            internal ConcurrentQueue<byte[]> Frames { get; } = new();

            internal byte[] Headers => Frames.Select(frame => frame[0]).ToArray();

            internal int FirstCloseStatus
            {
                get
                {
                    var close = Frames.First(frame => frame[0] == 0x88);
                    return (close[2] << 8) | close[3];
                }
            }

            internal int CloseCount => Volatile.Read(ref _closeCount);

            internal Task Closed => _closed.Task;

            internal Task FirstWriteStarted => _firstWrite.Task;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            internal void Feed(byte[] bytes) => _input.Writer.TryWrite(bytes);

            internal void Release() => _released.TrySetResult();

            // The socket's connection-close callback: reads end and stalled writes fail, as on a closed connection.
            internal void CloseConnection()
            {
                Interlocked.Increment(ref _closeCount);
                _input.Writer.TryComplete();
                _closed.TrySetResult();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous transport I/O.");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous transport I/O.");

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                while (_pendingOffset == _pending.Length)
                {
                    if (!await _input.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        return 0;
                    if (_input.Reader.TryRead(out var next))
                    {
                        _pending = next;
                        _pendingOffset = 0;
                    }
                }

                var length = Math.Min(count, _pending.Length - _pendingOffset);
                Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, length);
                _pendingOffset += length;
                return length;
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_closed.Task.IsCompleted)
                    throw new IOException("The connection is closed.");
                Frames.Enqueue(buffer.AsSpan(offset, count).ToArray());
                var first = Interlocked.Increment(ref _writes) == 1;
                if (first)
                    _firstWrite.TrySetResult();
                if (StallWrites || (first && HoldFirstWrite))
                {
                    _ = await Task.WhenAny(_released.Task, _closed.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!_released.Task.IsCompleted)
                        throw new IOException("The connection closed during the write.");
                }
            }

            protected override void Dispose(bool disposing)
            {
                _input.Writer.TryComplete();
                _released.TrySetResult();
                base.Dispose(disposing);
            }
        }
    }
}
