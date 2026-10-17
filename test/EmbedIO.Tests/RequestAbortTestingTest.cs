using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestAbortTestingTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task InProcessControllersSupportBothBindingModes(bool generated)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(RequestAbortBindings.RequestAbortController);
            else module.WithController<RequestAbortController>();
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                Assert.That(await client.GetStringAsync("/api/normal"), Is.EqualTo("normal"));
            });
        }
        [Test]
        public async Task InProcessCompletedTokenDetachesFromItsCaller()
        {
            CancellationToken captured = default;
            IHttpContext? completed = null;
            using var caller = new CancellationTokenSource();
            await TestWebServer.UseAsync(server => server.OnAny(async context =>
            {
                Assert.That(context.TryGetRequestAbortedToken(out captured), Is.True);
                Assert.That(context.GetRequestAbortedToken(true), Is.EqualTo(captured));
                completed = context;
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }), async client =>
            {
                using var response = await client.GetAsync("/", caller.Token);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                caller.Cancel();
                Assert.That(captured.IsCancellationRequested, Is.False);
                Assert.That(completed?.GetRequestAbortedToken(), Is.EqualTo(captured));
            });
        }
        [Test]
        public async Task InProcessPendingTokenObservesClientCancellation()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await TestWebServer.UseAsync(server => server.OnAny(async context =>
            {
                var token = context.GetRequestAbortedToken();
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
            }), async client =>
            {
                using var caller = new CancellationTokenSource();
                var pending = client.GetAsync("/", caller.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { using var response = await pending; }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            });
        }
    }
}
