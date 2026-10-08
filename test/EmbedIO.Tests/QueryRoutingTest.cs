using System.IO;
using System.Net.Sockets;
using System;
using System.Threading;
using EmbedIO.PlatformTests;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QueryRoutingTest
    {
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", HttpStatusCode.OK, "text/plain")]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", HttpStatusCode.OK, "text/plain")]
        [TestCase(HttpListenerMode.EmbedIO, "query", HttpStatusCode.NotFound, "text/plain")]
        [TestCase(HttpListenerMode.Microsoft, "query", HttpStatusCode.NotFound, "text/plain")]
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", HttpStatusCode.BadRequest, null)]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", HttpStatusCode.BadRequest, null)]
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", HttpStatusCode.BadRequest, "text")]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", HttpStatusCode.BadRequest, "text")]
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", HttpStatusCode.BadRequest, "text/plain, application/json")]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", HttpStatusCode.BadRequest, "text/plain, application/json")]
        public async Task RealListenerRoutesExactQuery(HttpListenerMode mode, string method, HttpStatusCode expected, string? mediaType)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(mode, url).WithAction("/", HttpVerbs.Query, async context =>
            {
                Assert.That(context.Request.HttpMethod, Is.EqualTo("QUERY"));
                await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", Encoding.UTF8);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                var wire = method + " / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n"
                    + (mediaType != null ? "Content-Type: " + mediaType + "\r\n" : string.Empty)
                    + "Content-Length: 10\r\n\r\nquery-body";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wire), stop.Token);
                using var received = new MemoryStream();
                await client.GetStream().CopyToAsync(received, stop.Token);
                var response = Encoding.ASCII.GetString(received.ToArray());
                Assert.That(response, Does.StartWith("HTTP/1.1 " + (int)expected + " "));
                if (expected == HttpStatusCode.OK)
                    Assert.That(response, Does.Contain("query-body"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Http2QueryRouteReceivesContent(bool mediaType)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Query,
                async context =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version20));
                    await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", Encoding.UTF8);
                });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var request = new HttpRequestMessage(new HttpMethod("QUERY"), url)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent("multiplexed-query", Encoding.UTF8, "text/plain"),
                };
                if (!mediaType) request.Content.Headers.Remove("Content-Type");
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(mediaType ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                if (mediaType)
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("multiplexed-query"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase("QUERY", null, HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", " \t", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "text", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "text/plain, application/json", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "text/plain; charset=", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "text/plain; note=\"unterminated", HttpStatusCode.BadRequest)]
        [TestCase("QUERY", "application/vnd.example+json; version=2", HttpStatusCode.OK)]
        [TestCase("QUERY", "text/plain; note=\"a,b\"", HttpStatusCode.OK)]
        [TestCase("QUERY", "text/plain", HttpStatusCode.OK)]
        [TestCase("POST", null, HttpStatusCode.OK)]
        [TestCase("query", null, HttpStatusCode.OK)]
        public Task QueryRequiresMediaTypeBeforeDispatch(string method, string? mediaType, HttpStatusCode expected)
        {
            var calls = 0;
            return TestWebServer.UseAsync(
                server => server.WithAction("/", HttpVerbs.Any, context =>
                {
                    calls++;
                    return context.SendStringAsync("handled", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/");
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("query"));
                    if (mediaType != null)
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    Assert.That(calls, Is.EqualTo(expected == HttpStatusCode.OK ? 1 : 0));
                });
        }

        [TestCase("QUERY", HttpStatusCode.OK)]
        [TestCase("POST", HttpStatusCode.NotFound)]
        [TestCase("query", HttpStatusCode.NotFound)]
        public Task QueryActionKeepsMethodAndBodyDistinct(string method, HttpStatusCode expected)
            => TestWebServer.UseAsync(
                server => server.WithAction("/", HttpVerbs.Query, async context =>
                {
                    Assert.That(context.Request.HttpMethod, Is.EqualTo("QUERY"));
                    var body = await context.GetRequestBodyAsStringAsync();
                    context.Response.Headers["Accept-Query"] = "text/plain";
                    await context.SendStringAsync("result:" + body, "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/");
                    request.Content = new StringContent("find=example", Encoding.UTF8, "text/plain");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    if (expected == HttpStatusCode.OK)
                    {
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("result:find=example"));
                        Assert.That(response.Headers.GetValues("Accept-Query"), Is.EqualTo(new[] { "text/plain" }));
                    }
                });
    }
}
