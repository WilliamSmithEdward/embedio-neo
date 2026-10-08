using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task CombinedDrainPreservesEveryProtocolUntilItsResponseFinishes(bool quicFirst, bool concurrent)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            var entered = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var release = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix)
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    var index = context.Request.ProtocolVersion.Major - 1;
                    entered[index].TrySetResult();
                    await release[index].Task.WaitAsync(stop.Token);
                    await context.SendStringAsync(context.Request.ProtocolVersion.ToString(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            var clients = Enumerable.Range(1, 3).Select(version =>
            {
                var client = Client(certificate);
                client.DefaultRequestVersion = version == 1 ? HttpVersion.Version11 : new Version(version, 0);
                return client;
            }).ToArray();
            var running = server.RunAsync(stop.Token);
            var requests = clients.Select(client => client.GetStringAsync(prefix)).ToArray();
            try
            {
                await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(stop.Token);
                var drain = server.DrainAsync(TimeSpan.FromSeconds(15));
                var other = concurrent ? server.DrainAsync(TimeSpan.FromMilliseconds(1)) : Task.CompletedTask;
                Assert.That(() => server.Listener.Start(), Throws.InstanceOf<InvalidOperationException>());
                for (var index = 0; index < 3; index++)
                    if ((index == 2) == quicFirst)
                    {
                        release[index].TrySetResult();
                        Assert.That(await requests[index].WaitAsync(stop.Token), Is.EqualTo(clients[index].DefaultRequestVersion.ToString()));
                        clients[index].Dispose();
                    }
                await Task.Delay(50, stop.Token);
                Assert.That(drain.IsCompleted, Is.False, "The other transport still owns an accepted response.");
                for (var index = 0; index < 3; index++)
                    if ((index == 2) != quicFirst)
                    {
                        release[index].TrySetResult();
                        Assert.That(await requests[index].WaitAsync(stop.Token), Is.EqualTo(clients[index].DefaultRequestVersion.ToString()));
                        clients[index].Dispose();
                    }
                await Task.WhenAll(drain, other).WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                server.Listener.Start();
                Assert.That(server.Listener.IsListening, Is.True);
            }
            finally
            {
                foreach (var signal in release) signal.TrySetResult();
                foreach (var client in clients) client.Dispose();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase("deadline")]
        [TestCase("drain-cancel")]
        [TestCase("run-cancel")]
        [TestCase("listener-stop")]
        [TestCase("dispose")]
        public async Task CombinedDrainCanAbortAllProtocols(string reason)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            var entered = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancelDrain = new CancellationTokenSource();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix)
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    entered[context.Request.ProtocolVersion.Major - 1].TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                });
            var clients = Enumerable.Range(2, 2).Select(version =>
            {
                var client = Client(certificate);
                client.DefaultRequestVersion = version == 1 ? HttpVersion.Version11 : new Version(version, 0);
                return client;
            }).ToArray();
            var running = server.RunAsync(stop.Token);
            var requests = clients.Select(client => client.GetStringAsync(prefix)).ToArray();
            var http1 = Http1AbortProbe.AssertClosedWithoutResponseAsync(prefix, certificate);
            var phase = "await admission";
            Exception? failure = null;
            try
            {
                await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(stop.Token);
                phase = "start drain";
                var drain = server.DrainAsync(reason == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(15), cancelDrain.Token);
                phase = "trigger " + reason;
                if (reason == "drain-cancel") cancelDrain.Cancel();
                if (reason == "run-cancel") stop.Cancel();
                if (reason == "listener-stop") server.Listener.Stop();
                if (reason == "dispose") server.Dispose();
                phase = "await drain";
                if (reason == "drain-cancel")
                    await Assert.ThatAsync(async () => await drain.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
                else await drain.WaitAsync(TimeSpan.FromSeconds(5));
                phase = "await server";
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                phase = "await clients";
                await http1.WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var request in requests)
                    await Assert.ThatAsync(async () => await request, Throws.InstanceOf<HttpRequestException>());
                Assert.That(server.Listener.IsListening, Is.False);
            }
            catch (Exception error)
            {
                failure = error;
                TestContext.Error.WriteLine($"Combined drain failed: reason={reason}, phase={phase}, server={server.State}, run={running.Status}, HTTP/1={http1.Status}, HTTP/2={requests[0].Status}, HTTP/3={requests[1].Status}, admitted={string.Join(",", entered.Select(signal => signal.Task.IsCompleted))}. {error}");
                throw;
            }
            finally
            {
                try
                {
                    release.TrySetResult(); stop.Cancel();
                    foreach (var client in clients) client.Dispose();
                    await running.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (failure != null)
                {
                    // Preserve the original assertion or transport failure instead of
                    // replacing it with a secondary listener-stop cleanup exception.
                    TestContext.Error.WriteLine($"Combined drain cleanup also failed: {error}");
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CombinedDrainAbortsAFullUndispatchedQueue(bool cancel)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            server.Listener.Start();
            // Inspect only the fixed combined-session queue to prove backpressure,
            // rather than relying on a sleep or on requests merely being submitted.
            var session = server.Listener.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server.Listener)
                ?? throw new AssertionException("Missing combined session.");
            var queue = (Channel<IHttpContextImpl>)(session.GetType().GetField("_contexts", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session)
                ?? throw new AssertionException("Missing combined queue."));
            var clients = Enumerable.Range(1, 3).Select(version =>
            {
                var client = Client(certificate);
                client.DefaultRequestVersion = version == 1 ? HttpVersion.Version11 : new Version(version, 0);
                return client;
            }).ToArray();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var cancelDrain = new CancellationTokenSource();
            var requests = clients.SelectMany(client => Enumerable.Range(0, 96).Select(_ => client.GetAsync(prefix, stop.Token))).ToArray();
            try
            {
                while (queue.Reader.Count < 256) await Task.Delay(10, stop.Token);
                var drain = server.DrainAsync(cancel ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), cancelDrain.Token);
                if (cancel)
                {
                    cancelDrain.Cancel();
                    await Assert.ThatAsync(async () => await drain.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
                }
                else await drain.WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var request in requests)
                    await Assert.ThatAsync(async () => { using var response = await request; }, Throws.InstanceOf<HttpRequestException>());
                Assert.That(queue.Reader.Count, Is.Zero);
                Assert.That(server.Listener.IsListening, Is.False);
            }
            finally
            {
                stop.Cancel();
                foreach (var client in clients) client.Dispose();
            }
        }

        [Test]
        public async Task CombinedIdleDrainPreservesSharedTcpSibling()
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            using var sibling = new Net.HttpListener(certificate);
            server.Listener.Start();
            sibling.AddPrefix(prefix + "sibling/"); sibling.Start();
            await server.DrainAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(server.Listener.IsListening, Is.False);
            Assert.That(sibling.IsListening, Is.True);
            foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
            {
                using var client = Client(certificate); client.DefaultRequestVersion = version;
                var request = client.GetAsync(prefix + "sibling/");
                var context = await sibling.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                context.Response.ContentLength64 = 0;
                await context.Response.OutputStream.FlushAsync();
                context.Close();
                using var response = await request;
                Assert.That(response.Version, Is.EqualTo(version));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
        }

        [Test]
        public async Task CombinedIdleDrainSupportsRestartAndPreCanceledCallHasNoEffects()
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            await server.DrainAsync(TimeSpan.FromSeconds(1));
            server.Listener.Start();
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.That(() => server.DrainAsync(TimeSpan.FromSeconds(1), canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(server.Listener.IsListening, Is.True);
            await server.DrainAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));
            server.Listener.Start();
            Assert.That(server.Listener.IsListening, Is.True);
            await server.DrainAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
