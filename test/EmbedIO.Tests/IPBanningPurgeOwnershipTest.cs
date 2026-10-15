using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class IPBanningPurgeOwnershipTest
    {
        [Test]
        public async Task ExpiredSnapshotCannotRemoveAnAutomaticallyRenewedBan()
        {
            using var module = new IPBanningModule("/purge-ban-ownership");
            module.RegisterCriterion(new AlwaysBan());
            using var address = new PausingAddress();
            Assert.That(module.TryBanClient(address, DateTime.Now.AddMinutes(-1)), Is.True);
            var configuration = Configuration(module);
            var purging = Task.Run(() => address.RunPaused(configuration.Purge));
            try
            {
                await address.Paused.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAsync<HttpException>(async () => await configuration.CheckClient(address));
            }
            finally
            {
                address.Resume();
                await purging.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(module.BannedIPs.Any(ban => ban.IPAddress.Equals(address) && ban.ExpiresAt > DateTime.Now.Ticks),
                Is.True, "Purge must remove only the expired entry it inspected, not a replacement ban.");
        }

        [Test]
        public async Task RegexPurgeCannotDiscardAConcurrentRecentMatch()
        {
            using var module = new IPBanningModule("/purge-regex-ownership");
            using var criterion = new IPBanningRegexCriterion(module, new[] { "ownership-probe" }, 2, 600);
            using var address = new PausingAddress();
            var match = typeof(IPBanningRegexCriterion).GetMethod("MatchIP", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing regex match boundary.");
            void RecordMatch() => match.Invoke(criterion, new object[] { address, "ownership-probe" });
            RecordMatch();
            Assert.That(await criterion.ValidateIPAddress(address), Is.False);

            var purging = Task.Run(() => address.RunPaused(criterion.PurgeData));
            Task? matching = null;
            try
            {
                await address.Paused.WaitAsync(TimeSpan.FromSeconds(10));
                matching = Task.Run(RecordMatch);
                // The original implementation allows the append to finish after its
                // snapshot. A synchronized implementation may wait for purge instead;
                // release the pause in either case, then assert the final hit count.
                await Task.WhenAny(matching, Task.Delay(TimeSpan.FromSeconds(1)));
            }
            finally
            {
                address.Resume();
                await purging.WaitAsync(TimeSpan.FromSeconds(10));
                if (matching != null) await matching.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(await criterion.ValidateIPAddress(address), Is.True,
                "Both recent matches must survive the purge.");
        }

        [Test]
        public void PurgeRemovesExpiredBansAndPreservesLiveBans()
        {
            using var module = new IPBanningModule("/purge-ban-control");
            var expired = IPAddress.Parse("192.0.2.18");
            var live = IPAddress.Parse("192.0.2.19");
            Assert.That(module.TryBanClient(expired, DateTime.Now.AddMinutes(-1)), Is.True);
            Assert.That(module.TryBanClient(live, DateTime.Now.AddMinutes(10)), Is.True);

            Configuration(module).Purge();

            Assert.That(module.BannedIPs.Select(ban => ban.IPAddress), Is.EquivalentTo(new[] { live }));
        }

        [Test]
        public async Task RegexPurgePreservesRecentMatchesAndClearResetsThem()
        {
            using var module = new IPBanningModule("/purge-regex-control");
            using var criterion = new IPBanningRegexCriterion(module, new[] { "ownership-probe" }, 2, 600);
            var address = IPAddress.Parse("192.0.2.20");
            var match = typeof(IPBanningRegexCriterion).GetMethod("MatchIP", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing regex match boundary.");
            match.Invoke(criterion, new object[] { address, "ownership-probe" });
            match.Invoke(criterion, new object[] { address, "ownership-probe" });

            criterion.PurgeData();
            Assert.That(await criterion.ValidateIPAddress(address), Is.True);
            criterion.ClearIPAddress(address);
            criterion.PurgeData();
            Assert.That(await criterion.ValidateIPAddress(address), Is.False);
        }

        private static IPBanningConfiguration Configuration(IPBanningModule module)
            => (IPBanningConfiguration)(typeof(IPBanningModule).GetProperty("Configuration", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(module)
                ?? throw new AssertionException("Missing module configuration."));

        // The dictionary looks up the current entry, then hashes its key again when
        // removing/replacing it. Pause at that public virtual boundary: no collection
        // fields or runtime internals are modified by the fixture.
        private sealed class PausingAddress : IPAddress, IDisposable
        {
            private readonly TaskCompletionSource<bool> _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _resume = new(false);
            private int _thread;
            private int _hashes;

            internal PausingAddress() : base(new byte[] { 192, 0, 2, 17 }) { }
            internal Task Paused => _paused.Task;
            internal void Resume() => _resume.Set();
            internal void RunPaused(Action action)
            {
                _thread = Environment.CurrentManagedThreadId;
                try { action(); }
                finally { _thread = 0; }
            }
            public override int GetHashCode()
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref _thread) && ++_hashes == 2)
                {
                    _paused.TrySetResult(true);
                    if (!_resume.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Purge ownership probe was not released.");
                }
                return base.GetHashCode();
            }
            public void Dispose() => _resume.Dispose();
        }

        private sealed class AlwaysBan : IIPBanningCriterion
        {
            public Task<bool> ValidateIPAddress(IPAddress address) => Task.FromResult(true);
            public void ClearIPAddress(IPAddress address) { }
            public void PurgeData() { }
            public void Dispose() { }
        }
    }
}
