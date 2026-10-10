using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class Http1ResponseTrailersTest
    {
        [TestCase(0)]
        [TestCase(3)]
        [TestCase(65537)]
        public async Task DeclaredChunkedTrailersFollowTheBodyAndKeepAliveWorks(int size)
        {
            var expected = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                var sections = context.Response as IHttpResponseSections ?? throw new AssertionException("Missing optional response capability.");
                sections.DeclareTrailers("X-Body-Check", "X-Finished");
                if (expected.Length != 0) await context.Response.OutputStream.WriteAsync(expected, stop.Token);
                var undeclared = new WebHeaderCollection { ["X-Undeclared"] = "forbidden" };
                Assert.Throws<System.IO.InvalidDataException>(() => sections.SetTrailers(undeclared));
                var trailers = new WebHeaderCollection { ["X-Body-Check"] = "verified", ["X-Finished"] = "yes" };
                sections.SetTrailers(trailers);
                trailers["X-Finished"] = "caller-mutated";
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
                using var client = new HttpClient(handler);
                for (var i = 0; i < 3; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url)
                    { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    request.Headers.TE.ParseAdd("trailers");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Headers.TransferEncodingChunked, Is.True);
                    Assert.That(response.Content.Headers.ContentLength, Is.Null);
                    Assert.That(await response.Content.ReadAsByteArrayAsync(stop.Token), Is.EqualTo(expected));
                    Assert.That(response.Headers.Contains("x-body-check"), Is.False);
                    Assert.That(response.TrailingHeaders.GetValues("x-body-check"), Is.EqualTo(new[] { "verified" }));
                    Assert.That(response.TrailingHeaders.GetValues("x-finished"), Is.EqualTo(new[] { "yes" }));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
