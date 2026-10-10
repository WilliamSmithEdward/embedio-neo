using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task LateCancellationDoesNotFaultCleanupOfAWireCompleteResponse(bool exchangeCancellation)
        {
            var observed = new TaskCompletionSource<(Exception? Error, int Closed)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                var closed = 0;
                context.OnClose(_ => Interlocked.Increment(ref closed));
                Exception? failure = null;
                try
                {
                    context.Response.ContentLength64 = 3;
                    await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 }, context.CancellationToken);
                    if (exchangeCancellation)
                        (exchange.GetType().GetMethod("Cancel", Flags) ?? throw new AssertionException("Missing exchange cancellation."))
                            .Invoke(exchange, new object[] { new IOException("Connection ended after committed response.") });
                    else context.CancellationToken = new CancellationToken(true);
                    context.Close();
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { failure = error; }
                finally
                {
                    context.Close();
                    var completion = (Task)(context.GetType().GetProperty("Completion", Flags)?.GetValue(context)
                        ?? throw new AssertionException("Missing context completion."));
                    try { await completion; }
                    catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { failure ??= error; }
                    observed.TrySetResult((failure, closed));
                }
            }, async client =>
            {
                Assert.That(await client.GetByteArrayAsync("completed"), Is.EqualTo(new byte[] { 1, 2, 3 }));
                var outcome = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(outcome.Error, Is.Null, "Late cancellation cannot undo an already committed END_STREAM.");
                Assert.That(outcome.Closed, Is.EqualTo(1));
            });
        }
    }
}
