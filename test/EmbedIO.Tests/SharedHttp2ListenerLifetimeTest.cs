using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class SharedHttp2ListenerLifetimeTest
    {
        private static async Task ObserveStoppedListenerAsync(Task running)
        {
            try { await running; }
            catch (HttpListenerException error) when (error.ErrorCode == 995) { }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task StoppingOneOwnerPreservesSiblingStreamsAndConnection(bool dispose, bool completedOwner)
        {
            var prefix = Resources.GetServerAddress();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ownerEntered = new TaskCompletionSource<(IPEndPoint Peer, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingEntered = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingCalls = 0;
            using var owner = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    ownerEntered.TrySetResult((context.RemoteEndPoint, context.CancellationToken));
                    await releaseOwner.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("owner", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var sibling = new WebServer(HttpListenerMode.EmbedIO, prefix + "sibling/")
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    Interlocked.Increment(ref siblingCalls);
                    siblingEntered.TrySetResult(context.RemoteEndPoint);
                    await releaseSibling.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync(context.RemoteEndPoint.ToString(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            var ownerRun = ObserveStoppedListenerAsync(owner.RunAsync(deadline.Token));
            var siblingRun = sibling.RunAsync(deadline.Token);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(10),
            };
            var ownerRequest = client.GetStringAsync(prefix, deadline.Token);
            try
            {
                var first = await ownerEntered.Task.WaitAsync(deadline.Token);
                if (completedOwner)
                {
                    releaseOwner.TrySetResult();
                    Assert.That(await ownerRequest, Is.EqualTo("owner"));
                }
                var siblingRequest = client.GetStringAsync(prefix + "sibling/", deadline.Token);
                var peer = await siblingEntered.Task.WaitAsync(deadline.Token);
                Assert.That(peer, Is.EqualTo(first.Peer), "Both owners must use the same HTTP/2 connection.");
                if (dispose) owner.Dispose();
                else owner.Listener.Stop();
                await ownerRun.WaitAsync(TimeSpan.FromSeconds(5));
                if (!completedOwner)
                {
                    await Assert.ThatAsync(async () => await ownerRequest, Throws.InstanceOf<HttpRequestException>());
                    Assert.That(first.Token.IsCancellationRequested, Is.True);
                }
                Assert.That(sibling.State, Is.EqualTo(WebServerState.Listening));
                releaseSibling.TrySetResult();
                Assert.That(await siblingRequest, Is.EqualTo(peer.ToString()));
                Assert.That(siblingCalls, Is.EqualTo(1), "A client retry must not hide a terminated sibling stream.");
                Assert.That(await client.GetStringAsync(prefix + "sibling/after", deadline.Token), Is.EqualTo(peer.ToString()),
                    "The surviving owner must keep the original connection usable.");
            }
            finally
            {
                releaseOwner.TrySetResult(); releaseSibling.TrySetResult(); deadline.Cancel();
                try { await ownerRequest; } catch (HttpRequestException) { } catch (OperationCanceledException) { }
                await Task.WhenAll(ownerRun, siblingRun).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
