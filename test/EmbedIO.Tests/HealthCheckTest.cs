using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Health;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class HealthCheckTest
    {
        private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Task<HealthCheckResult> Healthy(CancellationToken _) => Task.FromResult(new HealthCheckResult(HealthStatus.Healthy));

        [TestCase(-1)]
        [TestCase(3)]
        public void InvalidStatusRejected(int value) => Assert.That(() => new HealthCheckResult((HealthStatus)value), Throws.TypeOf<ArgumentOutOfRangeException>());

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(60001)]
        public void InvalidCheckTimeoutRejected(int milliseconds)
            => Assert.That(() => new HealthCheckRegistration("db", Healthy, TimeSpan.FromMilliseconds(milliseconds)), Throws.TypeOf<ArgumentOutOfRangeException>());

        [TestCase("")]
        [TestCase(" ")]
        public void InvalidNameRejected(string name) => Assert.That(() => new HealthCheckRegistration(name, Healthy), Throws.ArgumentException);

        [Test]
        public void ConfigurationBoundsAndCopiesRegistrations()
        {
            using var server = new TestWebServer();
            Assert.That(() => new HealthCheckResult(HealthStatus.Healthy, new string('x', 513)), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new HealthCheckRegistration(new string('x', 65), Healthy), Throws.ArgumentException);
            Assert.That(() => new HealthCheckService(server, Enumerable.Range(0, 33).Select(i => new HealthCheckRegistration(i.ToString(), Healthy))), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new HealthCheckService(server, new[] { new HealthCheckRegistration("db", Healthy), new HealthCheckRegistration("db", Healthy) }), Throws.ArgumentException);
            using var health = new HealthCheckService(server);
            Assert.That(() => new HealthCheckModule(health, includeDetails: true), Throws.ArgumentException);
            Assert.That(default(HealthCheckResult).Status, Is.EqualTo(HealthStatus.Unhealthy));
        }

        [Test]
        public async Task CreatedListeningStoppedAndDisposedStates()
        {
            using var server = new TestWebServer();
            using var health = new HealthCheckService(server);
            Assert.That(health.CheckLiveness().Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            using var stop = new CancellationTokenSource();
            var waiting = health.WaitForReadyAsync(TimeSpan.FromSeconds(3), pollInterval: TimeSpan.FromMinutes(1));
            var running = server.RunAsync(stop.Token);
            await waiting;
            Assert.That(health.CheckLiveness().Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Healthy));
            stop.Cancel(); await running;
            Assert.That(health.CheckLiveness().Status, Is.EqualTo(HealthStatus.Unhealthy));
            using var afterStop = new HealthCheckService(server);
            Assert.That((await afterStop.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.FromSeconds(1)), Throws.TypeOf<InvalidOperationException>());
            health.Dispose(); health.Dispose();
            Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
        }

        [TestCase(HealthStatus.Unhealthy)]
        [TestCase(HealthStatus.Degraded)]
        [TestCase(HealthStatus.Healthy)]
        public async Task AggregateUsesWorstStatusAndSnapshot(HealthStatus status)
        {
            using var server = new TestWebServer();
            var registrations = new[] { new HealthCheckRegistration("dependency", _ => Task.FromResult(new HealthCheckResult(status, "application description"))), new HealthCheckRegistration("other", Healthy) };
            using var health = new HealthCheckService(server, registrations);
            registrations[0] = new HealthCheckRegistration("replacement", Healthy);
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var report = await health.CheckReadinessAsync();
                Assert.That(report.Status, Is.EqualTo(status)); Assert.That(report.Checks.Keys, Is.EquivalentTo(new[] { "dependency", "other" }));
                Assert.That(report.Checks["dependency"].Description, Is.EqualTo("application description"));
            }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task ReadinessWaitDoesNotWarmHandlersAndSeesRecovery()
        {
            using var server = new TestWebServer(); var ready = 0; var requests = 0; var checks = 0;
            server.WithModule(new ActionModule(_ => { Interlocked.Increment(ref requests); return Task.CompletedTask; }));
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("startup", _ =>
            { Interlocked.Increment(ref checks); return Task.FromResult(new HealthCheckResult(Volatile.Read(ref ready) == 1 ? HealthStatus.Healthy : HealthStatus.Unhealthy)); }) });
            using var stop = new CancellationTokenSource(); var wait = health.WaitForReadyAsync(TimeSpan.FromSeconds(3), pollInterval: TimeSpan.FromMilliseconds(2));
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
                Assert.That(health.CheckLiveness().Status, Is.EqualTo(HealthStatus.Healthy));
                Volatile.Write(ref ready, 1); await wait;
                Assert.That(requests, Is.Zero); Assert.That(checks, Is.Positive);
                Volatile.Write(ref ready, 0);
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task ConcurrentProbesShareEvaluationAndWaiterCancellationIsIsolated()
        {
            using var server = new TestWebServer(); var entered = Signal<bool>(); var release = Signal<HealthCheckResult>(); var calls = 0;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("held", _ => { Interlocked.Increment(ref calls); entered.TrySetResult(true); return release.Task; }) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                using var canceled = new CancellationTokenSource();
                var first = health.CheckReadinessAsync(canceled.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var others = Enumerable.Range(0, 20).Select(_ => health.CheckReadinessAsync()).ToArray();
                canceled.Cancel(); await Assert.ThatAsync(async () => await first, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(calls, Is.EqualTo(1)); release.TrySetResult(new HealthCheckResult(HealthStatus.Healthy));
                var results = await Task.WhenAll(others); Assert.That(results.All(r => r.Status == HealthStatus.Healthy), Is.True);
            }
            finally { release.TrySetResult(new HealthCheckResult(HealthStatus.Unhealthy)); stop.Cancel(); await running; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TimeoutBoundsSynchronousAndAsynchronousUncooperativeChecks(bool synchronous)
        {
            using var server = new TestWebServer(); using var release = new ManualResetEventSlim();
            var entered = Signal<bool>(); var finished = Signal<bool>(); var canceled = Signal<bool>(); var calls = 0;
            var held = Signal<HealthCheckResult>();
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("stuck", async token =>
            {
                using var registration = token.Register(() => canceled.TrySetResult(true));
                Interlocked.Increment(ref calls); entered.TrySetResult(true);
                try { if (synchronous) release.Wait(); else await held.Task; return new HealthCheckResult(HealthStatus.Healthy); }
                finally { finished.TrySetResult(true); }
            }, TimeSpan.FromMilliseconds(100)) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var probe = health.CheckReadinessAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That((await probe.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(HealthStatus.Unhealthy));
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var reports = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => health.CheckReadinessAsync()));
                Assert.That(reports.All(r => r.Status == HealthStatus.Unhealthy), Is.True); Assert.That(calls, Is.EqualTo(1));
            }
            finally { release.Set(); held.TrySetResult(new HealthCheckResult(HealthStatus.Healthy)); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); await running; }
        }

        [Test]
        public async Task CallbackExceptionsAreGenericAndDoNotBreakFutureChecks()
        {
            using var server = new TestWebServer(); var fail = true;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("db", _ => fail ? throw new InvalidOperationException("secret connection string") : Healthy(default)) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var report = await health.CheckReadinessAsync(); Assert.That(report.Status, Is.EqualTo(HealthStatus.Unhealthy));
                Assert.That(report.Checks["db"].Description, Does.Not.Contain("secret"));
                fail = false; Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Healthy));
            }
            finally { stop.Cancel(); await running; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StopOrDisposalInvalidatesPendingSuccessAndCancelsCheck(bool dispose)
        {
            using var server = new TestWebServer(); var entered = Signal<bool>(); var canceled = Signal<bool>(); var release = Signal<HealthCheckResult>();
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("held", async token =>
            { using var registration = token.Register(() => canceled.TrySetResult(true)); entered.TrySetResult(true); return await release.Task; }) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var probe = health.CheckReadinessAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (dispose) health.Dispose(); else { stop.Cancel(); await running; }
                Assert.That((await probe.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(HealthStatus.Unhealthy));
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3)); release.TrySetResult(new HealthCheckResult(HealthStatus.Healthy));
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            }
            finally { release.TrySetResult(new HealthCheckResult(HealthStatus.Unhealthy)); stop.Cancel(); await running; }
        }

        [Test]
        public async Task ReadinessWaitDeadlineCancellationAndStop()
        {
            using var server = new TestWebServer(); using var health = new HealthCheckService(server);
            await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.FromMilliseconds(30)), Throws.TypeOf<TimeoutException>());
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.FromSeconds(1), canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            var wait = health.WaitForReadyAsync(TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromMinutes(1));
            health.Dispose(); await Assert.ThatAsync(async () => await wait.WaitAsync(TimeSpan.FromSeconds(3)), Throws.TypeOf<InvalidOperationException>());
            await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());
            await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.FromSeconds(1), pollInterval: TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EndpointsAreMinimalOrExplicitlyAuthorizedDetails(bool details)
        {
            using var server = new TestWebServer(); var ready = false; var calls = 0;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("database", _ =>
            { Interlocked.Increment(ref calls); return Task.FromResult(new HealthCheckResult(ready ? HealthStatus.Healthy : HealthStatus.Degraded, "sensitive application detail")); }) });
            server.WithModule(new HealthCheckModule(health, includeDetails: details, authorize: details ? context => Task.FromResult(context.Request.Headers["X-Test-Access"] == "allowed") : null));
            server.WithModule(new ActionModule(_ => Task.CompletedTask));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                if (details)
                {
                    using var denied = await server.Client.GetAsync("/health/ready"); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); Assert.That(calls, Is.Zero);
                    server.Client.DefaultRequestHeaders.Add("X-Test-Access", "allowed");
                }
                using var live = await server.Client.GetAsync("/health/live"); Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(calls, Is.Zero);
                using var notReady = await server.Client.GetAsync("/health/ready"); Assert.That(notReady.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                Assert.That(notReady.Headers.CacheControl?.NoStore, Is.True);
                var body = await notReady.Content.ReadAsStringAsync();
                if (details) { using var json = JsonDocument.Parse(body); Assert.That(json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("description").GetString(), Is.EqualTo("sensitive application detail")); }
                else Assert.That(body, Is.EqualTo("Degraded"));
                ready = true;
                using var healthy = await server.Client.GetAsync("/health/ready"); Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/health/ready"); using var head = await server.Client.SendAsync(headRequest);
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var wrongMethod = await server.Client.PostAsync("/health/ready", new StringContent("")); Assert.That(wrongMethod.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                using var other = await server.Client.GetAsync("/health/readiness"); Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task AuthorizationExceptionsDenyWithoutRunningChecks()
        {
            using var server = new TestWebServer(); var calls = 0;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("db", _ => { calls++; return Healthy(default); }) });
            server.WithModule(new HealthCheckModule(health, authorize: _ => throw new InvalidOperationException("secret")));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try { using var response = await server.Client.GetAsync("/health/ready"); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); Assert.That(calls, Is.Zero); Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("secret")); }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task ServicesHaveIndependentExecutionSlotsForTheSameRegistration()
        {
            using var first = new TestWebServer(); using var second = new TestWebServer();
            var entered = Signal<bool>(); var release = Signal<HealthCheckResult>(); var calls = 0;
            var registration = new HealthCheckRegistration("shared declaration", _ => { if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult(true); return release.Task; });
            using var one = new HealthCheckService(first, new[] { registration }); using var two = new HealthCheckService(second, new[] { registration });
            using var stop = new CancellationTokenSource(); var runningOne = first.RunAsync(stop.Token); var runningTwo = second.RunAsync(stop.Token);
            try
            {
                var probeOne = one.CheckReadinessAsync(); var probeTwo = two.CheckReadinessAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); one.Dispose();
                Assert.That((await probeOne).Status, Is.EqualTo(HealthStatus.Unhealthy));
                release.TrySetResult(new HealthCheckResult(HealthStatus.Healthy));
                Assert.That((await probeTwo).Status, Is.EqualTo(HealthStatus.Healthy));
            }
            finally { release.TrySetResult(new HealthCheckResult(HealthStatus.Unhealthy)); stop.Cancel(); await Task.WhenAll(runningOne, runningTwo); }
        }

        [Test]
        public async Task ThrowingAndBlockingCancellationCallbacksDoNotBlockReadinessOrDisposal()
        {
            using var server = new TestWebServer(); using var unblock = new ManualResetEventSlim();
            var entered = Signal<bool>(); var callbackEntered = Signal<bool>(); var release = Signal<HealthCheckResult>(); var finished = Signal<bool>();
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("cancellation", async token =>
            {
                using var registration = token.Register(() => { callbackEntered.TrySetResult(true); unblock.Wait(); throw new InvalidOperationException("application callback failure"); });
                entered.TrySetResult(true);
                try { return await release.Task; } finally { finished.TrySetResult(true); }
            }, TimeSpan.FromMilliseconds(100)) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var probe = health.CheckReadinessAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That((await probe.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(HealthStatus.Unhealthy));
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                health.Dispose(); Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            }
            finally { unblock.Set(); release.TrySetResult(new HealthCheckResult(HealthStatus.Healthy)); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); await running; }
        }

        [Test]
        public async Task ConstructionAloneDoesNotInstallEndpointsAndThirtyTwoChecksAreSupported()
        {
            using var server = new TestWebServer();
            using var health = new HealthCheckService(server, Enumerable.Range(0, 32).Select(i => new HealthCheckRegistration(i.ToString(), Healthy)));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                using var response = await server.Client.GetAsync("/health/ready"); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                var report = await health.CheckReadinessAsync(); Assert.That(report.Checks, Has.Count.EqualTo(32)); Assert.That(report.Status, Is.EqualTo(HealthStatus.Healthy));
            }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task StartupFailureUnblocksReadinessWithoutWarmingAnyRoutes()
        {
            using var server = new TestWebServer(); using var health = new HealthCheckService(server);
            server.WithModule(new FailingStartupModule());
            var wait = health.WaitForReadyAsync(TimeSpan.FromSeconds(3));
            await Assert.ThatAsync(() => server.RunAsync(), Throws.TypeOf<InvalidOperationException>());
            await Assert.ThatAsync(async () => await wait, Throws.TypeOf<InvalidOperationException>());
            Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
        }
        private sealed class FailingStartupModule : WebModuleBase
        {
            internal FailingStartupModule() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override void OnStart(CancellationToken cancellationToken) => throw new InvalidOperationException("startup failed");
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
        }

        [Test]
        public async Task DrainRejectsPendingAndFutureReadinessBeforeLateCheckSuccess()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            using var server = new WebServer(HttpListenerMode.EmbedIO, "http://127.0.0.1:" + port + "/");
            var entered = Signal<bool>(); var release = Signal<HealthCheckResult>(); var finished = Signal<bool>();
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("held", async _ =>
            { entered.TrySetResult(true); try { return await release.Task; } finally { finished.TrySetResult(true); } }) });
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                var probe = health.CheckReadinessAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var draining = server.DrainAsync(TimeSpan.FromSeconds(3));
                Assert.That(server.DrainStarted.IsCancellationRequested, Is.True);
                Assert.That((await probe.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(HealthStatus.Unhealthy));
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
                release.TrySetResult(new HealthCheckResult(HealthStatus.Healthy)); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy)); await draining;
            }
            finally { release.TrySetResult(new HealthCheckResult(HealthStatus.Unhealthy)); stop.Cancel(); await running; }
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task RealHttpClientsReadinessAndDrain(int protocol)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var prefix = "http://127.0.0.1:" + port + "/";
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix); var ready = false;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("application", _ => Task.FromResult(new HealthCheckResult(ready ? HealthStatus.Healthy : HealthStatus.Unhealthy))) });
            server.WithModule(new HealthCheckModule(health));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(prefix) };
            try
            {
                using var first = await Send("ready"); Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                ready = true; await health.WaitForReadyAsync(TimeSpan.FromSeconds(3));
                using var second = await Send("ready"); Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(second.Version.Major, Is.EqualTo(protocol));
                await server.DrainAsync(TimeSpan.FromSeconds(3));
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
                await Assert.ThatAsync(() => health.WaitForReadyAsync(TimeSpan.FromSeconds(1)), Throws.TypeOf<InvalidOperationException>());
            }
            finally { stop.Cancel(); await running; }
            async Task<HttpResponseMessage> Send(string name)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "health/" + name) { Version = protocol == 1 ? HttpVersion.Version11 : HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                return await client.SendAsync(request);
            }
        }
    }
}
