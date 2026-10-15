using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QueryPreconditionWireTest
    {
        internal static async Task Respond(IHttpContext context)
        {
            var body = await context.GetRequestBodyAsStringAsync();
            var json = context.Request.Headers[HttpHeaderNames.Accept] == "application/json";
            var type = json ? "application/json" : "text/plain";
            var content = json ? JsonSerializer.Serialize(body) : "result:" + body;
            var tag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body + "|" + type))) + "\"";
            var modified = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
            context.Response.Headers[HttpHeaderNames.ETag] = tag;
            context.Response.Headers[HttpHeaderNames.LastModified] = modified.ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            var status = context.Request.EvaluatePreconditions(tag, modified);
            if (status.HasValue) { context.Response.StatusCode = (int)status.GetValueOrDefault(); return; }
            await context.SendStringAsync(content, type, WebServer.Utf8NoBomEncoding);
        }
        internal static async Task Verify(HttpClient client, string url, Version version, CancellationToken token)
        {
            async Task<HttpResponseMessage> Send(string body, string? none, string? match, bool json)
            {
                using var request = new HttpRequestMessage(new HttpMethod("QUERY"), url)
                {
                    Version = version,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent(body, WebServer.Utf8NoBomEncoding, "text/plain")
                };
                if (none != null) request.Headers.TryAddWithoutValidation("If-None-Match", none);
                if (match != null) request.Headers.TryAddWithoutValidation("If-Match", match);
                if (json) request.Headers.TryAddWithoutValidation("Accept", "application/json");
                return await client.SendAsync(request, token);
            }
            using var first = await Send("one", null, null, false);
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var tag = first.Headers.ETag?.ToString() ?? throw new AssertionException("Missing selected-result tag.");
            using var unchanged = await Send("one", "W/" + tag, null, false);
            Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(unchanged.Version, Is.EqualTo(version));
            Assert.That(await unchanged.Content.ReadAsByteArrayAsync(token), Is.Empty);
            Assert.That(unchanged.Headers.ETag?.ToString(), Is.EqualTo(tag));
            using var different = await Send("two", tag, null, false);
            Assert.That(different.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await different.Content.ReadAsStringAsync(token), Is.EqualTo("result:two"));
            using var negotiated = await Send("one", tag, null, true);
            Assert.That(negotiated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(negotiated.Headers.ETag?.ToString(), Is.Not.EqualTo(tag));
            using var failed = await Send("one", tag, "\"other\"", false);
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
            Assert.That(await failed.Content.ReadAsByteArrayAsync(token), Is.Empty);
            using var healthy = await Send("one", null, tag, false);
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        public async Task QueryValidatorsDescribeResultsAndNegotiation(HttpListenerMode mode, int protocol)
        {
            if (protocol == 2 && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Connection") == null)
                Assert.Ignore("The legacy asset has no HTTP/2 transport.");
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(mode, url).WithAction("/", HttpVerbs.Query, Respond);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                await Verify(client, url, protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11, stop.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    public partial class Http3ListenerTest
    {
        [Test]
        public async Task QueryPreconditionsUseEquivalentResultOnHttp3()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Query, QueryPreconditionWireTest.Respond);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try { await QueryPreconditionWireTest.Verify(client, prefix, HttpVersion.Version30, stop.Token); }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
