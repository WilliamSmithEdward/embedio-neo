using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class Http1ResponseTrailersTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Invoke(IHttpResponse response, string method, object argument)
            => (response.GetType().GetMethod(method, Flags) ?? throw new AssertionException("Missing internal trailer capability."))
                .Invoke(response, new[] { argument });

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
                Invoke(context.Response, "PrepareTrailers", new[] { "X-Body-Check", "X-Finished" });
                if (expected.Length != 0) await context.Response.OutputStream.WriteAsync(expected, stop.Token);
                var undeclared = new WebHeaderCollection { ["X-Undeclared"] = "forbidden" };
                var failure = Assert.Throws<TargetInvocationException>(() => Invoke(context.Response, "SetTrailers", undeclared));
                Assert.That(failure?.InnerException, Is.InstanceOf<System.IO.InvalidDataException>());
                var trailers = new WebHeaderCollection { ["X-Body-Check"] = "verified", ["X-Finished"] = "yes" };
                Invoke(context.Response, "SetTrailers", trailers);
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
