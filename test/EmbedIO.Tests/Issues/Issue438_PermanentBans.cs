using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public partial class Issue438_ClientBanning
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task PermanentBansWorkLiveAndAcrossRealServerRestarts(HttpListenerMode mode)
        {
            using var folder = new StoreFolder();
            string url;
            using (var first = Create().WithPermanentBanStore(folder.File))
            {
                await using var host = new Host(mode, first);
                url = host.Url;
                Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
                Assert.That(first.TryBanClientPermanently("Alice"), Is.True);
                Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(await host.Status("Bob"), Is.EqualTo(HttpStatusCode.OK));
                Assert.That(first.TryUnbanClient("Alice"), Is.False);
                first.TryBanClient("temporary", TimeSpan.FromMinutes(1));
                Assert.That(await host.Status("temporary"), Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(first.BannedClients.Single().ClientKey, Is.EqualTo("temporary"));
            }
            using (var second = Create().WithPermanentBanStore(folder.File))
            {
                await using var host = new Host(mode, second, url: url);
                Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(await host.Status("temporary"), Is.EqualTo(HttpStatusCode.OK));
                Assert.That(second.BannedClients, Is.Empty);
                Assert.That(second.PermanentBannedClients, Is.EqualTo(new[] { "Alice" }));
                Assert.That(second.TryUnbanClientPermanently("Alice"), Is.True);
                Assert.That(await host.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
            }
            using var third = Create().WithPermanentBanStore(folder.File);
            await using var finalHost = new Host(mode, third, url: url);
            Assert.That(third.PermanentBannedClients, Is.Empty);
            Assert.That(await finalHost.Status("Alice"), Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void InMemoryPermanentBansAreExplicitAndShareTheCapacityBound()
        {
            using var module = Create(capacity: 2);
            module.TryBanClient("temporary", TimeSpan.FromMinutes(1));
            Assert.That(module.TryBanClientPermanently("temporary"), Is.True);
            Assert.That(module.BannedClients, Is.Empty);
            Assert.That(module.TryBanClient("temporary", TimeSpan.FromMinutes(1)), Is.False);
            var snapshot = module.PermanentBannedClients;
            Assert.That(module.TryBanClientPermanently("second"), Is.True);
            Assert.That(module.TryBanClientPermanently("third"), Is.False);
            Assert.That(module.TryBanClient("third", TimeSpan.FromMinutes(1)), Is.False);
            Assert.That(snapshot, Is.EqualTo(new[] { "temporary" }));
            Assert.That(module.TryUnbanClientPermanently("temporary"), Is.True);
            Assert.That(module.TryUnbanClientPermanently("temporary"), Is.False);
            Assert.That(module.TryBanClient("third", TimeSpan.FromMinutes(1)), Is.True);
            using var unrelated = Create();
            Assert.That(unrelated.PermanentBannedClients, Is.Empty);
        }

        [TestCase("{")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"version\":2,\"keys\":[]}")]
        [TestCase("{\"version\":\"1\",\"keys\":[]}")]
        [TestCase("{\"version\":1,\"keys\":null}")]
        [TestCase("{\"version\":1,\"keys\":[1]}")]
        [TestCase("{\"version\":1,\"keys\":[\" \" ]}")]
        [TestCase("{\"version\":1,\"keys\":[\"Alice\",\"Alice\"]}")]
        [TestCase("{\"version\":1,\"keys\":[\"\\uD800\"]}")]
        [TestCase("{\"version\":1,\"keys\":[],\"extra\":true}")]
        [TestCase("{\"version\":1,\"version\":1,\"keys\":[]}")]
        [TestCase("{\"version\":1,\"keys\":[],}")]
        public void CorruptStoreIsNotSilentlyDroppedOrOverwritten(string content)
        {
            using var folder = new StoreFolder();
            File.WriteAllText(folder.File, content);
            var original = File.ReadAllBytes(folder.File);
            using var module = Create();
            var failed = false;
            try { module.WithPermanentBanStore(folder.File); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { failed = true; }
            Assert.That(failed, Is.True);
            Assert.That(module.PermanentBannedClients, Is.Empty);
            Assert.That(File.ReadAllBytes(folder.File), Is.EqualTo(original));
            File.WriteAllText(folder.File, "{\"version\":1,\"keys\":[\"repaired\"]}");
            module.WithPermanentBanStore(folder.File);
            Assert.That(module.PermanentBannedClients, Is.EqualTo(new[] { "repaired" }));
        }

        [Test]
        public void StoreRejectsCapacityOverflowWithoutChangingMemoryOrDisk()
        {
            using var folder = new StoreFolder();
            File.WriteAllText(folder.File, "{\"version\":1,\"keys\":[\"Alice\",\"Bob\"]}");
            var original = File.ReadAllBytes(folder.File);
            using var module = Create(capacity: 1);
            Assert.Throws<InvalidDataException>(() => module.WithPermanentBanStore(folder.File));
            Assert.That(File.ReadAllBytes(folder.File), Is.EqualTo(original));
            Assert.That(module.PermanentBannedClients, Is.Empty);
        }

        [Test]
        public void StoreWriteFailureDoesNotConfirmAPermanentBanOrUnban()
        {
            using var folder = new StoreFolder();
            using var module = Create().WithPermanentBanStore(folder.File);
            module.TryBanClientPermanently("Alice");
            var original = File.ReadAllBytes(folder.File);
            var saved = folder.File + ".saved";
            File.Move(folder.File, saved);
            Directory.CreateDirectory(folder.File);
            try
            {
                Assert.Throws<IOException>(() => module.TryBanClientPermanently("Bob"));
                Assert.Throws<IOException>(() => module.TryUnbanClientPermanently("Alice"));
                Assert.That(module.PermanentBannedClients, Is.EqualTo(new[] { "Alice" }));
                Assert.That(File.ReadAllBytes(saved), Is.EqualTo(original));
                Assert.That(Directory.GetFiles(folder.Path, "*.tmp"), Is.Empty);
            }
            finally { Directory.Delete(folder.File); File.Move(saved, folder.File); }
            Assert.That(module.TryUnbanClientPermanently("Alice"), Is.True);
            Assert.That(module.PermanentBannedClients, Is.Empty);
        }

        [Test]
        public void CooperatingWriterLeaseIsReleasedOnDispose()
        {
            using var folder = new StoreFolder();
            var first = Create().WithPermanentBanStore(folder.File);
            using var second = Create();
            try
            {
                first.TryBanClientPermanently("Alice");
                Assert.Throws<IOException>(() => second.WithPermanentBanStore(folder.File));
            }
            finally { first.Dispose(); }
            second.WithPermanentBanStore(folder.File);
            Assert.That(second.PermanentBannedClients, Is.EqualTo(new[] { "Alice" }));
        }

        [Test]
        public void UnicodeKeysRoundTripExactlyAndStagingFilesAreNotLoaded()
        {
            using var folder = new StoreFolder();
            var key = "Ålice_東京_😀_\"\\";
            using (var first = Create().WithPermanentBanStore(folder.File))
            {
                first.TryBanClientPermanently(key);
                Assert.Throws<ArgumentException>(() => first.TryBanClientPermanently("\uD800"));
                Assert.Throws<ArgumentException>(() => first.TryBanClient("\uDC00", TimeSpan.FromMinutes(1)));
            }
            File.WriteAllText(folder.File + ".abandoned.tmp", "{incomplete");
            using var second = Create().WithPermanentBanStore(folder.File);
            Assert.That(second.PermanentBannedClients, Is.EqualTo(new[] { key }));
        }

        [Test]
        public void StoreMergesPreconfiguredPermanentKeysAndPromotesTemporaryKeys()
        {
            using var folder = new StoreFolder();
            File.WriteAllText(folder.File, "{\"version\":1,\"keys\":[\"loaded\"]}");
            using (var module = Create(capacity: 2))
            {
                module.TryBanClientPermanently("configured");
                module.TryBanClient("loaded", TimeSpan.FromMinutes(1));
                module.WithPermanentBanStore(folder.File);
                Assert.That(module.BannedClients, Is.Empty);
                Assert.That(module.PermanentBannedClients, Is.EquivalentTo(new[] { "loaded", "configured" }));
                module.Start(CancellationToken.None);
                Assert.Throws<InvalidOperationException>(() => module.WithPermanentBanStore(folder.File + ".other"));
                Assert.That(module.TryUnbanClientPermanently("loaded"), Is.True);
            }
            using var next = Create().WithPermanentBanStore(folder.File);
            Assert.That(next.PermanentBannedClients, Is.EqualTo(new[] { "configured" }));
        }

        [Test]
        public async Task ConcurrentPermanentUpdatesPersistTheSameOrdinalSetAsMemory()
        {
            using var folder = new StoreFolder();
            string[] snapshot;
            using (var module = Create(capacity: 32).WithPermanentBanStore(folder.File))
            {
                await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
                {
                    var key = "client-" + i;
                    module.TryBanClientPermanently(key);
                    module.TryUnbanClientPermanently(key);
                    module.TryBanClientPermanently(key);
                })));
                snapshot = module.PermanentBannedClients.ToArray();
            }
            using var reopened = Create(capacity: 32).WithPermanentBanStore(folder.File);
            Assert.That(reopened.PermanentBannedClients.Count, Is.EqualTo(16));
            Assert.That(reopened.PermanentBannedClients, Is.EquivalentTo(snapshot));
        }

        private sealed class StoreFolder : IDisposable
        {
            public string Path { get; }
            public string File => System.IO.Path.Combine(Path, "bans.json");
            public StoreFolder()
            {
                var root = System.IO.Path.GetFullPath("TestResults/upstream-438/store-tests");
                Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")));
                Assert.That(Path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
                Directory.CreateDirectory(Path);
            }
            public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        }
    }
}
