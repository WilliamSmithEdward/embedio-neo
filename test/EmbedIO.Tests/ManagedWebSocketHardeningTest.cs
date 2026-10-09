using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Raw-wire regressions for issue #190. Every case uses an independent RFC 6455
    // client so the server cannot pass by sharing the code under test.
    public class ManagedWebSocketHardeningTest
    {
        private sealed class Limited : WebSocketModule
        {
            internal int Calls;
            internal int Largest;
            internal Limited(int maximum) : base("/ws", false) => MaxMessageSize = maximum;
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Interlocked.Increment(ref Calls);
                InterlockedMax(ref Largest, buffer.Length);
                return context.WebSocket.SendAsync(buffer, result.MessageType == 0, context.CancellationToken);
            }
            private static void InterlockedMax(ref int target, int value)
            {
                int current;
                while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
            }
        }

        private sealed class ClosesOnConnect : WebSocketModule
        {
            internal ClosesOnConnect() : base("/ws", false) { }
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                // Do not await: the handshake completes while the client still sends.
                _ = context.WebSocket.CloseAsync(CloseStatusCode.Normal, "bye", context.CancellationToken);
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Task.CompletedTask;
        }

        internal sealed class RawClient : IDisposable
        {
            private static readonly byte[] Key = { 0x37, 0xfa, 0x21, 0x3d };
            private readonly TcpClient _tcp = new();
            internal NetworkStream Stream { get; private set; } = null!;

            internal static async Task<RawClient> ConnectAsync(string url, CancellationToken token, byte[]? pipelined = null)
            {
                var client = new RawClient();
                await client._tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, token);
                client.Stream = client._tcp.GetStream();
                var handshake = Encoding.ASCII.GetBytes($"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
                await client.Stream.WriteAsync(pipelined == null ? handshake : handshake.Concat(pipelined).ToArray(), token);
                var header = new StringBuilder(); var single = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(header.Length, Is.LessThan(8192));
                    await client.Stream.ReadExactlyAsync(single, token); header.Append((char)single[0]);
                }
                Assert.That(header.ToString(), Does.StartWith("HTTP/1.1 101"));
                return client;
            }

            internal static byte[] Frame(byte flags, byte[] payload, long? announced = null)
            {
                var length = announced ?? payload.Length;
                using var wire = new MemoryStream();
                wire.WriteByte(flags);
                if (length < 126) wire.WriteByte((byte)(0x80 | length));
                else if (length <= ushort.MaxValue) { wire.WriteByte(0xfe); wire.WriteByte((byte)(length >> 8)); wire.WriteByte((byte)length); }
                else { wire.WriteByte(0xff); for (var shift = 56; shift >= 0; shift -= 8) wire.WriteByte((byte)(length >> shift)); }
                wire.Write(Key);
                for (var i = 0; i < payload.Length; ++i) wire.WriteByte((byte)(payload[i] ^ Key[i % 4]));
                return wire.ToArray();
            }

            internal Task SendAsync(byte flags, byte[] payload, CancellationToken token)
                => Stream.WriteAsync(Frame(flags, payload), token).AsTask();

            internal async Task<(byte Flags, byte[] Payload)> ReadFrameAsync(CancellationToken token)
            {
                var header = new byte[2]; await Stream.ReadExactlyAsync(header, token);
                Assert.That(header[1] & 0x80, Is.Zero, "Server frames must not be masked.");
                long length = header[1] & 0x7f;
                if (length == 126) { var ext = new byte[2]; await Stream.ReadExactlyAsync(ext, token); length = (ext[0] << 8) | ext[1]; }
                else if (length == 127) { var ext = new byte[8]; await Stream.ReadExactlyAsync(ext, token); length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ext); }
                var payload = new byte[length]; await Stream.ReadExactlyAsync(payload, token);
                return (header[0], payload);
            }

            // Reassemble one data message, answering nothing and failing on close.
            internal async Task<(int Opcode, byte[] Payload)> ReadMessageAsync(CancellationToken token)
            {
                using var message = new MemoryStream();
                var opcode = -1;
                while (true)
                {
                    var (flags, payload) = await ReadFrameAsync(token);
                    if ((flags & 0x0f) == 0x8) Assert.Fail($"Unexpected close {CloseCode(payload)}.");
                    if ((flags & 0x0f) >= 0x8) continue;
                    if (opcode < 0) opcode = flags & 0x0f;
                    message.Write(payload);
                    if ((flags & 0x80) != 0) return (opcode, message.ToArray());
                }
            }

            internal static int CloseCode(byte[] payload) => payload.Length < 2 ? 1005 : (payload[0] << 8) | payload[1];

            public void Dispose() => _tcp.Dispose();
        }

        private static async Task WithServerAsync(WebSocketModule module, Func<string, CancellationToken, Task> body, HttpListenerMode mode = HttpListenerMode.EmbedIO)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(module)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = server.RunAsync(stop.Token);
            try
            {
                await body(url, stop.Token);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await http.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

        [TestCase(1024, false)]
        [TestCase(1024, true)]
        [TestCase(70000, false)]
        [TestCase(70000, true)]
        public async Task MaxMessageSizeDeliversMessagesAtTheLimit(int length, bool fragmented)
        {
            var module = new Limited(length);
            await WithServerAsync(module, async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                var bytes = Pattern(length);
                if (fragmented)
                {
                    await client.SendAsync(0x02, bytes.Take(length / 3).ToArray(), token);
                    await client.SendAsync(0x00, bytes.Skip(length / 3).Take(length / 3).ToArray(), token);
                    await client.SendAsync(0x80, bytes.Skip(2 * (length / 3)).ToArray(), token);
                }
                else await client.SendAsync(0x82, bytes, token);
                var (opcode, echoed) = await client.ReadMessageAsync(token);
                Assert.That(opcode, Is.EqualTo(2));
                Assert.That(echoed, Is.EqualTo(bytes));
                await client.SendAsync(0x88, new byte[] { 3, 232 }, token);
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags, Is.EqualTo(0x88));
            });
            Assert.That(module.Calls, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task MaxMessageSizeClosesOversizedMessagesWith1009BeforeCallbacks(HttpListenerMode mode, bool fragmented)
        {
            const int Maximum = 1024;
            var module = new Limited(Maximum);
            await WithServerAsync(module, async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                var bytes = Pattern(Maximum + 1);
                if (fragmented)
                {
                    await client.SendAsync(0x02, bytes.Take(600).ToArray(), token);
                    await client.SendAsync(0x80, bytes.Skip(600).ToArray(), token);
                }
                else await client.SendAsync(0x82, bytes, token);
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags, Is.EqualTo(0x88));
                Assert.That(RawClient.CloseCode(close.Payload), Is.EqualTo(1009));
            }, mode);
            Assert.That(module.Calls, Is.Zero);
        }

        [Test]
        public async Task MaxMessageSizeRejectsAnnouncedLengthBeforeWaitingForPayload()
        {
            // A peer announcing 256 MiB must not hold the connection or grow buffers
            // while the server waits for bytes it has already decided to refuse.
            var module = new Limited(4096);
            await WithServerAsync(module, async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                var header = RawClient.Frame(0x82, Array.Empty<byte>(), 256L * 1024 * 1024);
                await client.Stream.WriteAsync(header, token);
                using var prompt = CancellationTokenSource.CreateLinkedTokenSource(token);
                prompt.CancelAfter(TimeSpan.FromSeconds(5));
                var close = await client.ReadFrameAsync(prompt.Token);
                Assert.That(close.Flags, Is.EqualTo(0x88));
                Assert.That(RawClient.CloseCode(close.Payload), Is.EqualTo(1009));
            });
            Assert.That(module.Calls, Is.Zero);
        }

        [Test]
        public async Task MaxMessageSizeRejectsAccumulatedFragmentsBeforeTheFinalFrame()
        {
            var module = new Limited(4096);
            await WithServerAsync(module, async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x01, Encoding.ASCII.GetBytes(new string('a', 3000)), token);
                await client.SendAsync(0x00, Encoding.ASCII.GetBytes(new string('b', 3000)), token);
                using var prompt = CancellationTokenSource.CreateLinkedTokenSource(token);
                prompt.CancelAfter(TimeSpan.FromSeconds(5));
                var close = await client.ReadFrameAsync(prompt.Token);
                Assert.That(RawClient.CloseCode(close.Payload), Is.EqualTo(1009));
            });
            Assert.That(module.Calls, Is.Zero);
        }

        // Reproduces loss of frames that arrive in the same read as the upgrade
        // request. The bytes sit in HttpConnection's input buffer, which the socket
        // never sees; the correction belongs to the HTTP/1 transport shared with PR
        // #182 and is held for coordination there (#190).
        [Test]
        [Explicit("Requires HttpConnection to hand buffered upgrade bytes to the WebSocket; pending coordination with PR #182 (#190).")]
        public async Task FramesPipelinedWithTheUpgradeRequestAreNotLost()
        {
            var module = new Limited(0);
            await WithServerAsync(module, async (url, token) =>
            {
                var pipelined = RawClient.Frame(0x81, Encoding.ASCII.GetBytes("early"));
                using var client = await RawClient.ConnectAsync(url, token, pipelined);
                using var prompt = CancellationTokenSource.CreateLinkedTokenSource(token);
                prompt.CancelAfter(TimeSpan.FromSeconds(5));
                var (opcode, echoed) = await client.ReadMessageAsync(prompt.Token);
                Assert.That(opcode, Is.EqualTo(1));
                Assert.That(Encoding.ASCII.GetString(echoed), Is.EqualTo("early"));
            });
        }

        // The peer has more bytes in flight when the server rejects a message. The
        // server must still deliver its status and close without a reset.
        [TestCase(false)]
        [TestCase(true)]
        public async Task RejectedMessageClosesWithStatusWhilePeerBytesAreInFlight(bool oversized)
        {
            var module = new Limited(oversized ? 4096 : 0);
            await WithServerAsync(module, async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                var rejected = oversized
                    ? RawClient.Frame(0x82, Pattern(65536))
                    : RawClient.Frame(0x81, new byte[] { 0x41, 0xc0, 0x80 });
                var trailing = Enumerable.Range(0, 8).SelectMany(_ => RawClient.Frame(0x82, Pattern(1000)));
                await client.Stream.WriteAsync(rejected.Concat(trailing).ToArray(), token);
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags, Is.EqualTo(0x88));
                Assert.That(RawClient.CloseCode(close.Payload), Is.EqualTo(oversized ? 1009 : 1007));
                await client.SendAsync(0x88, close.Payload.Take(2).ToArray(), token);
                var buffer = new byte[1];
                Assert.That(await client.Stream.ReadAsync(buffer, token), Is.Zero, "The server must close after the acknowledgement without a reset.");
            });
            Assert.That(module.Calls, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(125)]
        [TestCase(126)]
        [TestCase(65535)]
        [TestCase(65536)]
        [TestCase(65537)]
        [TestCase(2 * 65536 + 1)]
        public async Task OutgoingMessagesUseOneFramePerSixtyFourKibibytes(int length)
        {
            await WithServerAsync(new Limited(0), async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                var bytes = Pattern(length);
                await client.Stream.WriteAsync(RawClient.Frame(0x82, bytes), token);
                using var echoed = new MemoryStream();
                var frames = 0;
                while (true)
                {
                    var (flags, payload) = await client.ReadFrameAsync(token);
                    Assert.That(flags & 0x70, Is.Zero, "No unnegotiated extension bits.");
                    Assert.That(flags & 0x0f, Is.EqualTo(frames++ == 0 ? 2 : 0));
                    Assert.That(payload.Length, Is.LessThanOrEqualTo(65536));
                    echoed.Write(payload);
                    if ((flags & 0x80) != 0) break;
                    Assert.That(payload.Length, Is.EqualTo(65536), "Only the final frame may be short.");
                }
                Assert.That(frames, Is.EqualTo(Math.Max(1, (length + 65535) / 65536)));
                Assert.That(echoed.ToArray(), Is.EqualTo(bytes));
            });
        }

        [Test]
        public void UnmaskingMatchesTheBytewiseDefinitionForEveryLengthAndKey()
        {
            var mask = (typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.PayloadData", true)
                ?.GetMethod("Mask", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing static mask method."))
                .CreateDelegate<Action<byte[], byte[]>>();
            var random = new Random(190);
            for (var length = 0; length <= 67; ++length)
            {
                for (var trial = 0; trial < 8; ++trial)
                {
                    var data = new byte[length]; random.NextBytes(data);
                    var key = new byte[4]; random.NextBytes(key);
                    var expected = data.Select((b, i) => (byte)(b ^ key[i % 4])).ToArray();
                    mask(data, key);
                    Assert.That(data, Is.EqualTo(expected), $"length {length}");
                }
            }
        }

        [Test]
        public async Task FragmentedMessageStartedAfterServerCloseStillReachesTheCloseAcknowledgement()
        {
            await WithServerAsync(new ClosesOnConnect(), async (url, token) =>
            {
                using var client = await RawClient.ConnectAsync(url, token);
                // Read the server close first so these frames are in flight while the
                // server waits for the acknowledgement in CloseSent.
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags, Is.EqualTo(0x88));
                var burst = RawClient.Frame(0x01, Encoding.ASCII.GetBytes("par"))
                    .Concat(RawClient.Frame(0x80, Encoding.ASCII.GetBytes("tial")))
                    .Concat(RawClient.Frame(0x88, new byte[] { 3, 232 })).ToArray();
                await client.Stream.WriteAsync(burst, token);
                // A server that read through to the acknowledgement closes cleanly with
                // EOF. One that stopped early leaves unread bytes and resets.
                var buffer = new byte[1];
                Assert.That(await client.Stream.ReadAsync(buffer, token), Is.Zero);
            });
        }
    }
}
