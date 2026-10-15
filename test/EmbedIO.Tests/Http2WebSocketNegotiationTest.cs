using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private sealed class NegotiationSocketModule : WebSocketModule
        {
            internal NegotiationSocketModule() : base("/ws", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
        }

        [TestCase("12", true, false)]
        [TestCase("12", true, true)]
        [TestCase("12", false, false)]
        [TestCase("12", false, true)]
        [TestCase("", true, false)]
        [TestCase("", true, true)]
        [TestCase("", false, false)]
        [TestCase("", false, true)]
        [TestCase(null, true, false)]
        [TestCase(null, true, true)]
        [TestCase(null, false, false)]
        [TestCase(null, false, true)]
        public async Task InvalidWebSocketVersionReturns400AndPreservesSiblingStream(string? version, bool ended, bool secure)
        {
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                if (certificate != null) options.WithCertificate(certificate);
            }).WithModule(new NegotiationSocketModule())
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True);
                using var socket = new TcpClient();
                var uri = new Uri(url);
                await socket.ConnectAsync(uri.Host, uri.Port, stop.Token);
                Stream wire = socket.GetStream();
                var expectedCertificate = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
                using var tls = secure ? new SslStream(wire, false, (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == expectedCertificate) : null;
                if (tls != null)
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                        ApplicationProtocols = new() { SslApplicationProtocol.Http2 },
                    }, stop.Token);
                    Assert.That(tls.NegotiatedApplicationProtocol, Is.EqualTo(SslApplicationProtocol.Http2));
                    wire = tls;
                }
                await wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), stop.Token);
                await SendWire(wire, 4, 0, 0, Array.Empty<byte>(), stop.Token);
                _ = await Until(wire, 4, 0, stop.Token);
                await SendWire(wire, 4, 1, 0, Array.Empty<byte>(), stop.Token);
                using var block = new MemoryStream();
                Literal(block, ":method", "CONNECT");
                Literal(block, ":scheme", secure ? "https" : "http");
                Literal(block, ":path", "/ws");
                Literal(block, ":authority", uri.Authority);
                Literal(block, ":protocol", "websocket");
                if (version != null) Literal(block, "sec-websocket-version", version);
                await SendWire(wire, 1, ended ? (byte)5 : (byte)4, 1, block.ToArray(), stop.Token);
                var resets = 0;
                async Task<(byte Type, byte Flags, int Id, byte[] Payload)> Next(byte type, int id)
                {
                    for (var i = 0; i < 128; i++)
                    {
                        var frame = await ReceiveWire(wire, stop.Token);
                        Assert.That(frame.Type, Is.Not.EqualTo(7), "Rejection must not send GOAWAY.");
                        if (frame.Type == 3)
                        {
                            Assert.That(ended, Is.False);
                            Assert.That(frame.Id, Is.EqualTo(1));
                            Assert.That(frame.Payload, Is.EqualTo(new byte[4]), "Only the rejected request input is canceled with NO_ERROR.");
                            Assert.That(++resets, Is.EqualTo(1));
                            if (type == 3 && id == 1) return frame;
                            continue;
                        }
                        if (frame.Type == type && frame.Id == id) return frame;
                    }
                    throw new IOException("Expected negotiation frame did not arrive.");
                }
                var decoder = Activator.CreateInstance(Type("HpackDecoder"), Flags, null, new object[] { 32768 }, null)
                    ?? throw new AssertionException("Missing decoder.");
                var decode = (Type("HpackDecoder").GetMethod("Decode", Flags) ?? throw new AssertionException("Missing Decode.")).CreateDelegate<Func<byte[], Array>>(decoder);
                var fields = decode((await Next(1, 1)).Payload).Cast<object>()
                    .GroupBy(field => Property<string>(field, "Name"))
                    .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(field => Property<string>(field, "Value"))));
                Assert.That(fields[":status"], Is.EqualTo("400"));
                Assert.That(fields["sec-websocket-version"], Is.EqualTo("13"));
                using var next = new MemoryStream();
                Literal(next, ":method", "GET"); Literal(next, ":scheme", secure ? "https" : "http");
                Literal(next, ":path", "/healthy"); Literal(next, ":authority", uri.Authority);
                await SendWire(wire, 1, 5, 3, next.ToArray(), stop.Token);
                var response = decode((await Next(1, 3)).Payload).Cast<object>()
                    .GroupBy(field => Property<string>(field, "Name"))
                    .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(field => Property<string>(field, "Value"))));
                Assert.That(response[":status"], Is.EqualTo("200"));
                Assert.That(Encoding.UTF8.GetString((await Next(0, 3)).Payload), Is.EqualTo("healthy"));
                if (!ended && resets == 0) _ = await Next(3, 1);
                Assert.That(resets, Is.EqualTo(ended ? 0 : 1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static void Literal(Stream block, string name, string value)
        {
            var key = Encoding.ASCII.GetBytes(name); var bytes = Encoding.ASCII.GetBytes(value);
            Assert.That(key.Length, Is.LessThan(127)); Assert.That(bytes.Length, Is.LessThan(127));
            block.WriteByte(0); block.WriteByte((byte)key.Length); block.Write(key, 0, key.Length);
            block.WriteByte((byte)bytes.Length); block.Write(bytes, 0, bytes.Length);
        }
    }
}
