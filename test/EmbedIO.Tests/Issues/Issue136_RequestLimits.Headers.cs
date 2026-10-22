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
            Assert.That(options.MaximumRequestHeaderBytes, Is.EqualTo(32 * 1024));
            Assert.That(options.MaximumRequestHeaderCount, Is.EqualTo(100));
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
            Assert.That(wire, Does.Not.Contain("HTTP/1.1 100"));
            Assert.That(wire, Does.Contain("Connection: close"), "The response announces that the connection will not be reused.");
            Assert.That(wire, Does.Contain("\r\n\r\n7\r\nignored\r\n"));
        }

        [Test]
        public async Task WithoutABodyLimitContinueIsStillSentBeforeDispatch()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(null),
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

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RawBodyHeaderRejectionPrecedesBodyAndOccupiedAdmission(int protocol)
        {
            using var gate = new Gate(1);
            var posts = 0;
            await using var host = await MultiHost.StartAsync(protocol,
                options => options.WithMaximumRequestBodyBytes(2).WithRequestHeaderLimits(256)
                    .WithMaximumConcurrentRequests(1), async context =>
                {
                    if (context.Request.HttpMethod == "POST") Interlocked.Increment(ref posts);
                    await gate.HandleAsync(context);
                }, HttpVerbs.Any);
            var held = host.SendAsync(HttpMethod.Get, null);
            try
            {
                await gate.Entered.WaitAsync(Deadline);
                if (protocol == 1)
                {
                    // Observe the actual interim-response wire. HttpClient may
                    // serialize content after a final refusal, so its Sent flag
                    // alone cannot establish an HTTP/1 upload invitation.
                    var endpoint = new Uri(host.Url);
                    using var tcp = new TcpClient();
                    using var connectTimeout = new CancellationTokenSource(Deadline);
                    await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, connectTimeout.Token);
                    using var peer = new RawSocket(tcp);
                    await peer.SendAsync($"POST / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nContent-Length: 3\r\nExpect: 100-continue\r\nX-Large: {new string('a', 300)}\r\n\r\n");
                    var wire = await peer.ReadToEndAsync();
                    Assert.That(wire, Does.StartWith("HTTP/1.1 431"),
                        "Header policy must precede known-length 413 and occupied-slot 503.");
                    Assert.That(wire, Does.Not.Contain("HTTP/1.1 100"));
                    Assert.That(wire, Does.Contain("Request header fields exceed the configured limit."));
                    Assert.That(wire, Does.Contain("Connection: close"));
                }
                else
                {
                    var content = new TrackingContent(3);
                    using var rejected = await host.SendAsync(HttpMethod.Post, content, request =>
                    {
                        request.Headers.ExpectContinue = true;
                        request.Headers.Add("X-Large", new string('a', 300));
                    });
                    Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.RequestHeaderFieldsTooLarge),
                        "Header policy must precede known-length 413 and occupied-slot 503.");
                    Assert.That(await rejected.Content.ReadAsStringAsync(), Does.Contain("header fields exceed"));
                    Assert.That(content.Sent, Is.False, "A simultaneous policy rejection must not invite the entity.");
                }
                Assert.That(Volatile.Read(ref posts), Is.Zero);
                Assert.That(gate.EnteredCount, Is.EqualTo(1));
            }
            finally { gate.Release(); }
            using var first = await held;
            Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("released"));
            await host.WaitForAdmissionReleaseAsync();
            using var healthy = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await healthy.Content.ReadAsStringAsync(), Is.EqualTo("released"));
            Assert.That(gate.EnteredCount, Is.EqualTo(2));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RawBodyRouteEffectiveLimitPrecedesContinueAndGlobalRefusal(int protocol)
        {
            var posts = 0;
            await using var host = await MultiHost.StartAsync(protocol, options => options.WithMaximumRequestBodyBytes(2)
                .WithRouteRequestLimits("/raised/{id}", policy => policy.MaximumRequestBodyBytes = 4)
                .WithRouteRequestLimits("/raised", policy => policy.MaximumRequestBodyBytes = 0)
                .WithRouteRequestLimits("/lifted", policy => policy.MaximumRequestBodyBytes = long.MaxValue)
                .WithRouteRequestLimits("/empty", policy => policy.MaximumRequestBodyBytes = 0), async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    { await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding); return; }
                    Interlocked.Increment(ref posts);
                    // A request without body content has nothing to count, so it keeps the plain input.
                    var counted = !context.Request.Url.AbsolutePath.StartsWith("/lifted", StringComparison.Ordinal)
                        && context.Request.HasEntityBody;
                    Assert.That(context.Request.InputStream.GetType().Name,
                        counted ? Is.EqualTo("RawRequestBodyStream") : Is.Not.EqualTo("RawRequestBodyStream"));
                    var body = await context.GetRequestBodyAsByteArrayAsync();
                    await context.SendStringAsync(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "text/plain", WebServer.Utf8NoBomEncoding);
                }, HttpVerbs.Any);
            async Task Post(string path, int length)
            {
                using var response = await host.SendAsync(HttpMethod.Post, new ByteArrayContent(new byte[length]), request =>
                {
                    request.RequestUri = new Uri(host.Url + path);
                    // RFC 9110 section 10.1.1 forbids Expect: 100-continue
                    // without content; zero still exercises the zero-byte policy.
                    // https://www.rfc-editor.org/rfc/rfc9110.html#section-10.1.1
                    request.Headers.ExpectContinue = length > 0;
                });
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            async Task Refuse(string path, int length)
            {
                var before = Volatile.Read(ref posts);
                if (protocol == 1)
                {
                    var endpoint = new Uri(host.Url);
                    using var tcp = new TcpClient();
                    using var timeout = new CancellationTokenSource(Deadline);
                    await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, timeout.Token);
                    using var peer = new RawSocket(tcp);
                    await peer.SendAsync($"POST /{path} HTTP/1.1\r\nHost: {endpoint.Authority}\r\nContent-Length: {length}\r\nExpect: 100-continue\r\n\r\n");
                    var wire = await peer.ReadToEndAsync();
                    Assert.That(wire, Does.StartWith("HTTP/1.1 413"));
                    Assert.That(wire, Does.Not.Contain("HTTP/1.1 100"));
                    Assert.That(wire, Does.Contain("Request body exceeds the configured byte limit."),
                        "Known refusal retains the existing error formatter.");
                }
                else
                {
                    var content = new TrackingContent(length);
                    using var response = await host.SendAsync(HttpMethod.Post, content, request =>
                    {
                        request.RequestUri = new Uri(host.Url + path);
                        request.Headers.ExpectContinue = true;
                    });
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
                    Assert.That(await response.Content.ReadAsStringAsync(),
                        Does.Contain("Request body exceeds the configured byte limit."),
                        "Known refusal retains the existing error formatter.");
                    Assert.That(content.Sent, Is.False);
                }
                Assert.That(Volatile.Read(ref posts), Is.EqualTo(before));
            }
            // The parameterized first match raises the global cap before Continue.
            await Post("raised/%34%32/items", 4);
            await Refuse("raised/42/items", 5);
            await Post("lifted/nested", 6);
            await Post("empty", 0);
            await Refuse("empty", 1);
            await Refuse("other", 3);
            using var healthy = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(await healthy.Content.ReadAsStringAsync(), Is.EqualTo("healthy"));
            Assert.That(posts, Is.EqualTo(3));
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

            public static async Task<MultiHost> StartAsync(int protocol, Action<WebServerOptions> configure, Func<IHttpContext, Task> handler, HttpVerbs verbs = HttpVerbs.Get, Action<WebServer>? configureServer = null)
            {
                X509Certificate2? certificate = null;
                string url;
                if (protocol == 3)
                {
                    var supported = QuicConnection.IsSupported
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
                configureServer?.Invoke(server);

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

            internal async Task WaitForAdmissionReleaseAsync()
            {
                // Response bytes can arrive before the handler's finally returns
                // its admission slot. Observe that real settlement before probing
                // listener health; never release or mutate the engine semaphore.
                var owner = _server.GetType().BaseType ?? throw new AssertionException("Missing server base.");
                var slots = owner.GetField("_requestSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(_server) as SemaphoreSlim ?? throw new AssertionException("Missing owned admission slots.");
                using var timeout = new CancellationTokenSource(Deadline);
                while (slots.CurrentCount == 0) await Task.Delay(1, timeout.Token);
                Assert.That(slots.CurrentCount, Is.EqualTo(1));
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
