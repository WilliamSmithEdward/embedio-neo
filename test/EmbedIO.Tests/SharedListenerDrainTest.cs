using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class SharedListenerDrainTest
    {
        [TestCase(false, "complete", false)]
        [TestCase(false, "complete", true)]
        [TestCase(true, "complete", false)]
        [TestCase(true, "complete", true)]
        [TestCase(false, "deadline", false)]
        [TestCase(false, "deadline", true)]
        [TestCase(true, "deadline", false)]
        [TestCase(true, "deadline", true)]
        [TestCase(false, "cancel", false)]
        [TestCase(false, "cancel", true)]
        [TestCase(true, "cancel", false)]
        [TestCase(true, "cancel", true)]
        [TestCase(false, "stop", false)]
        [TestCase(false, "stop", true)]
        [TestCase(true, "stop", false)]
        [TestCase(true, "stop", true)]
        [TestCase(false, "dispose", false)]
        [TestCase(false, "dispose", true)]
        [TestCase(true, "dispose", false)]
        [TestCase(true, "dispose", true)]
        public async Task PublicDrainPreservesSiblingTraffic(bool http2, string ending, bool mixedEndpoints)
        {
            var prefix = Resources.GetServerAddress();
            var exclusivePrefix = Resources.GetServerAddress();
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var cancelDrain = new CancellationTokenSource();
            var entered = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exclusiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingEntered = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerCalls = 0;
            using var owner = new WebServer(HttpListenerMode.EmbedIO, mixedEndpoints ? new[] { prefix + "owner/", exclusivePrefix } : new[] { prefix + "owner/" })
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    Interlocked.Increment(ref ownerCalls);
                    if (context.Request.Url.Port == new Uri(exclusivePrefix).Port) exclusiveEntered.TrySetResult();
                    else entered.TrySetResult(context.RemoteEndPoint);
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("owner", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var sibling = new WebServer(HttpListenerMode.EmbedIO, prefix + "sibling/")
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    siblingEntered.TrySetResult(context.RemoteEndPoint);
                    await siblingRelease.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync(context.RemoteEndPoint.ToString(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            var running = owner.RunAsync(lifetime.Token);
            var otherRunning = sibling.RunAsync(lifetime.Token);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(10),
            };
            var request = client.GetStringAsync(prefix + "owner/", lifetime.Token);
            // On abort, inspect the original HTTP/1 transport directly. HttpClient
            // may retry a reset connection before reporting the already completed stop.
            var rawExclusiveAbort = mixedEndpoints && !http2 && ending != "complete";
            var exclusiveRequest = mixedEndpoints && !rawExclusiveAbort ? client.GetStringAsync(exclusivePrefix, lifetime.Token) : Task.FromResult("owner");
            var exclusiveAbort = rawExclusiveAbort ? Http1AbortProbe.AssertClosedWithoutResponseAsync(exclusivePrefix) : Task.CompletedTask;
            try
            {
                if (mixedEndpoints) await exclusiveEntered.Task.WaitAsync(lifetime.Token);
                var ownerPeer = await entered.Task.WaitAsync(lifetime.Token);
                var other = client.GetStringAsync(prefix + "sibling/", lifetime.Token);
                var siblingPeer = await siblingEntered.Task.WaitAsync(lifetime.Token);
                if (http2) Assert.That(siblingPeer, Is.EqualTo(ownerPeer));
                var drain = owner.DrainAsync(ending == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5), cancelDrain.Token);
                Assert.That(drain.IsCompleted, Is.False);
                if (ending == "complete")
                {
                    // While the accepted response is pending, new owner traffic must
                    // be refused instead of joining the application's drain set.
                    await Assert.ThatAsync(async () => await client.GetStringAsync(prefix + "owner/late", lifetime.Token), Throws.InstanceOf<HttpRequestException>());
                    Assert.That(ownerCalls, Is.EqualTo(mixedEndpoints ? 2 : 1));
                    release.TrySetResult();
                    Assert.That(await request, Is.EqualTo("owner"));
                    Assert.That(await exclusiveRequest, Is.EqualTo("owner"));
                }
                else
                {
                    if (ending == "cancel") cancelDrain.Cancel();
                    if (ending == "stop") owner.Listener.Stop();
                    if (ending == "dispose") owner.Dispose();
                    await Assert.ThatAsync(async () => await request, Throws.InstanceOf<HttpRequestException>());
                    if (rawExclusiveAbort) await exclusiveAbort.WaitAsync(lifetime.Token);
                    else if (mixedEndpoints)
                        await Assert.ThatAsync(async () => await exclusiveRequest, Throws.InstanceOf<HttpRequestException>());
                }
                if (ending == "cancel")
                    await Assert.ThatAsync(async () => await drain.WaitAsync(lifetime.Token), Throws.InstanceOf<OperationCanceledException>());
                else await drain.WaitAsync(lifetime.Token);
                await running.WaitAsync(lifetime.Token);
                Assert.That(owner.Listener.IsListening, Is.False);
                Assert.That(sibling.State, Is.EqualTo(WebServerState.Listening));
                Assert.That(other.IsCompleted, Is.False);
                siblingRelease.TrySetResult();
                Assert.That(await other, Is.EqualTo(siblingPeer.ToString()));
                var healthy = await client.GetStringAsync(prefix + "sibling/after", lifetime.Token);
                if (http2) Assert.That(healthy, Is.EqualTo(siblingPeer.ToString()));
                else Assert.That(healthy, Is.Not.Empty);
            }
            finally
            {
                release.TrySetResult(); siblingRelease.TrySetResult(); lifetime.Cancel();
                try { await request; } catch (HttpRequestException) { } catch (OperationCanceledException) { }
                try { await exclusiveRequest; } catch (HttpRequestException) { } catch (OperationCanceledException) { }
                await exclusiveAbort.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(running, otherRunning).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
