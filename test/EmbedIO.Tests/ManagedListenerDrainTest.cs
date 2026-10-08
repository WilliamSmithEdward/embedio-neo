using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedListenerDrainTest
    {
        private static HttpClient Client(bool http2) => new(new SocketsHttpHandler { UseProxy = false })
        {
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task DrainPreservesAcceptedResponseAndFirstDeadline(bool http2, bool concurrent)
        {
            var prefix = Resources.GetServerAddress();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(stop.Token);
                    await context.SendStringAsync("drained response", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var running = server.RunAsync(stop.Token);
            using var client = Client(http2);
            var response = client.GetStringAsync(prefix);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var drain = server.DrainAsync(TimeSpan.FromSeconds(10));
                var other = concurrent ? server.DrainAsync(TimeSpan.FromMilliseconds(1)) : Task.CompletedTask;
                if (concurrent) await Task.Delay(50, stop.Token);
                Assert.That(drain.IsCompleted, Is.False);
                Assert.That(() => server.Listener.Start(), Throws.InstanceOf<InvalidOperationException>());
                Assert.That(() => server.Listener.AddPrefix(Resources.GetServerAddress()), Throws.InstanceOf<InvalidOperationException>());
                Assert.That(server.Listener.Prefixes, Has.Count.EqualTo(1));
                release.TrySetResult();
                Assert.That(await response, Is.EqualTo("drained response"));
                await Task.WhenAll(drain, other).WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                using var replacement = new WebServer(HttpListenerMode.EmbedIO, prefix);
                replacement.Listener.Start();
                Assert.That(replacement.Listener.IsListening, Is.True);
            }
            finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(false, "deadline")]
        [TestCase(true, "deadline")]
        [TestCase(false, "drain-cancel")]
        [TestCase(true, "drain-cancel")]
        [TestCase(false, "run-cancel")]
        [TestCase(true, "run-cancel")]
        [TestCase(false, "listener-stop")]
        [TestCase(true, "listener-stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        public async Task DrainCanAbortAnUnfinishedResponse(bool http2, string reason)
        {
            var prefix = Resources.GetServerAddress();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var cancelDrain = new CancellationTokenSource();
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    try { await release.Task.WaitAsync(context.CancellationToken); }
                    finally { exited.TrySetResult(); }
                }));
            var running = server.RunAsync(stop.Token);
            using var client = Client(http2);
            var response = client.GetStringAsync(prefix);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var drain = server.DrainAsync(reason == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), cancelDrain.Token);
                if (reason == "drain-cancel") cancelDrain.Cancel();
                if (reason == "run-cancel") stop.Cancel();
                if (reason == "listener-stop") server.Listener.Stop();
                if (reason == "dispose") server.Dispose();
                if (reason == "drain-cancel")
                    await Assert.ThatAsync(async () => await drain.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
                else await drain.WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThatAsync(async () => await response, Throws.InstanceOf<HttpRequestException>());
                if (http2) await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(server.Listener.IsListening, Is.False);
            }
            finally
            {
                release.TrySetResult(); stop.Cancel();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DrainPreservesRequestsStillWaitingInTheListenerQueue(bool http2)
        {
            var prefix = Resources.GetServerAddress();
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix);
            server.Listener.Start();
            using var client = Client(http2);
            var requests = Enumerable.Range(0, http2 ? 8 : 1).Select(_ => client.GetStringAsync(prefix)).ToArray();
            var queue = (IDictionary)(typeof(Net.HttpListener).GetField("_ctxQueue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(server.Listener)
                ?? throw new AssertionException("Missing pending context queue."));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (queue.Count != requests.Length) await Task.Delay(10, stop.Token);
            var drain = server.DrainAsync(TimeSpan.FromSeconds(5));
            Assert.That(drain.IsCompleted, Is.False);
            foreach (var request in requests)
            {
                var context = await server.Listener.GetContextAsync(stop.Token);
                context.Response.ContentLength64 = 3;
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("abc"));
                context.Close();
            }
            Assert.That(await Task.WhenAll(requests).WaitAsync(stop.Token), Is.All.EqualTo("abc"));
            await drain.WaitAsync(stop.Token);
        }

        [Test]
        public async Task DrainClosesHttp2ConnectionWaitingForClientSettings()
        {
            var prefix = Resources.GetServerAddress();
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix);
            server.Listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, new Uri(prefix).Port);
            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var header = new byte[9];
            await stream.ReadExactlyAsync(header, stop.Token);
            Assert.That(header[3], Is.EqualTo(4));
            var settings = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(settings, stop.Token);
            await server.DrainAsync(TimeSpan.FromSeconds(5)).WaitAsync(stop.Token);
            try { Assert.That(await stream.ReadAsync(header, stop.Token), Is.Zero); }
            catch (IOException) { /* A reset also proves the unfinished transport was closed. */ }
            Assert.That(server.Listener.IsListening, Is.False);
        }

        [Test]
        public async Task SharedEndpointDrainRejectsBeforeChangingAdmission()
        {
            var prefix = Resources.GetServerAddress();
            using var first = new WebServer(HttpListenerMode.EmbedIO, prefix);
            using var sibling = new WebServer(HttpListenerMode.EmbedIO, prefix + "sibling/");
            first.Listener.Start(); sibling.Listener.Start();
            Assert.That(() => first.DrainAsync(TimeSpan.FromSeconds(1)), Throws.InstanceOf<NotSupportedException>());
            using var client = Client(false);
            foreach (var server in new[] { first, sibling })
            {
                var request = client.GetAsync(server == first ? prefix : prefix + "sibling/");
                var context = await server.Listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                context.Response.ContentLength64 = 0;
                await context.Response.OutputStream.FlushAsync();
                context.Close();
                using var response = await request;
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
        }

        [Test]
        public async Task IdleDrainAllowsListenerRestartAndCanceledCallHasNoSideEffects()
        {
            var prefix = Resources.GetServerAddress();
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix);
            await server.DrainAsync(TimeSpan.FromSeconds(1));
            server.Listener.Start();
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.That(() => server.DrainAsync(TimeSpan.FromSeconds(1), canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(server.Listener.IsListening, Is.True);
            await server.DrainAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
            server.Listener.Start();
            Assert.That(server.Listener.IsListening, Is.True);
            await server.DrainAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
