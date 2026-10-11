using System;
using System.Net;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
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
        private SourceLevels _priorLevel;
        private ConsoleTraceListener? _trace;
        [SetUp]
        public void CaptureCandidateConnectionFailures()
        {
            var source = EmbedIO.Diagnostics.Log.Source;
            _priorLevel = source.Switch.Level;
            _trace = new ConsoleTraceListener(true);
            source.Listeners.Add(_trace);
            source.Switch.Level = SourceLevels.Warning;
        }
        [TearDown]
        public void RestoreCandidateDiagnostics()
        {
            var source = EmbedIO.Diagnostics.Log.Source;
            source.Switch.Level = _priorLevel;
            if (_trace != null) { source.Listeners.Remove(_trace); _trace.Dispose(); _trace = null; }
        }
        private static IHttpListener Create(X509Certificate2 certificate)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener");
            if (type == null) Assert.Ignore("HTTP/3 is unavailable in the selected asset.");
            return (IHttpListener)(Activator.CreateInstance(type ?? throw new AssertionException("Missing listener type."),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { certificate, true }, null)
                ?? throw new AssertionException("Missing native listener constructor."));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeBindingDispatchesApplicationContextsAndRestarts(bool ipv6)
        {
            if (!QuicListener.IsSupported)
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required.");
                Assert.Ignore("QUIC is unavailable.");
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = Create(certificate);
            var port = new Uri(Resources.GetServerAddress()).Port;
            var address = new Uri("https://" + (ipv6 ? "[::1]" : "127.0.0.1") + ":" + port + "/");
            listener.AddPrefix(address.AbsoluteUri);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var cycle = 0; cycle < 3; cycle++)
            {
                listener.Start();
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, peer, _, _) => peer != null
                            && peer.GetCertHashString() == certificate.GetCertHashString(),
                    },
                };
                using var client = new HttpClient(handler)
                { DefaultRequestVersion = HttpVersion.Version30, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
                var response = client.GetStringAsync(new Uri(address, "echo"), deadline.Token);
                var context = await listener.GetContextAsync(deadline.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
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
                client.Dispose();
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
                await Assert.ThrowsAsync<HttpRequestException>(async () => await rejected.GetStringAsync(address, deadline.Token));
            using var healthy = Client(certificate, true);
            var result = healthy.GetStringAsync(address, deadline.Token);
            var context = await listener.GetContextAsync(deadline.Token);
            await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await result, Is.EqualTo("healthy"));
            Assert.That(listener.IsListening, Is.True);
            healthy.Dispose();
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
            using var client = Client(certificate, true);
            var response = client.GetStringAsync(address, deadline.Token);
            var context = await listener.GetContextAsync(deadline.Token);
            var drainMethod = listener.GetType().GetMethod("DrainAsync")
                ?? throw new AssertionException("Missing drain operation.");
            var beginDrain = drainMethod.CreateDelegate<Func<TimeSpan, CancellationToken, Task>>(listener);
            var drain = beginDrain(TimeSpan.FromSeconds(5), deadline.Token);
            Assert.That(drain.IsCompleted, Is.False);
            await context.SendStringAsync("drained", "text/plain", WebServer.Utf8NoBomEncoding);
            context.Close();
            Assert.That(await response, Is.EqualTo("drained"));
            client.Dispose();
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
            using var client = Client(certificate, true);
            var response = client.GetStringAsync(first, deadline.Token);
            var context = await replacement.GetContextAsync(deadline.Token);
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
