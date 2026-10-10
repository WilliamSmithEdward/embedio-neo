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
    public sealed class ResponseSectionLifetimeTest
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task DelayedTrailersOrClientAbortLeaveTheServerUsable(bool http2, bool abort)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/healthy")
                {
                    context.Response.ContentLength64 = 1;
                    await context.Response.OutputStream.WriteAsync(new byte[] { 9 }, context.CancellationToken);
                    return;
                }

                try
                {
                    var sections = context.Response as IHttpResponseSections
                        ?? throw new AssertionException("Missing response capability.");
                    if (http2) context.Response.ContentLength64 = abort ? 6 : 3;
                    sections.DeclareTrailers("x-complete");
                    await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 }, context.CancellationToken);
                    if (abort && http2)
                    {
                        await Assert.ThatAsync(() => Task.Delay(Timeout.Infinite, context.CancellationToken),
                            Throws.InstanceOf<OperationCanceledException>());
                    }
                    else
                    {
                        await release.Task.WaitAsync(deadline.Token);
                        if (!abort) sections.SetTrailers(new WebHeaderCollection { ["x-complete"] = "yes" });
                    }
                    finished.TrySetResult();
                }
                catch (Exception exception)
                {
                    finished.TrySetException(exception);
                    throw;
                }
            });
            var running = server.RunAsync(deadline.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler
                { UseProxy = false, MaxConnectionsPerServer = 1, MaxResponseDrainSize = 0 });
                using var request = Request(url, http2);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                var prefix = new byte[3];
                await body.ReadExactlyAsync(prefix, deadline.Token);
                Assert.That(prefix, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(response.TrailingHeaders.Contains("x-complete"), Is.False);
                Assert.That(finished.Task.IsCompleted, Is.False);
                if (abort)
                {
                    response.Dispose();
                    // HTTP/1 has no stream-reset notification while the handler is idle.
                    // Let it finish, then verify cleanup preserves service for other clients.
                    if (!http2) release.TrySetResult();
                }
                else
                {
                    release.TrySetResult();
                    Assert.That(await body.ReadAsync(new byte[1], deadline.Token), Is.Zero);
                    Assert.That(response.TrailingHeaders.GetValues("x-complete"), Is.EqualTo(new[] { "yes" }));
                }
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var healthyRequest = Request(url + "healthy", http2);
                using var healthy = await client.SendAsync(healthyRequest, deadline.Token);
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await healthy.Content.ReadAsByteArrayAsync(deadline.Token), Is.EqualTo(new byte[] { 9 }));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                release.TrySetResult();
                deadline.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static HttpRequestMessage Request(string url, bool http2)
            => new(HttpMethod.Get, url)
            {
                Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
    }
}
