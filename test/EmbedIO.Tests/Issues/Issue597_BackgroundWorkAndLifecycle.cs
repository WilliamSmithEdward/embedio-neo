using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue597_BackgroundWorkAndLifecycle
    {
        [Test]
        public async Task AwaitedRequestExceptionReturns500WithoutStoppingListener()
        {
            await using var server = new Listener(Resources.GetServerAddress(), async context =>
            {
                if (context.Request.Url.AbsolutePath == "/fail")
                {
                    await Task.Yield();
                    throw new InvalidOperationException("request failure");
                }

                await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            using var failed = await server.Client.GetAsync("fail");
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(await server.Client.GetStringAsync("health"), Is.EqualTo("healthy"));
            Assert.That(server.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ApplicationOwnedWorkerObservesFailureOrShutdownAfterImmediateResponse(bool cancel)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var workerStop = new CancellationTokenSource();
            Task? worker = null;
            async Task RunWorker()
            {
                try
                {
                    await release.Task.WaitAsync(workerStop.Token);
                    throw new InvalidOperationException("background failure");
                }
                catch (OperationCanceledException) when (workerStop.IsCancellationRequested)
                {
                    observed.SetResult("canceled");
                }
                catch (InvalidOperationException error)
                {
                    observed.SetResult(error.Message);
                }
            }

            await using var server = new Listener(Resources.GetServerAddress(), context =>
            {
                if (context.Request.Url.AbsolutePath == "/work")
                {
                    worker = RunWorker();
                    return context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                }

                return context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            try
            {
                using var accepted = await server.Client.PostAsync("work", null);
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await accepted.Content.ReadAsStringAsync(), Is.EqualTo("accepted"));
                Assert.That(worker, Is.Not.Null);
                Assert.That(worker.IsCompleted, Is.False);
                if (cancel) workerStop.Cancel();
                else release.SetResult();
                Assert.That(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                    Is.EqualTo(cancel ? "canceled" : "background failure"));
                await worker.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await server.Client.GetStringAsync("health"), Is.EqualTo("healthy"));
                Assert.That(server.Server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                workerStop.Cancel();
                if (worker != null) await worker.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public async Task AwaitedShutdownAndNewServerCanRebindSameLoopbackPort()
        {
            var url = Resources.GetServerAddress();
            for (var generation = 0; generation < 3; generation++)
            {
                var marker = generation.ToString();
                await using var server = new Listener(url, context =>
                    context.SendStringAsync(marker, "text/plain", WebServer.Utf8NoBomEncoding));
                Assert.That(await server.Client.GetStringAsync("/"), Is.EqualTo(marker));
            }
        }

        private sealed class Listener : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            public WebServer Server { get; }
            public HttpClient Client { get; }

            public Listener(string url, Func<IHttpContext, Task> handler)
            {
                Server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                    .WithModule(new ActionModule("/", HttpVerbs.Any, handler.Invoke));
                Client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
                _running = Server.RunAsync(_stop.Token);
                Assert.That(Server.State, Is.EqualTo(WebServerState.Listening));
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { Client.Dispose(); Server.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
