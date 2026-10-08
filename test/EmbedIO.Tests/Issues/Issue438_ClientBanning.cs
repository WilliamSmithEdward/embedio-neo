using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Security;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public partial class Issue438_ClientBanning
    {
        [TestCase("0.0.0.0/0", "255.255.255.255", true)]
        [TestCase("0.0.0.0/0", "::1", false)]
        [TestCase("192.0.2.129/25", "192.0.2.128", true)]
        [TestCase("192.0.2.129/25", "192.0.2.255", true)]
        [TestCase("192.0.2.129/25", "192.0.2.127", false)]
        [TestCase("192.0.2.128/31", "192.0.2.129", true)]
        [TestCase("192.0.2.128/31", "192.0.2.130", false)]
        [TestCase("192.0.2.128/32", "192.0.2.129", false)]
        [TestCase("192.0.2.128", "::ffff:192.0.2.128", true)]
        [TestCase("::ffff:192.0.2.128/120", "192.0.2.255", true)]
        [TestCase("::/0", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
        [TestCase("::/0", "::ffff:192.0.2.1", false)]
        [TestCase("2001:db8:abcd::1/48", "2001:db8:abcd:ffff::1", true)]
        [TestCase("2001:db8:abcd::1/48", "2001:db8:abce::1", false)]
        [TestCase("2001:db8::/127", "2001:db8::1", true)]
        [TestCase("2001:db8::/127", "2001:db8::2", false)]
        [TestCase("::1/128", "::1", true)]
        [TestCase("::1/128", "::2", false)]
        [TestCase("fe80::/64", "fe80::1234%5", true)]
        public void CidrBoundariesFamiliesAndMappedAddresses(string network, string address, bool expected)
            => Assert.That(Contains(Parse(network), IPAddress.Parse(address)), Is.EqualTo(expected));

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("localhost")]
        [TestCase("127.0.0.1/")]
        [TestCase("127.0.0.1/-1")]
        [TestCase("127.0.0.1/+1")]
        [TestCase("127.0.0.1/33")]
        [TestCase("127.0.0.1/24/1")]
        [TestCase("256.0.0.1/24")]
        [TestCase("127.1")]
        [TestCase("0x7f000001")]
        [TestCase("127.000.0.1")]
        [TestCase("::/129")]
        [TestCase("fe80::%4/64")]
        [TestCase("::ffff:192.0.2.1/95")]
        [TestCase(" ::1")]
        public void InvalidNetworkConfigurationIsRejected(string? network)
        {
            using var module = Create();
            Assert.Throws<ArgumentException>(() => module.WithDeniedNetworks(network!));
        }

        [TestCase("0.0.0.0/0")]
        [TestCase("192.0.2.0/24")]
        [TestCase("128.0.0.0/1")]
        [TestCase("::/0")]
        [TestCase("2001:db8::/32")]
        [TestCase("8000::/1")]
        public void CidrMembershipMatchesFrameworkOracleWithoutEnumeratingSubnets(string network)
        {
            var expected = IPNetwork.Parse(network);
            var actual = Parse(network);
            var random = new Random(438);
            for (var i = 0; i < 1000; i++)
            {
                var bytes = new byte[expected.BaseAddress.GetAddressBytes().Length];
                random.NextBytes(bytes);
                var address = new IPAddress(bytes);
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                Assert.That(Contains(actual, address), Is.EqualTo(expected.Contains(address)), address.ToString());
            }
        }

        [Test]
        public void KeyCaseSnapshotDurationAndCapacityContractsAreIndependent()
        {
            using var first = Create(capacity: 2);
            using var second = Create(capacity: 2);
            Assert.That(first.TryBanClient("Alice", TimeSpan.FromMinutes(10)), Is.True);
            var snapshot = first.BannedClients;
            var expiration = snapshot.Single().ExpiresAt;
            Assert.That(first.TryBanClient("Alice", TimeSpan.FromMinutes(1)), Is.True);
            Assert.That(first.BannedClients.Single().ExpiresAt, Is.EqualTo(expiration));
            Assert.That(first.TryBanClient("alice", TimeSpan.FromMinutes(1)), Is.True);
            Assert.That(first.TryBanClient("third", TimeSpan.FromMinutes(1)), Is.False);
            Assert.That(second.BannedClients, Is.Empty);
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(first.TryUnbanClient("Alice"), Is.True);
            Assert.That(first.TryUnbanClient("Alice"), Is.False);
            Assert.That(first.TryBanClient("third", TimeSpan.FromMinutes(1)), Is.True);
            Assert.That(first.BannedClients.Select(b => b.ClientKey), Is.EquivalentTo(new[] { "alice", "third" }));
        }

        [Test]
        public async Task ExpiredBansFreeCapacityWithoutAPurgeWorker()
        {
            using var module = Create(capacity: 1);
            module.TryBanClient("first", TimeSpan.FromMilliseconds(200));
            var expiration = module.BannedClients.Single().ExpiresAt;
            while (DateTimeOffset.UtcNow < expiration) await Task.Delay(10);
            Assert.That(module.BannedClients, Is.Empty);
            Assert.That(module.TryUnbanClient("first"), Is.False);
            Assert.That(module.TryBanClient("second", TimeSpan.FromMinutes(1)), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void InvalidClientKeysAreRejected(string? key)
        {
            using var module = Create();
            Assert.That(() => module.TryBanClient(key!, TimeSpan.FromMinutes(1)), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => module.TryUnbanClient(key!), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void InvalidDurationsLimitsCallbacksAndLongKeysAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new ClientBanningModule("/", null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClientBanningModule("/", _ => "key", maximumBannedClients: 0));
            using var module = Create();
            Assert.Throws<ArgumentOutOfRangeException>(() => module.TryBanClient("key", TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => module.TryBanClient("key", TimeSpan.MinValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => module.TryBanClient("key", TimeSpan.MaxValue));
            Assert.Throws<ArgumentException>(() => module.TryBanClient(new string('x', 1025), TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentNullException>(() => module.WithCriterion(null!));
            Assert.Throws<ArgumentNullException>(() => module.WithAllowedNetworks(null!));
        }

        [Test]
        public async Task ConcurrentControlsNeverExceedCapacityOrAffectOtherModules()
        {
            using var module = Create(capacity: 16);
            using var unrelated = Create();
            await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            {
                for (var j = 0; j < 50; j++)
                {
                    module.TryBanClient(i.ToString(), TimeSpan.FromSeconds(30));
                    Assert.That(module.BannedClients.Count, Is.LessThanOrEqualTo(16));
                    module.TryUnbanClient(i.ToString());
                }
            })));
            Assert.That(unrelated.BannedClients, Is.Empty);
        }

        [Test]
        public void StartupFreezesPolicyButNotLiveControlsAndDisposalIsIdempotent()
        {
            var module = Create();
            module.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => module.WithAllowedNetworks("127.0.0.1"));
            Assert.Throws<InvalidOperationException>(() => module.WithDeniedNetworks("::1"));
            Assert.Throws<InvalidOperationException>(() => module.WithCriterion(_ => Task.FromResult(false)));
            Assert.That(module.TryBanClient("key", TimeSpan.FromMinutes(1)), Is.True);
            Assert.That(module.TryUnbanClient("key"), Is.True);
            module.Dispose(); module.Dispose();
            Assert.Throws<ObjectDisposedException>(() => { _ = module.BannedClients; });
            Assert.Throws<ObjectDisposedException>(() => module.TryBanClient("key", TimeSpan.FromMinutes(1)));
            Assert.Throws<ObjectDisposedException>(() => module.TryUnbanClient("key"));
            Assert.Throws<ObjectDisposedException>(() => module.Start(CancellationToken.None));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task LiveBanAndUnbanPreserveServerAndHealthyClients(HttpListenerMode mode)
        {
            using var module = Create();
            await using var host = new Host(mode, module);
            Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
            module.TryBanClient("Alice", TimeSpan.FromMinutes(1));
            Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await host.Status("alice"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await host.Status("Bob"), Is.EqualTo(HttpStatusCode.OK));
            module.TryUnbanClient("Alice");
            Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(host.Hits, Is.EqualTo(4));
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task ContextCriterionPersistsKeysAcrossDifferentRequestsAndNestedRoutes(HttpListenerMode mode, bool nested)
        {
            using var module = Create().WithCriterion(c => Task.FromResult(c.Request.Headers["User-Agent"] == "blocked-agent"));
            await using var host = new Host(mode, module, nested);
            Assert.That(await host.Status("Alice", "blocked-agent"), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await host.Status("Alice", "healthy-agent"), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await host.Status("Bob", "healthy-agent"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(module.BannedClients.Single().ClientKey, Is.EqualTo("Alice"));
            module.TryUnbanClient("Alice");
            Assert.That(await host.Status("Alice", "healthy-agent"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(host.Hits, Is.EqualTo(2));
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task PermanentNetworkPoliciesHaveExplicitPrecedenceAndDoNotBecomeTemporaryBans(HttpListenerMode mode, bool allow)
        {
            var callbacks = 0;
            using var module = Create().WithDeniedNetworks("0.0.0.0/0")
                .WithCriterion(_ => { callbacks++; return Task.FromResult(true); });
            if (allow) module.WithAllowedNetworks("127.0.0.42/8");
            module.TryBanClient("Alice", TimeSpan.FromMinutes(1));
            await using var host = new Host(mode, module);
            var expected = allow ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
            Assert.That(await host.Status("Alice"), Is.EqualTo(expected));
            module.TryUnbanClient("Alice");
            Assert.That(await host.Status("Bob"), Is.EqualTo(expected));
            Assert.That(module.BannedClients, Is.Empty);
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ManagedIpv6TransportExercisesDenyAndAllowPrecedence(bool allow)
        {
            if (!System.Net.Sockets.Socket.OSSupportsIPv6) Assert.Ignore("IPv6 transport is unavailable on this host.");
            var port = new Uri(Resources.GetServerAddress()).Port;
            var url = $"http://[::1]:{port}/";
            using var module = new ClientBanningModule("/restricted", _ => "key", maximumBannedClients: 1)
                .WithDeniedNetworks("::/0");
            module.TryBanClientPermanently("key");
            if (allow) module.WithAllowedNetworks("::1/128");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(module).OnGet("/", c => c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var response = await client.GetAsync(url + "restricted");
                Assert.That(response.StatusCode, Is.EqualTo(allow ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("ok"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task FailedNetworkBatchDoesNotPartiallyAddADenyRule(HttpListenerMode mode)
        {
            using var module = Create();
            Assert.Throws<ArgumentException>(() => module.WithDeniedNetworks("127.0.0.1", "invalid"));
            await using var host = new Host(mode, module);
            Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task CapacityIsFailClosedAndUnbanRestoresAvailability(HttpListenerMode mode)
        {
            using var module = Create(capacity: 1);
            await using var host = new Host(mode, module);
            module.TryBanClient("Alice", TimeSpan.FromMinutes(1));
            Assert.That(await host.Status("Bob"), Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(module.TryBanClient("Bob", TimeSpan.FromMinutes(1)), Is.False);
            Assert.That(module.BannedClients.Count, Is.EqualTo(1));
            module.TryUnbanClient("Alice");
            Assert.That(await host.Status("Bob"), Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task CallbackFailureRejectsOnlyItsRequestAndHealthyFollowUpWorks(HttpListenerMode mode)
        {
            using var module = Create().WithCriterion(c => c.Request.Headers["X-Client"] == "failure"
                ? Task.FromException<bool>(new InvalidOperationException("controlled criterion failure")) : Task.FromResult(false));
            await using var host = new Host(mode, module);
            Assert.That(await host.Status("failure"), Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(await host.Status("healthy"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(module.BannedClients, Is.Empty);
            Assert.That(host.Hits, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.Microsoft, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        [TestCase(HttpListenerMode.EmbedIO, 3)]
        [TestCase(HttpListenerMode.Microsoft, 3)]
        public async Task BanOrDisposeDuringCallbackCannotAllowThePendingRequest(HttpListenerMode mode, int action)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var module = Create(capacity: 1).WithCriterion(async _ => { entered.TrySetResult(true); await release.Task; return false; });
            await using var host = new Host(mode, module);
            var request = host.Status("Alice");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (action == 2) module.Dispose();
                else if (action == 1) module.TryBanClientPermanently("Alice");
                else module.TryBanClient(action == 3 ? "other" : "Alice", TimeSpan.FromMinutes(1));
                release.TrySetResult(true);
                Assert.That(await request, Is.EqualTo(action == 2 ? HttpStatusCode.InternalServerError : HttpStatusCode.Forbidden));
                Assert.That(host.Hits, Is.Zero);
            }
            finally { release.TrySetResult(true); await request; }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task CancellationAfterCriterionCannotRecordABanOrRunTheHandler(HttpListenerMode mode)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var module = Create().WithCriterion(async context =>
            {
                entered.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { cancelled.TrySetResult(true); }
                return true;
            });
            await using var host = new Host(mode, module);
            var request = host.Response("Alice");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var response = await request;
                // Unix HttpListener can close a cancelled context with its default
                // empty 200 response. The protected response must never be emitted.
                Assert.That(response.Body, Is.Not.EqualTo("ok"));
                if (response.Status == HttpStatusCode.OK) Assert.That(response.Body, Is.Empty);
                TestContext.Out.WriteLine($"Cancelled response: {response.Status}; body length: {response.Body.Length}; protected handler hits: {host.Hits}");
            }
            catch (HttpRequestException) { /* Server shutdown may close the transport before an HTTP response. */ }
            Assert.That(module.BannedClients, Is.Empty);
            Assert.That(host.Hits, Is.Zero);
        }

        private static ClientBanningModule Create(int capacity = 4096) => new("/", c => c.Request.Headers["X-Client"] ?? "anonymous", maximumBannedClients: capacity);
        private static readonly Type NetworkType = typeof(ClientBanningModule).Assembly.GetType("EmbedIO.Security.Internal.ClientNetwork")!;
        private static object Parse(string network) => NetworkType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { network })!;
        private static bool Contains(object network, IPAddress address) => (bool)NetworkType.GetMethod("Contains", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(network, new object[] { address })!;

        private sealed class Host : IAsyncDisposable
        {
            private readonly string _url;
            private readonly CancellationTokenSource _stop = new();
            private readonly HttpClient _client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            private readonly Task _running;
            private readonly string _path;
            public WebServer Server { get; }
            public string Url => _url;
            public int Hits;
            public Host(HttpListenerMode mode, ClientBanningModule module, bool nested = false, string? url = null)
            {
                _url = url ?? Resources.GetServerAddress().Replace("localhost", "127.0.0.1");
                Server = new WebServer(o => o.WithUrlPrefix(_url).WithMode(mode));
                var handler = new ActionModule("/", HttpVerbs.Any, c => { Interlocked.Increment(ref Hits); return c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding); });
                if (nested) Server.WithModule(new ModuleGroup("/nested", false).WithModule(module).WithModule(handler));
                else Server.WithModule(module).WithModule(handler);
                _path = nested ? "nested/child" : "child";
                _running = Server.RunAsync(_stop.Token);
            }
            public async Task<HttpStatusCode> Status(string key, string agent = "healthy-agent")
                => (await Response(key, agent)).Status;

            public async Task<(HttpStatusCode Status, string Body)> Response(string key, string agent = "healthy-agent")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url + _path);
                request.Headers.TryAddWithoutValidation("X-Client", key);
                request.Headers.TryAddWithoutValidation("User-Agent", agent);
                using var response = await _client.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { _client.Dispose(); Server.Dispose(); _stop.Dispose(); }
            }

            public async Task StopAsync() { _stop.Cancel(); await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
