using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Finite request-limit defaults, modelled on Kestrel's KestrelServerLimits in ASP.NET
    // Core 10.0.13: each default is pinned, and so is the explicit value that replaces it,
    // including null where null has always meant unlimited.
    public partial class Issue136_RequestLimits
    {
        // ---------------------------------------------------------------- defaults

        [Test]
        public void FiniteDefaultsAndExplicitUnlimitedOverrides()
        {
            var options = new WebServerOptions();
            Assert.That(options.MaximumRequestBodyBytes, Is.EqualTo(30_000_000L));
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.EqualTo(30_000_000L), "The decoded limit follows the body limit.");
            Assert.That(options.MaximumRequestHeaderBytes, Is.EqualTo(32 * 1024));
            Assert.That(options.MaximumRequestHeaderCount, Is.EqualTo(100));
            Assert.That(options.RequestBodyReadTimeout, Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(options.RequestTimeout, Is.Null, "Request deadlines stay opt-in.");
            Assert.That(options.MaximumConcurrentRequests, Is.Null, "Concurrency limits stay opt-in.");
            Assert.That(options.RequestHeadersTimeout, Is.Null, "Null applies the built-in first-head deadline.");

            options.MaximumRequestBodyBytes = null;
            options.MaximumRequestHeaderBytes = null;
            options.MaximumRequestHeaderCount = null;
            options.RequestBodyReadTimeout = null;
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.Null, "Without a body limit an unset decoded limit is unlimited too.");
            Assert.That(options.MaximumRequestHeaderBytes, Is.Null);
            Assert.That(options.MaximumRequestHeaderCount, Is.Null);
            Assert.That(options.RequestBodyReadTimeout, Is.Null);
        }

        [Test]
        public void DecompressedLimitFollowsTheBodyLimitUntilSet()
        {
            var options = new WebServerOptions().WithMaximumRequestBodyBytes(1000);
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.EqualTo(1000));
            options.WithMaximumDecompressedRequestBodyBytes(5000);
            options.MaximumRequestBodyBytes = 10;
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.EqualTo(5000), "A set value no longer follows the body limit.");
            options.MaximumDecompressedRequestBodyBytes = null;
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.Null, "An explicit null removes the decoded limit.");
            Assert.That(() => options.MaximumDecompressedRequestBodyBytes = -1, Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        // A connection that never completes its first request head is closed after 30 seconds,
        // too long to wait for here: read the deadline each listener is built with.
        [Test]
        public void FirstRequestHeadDeadlineDefaultsToThirtySeconds()
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
            var policyType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpConnectionPolicy")
                ?? throw new AssertionException("Missing TCP connection policy.");
            int HeaderTimeout(object policy) => (int)(policyType.GetProperty("HeaderTimeoutMilliseconds", members)?.GetValue(policy)
                ?? throw new AssertionException("Missing first-head deadline."));
            object Create(WebServerOptions options) => typeof(WebServerOptions).GetMethod("CreateConnectionPolicy", members)?.Invoke(options, null)
                ?? throw new AssertionException("Missing connection policy factory.");

            Assert.That(HeaderTimeout(Create(new WebServerOptions())), Is.EqualTo(30_000));
            Assert.That(HeaderTimeout(Create(new WebServerOptions { MaximumConnections = 10 })), Is.EqualTo(30_000),
                "Other connection limits keep the default first-head deadline.");
            Assert.That(HeaderTimeout(Create(new WebServerOptions { RequestHeadersTimeout = TimeSpan.FromSeconds(90) })), Is.EqualTo(90_000),
                "An explicit deadline replaces the default.");
        }

        // ---------------------------------------------------------------- body bytes

        [Test]
        public async Task DefaultBodyLimitRefusesADeclaredLengthOverThirtyMillionBytes()
        {
            var entered = 0;
            await using var host = Host.Start(_ => { }, context =>
            {
                _ = System.Threading.Interlocked.Increment(ref entered);
                return context.SendStringAsync("dispatched", "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using (var socket = await host.ConnectAsync())
            {
                // No body follows: a refusal never waits for it.
                await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 30000001\r\n\r\n");
                var wire = await socket.ReadToEndAsync();
                Assert.That(wire, Does.StartWith("HTTP/1.1 413"));
                Assert.That(wire, Does.Contain("Connection: close"));
            }
            Assert.That(entered, Is.Zero);
            using (var socket = await host.ConnectAsync())
            {
                await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 30000000\r\n\r\n");
                Assert.That(await socket.ReadHeadAsync(), Does.StartWith("HTTP/1.1 200"), "A body of exactly the limit is dispatched.");
            }
            Assert.That(entered, Is.EqualTo(1));
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task DefaultBodyLimitRefusesAnOversizedBodyWithoutInvitingIt(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, _ => { }, Host.EchoLength, HttpVerbs.Any);
            var content = new TrackingContent(30_000_001);
            using var response = await host.SendAsync(HttpMethod.Post, content, request => request.Headers.ExpectContinue = true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(content.Sent, Is.False);
        }

        [Test]
        public async Task ExplicitNullBodyLimitAcceptsADeclaredLengthOverTheDefault()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(null),
                context => context.SendStringAsync("dispatched", "text/plain", WebServer.Utf8NoBomEncoding), HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 30000001\r\n\r\n");
            Assert.That(await socket.ReadHeadAsync(), Does.StartWith("HTTP/1.1 200"));
        }

        // A body the handler never opened is left to the transport, which ends it after the
        // response (RST_STREAM on HTTP/2, STOP_SENDING on HTTP/3), rather than drained through
        // the counted view first. On HTTP/3 this covers every GET sent without content-length,
        // which cannot be known bodyless at dispatch; on HTTP/2 a POST whose upload stalls.
        [TestCase(2)]
        [TestCase(3)]
        public async Task UnopenedBodyIsNotDrainedThroughTheCountedView(int protocol)
        {
            var closed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await MultiHost.StartAsync(protocol, _ => { }, context =>
            {
                context.OnClose(closing => closed.TrySetResult(PrivateField(closing.Request, "_limitedInput")));
                return context.SendStringAsync("ignored", "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using var stall = new StallingContent(10);
            using (var response = await host.SendAsync(protocol == 3 ? HttpMethod.Get : HttpMethod.Post, protocol == 3 ? null : stall))
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ignored"));
            Assert.That(await closed.Task.WaitAsync(Deadline), Is.Null, "No counted view was built to drain the body.");
        }

        // The server drains an unread HTTP/1 body to reuse the connection. It reads the framing
        // input directly, so a handler that disposed its view of the body, which a read deadline
        // wraps, still leaves the connection reusable.
        [Test]
        public async Task DisposedBodyViewKeepsTheHttp1ConnectionReusable()
        {
            var ports = new System.Collections.Concurrent.ConcurrentQueue<int>();
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(null), async context =>
            {
                ports.Enqueue(context.Request.RemoteEndPoint.Port);
                using (var reader = new StreamReader(context.Request.InputStream)) _ = await reader.ReadLineAsync();
                await context.SendStringAsync("read", "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            for (var i = 0; i < 2; ++i)
            {
                using var response = await client.SendAsync(Post(client, host.Url, new StringContent("line\nrest of the body")));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("read"));
            }
            Assert.That(ports.ToArray().Distinct().Count(), Is.EqualTo(1), "Both requests used one connection.");
        }

        // Under the defaults an HTTP/1 handler reads a view of the body, which is closed once
        // disposed, as HTTP/2 and HTTP/3 bodies always were. The body helpers dispose the request
        // stream, so a second read of one request now fails instead of returning an empty body.
        // Without a body limit or read deadline the handler reads the physical HTTP/1 body, which
        // kept serving reads after disposal; HttpApplicationLifecycleAuditTest pins that.
        [Test]
        public async Task ReadingADisposedHttp1BodyThrowsUnderTheDefaults()
        {
            await using var host = Host.Start(_ => { }, async context =>
            {
                var first = await context.GetRequestBodyAsStringAsync();
                var text = await SecondRead(context.GetRequestBodyAsStringAsync);
                var bytes = await SecondRead(async () => (await context.GetRequestBodyAsByteArrayAsync()).Length.ToString(CultureInfo.InvariantCulture));
                var stream = await SecondRead(async () => (await context.Request.InputStream.ReadAsync(new byte[1])).ToString(CultureInfo.InvariantCulture));
                await context.SendStringAsync(first + "|" + text + "|" + bytes + "|" + stream, "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            using var response = await client.SendAsync(Post(client, host.Url, new StringContent("abc")));
            Assert.That(await response.Content.ReadAsStringAsync(),
                Is.EqualTo("abc|ArgumentException|ObjectDisposedException RawRequestBodyStream|ObjectDisposedException RawRequestBodyStream"));
        }

        private static async Task<string> SecondRead(Func<Task<string>> read)
        {
            try { return "read " + await read(); }
            catch (ObjectDisposedException disposed) { return nameof(ObjectDisposedException) + " " + disposed.ObjectName; }
            catch (ArgumentException failure) { return failure.GetType().Name; }
        }

        // ---------------------------------------------------------------- body read deadline

        // Read from the request the handler sees: the deadline reaches every request that
        // carries a body, and a bodyless request does not pay for either body policy.
        [TestCase(1, "default", 60d)]
        [TestCase(2, "default", 60d)]
        [TestCase(3, "default", 60d)]
        [TestCase(1, "5", 5d)]
        [TestCase(1, "null", null)]
        [TestCase(2, "null", null)]
        public async Task BodyReadDeadlineReachesRequestsThatCarryABody(int protocol, string configured, double? expected)
        {
            await using var host = await MultiHost.StartAsync(protocol, o =>
            {
                if (configured == "null") o.RequestBodyReadTimeout = null;
                else if (configured != "default") o.RequestBodyReadTimeout = TimeSpan.FromSeconds(double.Parse(configured, CultureInfo.InvariantCulture));
            }, async context =>
            {
                var request = context.Request;
                var policy = PrivateField(request, "_bodyPolicy") != null;
                var timeout = (TimeSpan?)PrivateField(request, "_readTimeout");
                await request.InputStream.CopyToAsync(Stream.Null);
                await context.SendStringAsync(policy + "|" + timeout?.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using (var post = await host.SendAsync(HttpMethod.Post, new ByteArrayContent(new byte[10])))
            {
                Assert.That(await post.Content.ReadAsStringAsync(),
                    Is.EqualTo("True|" + expected?.ToString(CultureInfo.InvariantCulture)));
            }
            // An HTTP/3 request without content-length cannot tell at dispatch whether a body follows.
            if (protocol == 3) return;
            using var get = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(await get.Content.ReadAsStringAsync(), Is.EqualTo("False|"), "A bodyless request carries neither body policy.");
        }

        // ---------------------------------------------------------------- header fields

        // HTTP/1 counts Host and Connection like any field.
        [TestCase(100, "200")]
        [TestCase(101, "431")]
        public async Task DefaultHeaderCountAcceptsOneHundredFields(int fields, string status)
        {
            await using var host = Host.Start(_ => { }, context => context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding));
            using var socket = await host.ConnectAsync();
            await socket.SendAsync(Http1Head(host.Authority, fields));
            Assert.That(await socket.ReadToEndAsync(), Does.StartWith("HTTP/1.1 " + status));
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task DefaultHeaderCountAppliesToEveryProtocol(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, _ => { },
                context => context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding));
            using (var over = await host.SendAsync(HttpMethod.Get, null, request => AddFields(request, 101)))
                Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.RequestHeaderFieldsTooLarge));
            using var under = await host.SendAsync(HttpMethod.Get, null, request => AddFields(request, 90));
            Assert.That(await under.Content.ReadAsStringAsync(), Is.EqualTo("handled"));
        }

        [TestCase(null, 150, "200")]
        [TestCase(120, 120, "200")]
        [TestCase(120, 121, "431")]
        public async Task ExplicitHeaderCountReplacesTheDefault(int? limit, int fields, string status)
        {
            await using var host = Host.Start(o => o.MaximumRequestHeaderCount = limit,
                context => context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding));
            using var socket = await host.ConnectAsync();
            await socket.SendAsync(Http1Head(host.Authority, fields));
            Assert.That(await socket.ReadToEndAsync(), Does.StartWith("HTTP/1.1 " + status));
        }

        // ---------------------------------------------------------------- decoded bytes

        // 5000 zero bytes compress to a few dozen, so every case passes the transferred limit
        // and only the decoded limit decides.
        [Test]
        public async Task DecompressedLimitFollowsTheEffectiveBodyLimit()
        {
            byte[] body;
            using (var encoded = new MemoryStream())
            {
                using (var gzip = new GZipStream(encoded, CompressionMode.Compress, true)) gzip.Write(new byte[5000], 0, 5000);
                body = encoded.ToArray();
            }
            await using (var host = Host.Start(o => o.WithSupportCompressedRequests(true).WithMaximumRequestBodyBytes(1000)
                .WithRouteRequestLimits("/lifted", p => p.MaximumRequestBodyBytes = long.MaxValue)
                .WithRouteRequestLimits("/tight", p => p.MaximumRequestBodyBytes = 100), DecodedLength, HttpVerbs.Any))
            {
                using var client = Host.CreateClient(1);
                Assert.That(await PostGzipAsync(client, host.Url, body), Is.EqualTo("413"), "The server's body limit bounds decoded bytes.");
                Assert.That(await PostGzipAsync(client, host.Url + "lifted", body), Is.EqualTo("5000"), "Lifting a route's body limit lifts its decoded limit.");
                Assert.That(await PostGzipAsync(client, host.Url + "tight", body), Is.EqualTo("413"), "A route's body limit bounds its decoded bytes.");
            }
            await using (var host = Host.Start(o => o.WithSupportCompressedRequests(true).WithMaximumRequestBodyBytes(1000)
                .WithMaximumDecompressedRequestBodyBytes(null), DecodedLength, HttpVerbs.Any))
            {
                using var client = Host.CreateClient(1);
                Assert.That(await PostGzipAsync(client, host.Url, body), Is.EqualTo("5000"), "An explicit null keeps decoding unlimited.");
            }
        }

        // ---------------------------------------------------------------- helpers

        private static object? PrivateField(object target, string name)
            => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException($"Missing request field {name}.")).GetValue(target);

        private static string Http1Head(string authority, int fields)
        {
            var head = new StringBuilder("GET / HTTP/1.1\r\nHost: ").Append(authority).Append("\r\nConnection: close\r\n");
            for (var i = 2; i < fields; ++i) head.Append("X-Field-").Append(i).Append(": v\r\n");
            return head.Append("\r\n").ToString();
        }

        private static void AddFields(HttpRequestMessage request, int fields)
        {
            for (var i = 0; i < fields; ++i) request.Headers.Add("X-Field-" + i, "v");
        }

        private static async Task DecodedLength(IHttpContext context)
        {
            using var input = context.OpenRequestStream();
            var total = 0L;
            var buffer = new byte[4096];
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, context.CancellationToken)) > 0) total += read;
            await context.SendStringAsync(total.ToString(CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
        }

        private static async Task<string> PostGzipAsync(HttpClient client, string url, byte[] body)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentEncoding.Add("gzip");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(Post(client, url, content));
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync()
                : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
    }
}
