using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private sealed class Http3Echo : WebSocketModule
        {
            internal readonly TaskCompletionSource Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int DisconnectCount;
            internal Http3Echo() : base("/ws", false) { AddProtocol("echo"); }
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Assert.That(context.AcceptedProtocol, Is.EqualTo("echo"));
                Assert.That(context.Cookies["test"]?.Value, Is.EqualTo("value"));
                Assert.That(context.IsSecureConnection, Is.True);
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => context.WebSocket.SendAsync(buffer, result.MessageType == (int)WebSocketMessageType.Text, context.CancellationToken);
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            { Interlocked.Increment(ref DisconnectCount); Disconnected.TrySetResult(); return Task.CompletedTask; }
        }
        [TestCase(262144, true, false, false)]
        [TestCase(262144, true, true, false)]
        [TestCase(0, true, false, false)]
        [TestCase(127, true, false, false)]
        [TestCase(262144, false, false, false)]
        [TestCase(127, true, true, false)]
        [TestCase(262144, false, true, false)]
        [TestCase(127, true, false, true)]
        public async Task Http3WebSocketEchoAndClosurePreserveSiblingRequests(int length, bool text, bool fragmented, bool abort)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await WebSocketWire(length, text, fragmented, abort);
        }
        [TestCase("other", "13", "501")]
        [TestCase("WebSocket", "13", "501")]
        [TestCase("websocket", "12", "400")]
        [TestCase("websocket", "", "400")]
        public async Task Http3RejectedWebSocketNegotiationPreservesSiblingRequests(string protocol, string version, string status)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await WebSocketWire(0, true, false, false, protocol, version, status);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task WebSocketWire(int length, bool text, bool fragmented, bool abort, string protocol = "websocket", string version = "13", string status = "200")
        {
            using var certificate = Certificate();
            var prefix = Prefix(); var uri = new Uri(prefix);
            var echo = new Http3Echo();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(echo)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() }
            }, stop.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                await ReadConnectSetting(connection, peerStreams, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                await SendFieldSection(request, new[]
                {
                    (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/ws"), (":protocol", protocol),
                    ("sec-websocket-version", version), ("sec-websocket-protocol", "echo"), ("cookie", "test=value")
                }, false, stop.Token);
                var headers = await ReadResponseFields(request, stop.Token);
                Assert.That(headers[":status"], Is.EqualTo(status));
                if (status != "200")
                {
                    if (status == "400") Assert.That(headers["sec-websocket-version"], Is.EqualTo("13"));
                    request.CompleteWrites();
                    await HealthySibling(connection, uri.Authority, stop.Token);
                    Assert.That(echo.DisconnectCount, Is.Zero);
                    return;
                }
                Assert.That(headers["sec-websocket-protocol"], Is.EqualTo("echo"));
                Assert.That(headers["sec-websocket-accept"], Is.Null);
                Assert.That(headers["content-length"], Is.Null);
                using var tunnel = new ClientDataStream(request);
                using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(tunnel,
                    new WebSocketCreationOptions { IsServer = false, SubProtocol = "echo", KeepAliveInterval = Timeout.InfiniteTimeSpan });
                var bytes = text ? Encoding.UTF8.GetBytes(new string('é', length)) : Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
                var kind = text ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                if (fragmented)
                {
                    await socket.SendAsync(bytes.AsMemory(0, bytes.Length / 2), kind, false, stop.Token);
                    await socket.SendAsync(bytes.AsMemory(bytes.Length / 2), kind, true, stop.Token);
                }
                else await socket.SendAsync(bytes, kind, true, stop.Token);
                using var received = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    Assert.That(result.MessageType, Is.EqualTo(kind));
                    received.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                Assert.That(received.ToArray(), Is.EqualTo(bytes));
                await HealthySibling(connection, uri.Authority, stop.Token);
                if (abort)
                {
                    socket.Abort(); request.Abort(QuicAbortDirection.Both, 0x10c);
                }
                else
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token);
                    request.CompleteWrites();
                    Assert.That(await request.ReadAsync(new byte[1], stop.Token), Is.EqualTo(0), "A normal WebSocket close must finish the QUIC stream, not reset it.");
                }
                await echo.Disconnected.Task.WaitAsync(stop.Token);
                Assert.That(echo.DisconnectCount, Is.EqualTo(1));
                await HealthySibling(connection, uri.Authority, stop.Token);
            }
            finally
            {
                stop.Cancel();
                foreach (var peer in peerStreams) await peer.DisposeAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ReadConnectSetting(QuicConnection connection, List<QuicStream> peers, CancellationToken token)
        {
            while (true)
            {
                var stream = await connection.AcceptInboundStreamAsync(token); peers.Add(stream);
                if (await ReadInteger(stream, token) != 0) continue;
                Assert.That(await ReadInteger(stream, token), Is.EqualTo(4));
                var size = await ReadInteger(stream, token);
                Assert.That(size, Is.InRange(0, 65536));
                var payload = new byte[(int)size]; await stream.ReadExactlyAsync(payload, token);
                using var settings = new MemoryStream(payload);
                var enabled = false;
                while (settings.Position < settings.Length)
                {
                    var id = await ReadInteger(settings, token); var value = await ReadInteger(settings, token);
                    if (id == 8) enabled = value == 1;
                }
                Assert.That(enabled, Is.True, "The server must advertise RFC 9220 before extended CONNECT.");
                return;
            }
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task HealthySibling(QuicConnection connection, string authority, CancellationToken token)
        {
            await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
            await SendFieldSection(request, new[] { (":method", "GET"), (":scheme", "https"), (":authority", authority), (":path", "/healthy") }, true, token);
            Assert.That((await ReadResponseFields(request, token))[":status"], Is.EqualTo("200"));
            using var body = new ClientDataStream(request);
            using var received = new MemoryStream(); await body.CopyToAsync(received, token);
            Assert.That(Encoding.UTF8.GetString(received.ToArray()), Is.EqualTo("healthy"));
        }
        private static void WritePrefix(Stream output, int value, int bits, int flags)
        {
            var maximum = (1 << bits) - 1;
            output.WriteByte((byte)(flags | Math.Min(value, maximum)));
            if (value < maximum) return;
            value -= maximum;
            while (value >= 128) { output.WriteByte((byte)(128 | (value & 127))); value >>= 7; }
            output.WriteByte((byte)value);
        }
        // Independent literal-only field encoder: no production QPACK encoder is used.
        private static byte[] LiteralFields((string Name, string Value)[] fields)
        {
            using var output = new MemoryStream(); output.WriteByte(0); output.WriteByte(0);
            foreach (var (name, value) in fields)
            {
                var nameBytes = Encoding.ASCII.GetBytes(name); var valueBytes = Encoding.ASCII.GetBytes(value);
                WritePrefix(output, nameBytes.Length, 3, 0x20); output.Write(nameBytes);
                WritePrefix(output, valueBytes.Length, 7, 0); output.Write(valueBytes);
            }
            return output.ToArray();
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task SendFieldSection(QuicStream stream, (string Name, string Value)[] fields, bool fin, CancellationToken token)
        {
            var encoded = LiteralFields(fields);
            using var frame = new MemoryStream(); WriteInteger(frame, 1); WriteInteger(frame, encoded.Length); frame.Write(encoded);
            await stream.WriteAsync(frame.ToArray(), fin, token);
        }
        private static async Task<NameValueCollection> ReadResponseFields(Stream stream, CancellationToken token)
        {
            Assert.That(await ReadInteger(stream, token), Is.EqualTo(1));
            var length = await ReadInteger(stream, token); Assert.That(length, Is.InRange(0, 65536));
            var payload = new byte[(int)length]; await stream.ReadExactlyAsync(payload, token);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QpackDecoder", true) ?? throw new AssertionException("Missing decoder.");
            using var decoder = (IDisposable)(Activator.CreateInstance(type, flags, null, new object[] { 0, 0, 65536, 65536, 0L, 65536 }, null) ?? throw new AssertionException("Missing decoder constructor."));
            var fields = (Array)(type.GetMethod("Submit", flags)?.Invoke(decoder, new object[] { 0L, payload }) ?? throw new AssertionException("Unexpected dynamic response reference."));
            var values = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields)
            {
                var fieldType = field?.GetType() ?? throw new AssertionException("Missing field.");
                values.Add((string)(fieldType.GetProperty("Name")?.GetValue(field) ?? throw new AssertionException("Missing name.")),
                    (string)(fieldType.GetProperty("Value")?.GetValue(field) ?? throw new AssertionException("Missing value.")));
            }
            return values;
        }
        private static void WriteInteger(Stream output, long value)
        {
            var length = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
            var bytes = new byte[length];
            for (var i = length - 1; i >= 0; --i) { bytes[i] = (byte)value; value >>= 8; }
            bytes[0] |= (byte)(length == 1 ? 0 : length == 2 ? 64 : length == 4 ? 128 : 192);
            output.Write(bytes);
        }
        private static async Task<long> ReadInteger(Stream input, CancellationToken token, bool allowEnd = false)
        {
            var bytes = new byte[8];
            if (await input.ReadAsync(bytes.AsMemory(0, 1), token) == 0)
            { if (allowEnd) return -1; throw new EndOfStreamException(); }
            var length = 1 << (bytes[0] >> 6);
            if (length > 1) await input.ReadExactlyAsync(bytes.AsMemory(1, length - 1), token);
            long value = bytes[0] & 63;
            for (var i = 1; i < length; ++i) value = (value << 8) | bytes[i];
            return value;
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private sealed class ClientDataStream : Stream
        {
            // The fixture owns the native stream. Disposing the WebSocket facade
            // must not hide a peer reset from the subsequent raw FIN assertion.
            private readonly QuicStream _stream;
            private long _remaining;
            internal ClientDataStream(QuicStream stream) { _stream = stream; }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (buffer.Length == 0) return 0;
                while (_remaining == 0)
                {
                    var type = await ReadInteger(_stream, token, true);
                    if (type == -1) return 0;
                    Assert.That(type, Is.EqualTo(0), "Only DATA follows successful CONNECT.");
                    _remaining = await ReadInteger(_stream, token);
                }
                var read = await _stream.ReadAsync(buffer.Slice(0, (int)Math.Min(buffer.Length, _remaining)), token);
                if (read == 0) throw new EndOfStreamException();
                _remaining -= read; return read;
            }
            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                using var header = new MemoryStream(); WriteInteger(header, 0); WriteInteger(header, buffer.Length);
                await _stream.WriteAsync(header.ToArray(), token); await _stream.WriteAsync(buffer, token);
            }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
