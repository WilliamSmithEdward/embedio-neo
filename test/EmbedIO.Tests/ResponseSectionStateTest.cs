using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class ResponseSectionStateTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RejectedSectionOperationsPreserveAHealthyResponseAndItsSnapshot(bool http2)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                var sections = context.Response as IHttpResponseSections ?? throw new AssertionException("Missing response capability.");
                await Assert.ThatAsync(() => sections.SendInformationalAsync(103, new WebHeaderCollection(), new CancellationToken(true)),
                    Throws.InstanceOf<OperationCanceledException>());
                Assert.Throws<InvalidOperationException>(() => sections.SetTrailers(new WebHeaderCollection { ["x-valid"] = "early" }));
                sections.DeclareTrailers("x-valid");
                sections.SetTrailers(new WebHeaderCollection { ["x-valid"] = "retained" });
                Assert.Throws<InvalidDataException>(() => sections.DeclareTrailers("content-length"));
                Assert.Throws<InvalidDataException>(() => sections.SetTrailers(new WebHeaderCollection { ["x-other"] = "undeclared" }));
                Assert.Throws<InvalidDataException>(() => sections.SetTrailers(new WebHeaderCollection { ["x-valid"] = new string('a', 32768) }));
                Assert.Throws<InvalidOperationException>(() => context.Response.StatusCode = 204);
                Assert.That(context.Response.StatusCode, Is.EqualTo(200));
                if (!http2)
                {
                    Assert.Throws<InvalidOperationException>(() => context.Response.ContentLength64 = 3);
                    Assert.Throws<InvalidOperationException>(() => context.Response.SendChunked = false);
                }
                else context.Response.ContentLength64 = 3;
                await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 }, context.CancellationToken);
                Assert.Throws<InvalidOperationException>(() => sections.DeclareTrailers("x-late"));
                await Assert.ThatAsync(() => sections.SendInformationalAsync(103, new WebHeaderCollection(), context.CancellationToken),
                    Throws.InstanceOf<InvalidOperationException>());
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 });
                for (var i = 0; i < 3; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url)
                    { Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(stop.Token), Is.EqualTo(new byte[] { 1, 2, 3 }));
                    Assert.That(response.TrailingHeaders.GetValues("x-valid"), Is.EqualTo(new[] { "retained" }));
                }
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
