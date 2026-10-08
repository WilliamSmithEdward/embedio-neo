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
        [TestCase(HttpListenerMode.EmbedIO, "QUERY", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.Microsoft, "QUERY", HttpStatusCode.OK)]
        [TestCase(HttpListenerMode.EmbedIO, "query", HttpStatusCode.NotFound)]
        [TestCase(HttpListenerMode.Microsoft, "query", HttpStatusCode.NotFound)]
        public async Task RealListenerRoutesExactQuery(HttpListenerMode mode, string method, HttpStatusCode expected)
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
                    + "Content-Type: text/plain\r\nContent-Length: 10\r\n\r\nquery-body";
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

        [Test]
        public async Task Http2QueryRouteReceivesContent()
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
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("multiplexed-query"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
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
