using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class SharedHttp2ListenerLifetimeTest
    {
        [TestCase("complete")]
        [TestCase("stop")]
        [TestCase("dispose")]
        [TestCase("peer-reset")]
        public async Task OwnerDrainWaitsOnlyForItsAcceptedHttp2Response(string ending)
        {
            var prefix = Resources.GetServerAddress();
            using var owner = new Net.HttpListener();
            using var sibling = new Net.HttpListener();
            owner.AddPrefix(prefix); sibling.AddPrefix(prefix + "sibling/");
            owner.Start(); sibling.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var cancelOwner = new CancellationTokenSource();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(8),
            };
            var firstRequest = client.GetStringAsync(prefix, cancelOwner.Token);
            var first = await owner.GetContextAsync(deadline.Token);
            var siblingRequest = client.GetStringAsync(prefix + "sibling/", deadline.Token);
            var second = await sibling.GetContextAsync(deadline.Token);
            Assert.That(second.RemoteEndPoint, Is.EqualTo(first.RemoteEndPoint));
            var connections = (IDictionary)(typeof(Net.HttpListener).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new AssertionException("Missing connection ownership table."));
            var connection = connections.Keys.Cast<object>().Single();
            var drain = (Task)(connection.GetType().GetMethod("DrainForListenerAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(connection, new object[] { owner })
                ?? throw new AssertionException("Missing owner drain operation."));
            Assert.That(drain.IsCompleted, Is.False);
            if (ending == "complete")
            {
                await first.SendStringAsync("owner", "text/plain", WebServer.Utf8NoBomEncoding);
                first.Close();
                Assert.That(await firstRequest, Is.EqualTo("owner"));
            }
            else
            {
                if (ending == "stop") owner.Stop();
                else if (ending == "dispose") owner.Dispose();
                else cancelOwner.Cancel();
                if (ending == "peer-reset")
                    await Assert.ThatAsync(async () => await firstRequest, Throws.InstanceOf<OperationCanceledException>());
                else await Assert.ThatAsync(async () => await firstRequest, Throws.InstanceOf<HttpRequestException>());
            }
            await drain.WaitAsync(deadline.Token);
            Assert.That(siblingRequest.IsCompleted, Is.False, "Owner completion must not require or terminate the sibling response.");
            await second.SendStringAsync("sibling", "text/plain", WebServer.Utf8NoBomEncoding);
            second.Close();
            Assert.That(await siblingRequest, Is.EqualTo("sibling"));
            var followup = client.GetStringAsync(prefix + "sibling/after", deadline.Token);
            var next = await sibling.GetContextAsync(deadline.Token);
            Assert.That(next.RemoteEndPoint, Is.EqualTo(second.RemoteEndPoint), "Owner drain must not send connection-wide GOAWAY.");
            await next.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            next.Close();
            Assert.That(await followup, Is.EqualTo("healthy"));
        }
        [Test]
        public async Task OwnerDrainIncludesResetResponseWhileCloseCallbackIsRunning()
        {
            var prefix = Resources.GetServerAddress();
            using var owner = new Net.HttpListener();
            using var sibling = new Net.HttpListener();
            owner.AddPrefix(prefix); sibling.AddPrefix(prefix + "sibling/");
            owner.Start(); sibling.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var cancelRequest = new CancellationTokenSource();
            using var releaseClose = new ManualResetEventSlim();
            var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            var request = client.GetStringAsync(prefix, cancelRequest.Token);
            var context = await owner.GetContextAsync(deadline.Token);
            context.OnClose(_ =>
            {
                closing.TrySetResult();
                releaseClose.Wait(deadline.Token);
            });
            Task? drain = null;
            try
            {
                cancelRequest.Cancel();
                await closing.Task.WaitAsync(deadline.Token);
                var contract = owner.GetType().GetInterface("EmbedIO.Net.Internal.IGracefulHttpListener")
                    ?? throw new AssertionException("Missing graceful listener contract.");
                drain = (Task)(contract.GetMethod("DrainAsync")?.Invoke(owner, new object[] { TimeSpan.FromSeconds(5), deadline.Token })
                    ?? throw new AssertionException("Missing owner drain operation."));
                // The snapshot is taken after dispatch entered cleanup, making the
                // ownership-removal race deterministic rather than timing dependent.
                await Task.WhenAny(drain, Task.Delay(100, deadline.Token));
                Assert.That(drain.IsCompleted, Is.False, "The accepted context still owns an unfinished close callback.");
                var followup = client.GetStringAsync(prefix + "sibling/", deadline.Token);
                var next = await sibling.GetContextAsync(deadline.Token);
                Assert.That(next.RemoteEndPoint, Is.EqualTo(context.RemoteEndPoint));
                await next.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                next.Close();
                Assert.That(await followup, Is.EqualTo("healthy"));
                releaseClose.Set();
                await drain.WaitAsync(deadline.Token);
            }
            finally
            {
                releaseClose.Set();
                try { await request; } catch (OperationCanceledException) { }
                if (drain != null) await drain.WaitAsync(deadline.Token);
            }
        }
    }
}
