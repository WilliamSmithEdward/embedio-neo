using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QueryFormatPolicyTest
    {
        [TestCase("application/json", "application/json", true)]
        [TestCase("application/json", "APPLICATION/JSON; charset=utf-8", true)]
        [TestCase("application/json", "text/plain", false)]
        [TestCase("application/*", "application/sql", true)]
        [TestCase("application/*", "text/plain", false)]
        [TestCase("*/*", "text/plain", true)]
        [TestCase("*/*", "application/*", false)]
        [TestCase("*/*", "*/json", false)]
        [TestCase("*/*", "text", false)]
        [TestCase("*/*", null, false)]
        [TestCase("application/sql;charset=utf-8", "application/sql;CHARSET=\"UTF-8\";x=y", true)]
        [TestCase("application/sql;charset=utf-8", "application/sql;charset=utf-16", false)]
        [TestCase("application/sql;charset=utf-8", "application/sql", false)]
        [TestCase("application/sql;profile=abc", "application/sql;profile=ABC", false)]
        [TestCase("application/sql;profile=abc", "application/sql;profile=\"abc\"", true)]
        [TestCase("application/sql;profile=\"a\\\"b\"", "application/sql;profile=\"a\\\"b\"", true)]
        [TestCase("*/*", "text/plain;charset=utf-8;Charset=utf-16", false)]
        [TestCase("*/*", "text/plain;charset", false)]
        [TestCase("3type/9sub", "3type/9sub", true)]
        public void MediaRangesAndParameterConstraints(string supported, string? actual, bool expected)
            => Assert.That(new QueryFormatPolicy(supported).IsSupported(actual), Is.EqualTo(expected));

        [TestCase("")]
        [TestCase("text")]
        [TestCase("text/plain, application/json")]
        [TestCase("*/json")]
        [TestCase("app*/json")]
        [TestCase("application/j*son")]
        [TestCase("text/plain;charset")]
        [TestCase("text/plain;charset=utf-8;CHARSET=utf-16")]
        [TestCase("text/plain;3x=y")]
        [TestCase("text/plain;x=\"ü\"")]
        [TestCase("text/plain;\r\nx=y")]
        public void InvalidOrUnrepresentableDiscoveryRangesAreRejected(string value)
            => Assert.Throws<ArgumentException>(() => _ = new QueryFormatPolicy(value));

        [Test]
        public void DiscoveryIsStructuredAndConfigurationIsOwned()
        {
            var values = new[] { "APPLICATION/JSON", "application/sql;CHARSET=UTF-8;version=1" };
            var policy = new QueryFormatPolicy(values);
            values[0] = "text/plain";
            Assert.That(policy.AcceptQueryHeaderValue, Is.EqualTo("\"application/json\", \"application/sql\";charset=\"UTF-8\";version=\"1\""));
            Assert.That(policy.IsSupported("application/json"), Is.True);
            Assert.That(policy.IsSupported("text/plain"), Is.False);
        }
        [Test]
        public void QuotedStringsEscapeQuotesAndBackslashes()
        {
            var policy = new QueryFormatPolicy("application/sql;profile=\"a\\\"b\\\\c\"");
            Assert.That(policy.AcceptQueryHeaderValue, Is.EqualTo("\"application/sql\";profile=\"a\\\"b\\\\c\""));
        }
        [Test]
        public void EmptyConfigurationIsRejected()
            => Assert.Throws<ArgumentException>(() => _ = new QueryFormatPolicy(Array.Empty<string>()));

        [TestCase(HttpListenerMode.EmbedIO, "GET", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "GET", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "HEAD", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "HEAD", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "OPTIONS", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "OPTIONS", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "POST", "application/json", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "POST", "application/json", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "query", "application/json", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "query", "application/json", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", "text/plain", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", "application/json", HttpStatusCode.UnsupportedMediaType)]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", "application/json", HttpStatusCode.UnsupportedMediaType)]
        public async Task Http1DiscoveryAndUnsupportedMediaAreResourceScoped(HttpListenerMode mode, string method, string mediaType, HttpStatusCode expected)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var policy = new QueryFormatPolicy("text/plain;charset=utf-8");
            var calls = 0;
            using var server = new WebServer(mode, url).OnAny(async context =>
            {
                policy.Apply(context);
                calls++;
                await context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                if (method == "query")
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                    var wire = "query /?one=two HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 5\r\n\r\nquery";
                    await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wire), stop.Token);
                    using var output = new MemoryStream();
                    await socket.GetStream().CopyToAsync(output, stop.Token);
                    var response = Encoding.ASCII.GetString(output.ToArray());
                    Assert.That(response, Does.StartWith("HTTP/1.1 200 "));
                    Assert.That(response, Does.Contain("Accept-Query: " + policy.AcceptQueryHeaderValue + "\r\n"));
                    Assert.That(calls, Is.EqualTo(1));
                }
                else
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), url + "?one=two");
                    if (method is "QUERY" or "POST") request.Content = new StringContent("query", Encoding.UTF8, mediaType);
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    Assert.That(response.Headers.GetValues(HttpHeaderNames.AcceptQuery), Is.EqualTo(new[] { policy.AcceptQueryHeaderValue }));
                    Assert.That(calls, Is.EqualTo(expected == HttpStatusCode.OK ? 1 : 0));
                    if (expected == HttpStatusCode.UnsupportedMediaType)
                        Assert.That(response.Headers.GetValues(HttpHeaderNames.Accept), Is.EqualTo(new[] { policy.AcceptHeaderValue }));
                }
                using var healthy = await client.GetAsync(url + "?different=component", stop.Token);
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(healthy.Headers.GetValues(HttpHeaderNames.AcceptQuery), Is.EqualTo(new[] { policy.AcceptQueryHeaderValue }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase("text/plain", HttpStatusCode.OK)]
        [TestCase("application/json", HttpStatusCode.UnsupportedMediaType)]
        public async Task Http2QueryPolicyPreservesExactProtocol(string mediaType, HttpStatusCode expected)
        {
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Connection") == null)
                Assert.Ignore("The legacy asset has no HTTP/2 transport.");
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var policy = new QueryFormatPolicy("text/plain;charset=utf-8");
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Query, async context =>
            {
                policy.Apply(context);
                await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", WebServer.Utf8NoBomEncoding);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var request = new HttpRequestMessage(new HttpMethod("QUERY"), url)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent("query", Encoding.UTF8, mediaType)
                };
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(response.StatusCode, Is.EqualTo(expected));
                Assert.That(response.Headers.GetValues(HttpHeaderNames.AcceptQuery), Is.EqualTo(new[] { policy.AcceptQueryHeaderValue }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
