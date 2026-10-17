using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestAbortResetIsStreamLocalAndRequestEndIsNotAnAbort(bool inputEnd)
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                var token = context.GetRequestAbortedToken(inputEnd);
                Assert.That(context.GetRequestAbortedToken(!inputEnd), Is.EqualTo(token), "The input policy is HTTP/1-only.");
                if (context.Request.Url.AbsolutePath == "/blocked")
                {
                    entered.TrySetResult(token);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(true); }
                }
                else
                {
                    Assert.That(token.IsCancellationRequested, Is.False, "The GET request already ended normally.");
                    context.Response.ContentLength64 = 3;
                    await context.Response.OutputStream.WriteAsync(new byte[] { 4, 5, 6 });
                    context.Close();
                    Assert.That(token.IsCancellationRequested, Is.False);
                }
            }, async client =>
            {
                using var cancel = new CancellationTokenSource();
                var blocked = client.GetAsync("blocked", cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetByteArrayAsync("sibling"), Is.EqualTo(new byte[] { 4, 5, 6 }));
                cancel.Cancel();
                await Assert.ThatAsync(async () => await blocked, Throws.InstanceOf<OperationCanceledException>());
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetByteArrayAsync("after-reset"), Is.EqualTo(new byte[] { 4, 5, 6 }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestAbortTokenDetachesAtWireCompletion(bool exchangeCancellation)
        {
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                using var parent = new CancellationTokenSource();
                context.CancellationToken = parent.Token;
                var token = context.GetRequestAbortedToken();
                context.Response.ContentLength64 = 3;
                await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 });
                if (exchangeCancellation)
                    (exchange.GetType().GetMethod("Cancel", Flags) ?? throw new AssertionException("Missing exchange cancellation."))
                        .Invoke(exchange, new object[] { new IOException("Late reset after complete response.") });
                else parent.Cancel();
                context.Close();
                await Task.Delay(30);
                observed.TrySetResult(!token.IsCancellationRequested && context.GetRequestAbortedToken() == token);
            }, async client =>
            {
                Assert.That(await client.GetByteArrayAsync("completed"), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            });
        }
    }
}
