using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.RateLimiting;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Opt-in request admission through System.Threading.RateLimiting.
    [TestFixture]
    public sealed class Issue141_RateLimiting
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        // The partition key: an application identity from a header, standing in for an authenticated user.
        private static string Caller(IHttpContext context) => context.Request.Headers["X-Caller"] ?? "anonymous";

        private static PartitionedRateLimiter<IHttpContext> FixedWindow(int permits)
            => PartitionedRateLimiter.Create<IHttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                Caller(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));

        // Answers every request; requests to /slow wait for the gate, or for the request's cancellation.
        private static ActionModule Answer(TaskCompletionSource<bool>? gate = null) => new(async context =>
        {
            if (gate != null && context.RequestedPath == "/slow")
                await gate.Task.WaitAsync(context.CancellationToken);

            await context.SendStringAsync("ok", "text/plain", WebServer.DefaultEncoding);
        });

        private static Task<HttpResponseMessage> Get(HttpClient client, string url, string? caller = null, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (caller != null)
                request.Headers.Add("X-Caller", caller);

            return client.SendAsync(request, cancellationToken);
        }

        private static async Task<int> Status(HttpClient client, string url, string? caller = null)
        {
            using var response = await Get(client, url, caller);
            return (int)response.StatusCode;
        }

        [Test]
        public async Task ExhaustedWindowAnswers429WithRetryAfter()
        {
            using var limiter = FixedWindow(2);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/api", limiter), Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, server.Url + "api/a"), Is.EqualTo(200));
            Assert.That(await Status(client, server.Url + "api/b"), Is.EqualTo(200));
            using (var rejected = await Get(client, server.Url + "api/c"))
            {
                Assert.That(rejected.StatusCode, Is.EqualTo((HttpStatusCode)429));
                var retryAfter = rejected.Headers.RetryAfter?.Delta;
                Assert.That(retryAfter, Is.Not.Null, "A window limiter knows when its window resets.");
                Assert.That(retryAfter?.TotalSeconds, Is.InRange(3500, 3600));
            }

            // Outside the module's base route, requests are not counted.
            for (var i = 0; i < 5; i++)
                Assert.That(await Status(client, server.Url + "public"), Is.EqualTo(200));
        }

        [Test]
        public async Task TokenBucketAllowsItsBurstThenRejects()
        {
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(context => RateLimitPartition.GetTokenBucketLimiter(
                Caller(context),
                _ => new TokenBucketRateLimiterOptions { TokenLimit = 3, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromHours(1), QueueLimit = 0 }));
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer());
            using var client = new HttpClient();

            var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Status(client, server.Url + "item")));
            Assert.That(statuses.Count(s => s == 200), Is.EqualTo(3));
            Assert.That(statuses.Count(s => s == 429), Is.EqualTo(5));
        }

        // Partitions are independent: one caller's exhausted budget leaves another's intact.
        [Test]
        public async Task PartitionsAreIndependent()
        {
            using var limiter = FixedWindow(1);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, server.Url + "x", "ada"), Is.EqualTo(200));
            Assert.That(await Status(client, server.Url + "x", "ada"), Is.EqualTo(429));
            Assert.That(await Status(client, server.Url + "x", "grace"), Is.EqualTo(200));
            Assert.That(await Status(client, server.Url + "x", "grace"), Is.EqualTo(429));
            Assert.That(await Status(client, server.Url + "x"), Is.EqualTo(200));
        }

        // Modules on different routes, and different servers, keep separate budgets.
        [Test]
        public async Task RoutesAndServersHaveSeparateBudgets()
        {
            using var reads = FixedWindow(1);
            using var writes = FixedWindow(2);
            await using var first = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/reads", reads), new RateLimitingModule("/writes", writes), Answer());
            using var other = FixedWindow(1);
            await using var second = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/reads", other), Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, first.Url + "reads/1"), Is.EqualTo(200));
            Assert.That(await Status(client, first.Url + "reads/2"), Is.EqualTo(429));
            Assert.That(await Status(client, first.Url + "writes/1"), Is.EqualTo(200));
            Assert.That(await Status(client, first.Url + "writes/2"), Is.EqualTo(200));
            Assert.That(await Status(client, first.Url + "writes/3"), Is.EqualTo(429));
            Assert.That(await Status(client, second.Url + "reads/1"), Is.EqualTo(200));
        }

        // The permit is held until the request completes, and a waiting request is queued.
        [Test]
        public async Task ConcurrencyPermitIsHeldUntilTheRequestCompletes()
        {
            ConcurrencyLimiter? partition = null;
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(_ => RateLimitPartition.Get(
                "all",
                _ => partition = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 1, QueueProcessingOrder = QueueProcessingOrder.OldestFirst })));
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer(gate));
            using var client = new HttpClient();

            var holder = Get(client, server.Url + "slow");
            await WaitFor(() => partition?.GetStatistics()?.CurrentAvailablePermits == 0);
            var queued = Get(client, server.Url + "fast");
            await WaitFor(() => partition?.GetStatistics()?.CurrentQueuedCount == 1);

            // Permit taken, queue full: the third is rejected, without Retry-After.
            using (var rejected = await Get(client, server.Url + "fast"))
            {
                Assert.That(rejected.StatusCode, Is.EqualTo((HttpStatusCode)429));
                Assert.That(rejected.Headers.RetryAfter, Is.Null);
            }

            Assert.That(queued.IsCompleted, Is.False, "The queued request waits for the held permit.");
            gate.SetResult(true);
            using (var first = await holder.WaitAsync(Wait))
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using (var second = await queued.WaitAsync(Wait))
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            await WaitFor(() => partition?.GetStatistics()?.CurrentAvailablePermits == 1);
            var statistics = partition?.GetStatistics();
            Assert.That(statistics?.TotalSuccessfulLeases, Is.EqualTo(2));
            Assert.That(statistics?.TotalFailedLeases, Is.EqualTo(1));
        }

        // Stopping the server cancels the queued wait and returns the held permit.
        [Test]
        public async Task ShutdownReleasesPermitsAndQueuedWaits()
        {
            ConcurrencyLimiter? partition = null;
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(_ => RateLimitPartition.Get(
                "all",
                _ => partition = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 4, QueueProcessingOrder = QueueProcessingOrder.OldestFirst })));
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient();
            Task<HttpResponseMessage> holder;
            Task<HttpResponseMessage>[] queued;
            await using (var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer(gate)))
            {
                holder = Get(client, server.Url + "slow");
                await WaitFor(() => partition?.GetStatistics()?.CurrentAvailablePermits == 0);
                queued = Enumerable.Range(0, 3).Select(_ => Get(client, server.Url + "fast")).ToArray();
                await WaitFor(() => partition?.GetStatistics()?.CurrentQueuedCount == 3);
            }

            await WaitFor(() => partition?.GetStatistics() is { CurrentAvailablePermits: 1, CurrentQueuedCount: 0 });
            foreach (var request in queued.Append(holder))
            {
                try
                {
                    using var response = await request.WaitAsync(Wait);
                }
                catch (HttpRequestException)
                {
                    // The server closed the connection while stopping.
                }
            }

            Assert.That(gate.Task.IsCompleted, Is.False, "The held request ended by cancellation, not by finishing.");
        }

        // A request's cancellation follows the server, not the client: a client that gives up
        // keeps its queue slot until its request gets a permit, which it then returns.
        [Test]
        public async Task AbandonedQueuedRequestIsServedThenReleased()
        {
            ConcurrencyLimiter? partition = null;
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(_ => RateLimitPartition.Get(
                "all",
                _ => partition = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 1, QueueProcessingOrder = QueueProcessingOrder.OldestFirst })));
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer(gate));
            using var client = new HttpClient();

            var holder = Get(client, server.Url + "slow");
            await WaitFor(() => partition?.GetStatistics()?.CurrentAvailablePermits == 0);
            using (var giveUp = new CancellationTokenSource())
            {
                var queued = Get(client, server.Url + "fast", cancellationToken: giveUp.Token);
                await WaitFor(() => partition?.GetStatistics()?.CurrentQueuedCount == 1);
                await giveUp.CancelAsync();
                await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            }

            // The abandoned request still holds the only queue slot, so a new one is rejected.
            Assert.That(partition?.GetStatistics()?.CurrentQueuedCount, Is.EqualTo(1));
            Assert.That(await Status(client, server.Url + "fast"), Is.EqualTo(429));

            gate.SetResult(true);
            using (var first = await holder.WaitAsync(Wait))
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // The abandoned request ran and returned its permit; nothing is left held.
            await WaitFor(() => partition?.GetStatistics() is { CurrentAvailablePermits: 1, CurrentQueuedCount: 0 });
            Assert.That(await Status(client, server.Url + "fast"), Is.EqualTo(200));
            Assert.That(partition?.GetStatistics()?.TotalSuccessfulLeases, Is.EqualTo(3));
        }

        [Test]
        public async Task RejectionsCanBeCustomized()
        {
            using var limiter = FixedWindow(1);
            var module = new RateLimitingModule("/", limiter)
            {
                RejectionStatusCode = 503,
                OnRejected = (context, lease) => context.SendStringAsync("{\"error\":\"busy\"}", "application/json", WebServer.DefaultEncoding),
            };
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module, Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, server.Url + "x"), Is.EqualTo(200));
            using var rejected = await Get(client, server.Url + "x");
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(rejected.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(await rejected.Content.ReadAsStringAsync(), Is.EqualTo("{\"error\":\"busy\"}"));
            Assert.That(rejected.Headers.RetryAfter?.Delta?.TotalSeconds, Is.InRange(3500, 3600));

            // The module stopped the request: the next module did not answer it.
            Assert.That(await rejected.Content.ReadAsStringAsync(), Does.Not.Contain("ok"));
        }

        // The module does not own the limiter; a limiter disposed under a running server fails requests.
        [Test]
        public async Task DisposedLimiterFailsRequestsUnderItsRoute()
        {
            var limiter = FixedWindow(10);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/api", limiter), Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, server.Url + "api/x"), Is.EqualTo(200));
            limiter.Dispose();
            Assert.That(await Status(client, server.Url + "api/x"), Is.EqualTo(500));
            Assert.That(await Status(client, server.Url + "public"), Is.EqualTo(200));
        }

        [Test]
        public void ConfigurationIsValidatedAndLockedAtStart()
        {
            Assert.Throws<ArgumentNullException>(() => _ = new RateLimitingModule("/", Missing<PartitionedRateLimiter<IHttpContext>>()));
            using var limiter = FixedWindow(1);
            var module = new RateLimitingModule("/", limiter);
            Assert.That(module.IsFinalHandler, Is.False);
            Assert.That(module.RejectionStatusCode, Is.EqualTo(429));
            Assert.Throws<ArgumentOutOfRangeException>(() => module.RejectionStatusCode = 200);
            Assert.Throws<ArgumentOutOfRangeException>(() => module.RejectionStatusCode = 600);
            module.RejectionStatusCode = 503;

            module.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => module.RejectionStatusCode = 429);
            Assert.Throws<InvalidOperationException>(() => module.OnRejected = (_, _) => Task.CompletedTask);
            Assert.That(module.RejectionStatusCode, Is.EqualTo(503));
        }

        [TestCase("192.0.2.7", "192.0.2.7")]
        [TestCase("::ffff:192.0.2.7", "192.0.2.7")]
        [TestCase("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
        [TestCase("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
        [TestCase("::1", "::/64")]
        public void ClientKeyGroupsIpv6NetworksAndUnmapsIpv4(string address, string expected)
            => Assert.That(RateLimitingModule.ClientKey(IPAddress.Parse(address)), Is.EqualTo(expected));

        [Test]
        public async Task ClientKeyUsesTheConnectionAddress()
        {
            string? key = null;
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new ActionModule(context =>
            {
                key = RateLimitingModule.ClientKey(context);
                return context.SendStringAsync("ok", "text/plain", WebServer.DefaultEncoding);
            }));
            using var client = new HttpClient();
            using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url))
            {
                // Forwarded headers are not trusted.
                request.Headers.Add("X-Forwarded-For", "198.51.100.1");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }

            Assert.That(key, Is.EqualTo("127.0.0.1").Or.EqualTo("::/64"));
        }

        // A partition left idle is removed, so state for past callers does not accumulate.
        [Test]
        public async Task IdlePartitionsAreRemoved()
        {
            var created = new ConcurrentDictionary<string, int>();
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(context => RateLimitPartition.Get(
                Caller(context),
                key =>
                {
                    created.AddOrUpdate(key, 1, (_, count) => count + 1);
                    return new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 });
                }));
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new RateLimitingModule("/", limiter), Answer());
            using var client = new HttpClient();

            Assert.That(await Status(client, server.Url + "x", "ada"), Is.EqualTo(200));
            Assert.That(await Status(client, server.Url + "x", "ada"), Is.EqualTo(200));
            Assert.That(created["ada"], Is.EqualTo(1));

            // System.Threading.RateLimiting removes partitions idle for about ten seconds.
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.That(await Status(client, server.Url + "x", "ada"), Is.EqualTo(200));
            Assert.That(created["ada"], Is.EqualTo(2), "The idle partition was removed and created again.");
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("The condition did not hold within the wait.");

                await Task.Delay(10);
            }
        }

        private static T Missing<T>()
            where T : class
            => (new T[1])[0];
    }
}
