using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public class NativeHttp3ListenerBindingTest
    {
        private static IHttpListener Create(X509Certificate2 certificate, bool native = true)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener");
            if (type == null) Assert.Ignore("HTTP/3 is unavailable in the selected asset.");
            return (IHttpListener)(Activator.CreateInstance(type ?? throw new AssertionException("Missing listener type."),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { certificate, native }, null)
                ?? throw new AssertionException("Missing native listener constructor."));
        }
        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public async Task NativeBindingDispatchesApplicationContextsAndRestarts(bool ipv6, bool native)
        {
            if (!QuicListener.IsSupported)
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required.");
                Assert.Ignore("QUIC is unavailable.");
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate, native);
            var port = new Uri(Resources.GetServerAddress()).Port;
            var address = new Uri("https://" + (ipv6 ? "[::1]" : "127.0.0.1") + ":" + port + "/");
            listener.AddPrefix(address.AbsoluteUri);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var cycle = 0; cycle < 3; cycle++)
            {
                listener.Start();
                var response = ReadResponseAsync(new Uri(address, "echo"), certificate, deadline.Token);
                var context = await AcceptContextAsync(listener, response, deadline.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
                    Assert.That(context.Request.Url.AbsolutePath, Is.EqualTo("/echo"));
                    Assert.That(context.Request.LocalEndPoint.Port, Is.EqualTo(port));
                    Assert.That(context.Request.RemoteEndPoint.Port, Is.GreaterThan(0));
                    // The runtime may attach the loopback interface's IPv6 scope
                    // ID. Preserve it in the context, compare the intended bytes.
                    Assert.That(context.Request.LocalEndPoint.Address.MapToIPv6().GetAddressBytes(),
                        Is.EqualTo((ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).MapToIPv6().GetAddressBytes()));
                });
                await context.SendStringAsync("native-ready", "text/plain", WebServer.Utf8NoBomEncoding);
                context.Close();
                Assert.That(await response, Is.EqualTo("native-ready"));
                listener.Stop();
                Assert.That(listener.IsListening, Is.False);
            }
        }
        [Test]
        public async Task RejectedPeerHandshakeDoesNotStopNativeApplicationDispatch()
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate);
            var address = new Uri("https://127.0.0.1:" + new Uri(Resources.GetServerAddress()).Port + "/");
            listener.AddPrefix(address.AbsoluteUri);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using (var rejected = Client(certificate, false))
                await Assert.ThrowsAsync<HttpRequestException>(async () => await rejected.GetStringAsync(new Uri("https://localhost:" + address.Port + "/"), deadline.Token));
            var result = ReadResponseAsync(address, certificate, deadline.Token);
            var context = await AcceptContextAsync(listener, result, deadline.Token);
            await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await result, Is.EqualTo("healthy"));
            Assert.That(listener.IsListening, Is.True);
            listener.Stop();
        }
        [Test]
        public async Task NativeDrainPreservesAnAdmittedResponseUntilCompletion()
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate);
            var address = new Uri("https://127.0.0.1:" + new Uri(Resources.GetServerAddress()).Port + "/");
            listener.AddPrefix(address.AbsoluteUri);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = ReadResponseAsync(address, certificate, deadline.Token);
            var context = await AcceptContextAsync(listener, response, deadline.Token);
            var drainMethod = listener.GetType().GetMethod("DrainAsync")
                ?? throw new AssertionException("Missing drain operation.");
            var beginDrain = drainMethod.CreateDelegate<Func<TimeSpan, CancellationToken, Task>>(listener);
            var drain = beginDrain(TimeSpan.FromSeconds(5), deadline.Token);
            Assert.That(drain.IsCompleted, Is.False);
            await context.SendStringAsync("drained", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await response, Is.EqualTo("drained"));
            await drain.WaitAsync(deadline.Token);
            Assert.That(listener.IsListening, Is.False);
        }
        [Test]
        public async Task NativeFailedMultiEndpointStartRollsBackEarlierBindings()
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ExclusiveAddressUse = true };
            occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var blockedPort = ((IPEndPoint)(occupied.LocalEndPoint ?? throw new AssertionException("Missing blocked endpoint."))).Port;
            var first = new Uri("https://127.0.0.1:" + new Uri(Resources.GetServerAddress()).Port + "/");
            using (var rejected = Create(certificate))
            {
                rejected.AddPrefix(first.AbsoluteUri);
                rejected.AddPrefix("https://127.0.0.1:" + blockedPort + "/");
                var failure = Assert.Throws<SocketException>(rejected.Start);
                Assert.That(failure.SocketErrorCode, Is.EqualTo(SocketError.AddressAlreadyInUse));
                Assert.That(rejected.IsListening, Is.False);
            }
            using var replacement = Create(certificate);
            replacement.AddPrefix(first.AbsoluteUri);
            replacement.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = ReadResponseAsync(first, certificate, deadline.Token);
            var context = await AcceptContextAsync(replacement, response, deadline.Token);
            await context.SendStringAsync("rebound", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await response, Is.EqualTo("rebound"));
        }
        [Test]
        public async Task NativeStopCompletesAPendingContextAccept()
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate);
            listener.AddPrefix("https://127.0.0.1:" + new Uri(Resources.GetServerAddress()).Port + "/");
            listener.Start();
            var pending = listener.GetContextAsync(CancellationToken.None);
            Assert.That(pending.IsCompleted, Is.False);
            listener.Stop();
            var failure = await Assert.ThrowsAsync<HttpListenerException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure.ErrorCode, Is.EqualTo(995));
            Assert.That(listener.IsListening, Is.False);
        }
        [TestCase(true)]
        [TestCase(false)]
        public async Task DnsHttpClientDispatchesThroughEitherProvider(bool native)
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate, native);
            var address = new Uri("https://localhost:" + new Uri(Resources.GetServerAddress()).Port + "/");
            listener.AddPrefix(address.AbsoluteUri);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = Client(certificate, true);
            var response = client.GetStringAsync(address, deadline.Token);
            var context = await AcceptContextAsync(listener, response, deadline.Token);
            Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
            await context.SendStringAsync("dns-client", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await response, Is.EqualTo("dns-client"));
        }
        private static async Task<string> ReadResponseAsync(Uri address, X509Certificate2 certificate, CancellationToken token)
        {
            // Keep the literal HTTP authority and exact remote IP, but use a
            // DNS TLS identity. The pinned Unix MsQuic client fails before
            // certificate callbacks for IP-literal TLS targets on both providers.
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address.Host.Trim('[', ']')), address.Port),
                MaxInboundBidirectionalStreams = 128,
                MaxInboundUnidirectionalStreams = 16,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString() == certificate.GetCertHashString(),
                },
            }, token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
            await control.WriteAsync(new byte[] { 0, 4, 0 }, token);
            await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
            // Independent static QPACK: GET, https, literal path and authority.
            var authority = Encoding.ASCII.GetBytes(address.Authority);
            var path = Encoding.ASCII.GetBytes(address.PathAndQuery);
            var fields = new byte[8 + path.Length + authority.Length];
            fields[2] = 0xd1; fields[3] = 0xd7;
            fields[4] = 0x51; fields[5] = checked((byte)path.Length);
            path.CopyTo(fields, 6);
            fields[6 + path.Length] = 0x50; fields[7 + path.Length] = checked((byte)authority.Length);
            authority.CopyTo(fields, 8 + path.Length);
            Assert.That(fields.Length, Is.LessThan(64));
            var wire = new byte[2 + fields.Length];
            wire[0] = 1; wire[1] = (byte)fields.Length; fields.CopyTo(wire, 2);
            await request.WriteAsync(wire, true, token);
            using var body = new MemoryStream();
            var seenHeaders = false;
            while (true)
            {
                var first = new byte[1];
                if (await request.ReadAsync(first, token) == 0) break;
                var type = await ReadIntegerAsync(request, first[0], token);
                await request.ReadExactlyAsync(first, token);
                var length = await ReadIntegerAsync(request, first[0], token);
                Assert.That(length, Is.LessThanOrEqualTo(65536));
                var payload = new byte[checked((int)length)];
                await request.ReadExactlyAsync(payload, token);
                if (type == 1)
                {
                    Assert.That(seenHeaders, Is.False);
                    Assert.That(payload.Length, Is.GreaterThanOrEqualTo(3));
                    // No dynamic table was advertised. Static index 25 is 200.
                    Assert.That(payload.AsSpan(0, 3).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0xd9 }));
                    seenHeaders = true;
                }
                else if (type == 0)
                {
                    Assert.That(seenHeaders, Is.True);
                    body.Write(payload);
                }
            }
            Assert.That(seenHeaders, Is.True);
            await connection.CloseAsync(0x100, token);
            return Encoding.UTF8.GetString(body.ToArray());
        }
        private static async Task<long> ReadIntegerAsync(QuicStream stream, byte first, CancellationToken token)
        {
            var width = 1 << (first >> 6);
            long value = first & 63;
            var rest = new byte[width - 1];
            await stream.ReadExactlyAsync(rest, token);
            foreach (var item in rest) value = (value << 8) | item;
            return value;
        }
        private static async Task<IHttpContextImpl> AcceptContextAsync(IHttpListener listener, Task<string> response, CancellationToken token)
        {
            var pending = listener.GetContextAsync(token);
            if (await Task.WhenAny(pending, response) == response)
            {
                // Surface a failed handshake or protocol exchange immediately,
                // rather than hiding its cause behind the context deadline.
                await response;
                Assert.Fail("The client completed before the application received its context.");
            }
            return await pending;
        }
        private static HttpClient Client(X509Certificate2 certificate, bool trust)
            => new(new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => trust && peer != null
                        && peer.GetCertHashString() == certificate.GetCertHashString(),
                },
            })
            { DefaultRequestVersion = HttpVersion.Version30, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }
}
