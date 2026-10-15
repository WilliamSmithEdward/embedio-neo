using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase("transport", false)]
        [TestCase("transport", true)]
        [TestCase("server", false)]
        [TestCase("server", true)]
        [TestCase("normal", false)]
        public async Task ContextClosePreservesCancellationBeforeLinkCallbackRuns(string source, bool callbackThrows)
        {
            var observed = new TaskCompletionSource<(bool TokenCanceled, bool CloseCanceled, int Callbacks)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(exchange =>
            {
                using var external = new CancellationTokenSource();
                var context = Adapter(exchange);
                context.CancellationToken = external.Token;
                var captured = context.CancellationToken;
                var callbacks = 0;
                var closeCanceled = false;
                context.OnClose(_ => closeCanceled = context.CancellationToken.IsCancellationRequested);
                using var observer = captured.Register(() =>
                {
                    Interlocked.Increment(ref callbacks);
                    if (callbackThrows) throw new InvalidOperationException("Controlled cancellation callback failure.");
                });
                // Cancellation callbacks execute in reverse registration order. This
                // late callback closes the context before its linked-source callback.
                var parent = source == "transport"
                    ? (CancellationTokenSource)(exchange.GetType().GetField("_stop", Flags)?.GetValue(exchange)
                        ?? throw new AssertionException("Missing exchange cancellation owner."))
                    : external;
                using var closer = parent.Token.Register(() =>
                {
                    try { context.Close(); }
                    catch (OperationCanceledException) { }
                });
                if (source == "normal") context.Close();
                else parent.Cancel();
                observed.TrySetResult((captured.IsCancellationRequested, closeCanceled, callbacks));
                var completion = (Task)(context.GetType().GetProperty("Completion", Flags)?.GetValue(context)
                    ?? throw new AssertionException("Missing context completion."));
                if (completion.IsFaulted) _ = completion.Exception;
                return source == "normal" ? Task.CompletedTask : Task.FromException(new OperationCanceledException());
            }, async client =>
            {
                var request = client.GetAsync("cancellation-order");
                var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { using var response = await request; }
                catch (HttpRequestException) { }
                Assert.That(result.TokenCanceled, Is.EqualTo(source != "normal"), "The application must retain an already-requested cancellation.");
                Assert.That(result.CloseCanceled, Is.EqualTo(source != "normal"), "Close callbacks must see the same cancellation.");
                Assert.That(result.Callbacks, Is.EqualTo(source == "normal" ? 0 : 1));
            });
        }
    }
}
