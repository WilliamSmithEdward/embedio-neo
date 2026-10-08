using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [SetUp]
        public void RequireQuic()
        {
            var supported = QuicListener.IsSupported && QuicConnection.IsSupported
                && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") != null;
            if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
            if (!supported) Assert.Ignore("The selected asset or host has no HTTP/3 transport.");
        }
        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }
        private static string Prefix()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return $"https://localhost:{((IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port}/";
        }
        private static HttpClient Client(X509Certificate2 certificate) => new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() }
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(15)
        };
        private static HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url)
            { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = content };
            return request;
        }
        [TestCase(true)]
        [TestCase(false)]
        public async Task QueryRouteReceivesHttp3Content(bool mediaType)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Query, async context =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
                    await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                using var request = Request(new HttpMethod("QUERY"), prefix,
                    new StringContent("quic-query", WebServer.Utf8NoBomEncoding, "text/plain"));
                if (!mediaType) (request.Content ?? throw new AssertionException("Missing content.")).Headers.Remove("Content-Type");
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(mediaType ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                if (mediaType)
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("quic-query"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(0, 1)]
        [TestCase(100003, 8)]
        [TestCase(1048576, 3)]
        public async Task WebServerModulesEchoConcurrentHttp3Uploads(int size, int concurrency)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
                    Assert.That(context.Request.IsSecureConnection, Is.True);
                    using var bytes = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(bytes, context.CancellationToken);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes.ToArray(), context.CancellationToken);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async index =>
                {
                    var bytes = Enumerable.Repeat((byte)(index + 1), size).ToArray();
                    using var request = Request(HttpMethod.Post, prefix + "echo", new ByteArrayContent(bytes));
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(stop.Token), Is.EqualTo(bytes));
                }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        [Test]
        public async Task PrefixRoutingAndSessionsUseTheNormalPipeline()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix + "scope/").WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithLocalSessionManager()
                .WithModule(new ActionModule("/scope", HttpVerbs.Get, async context =>
                {
                    var count = context.Session["count"] is int existing ? existing + 1 : 1;
                    context.Session["count"] = count;
                    await context.SendStringAsync(context.Request.Url.AbsolutePath + ":" + count, "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                Assert.That(await client.GetStringAsync(prefix + "scope/hello", stop.Token), Is.EqualTo("/scope/hello:1"));
                Assert.That(await client.GetStringAsync(prefix + "scope/hello", stop.Token), Is.EqualTo("/scope/hello:2"));
                using var outside = await client.GetAsync(prefix + "outside", stop.Token);
                Assert.That(outside.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                using var independent = Client(certificate);
                Assert.That(await independent.GetStringAsync(prefix + "scope/hello", stop.Token), Is.EqualTo("/scope/hello:1"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task StopOrClientResetCancelsApplication(bool clientReset)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedPort = 0;
            var healthyPort = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/blocked")
                    {
                        blockedPort = context.RemoteEndPoint.Port; entered.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                        finally { exited.TrySetResult(); }
                    }
                    else
                    {
                        healthyPort = context.RemoteEndPoint.Port;
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                    }
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var reset = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            var pending = client.GetAsync(prefix + "blocked", reset.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (clientReset) reset.Cancel(); else stop.Cancel();
                try { using var response = await pending; Assert.Fail("The pending request should be canceled."); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (clientReset)
                {
                    Assert.That(await client.GetStringAsync(prefix + "healthy", stop.Token), Is.EqualTo("healthy"));
                    Assert.That(healthyPort, Is.EqualTo(blockedPort));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        [Test]
        public async Task ListenerStopReleasesAcceptAndAllowsRestart()
        {
            using var certificate = Certificate();
            using var server = new WebServer(o => o.WithUrlPrefix(Prefix()).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            var listener = server.Listener;
            for (var iteration = 0; iteration < 3; ++iteration)
            {
                listener.Start();
                Assert.That(listener.IsListening, Is.True);
                using var canceled = new CancellationTokenSource();
                var canceledAccept = listener.GetContextAsync(canceled.Token);
                canceled.Cancel();
                await Assert.ThatAsync(async () => await canceledAccept, Throws.InstanceOf<OperationCanceledException>());
                using var client = Client(certificate);
                var response = client.GetAsync(listener.Prefixes[0]);
                var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await context.SendStringAsync("restart", "text/plain", WebServer.Utf8NoBomEncoding);
                context.Close();
                using var received = await response;
                Assert.That(await received.Content.ReadAsStringAsync(), Is.EqualTo("restart"));
                var pending = listener.GetContextAsync(CancellationToken.None);
                listener.Stop();
                await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<HttpListenerException>());
                Assert.That(listener.IsListening, Is.False);
            }
        }
        [Test]
        public async Task RejectedCertificateDoesNotStopAcceptingHealthyClients()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .OnGet("/", context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            using var rejecting = new HttpClient(new SocketsHttpHandler
            { UseProxy = false, SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => false } });
            using var rejected = Request(HttpMethod.Get, prefix);
            try
            {
                await Assert.ThatAsync(async () => { using var response = await rejecting.SendAsync(rejected, stop.Token); }, Throws.InstanceOf<HttpRequestException>());
                using var client = Client(certificate);
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
                Assert.That(server.Listener.IsListening, Is.True);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        [Test]
        public async Task PrefixesOnOtherPortsCannotRouteThroughThisEndpoint()
        {
            using var certificate = Certificate();
            var first = Prefix(); var second = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefixes(first + "first/", second + "second/")
                .WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .OnGet("/", context => context.SendStringAsync("routed", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                Assert.That(await client.GetStringAsync(first + "first/", stop.Token), Is.EqualTo("routed"));
                Assert.That(await client.GetStringAsync(second + "second/", stop.Token), Is.EqualTo("routed"));
                using var request = Request(HttpMethod.Get, first + "second/");
                request.Headers.Host = new Uri(second).Authority;
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        [TestCase("http://localhost:12345/")]
        [TestCase("https://localhost:12345/path?query=/")]
        [TestCase("https://localhost:12345/path#fragment/")]
        public void InvalidHttp3PrefixesAreRejectedBeforeBinding(string prefix)
        {
            using var certificate = Certificate();
            Assert.That(() => new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate)),
                Throws.InstanceOf<ArgumentException>());
        }
        [TestCase(false)]
        [TestCase(true)]
        public void MissingPrivateKeyIsRejectedBeforeBinding(bool publicOnly)
        {
            using var certificate = Certificate();
            using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            Assert.That(() => new WebServer(new WebServerOptions
            { Mode = HttpListenerMode.EmbedIOHttp3, Certificate = publicOnly ? publicCertificate : null }.WithUrlPrefix(Prefix())),
                Throws.InstanceOf<ArgumentException>());
        }
        [Test]
        public void FailedMultiEndpointStartupRollsBackEarlierBindings()
        {
            using var certificate = Certificate();
            var first = Prefix();
            using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var second = $"https://127.0.0.1:{((IPEndPoint)(occupied.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port}/";
            using var failing = new WebServer(o => o.WithUrlPrefixes(first, second).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            Assert.That(() => failing.Listener.Start(), Throws.Exception);
            Assert.That(failing.Listener.IsListening, Is.False);
            using var healthy = new WebServer(o => o.WithUrlPrefix(first).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            healthy.Listener.Start();
            Assert.That(healthy.Listener.IsListening, Is.True);
            healthy.Listener.Stop();
        }
    }
}
