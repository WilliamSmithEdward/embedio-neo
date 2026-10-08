using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Security;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue545_IPBanningIsolation
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task SameRouteServersKeepCriteriaWhitelistAndDisposalIndependent(HttpListenerMode mode)
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule(whitelist: new[] { "127.0.0.1", "::1" });
            first.RegisterCriterion(new Criterion());
            using var a = new Fixture(mode, first);
            using var b = new Fixture(mode, second);
            Assert.That(await a.Status(), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await b.Status(), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(first.BannedIPs.Count(), Is.EqualTo(1));
            Assert.That(second.BannedIPs, Is.Empty);
            a.Dispose();
            Assert.That(await b.Status(), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.TryBanClient(IPAddress.Parse("192.0.2.1"), 1), Is.True);
            Assert.That(IPBanningModule.GetBannedIPs().Count(), Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task SiblingGroupsWithSameRelativeRouteKeepExplicitBansIndependent(HttpListenerMode mode)
        {
            using var left = new IPBanningModule();
            using var right = new IPBanningModule();
            left.TryBanClient(IPAddress.Loopback, 1);
            left.TryBanClient(IPAddress.IPv6Loopback, 1);
            using var a = new ModuleGroup("/left", true).WithModule(left).WithModule(Reply());
            using var b = new ModuleGroup("/right", true).WithModule(right).WithModule(Reply());
            using var fixture = new Fixture(mode, a, b);
            Assert.That(await fixture.Status("left/"), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await fixture.Status("right/"), Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void InstanceBanOverloadsAndUnbanAreScoped(int overload)
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule();
            var before = DateTime.Now.AddMinutes(1).Ticks;
            var result = overload == 0 ? first.TryBanClient(IPAddress.Loopback, 1, false)
                : overload == 1 ? first.TryBanClient(IPAddress.Loopback, TimeSpan.FromMinutes(1), false)
                : first.TryBanClient(IPAddress.Loopback, DateTime.Now.AddMinutes(1), false);
            Assert.That(result, Is.True);
            Assert.That(first.BannedIPs.Single().ExpiresAt, Is.GreaterThanOrEqualTo(before));
            Assert.That(first.BannedIPs.Single().IsExplicit, Is.False);
            Assert.That(second.BannedIPs, Is.Empty);
            Assert.That(second.TryUnbanClient(IPAddress.Loopback), Is.False);
            Assert.That(first.TryUnbanClient(IPAddress.Loopback), Is.True);
            Assert.That(first.TryUnbanClient(IPAddress.Loopback), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void StaticOverloadsApplyToAllMatchingModulesOnly(int overload)
        {
            using var first = new IPBanningModule("/same");
            using var second = new IPBanningModule("/same");
            using var unrelated = new IPBanningModule("/other");
            var result = overload == 0 ? IPBanningModule.TryBanIP(IPAddress.Loopback, 1, "/same")
                : overload == 1 ? IPBanningModule.TryBanIP(IPAddress.Loopback, TimeSpan.FromMinutes(1), "/same")
                : IPBanningModule.TryBanIP(IPAddress.Loopback, DateTime.Now.AddMinutes(1), "/same");
            Assert.That(result, Is.True);
            Assert.That(first.BannedIPs.Count(), Is.EqualTo(1));
            Assert.That(second.BannedIPs.Count(), Is.EqualTo(1));
            Assert.That(unrelated.BannedIPs, Is.Empty);
            Assert.That(IPBanningModule.GetBannedIPs("/same").Count(), Is.EqualTo(1));
            Assert.That(IPBanningModule.TryUnbanIP(IPAddress.Loopback, "/same"), Is.True);
            Assert.That(first.BannedIPs, Is.Empty);
            Assert.That(second.BannedIPs, Is.Empty);
            Assert.That(IPBanningModule.TryUnbanIP(IPAddress.Loopback, "/same"), Is.False);
        }

        [Test]
        public void RouteUnionChoosesLatestExpiryAndInstanceViewsStayDistinct()
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule();
            first.TryBanClient(IPAddress.Loopback, 1);
            second.TryBanClient(IPAddress.Loopback, 2);
            Assert.That(IPBanningModule.GetBannedIPs().Single().ExpiresAt, Is.EqualTo(second.BannedIPs.Single().ExpiresAt));
            Assert.That(first.BannedIPs.Single().ExpiresAt, Is.LessThan(second.BannedIPs.Single().ExpiresAt));
        }

        [Test]
        public async Task AutomaticBanDurationsAndCriterionOwnershipAreIndependent()
        {
            using var first = new IPBanningModule(banMinutes: 1);
            using var second = new IPBanningModule(banMinutes: 30);
            var criterion = new Criterion();
            first.RegisterCriterion(criterion);
            second.RegisterCriterion(new Criterion());
            using var a = new Fixture(HttpListenerMode.EmbedIO, first);
            using var b = new Fixture(HttpListenerMode.EmbedIO, second);
            Assert.That(await a.Status(), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await b.Status(), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(second.BannedIPs.Single().ExpiresAt - first.BannedIPs.Single().ExpiresAt, Is.GreaterThan(TimeSpan.FromMinutes(28).Ticks));
            a.Dispose();
            Assert.That(criterion.Disposals, Is.EqualTo(1));
            Assert.That(second.BannedIPs.Count(), Is.EqualTo(1));
            Assert.That(await b.Status(), Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public void PurgingSurvivesSameRouteDisposalAndIsolatesCriterionFailures()
        {
            using var first = new IPBanningModule();
            using var faulty = new IPBanningModule();
            using var survivor = new IPBanningModule();
            var bad = new Criterion { FailPurge = true };
            var healthy = new Criterion();
            faulty.RegisterCriterion(bad);
            survivor.RegisterCriterion(healthy);
            survivor.TryBanClient(IPAddress.Loopback, DateTime.Now.AddMinutes(-1));
            first.Dispose();
            Purge();
            Assert.That(survivor.BannedIPs, Is.Empty);
            Assert.That(healthy.Purges, Is.EqualTo(1));
            Assert.That(bad.Purges, Is.EqualTo(1));
            survivor.TryBanClient(IPAddress.Loopback, 1);
            Assert.That(IPBanningModule.GetBannedIPs().Count(), Is.EqualTo(1));
        }

        [Test]
        public void UnbanClearsOnlyTheSelectedCriterionState()
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule();
            var a = new Criterion();
            var b = new Criterion();
            first.RegisterCriterion(a);
            second.RegisterCriterion(b);
            first.TryUnbanClient(IPAddress.Loopback);
            Assert.That(a.Clears, Is.EqualTo(1));
            Assert.That(b.Clears, Is.Zero);
            IPBanningModule.TryUnbanIP(IPAddress.Loopback);
            Assert.That(a.Clears, Is.EqualTo(2));
            Assert.That(b.Clears, Is.EqualTo(1));
        }

        [Test]
        public void StaticUnknownRouteKeepsArgumentErrorAndSingleModuleContract()
        {
            var route = "/" + Guid.NewGuid().ToString("N");
            Assert.That(Assert.Throws<ArgumentException>(() => IPBanningModule.GetBannedIPs(route)).ParamName, Is.EqualTo("baseRoute"));
            Assert.Throws<ArgumentException>(() => IPBanningModule.TryBanIP(IPAddress.Loopback, 1, route));
            Assert.Throws<ArgumentException>(() => IPBanningModule.TryUnbanIP(IPAddress.Loopback, route));
            using (var module = new IPBanningModule(route))
            {
                Assert.That(IPBanningModule.TryBanIP(IPAddress.Loopback, 1, route), Is.True);
                Assert.That(module.BannedIPs.Count(), Is.EqualTo(1));
            }
            Assert.Throws<ArgumentException>(() => IPBanningModule.GetBannedIPs(route));
        }

        [Test]
        public void DisposedInstanceControlsFailAndRepeatedDisposalDoesNotAffectSurvivor()
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule();
            first.Dispose();
            first.Dispose();
            Assert.Throws<ObjectDisposedException>(() => first.BannedIPs.ToArray());
            Assert.Throws<ObjectDisposedException>(() => first.TryBanClient(IPAddress.Loopback, 1));
            Assert.Throws<ObjectDisposedException>(() => first.TryUnbanClient(IPAddress.Loopback));
            Assert.That(second.TryBanClient(IPAddress.Loopback, 1), Is.True);
        }

        [Test]
        public async Task ConcurrentRegistrationRouteControlsPurgeAndDisposalKeepSurvivorRegistered()
        {
            using var survivor = new IPBanningModule("/race");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    using var transient = new IPBanningModule("/race");
                    IPBanningModule.TryBanIP(IPAddress.Loopback, 1, "/race");
                    IPBanningModule.GetBannedIPs("/race").ToArray();
                    IPBanningModule.TryUnbanIP(IPAddress.Loopback, "/race");
                    Purge();
                }
            }))).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(survivor.TryBanClient(IPAddress.Loopback, 1), Is.True);
            Assert.That(IPBanningModule.GetBannedIPs("/race").Count(), Is.EqualTo(1));
        }

        [Test]
        public async Task StartingOneModuleDoesNotLockAnotherModulesConfiguration()
        {
            using var first = new IPBanningModule();
            using var second = new IPBanningModule();
            using var a = new Fixture(HttpListenerMode.EmbedIO, first);
            Assert.That(await a.Status(), Is.EqualTo(HttpStatusCode.OK));
            Assert.DoesNotThrow(() => second.RegisterCriterion(new Criterion()));
            using var b = new Fixture(HttpListenerMode.EmbedIO, second);
            Assert.That(await b.Status(), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await a.Status(), Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void RegistryDoesNotKeepAbandonedModulesAlive()
        {
            var route = "/" + Guid.NewGuid().ToString("N");
            var weak = AbandonedModule(route);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
            Assert.That(weak.TryGetTarget(out _), Is.False);
            Purge();
            Assert.Throws<ArgumentException>(() => IPBanningModule.GetBannedIPs(route));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<IPBanningModule> AbandonedModule(string route) => new(new IPBanningModule(route));

        [Test]
        public async Task SlowCriterionClearDoesNotBlockUnrelatedModuleControls()
        {
            using var first = new IPBanningModule("/slow");
            using var independent = new IPBanningModule("/independent");
            using var criterion = new GatedCriterion(false);
            first.RegisterCriterion(criterion);
            var clearing = Task.Run(() => first.TryUnbanClient(IPAddress.Loopback));
            try
            {
                await criterion.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await Task.Run(() => independent.TryBanClient(IPAddress.Loopback, 1)).WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally { criterion.Release.Set(); await clearing.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Test]
        public async Task DisposalWaitsForItsActivePurgeButOtherModuleRemainsUsable()
        {
            using var first = new IPBanningModule();
            using var independent = new IPBanningModule();
            using var criterion = new GatedCriterion(true);
            first.RegisterCriterion(criterion);
            var purging = Task.Run(Purge);
            Task? disposing = null;
            try
            {
                await criterion.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                disposing = Task.Run(first.Dispose);
                Assert.That(await Task.Run(() => independent.TryBanClient(IPAddress.Loopback, 1)).WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(criterion.Disposed, Is.False);
            }
            finally
            {
                criterion.Release.Set();
                await purging.WaitAsync(TimeSpan.FromSeconds(5));
                if (disposing != null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(criterion.Disposed, Is.True);
            Assert.That(independent.BannedIPs.Count(), Is.EqualTo(1));
        }

        private sealed class GatedCriterion : IIPBanningCriterion
        {
            private readonly bool _gatePurge;
            public GatedCriterion(bool gatePurge) => _gatePurge = gatePurge;
            public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ManualResetEventSlim Release { get; } = new();
            public bool Disposed;
            public Task<bool> ValidateIPAddress(IPAddress address) => Task.FromResult(false);
            public void ClearIPAddress(IPAddress address) { if (!_gatePurge) Gate(); }
            public void PurgeData() { if (_gatePurge) Gate(); }
            private void Gate() { Entered.TrySetResult(true); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Criterion gate not released."); }
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Release.Set();
                Release.Dispose();
            }
        }

        private static void Purge() => ((((typeof(IPBanningModule)).Assembly.GetType("EmbedIO.Security.Internal.IPBanningExecutor")) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
            .GetMethod("Purge", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, null);

        private static ActionModule Reply() => new ActionModule("/", HttpVerbs.Any, context => context.SendStringAsync("ok", "text/plain", System.Text.Encoding.UTF8));

        private sealed class Criterion : IIPBanningCriterion
        {
            public int Clears, Purges, Disposals;
            public bool FailPurge;
            public Task<bool> ValidateIPAddress(IPAddress address) => Task.FromResult(true);
            public void ClearIPAddress(IPAddress address) => Clears++;
            public void PurgeData() { Purges++; if (FailPurge) throw new InvalidOperationException("controlled purge failure"); }
            public void Dispose() => Disposals++;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly WebServer _server;
            private readonly string _url = Resources.GetServerAddress();
            private readonly CancellationTokenSource _stop = new();
            private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
            private readonly Task _running;
            private bool _disposed;
            public Fixture(HttpListenerMode mode, params IWebModule[] modules)
            {
                _server = new WebServer(o => o.WithUrlPrefix(_url).WithMode(mode));
                foreach (var module in modules) _server.WithModule(module);
                if (modules.All(module => module is not ModuleGroup)) _server.WithModule(Reply());
                _running = _server.RunAsync(_stop.Token);
            }
            public async Task<HttpStatusCode> Status(string path = "")
            {
                using var response = await _client.GetAsync(_url + path);
                return response.StatusCode;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                try { _running.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                finally { _server.Dispose(); _client.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
