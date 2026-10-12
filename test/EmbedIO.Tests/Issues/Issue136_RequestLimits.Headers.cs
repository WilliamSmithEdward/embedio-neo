using System;
using System.IO;
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
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #136, second increment: header byte and field-count limits, 100 Continue held
    // back until a handler reads the body, and HTTP/3 coverage of the server-wide limits.
    // Protocol 1 and 2 use cleartext TCP (HTTP/2 by prior knowledge); 3 uses QUIC.
    public partial class Issue136_RequestLimits
    {
        // ---------------------------------------------------------------- header limits

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task OversizedHeaderFieldsAreRejectedBeforeAnyModuleRuns(int protocol)
        {
            var entered = 0;
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithRequestHeaderLimits(256), context =>
            {
                _ = Interlocked.Increment(ref entered);
                return context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            using (var over = await host.SendAsync(HttpMethod.Get, null, request => request.Headers.Add("X-Large", new string('a', 300))))
            {
                Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.RequestHeaderFieldsTooLarge));
                Assert.That(await over.Content.ReadAsStringAsync(), Does.Contain("header fields exceed"));
            }
            Assert.That(entered, Is.Zero);
            using var small = await host.SendAsync(HttpMethod.Get, null, request => request.Headers.Add("X-Small", new string('a', 100)));
            Assert.That(await small.Content.ReadAsStringAsync(), Is.EqualTo("handled"));
            Assert.That(entered, Is.EqualTo(1));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task TooManyHeaderFieldsAreRejected(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithRequestHeaderLimits(null, 4),
                context => context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding));
            using (var over = await host.SendAsync(HttpMethod.Get, null, request =>
            {
                for (var i = 0; i < 5; ++i) request.Headers.Add("X-Field-" + i, "v");
            }))
            {
                Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.RequestHeaderFieldsTooLarge));
            }
            // HTTP/1 also counts Host; HTTP/2 and HTTP/3 carry it as a pseudo-header.
            using var under = await host.SendAsync(HttpMethod.Get, null, request =>
            {
                request.Headers.Add("X-Field-0", "v");
                request.Headers.Add("X-Field-1", "v");
            });
            Assert.That(await under.Content.ReadAsStringAsync(), Is.EqualTo("handled"));
        }

        [Test]
        public async Task RepeatedHttp1FieldLinesCountIndividually()
        {
            await using var host = Host.Start(o => o.WithRequestHeaderLimits(null, 3),
                context => context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding));
            using (var socket = await host.ConnectAsync())
            {
                // Host plus three lines of one name: the stored collection keeps one value, the limit sees four.
                await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\nX-A: 1\r\nX-A: 2\r\nX-A: 3\r\nConnection: close\r\n\r\n");
                Assert.That(await socket.ReadToEndAsync(), Does.StartWith("HTTP/1.1 431"));
            }
            using (var socket = await host.ConnectAsync())
            {
                await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\nX-A: 1\r\nConnection: close\r\n\r\n");
                var wire = await socket.ReadToEndAsync();
                Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
                Assert.That(wire, Does.Contain("\r\n\r\n7\r\nhandled\r\n"));
            }
        }

        [Test]
        public async Task HeaderRejectionDoesNotWaitForARequestSlot()
        {
            using var gate = new Gate(1);
            await using var host = Host.Start(o => o.WithMaximumConcurrentRequests(1, 5).WithRequestHeaderLimits(256), gate.HandleAsync);
            using var client = Host.CreateClient(1);
            var held = client.GetStringAsync(host.Url);
            await gate.Entered.WaitAsync(Deadline);
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Url);
            request.Headers.Add("X-Large", new string('a', 300));
            using var rejected = await client.SendAsync(request);
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.RequestHeaderFieldsTooLarge), "The rejection precedes the queue.");
            gate.Release();
            Assert.That(await held, Is.EqualTo("released"));
        }

        [Test]
        public void HeaderLimitDefaultsValidationAndLocking()
        {
            var options = new WebServerOptions();
            Assert.That(options.MaximumRequestHeaderBytes, Is.Null);
            Assert.That(options.MaximumRequestHeaderCount, Is.Null);
            Assert.That(() => options.MaximumRequestHeaderBytes = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.MaximumRequestHeaderCount = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
            options.WithUrlPrefix(Resources.GetServerAddress()).WithRequestHeaderLimits(1024, 20);
            Assert.That(options.MaximumRequestHeaderBytes, Is.EqualTo(1024));
            Assert.That(options.MaximumRequestHeaderCount, Is.EqualTo(20));
            using var server = new WebServer(options);
            Assert.That(() => options.MaximumRequestHeaderBytes = 2048, Throws.InvalidOperationException);
            Assert.That(() => options.WithRequestHeaderLimits(null), Throws.InvalidOperationException);
        }

        // ---------------------------------------------------------------- deferred 100 Continue

        // The client waits up to 30 seconds for 100 Continue. A refused body is answered
        // without it, and the client never sends the content. HTTP/1 is checked on the wire
        // below: the .NET HTTP/1 client aborts its own connection after such a refusal.
        [TestCase(2)]
        [TestCase(3)]
        public async Task RefusedBodyIsNeverInvitedByContinue(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithMaximumRequestBodyBytes(100), Host.EchoLength, HttpVerbs.Any);
            var content = new TrackingContent(1000);
            using var response = await host.SendAsync(HttpMethod.Post, content, request => request.Headers.ExpectContinue = true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(content.Sent, Is.False, "The server did not invite an upload it refused.");
        }

        [Test]
        public async Task RefusedHttp1BodyIsAnsweredWithoutContinue()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(100), Host.EchoLength, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n");
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 413"), "No 100 Continue precedes the refusal.");
            Assert.That(wire, Does.Contain("Connection: close"));
        }

        // The handler's first read sends 100 Continue, long before the client's own timer.
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task AcceptedBodyIsInvitedWhenTheHandlerReadsIt(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithMaximumRequestBodyBytes(1000), Host.EchoLength, HttpVerbs.Any);
            var content = new TrackingContent(100);
            using var response = await host.SendAsync(HttpMethod.Post, content, request => request.Headers.ExpectContinue = true);
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("100"));
            Assert.That(content.Sent, Is.True);
        }

        [Test]
        public async Task UnreadBodyWithoutContinueClosesTheHttp1Connection()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(1000),
                context => context.SendStringAsync("ignored", "text/plain", WebServer.Utf8NoBomEncoding), HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 10\r\nExpect: 100-continue\r\n\r\n");
            // No body is sent: the server must answer and close rather than wait to drain it.
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire, Does.Not.Contain("100 Continue"));
            Assert.That(wire, Does.Contain("Connection: close"), "The response announces that the connection will not be reused.");
            Assert.That(wire, Does.Contain("\r\n\r\n7\r\nignored\r\n"));
        }

        [Test]
        public async Task WithoutABodyLimitContinueIsStillSentBeforeDispatch()
        {
            await using var host = Host.Start(_ => { },
                context => context.SendStringAsync("ignored", "text/plain", WebServer.Utf8NoBomEncoding), HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 4\r\nExpect: 100-continue\r\nConnection: close\r\n\r\nbody");
            Assert.That(await socket.ReadToEndAsync(), Does.StartWith("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200"));
        }

        // ---------------------------------------------------------------- HTTP/3 coverage

        [Test]
        public async Task Http3DeclaredAndCountedBodyLimits()
        {
            await using var host = await MultiHost.StartAsync(3, o => o.WithMaximumRequestBodyBytes(1000), Host.EchoLength, HttpVerbs.Any);
            using (var declared = await host.SendAsync(HttpMethod.Post, new ByteArrayContent(new byte[1001])))
                Assert.That(declared.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            using (var counted = await host.SendAsync(HttpMethod.Post, new StreamContent(new MemoryStream(new byte[5000]))))
                Assert.That(counted.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            using var exact = await host.SendAsync(HttpMethod.Post, new StreamContent(new MemoryStream(new byte[1000])));
            Assert.That(await exact.Content.ReadAsStringAsync(), Is.EqualTo("1000"));
        }

        [Test]
        public async Task Http3AdmissionRejectsBeyondTheQueue()
        {
            using var gate = new Gate(1);
            await using var host = await MultiHost.StartAsync(3, o => o.WithMaximumConcurrentRequests(1), gate.HandleAsync);
            var held = host.SendAsync(HttpMethod.Get, null);
            await gate.Entered.WaitAsync(Deadline);
            using (var rejected = await host.SendAsync(HttpMethod.Get, null))
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            gate.Release();
            using var first = await held;
            Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("released"));
        }

        [Test]
        public async Task Http3DeadlineAnswers503()
        {
            await using var host = await MultiHost.StartAsync(3, o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(300)),
                async context => await Task.Delay(Timeout.Infinite, context.CancellationToken));
            using var response = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("deadline"));
        }

        // ---------------------------------------------------------------- helpers

        private sealed class TrackingContent : HttpContent
        {
            private readonly int _length;
            private int _sent;
            public TrackingContent(int length) { _length = length; }
            public bool Sent => Volatile.Read(ref _sent) != 0;
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                Volatile.Write(ref _sent, 1);
                return stream.WriteAsync(new byte[_length], 0, _length);
            }
            protected override bool TryComputeLength(out long length) { length = _length; return true; }
        }

        // One server per protocol: cleartext TCP for HTTP/1 and HTTP/2, QUIC with a
        // generated certificate for HTTP/3. Clients wait 30 seconds for 100 Continue.
        private sealed class MultiHost : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            private readonly HttpClient _client;
            private readonly X509Certificate2? _certificate;
            private readonly Version _version;
            private MultiHost(WebServer server, string url, HttpClient client, Version version, X509Certificate2? certificate)
            {
                _server = server; Url = url; _client = client; _version = version; _certificate = certificate;
                _running = server.RunAsync(_stop.Token);
            }
            public string Url { get; }

            public static async Task<MultiHost> StartAsync(int protocol, Action<WebServerOptions> configure, Func<IHttpContext, Task> handler, HttpVerbs verbs = HttpVerbs.Get)
            {
                X509Certificate2? certificate = null;
                string url;
                if (protocol == 3)
                {
                    var supported = QuicListener.IsSupported && QuicConnection.IsSupported
                        && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") != null;
                    if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
                    if (!supported) Assert.Ignore("The selected asset or host has no HTTP/3 transport.");
                    // The non-Schannel QUIC backend exports the server credential to PKCS#12.
                    certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
                    using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    url = $"https://localhost:{((IPEndPoint)(probe.LocalEndPoint ?? throw new AssertionException("Missing UDP endpoint."))).Port}/";
                }
                else url = Resources.GetServerAddress();

                var mode = protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO;
                var server = new WebServer(o =>
                {
                    o.WithUrlPrefix(url).WithMode(mode);
                    if (certificate != null) o.WithCertificate(certificate);
                    configure(o);
                }).WithModule(new ActionModule("/", verbs, context => handler(context)));
                server.OnHttpException = HttpExceptionHandler.PlainTextResponse;

                var expected = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
                var client = new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false,
                    Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                            && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected,
                    },
                })
                { Timeout = Deadline };
                var host = new MultiHost(server, url, client, protocol switch { 1 => HttpVersion.Version11, 2 => HttpVersion.Version20, _ => HttpVersion.Version30 }, certificate);
                if (host._running.IsCompleted) await host._running;
                return host;
            }

            public Task<HttpResponseMessage> SendAsync(HttpMethod method, HttpContent? content, Action<HttpRequestMessage>? prepare = null)
            {
                var request = new HttpRequestMessage(method, Url) { Version = _version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = content };
                prepare?.Invoke(request);
                return SendAndDisposeAsync(request);
            }

            private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request)
            {
                using (request) return await _client.SendAsync(request);
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(Deadline); }
                finally
                {
                    _client.Dispose();
                    _server.Dispose();
                    _stop.Dispose();
                    _certificate?.Dispose();
                }
            }
        }
    }
}
