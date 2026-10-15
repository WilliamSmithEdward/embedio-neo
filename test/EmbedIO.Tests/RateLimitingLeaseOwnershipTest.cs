using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.RateLimiting;
using EmbedIO.Tests.Issues;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RateLimitingLeaseOwnershipTest
    {
        [Test]
        public async Task ClosedContextCannotLeakAnAcquiredConcurrencyPermit()
        {
            var closed = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new ActionModule(context =>
            {
                context.OnClose(completed => closed.TrySetResult(completed));
                return context.SendStringAsync("done", "text/plain", WebServer.DefaultEncoding);
            }));
            using var client = new HttpClient();
            using var response = await client.GetAsync(server.Url);
            response.EnsureSuccessStatusCode();
            var context = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(context.CancellationToken.IsCancellationRequested, Is.False);

            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 1,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));
            var module = new RateLimitingModule("/", limiter);
            // Invoke the admission boundary directly so error rendering on an already
            // closed response cannot obscure the failed ownership transfer.
            var handle = typeof(RateLimitingModule).GetMethod("OnRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing rate limiter request handler.");
            var admission = (Task)(handle.Invoke(module, new object[] { context })
                ?? throw new AssertionException("Admission returned no task."));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await admission);

            using (var next = limiter.AttemptAcquire(context))
            {
                Assert.That(next.IsAcquired, Is.True, "The failed close-callback registration must return its permit.");
                using var blocked = limiter.AttemptAcquire(context);
                Assert.That(blocked.IsAcquired, Is.False, "The limiter still enforces its single-permit budget.");
            }
            using var returned = limiter.AttemptAcquire(context);
            Assert.That(returned.IsAcquired, Is.True, "Disposing the subsequent lease still returns its permit.");
        }
    }
}
