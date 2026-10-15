using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Native MsQuic provider lifetime and resource coverage for program #181.
    // Application HTTP/3 still uses System.Net.Quic; this file drives only the
    // internal native provider. The explicit campaigns are bounded and write
    // evidence; docs/project/http3-native-lifetime-campaigns.md describes them.
    [NonParallelizable]
    public sealed class MsQuicNativeLifetimeCampaignTest
    {
        private const string CampaignCategory = "NativeQuicLifetimeCampaign";
        private static readonly byte[] H3 = { (byte)'h', (byte)'3' };
        private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);
        private static readonly ConcurrentQueue<string> Unobserved = new();
        // Darwin closes a released MsQuic socket on a later kqueue turn, so a
        // hang report states whether the listener port was handed out recently.
        private static readonly ConcurrentQueue<(int Port, DateTime Bound, string Role)> RecentPorts = new();
        private static void RememberPort(int port, string role)
        {
            RecentPorts.Enqueue((port, DateTime.UtcNow, role));
            while (RecentPorts.Count > 4096) RecentPorts.TryDequeue(out _);
        }
        private static string PortHistory(int port)
        {
            var uses = RecentPorts.Where(entry => entry.Port == port).Select(entry => entry.Role + " " + entry.Bound.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).ToList();
            return "port " + port.ToString(CultureInfo.InvariantCulture) + " used " + uses.Count.ToString(CultureInfo.InvariantCulture)
                + " time(s) among the last 4096 listener and client sockets (" + string.Join(", ", uses) + ")";
        }
        private static int _tracking;

        [TestCase(181)]
        [TestCase(238)]
        public async Task RepeatedOwnershipChurnReturnsNativeResourcesToBaseline(int seed)
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("churn-regression", seed);
            var random = new Random(seed);
            for (var i = 0; i < 6; i++) { trace.Iteration = i; await ChurnOnce(random, trace).ConfigureAwait(false); }
            probe.AssertReturned(baseline, trace);
        }

        // A pending read and a committed, flow-blocked write share one stream
        // while a single ending is applied. Each ending must resolve both.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public async Task PendingReadAndCommittedBlockedWriteResolveTogether(int ending)
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("pending-regression", ending);
            await PendingBothOnce(ending, trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        // Only part of one receive indication is consumed before the stream or
        // its connection ends; the remaining borrowed bytes must not hold
        // native shutdown or close open.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task PartiallyConsumedReceiveIndicationDoesNotHoldShutdown(int ending)
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("partial-receive-regression", ending);
            await PartialReceiveOnce(ending, trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        [Test]
        public async Task StreamAdmissionOverflowRejectsOnlyTheExcessAndAdmitsLaterStreams()
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("stream-admission-regression", 0);
            await StreamOverflowOnce(trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        [Test]
        public async Task ConnectionAdmissionOverflowRecoversForSubsequentConnections()
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("connection-admission-regression", 0);
            await ConnectionOverflowOnce(trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        [TestCase(7)]
        [TestCase(11)]
        public async Task ConcurrentDisposalWithInFlightPeerTrafficSettles(int seed)
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("concurrent-dispose-regression", seed);
            await ConcurrentDisposeOnce(new Random(seed), trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        [Test]
        public async Task ParentsStayOpenForTheirLastChildAndThenRelease()
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("parent-retention-regression", 0);
            await ParentRetentionOnce(trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        // Local direction operations arrive with the stream-control and
        // completion increments; earlier source revisions report their absence.
        [TestCase(false)]
        [TestCase(true)]
        public async Task LocalAbortAndGracefulFinResolveOnlyTheirDirectionUnderPendingOperations(bool fin)
        {
            if (!RequireQuic()) return;
            if (Capabilities.Abort == null) { Assert.Ignore("This source revision has no native stream Abort operation."); return; }
            if (fin && Capabilities.CompleteWrites == null) { Assert.Ignore("This source revision has no native graceful CompleteWrites operation."); return; }
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("direction-regression", fin ? 1 : 0);
            await DirectionOnce(fin ? 1 : 0, trace).ConfigureAwait(false);
            probe.AssertReturned(baseline, trace);
        }

        // Finalization is the only owner of abandoned stream and connection
        // wrappers; it must still shut them down and release every parent.
        [Test]
        public async Task AbandonedNativeHandlesAreReleasedByFinalization()
        {
            if (!RequireQuic()) return;
            using var probe = Probe.Open();
            var baseline = probe.Settle(null);
            var trace = new Trace("abandoned-regression", 0);
            var (parents, peer, remote) = await AbandonOnce(trace).ConfigureAwait(false);
            try
            {
                await Eventually(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    return parents.All(parent => !parent.TryGetTarget(out var owner) || owner.IsClosed);
                }, trace + ": finalization did not release the retained parents.").ConfigureAwait(false);
            }
            finally
            {
                await remote.DisposeAsync().ConfigureAwait(false);
                await peer.DisposeAsync().ConfigureAwait(false);
            }
            probe.AssertReturned(baseline, trace);
        }

        // Confirmed defect reproductions (docs/project/http3-native-lifetime-campaigns.md).
        // They fail on a4f7105 and on the combined pre-correction native core;
        // the deferred-close correction promotes them to ordinary regression coverage.
        [Test, Category("NativeQuicLifetimeRegression")]
        public async Task DisposedEstablishedConnectionsNotifyEveryPeerWithTheApplicationCode()
        {
            if (!RequireQuic()) return;
            await DisposeEstablishedOnce(4, new Trace("established-dispose-defect", 0)).ConfigureAwait(false);
        }

        [Test, Category("NativeQuicLifetimeRegression")]
        public async Task ConnectionOverflowDrainByDisposalRefusesEachPeerOnce()
        {
            if (!RequireQuic()) return;
            await DisposeQueuedOnce(16, new Trace("queued-dispose-defect", 0)).ConfigureAwait(false);
        }

        // A completed final write (FIN) followed by disposal must not reset
        // the stream before the peer has read the committed bytes.
        [Test, Category("NativeQuicLifetimeRegression")]
        public async Task DisposalAfterACompletedFinalWriteDeliversEveryByte()
        {
            if (!RequireQuic()) return;
            await FinThenDisposeOnce(65536, 8, new Trace("fin-dispose-defect", 0)).ConfigureAwait(false);
        }

        // Bounded campaigns, outside the ordinary suite. Select with
        // --filter "TestCategory=NativeQuicLifetimeCampaign"; see the guide for
        // the iteration, seed, output and source-revision variables.
        [Test, Explicit("Bounded native lifetime campaign."), Category(CampaignCategory)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public Task CampaignOwnershipChurn() => Campaign("ownership-churn", 1, (random, trace) => ChurnOnce(random, trace));

        [Test, Explicit("Bounded native lifetime campaign."), Category(CampaignCategory)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public Task CampaignPendingOperationEndings() => Campaign("pending-endings", 1, (random, trace) =>
        {
            var choice = random.Next(Capabilities.Abort == null ? 9 : 11);
            return choice < 6 ? PendingBothOnce(choice, trace)
                : choice < 9 ? PartialReceiveOnce(choice - 6, trace)
                : DirectionOnce(Capabilities.CompleteWrites == null ? 0 : choice - 9, trace);
        });

        [Test, Explicit("Bounded native lifetime campaign."), Category(CampaignCategory)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public Task CampaignConcurrentDisposal() => Campaign("concurrent-disposal", 1, (random, trace) => ConcurrentDisposeOnce(random, trace));

        [Test, Explicit("Bounded native lifetime campaign."), Category(CampaignCategory)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public Task CampaignAdmissionOverflow() => Campaign("admission-overflow", 10, (random, trace) =>
            random.Next(2) == 0 ? StreamOverflowOnce(trace) : ConnectionOverflowOnce(trace));

        [Test, Explicit("Bounded native lifetime campaign."), Category(CampaignCategory)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public Task CampaignParentRetention() => Campaign("parent-retention", 1, (_, trace) => ParentRetentionOnce(trace));

        private static async Task Campaign(string name, int divisor, Func<Random, Trace, Task> body)
        {
            if (!RequireQuic()) return;
            var iterations = Math.Max(1, Setting("EMBEDIO_NATIVE_QUIC_CAMPAIGN_ITERATIONS", 200) / divisor);
            var seed = Setting("EMBEDIO_NATIVE_QUIC_CAMPAIGN_SEED", 20261010);
            var output = Environment.GetEnvironmentVariable("EMBEDIO_NATIVE_QUIC_CAMPAIGN_OUTPUT")
                ?? Path.Combine("TestResults", "native-quic-lifetime");
            using var probe = Probe.Open();
            var trace = new Trace(name, seed);
            var samples = new List<Sample> { probe.Settle("baseline") };
            var random = new Random(seed);
            var clock = Stopwatch.StartNew();
            string? failure = null;
            try
            {
                var every = Math.Max(1, iterations / 10);
                for (var i = 0; i < iterations; i++)
                {
                    trace.Iteration = i;
                    await body(random, trace).ConfigureAwait(false);
                    if ((i + 1) % every == 0) samples.Add(probe.Sample("iteration-" + (i + 1).ToString(CultureInfo.InvariantCulture), i + 1));
                }
                samples.Add(probe.Settle("quiescent"));
                probe.AssertReturned(samples[0], trace);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = trace + Environment.NewLine + error;
                throw;
            }
            finally
            {
                if (failure != null) samples.Add(probe.Sample("after-failure", trace.Iteration));
                probe.Write(output, name, seed, iterations, trace, samples, failure, clock.Elapsed);
            }
        }

        // EMBEDIO_REQUIRE_QUIC=1 turns a missing provider into a failure.
        [SupportedOSPlatformGuard("windows")]
        [SupportedOSPlatformGuard("linux")]
        [SupportedOSPlatformGuard("macos")]
        private static bool RequireQuic()
        {
            if (QuicListener.IsSupported) return true;
            if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("Native QUIC is required but MsQuic is unavailable.");
            Assert.Ignore("The host does not provide MsQuic.");
            return false;
        }

        private static int Setting(string name, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ChurnOnce(Random random, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "create";
            var server = Server.Start(8, 8, false);
            var owners = new List<(string Name, Action Dispose)>();
            QuicConnection? peer = null;
            try
            {
                trace.Phase = "connect";
                var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                peer = client;
                var streams = new List<SafeHandle>();
                var count = random.Next(0, 4);
                for (var s = 0; s < count; s++)
                {
                    var local = random.Next(2) == 0;
                    trace.Phase = "stream-" + s.ToString(CultureInfo.InvariantCulture) + (local ? "-local" : "-peer");
                    streams.Add(local ? await Within(LocalEcho(connection, client, random.Next(2) == 0, token), trace).ConfigureAwait(false)
                        : await Within(PeerEcho(connection, client, token), trace).ConfigureAwait(false));
                }
                // Every owner is disposed in a seeded order, including parents
                // before their children and the peer before or after the server.
                foreach (var stream in streams) owners.Add(("stream", stream.Dispose));
                owners.Add(("connection", connection.Dispose));
                owners.Add(("listener", server.Listener.Dispose));
                owners.Add(("configuration", server.Configuration.Dispose));
                owners.Add(("registration", server.Registration.Dispose));
                owners.Add(("api", server.Api.Dispose));
                owners.Add(("peer", () => client.DisposeAsync().AsTask().GetAwaiter().GetResult()));
                var order = owners.OrderBy(_ => random.Next()).ToList();
                foreach (var (name, dispose) in order)
                {
                    trace.Phase = "dispose-" + name;
                    await Within(Task.Run(dispose, token), trace).ConfigureAwait(false);
                }
                trace.Phase = "closed";
                // Parents may release asynchronously after their last child.
                await Eventually(() => server.Registration.IsClosed && server.Api.IsClosed, trace + ": parents were not released.").ConfigureAwait(false);
                foreach (var stream in streams) Assert.That(stream.IsClosed, Is.True, trace.ToString());
                Assert.That(connection.IsClosed, Is.True, trace.ToString());
            }
            finally
            {
                if (peer != null) await peer.DisposeAsync().ConfigureAwait(false);
                server.Dispose();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<SafeHandle> PeerEcho(SafeHandle connection, QuicConnection peer, CancellationToken token)
        {
            await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
            var payload = Pattern(1024, 3);
            await remote.WriteAsync(payload, true, token).ConfigureAwait(false);
            var native = await AcceptStream(connection, token).ConfigureAwait(false);
            Assert.That(await ReadAll(native, token).ConfigureAwait(false), Is.EqualTo(payload));
            await Write(native, payload, true, token).ConfigureAwait(false);
            Assert.That(await ReadAll(remote, token).ConfigureAwait(false), Is.EqualTo(payload));
            return native;
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<SafeHandle> LocalEcho(SafeHandle connection, QuicConnection peer, bool unidirectional, CancellationToken token)
        {
            var native = await Result(Call(connection, "OpenStreamAsync", unidirectional, token)).WaitAsync(token).ConfigureAwait(false);
            await using var remote = await peer.AcceptInboundStreamAsync(token).ConfigureAwait(false);
            var payload = Pattern(2048, 5);
            await Write(native, payload, true, token).ConfigureAwait(false);
            Assert.That(await ReadAll(remote, token).ConfigureAwait(false), Is.EqualTo(payload));
            if (!unidirectional)
            {
                await remote.WriteAsync(payload, true, token).ConfigureAwait(false);
                Assert.That(await ReadAll(native, token).ConfigureAwait(false), Is.EqualTo(payload));
            }
            return native;
        }

        // Endings: 0 cancel both operations, 1 dispose the stream, 2 graceful
        // connection shutdown, 3 dispose the connection while the stream lives,
        // 4 peer aborts both directions, 5 dispose the listener (no effect).
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task PendingBothOnce(int ending, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "pending-" + ending.ToString(CultureInfo.InvariantCulture) + "-setup";
            using var server = Server.Start(8, 8, true);
            var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            await using var peer = client;
            using var owned = connection;
            await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
            await remote.WriteAsync(new byte[] { 42 }, token).ConfigureAwait(false);
            using var native = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
            var buffer = new byte[16];
            Assert.That(await Read(native, buffer, token).ConfigureAwait(false), Is.EqualTo(1));
            using var readCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var writeCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var reading = Read(native, buffer, readCancel.Token).AsTask();
            var writing = Write(native, Pattern(8 * 1024 * 1024, 9), false, writeCancel.Token).AsTask();
            trace.Phase = "pending-" + ending.ToString(CultureInfo.InvariantCulture) + "-blocked";
            await Task.Delay(50, token).ConfigureAwait(false);
            Assert.That(reading.IsCompleted, Is.False, trace + ": the read must be pending.");
            Assert.That(writing.IsCompleted, Is.False, trace + ": the unbuffered write must be flow-blocked.");
            trace.Phase = "pending-" + ending.ToString(CultureInfo.InvariantCulture) + "-ending";
            switch (ending)
            {
                case 0:
                    await readCancel.CancelAsync().ConfigureAwait(false);
                    await ExpectAsync<OperationCanceledException>(reading, trace).ConfigureAwait(false);
                    Assert.That(writing.IsCompleted, Is.False, trace + ": read cancellation must not end the write.");
                    await writeCancel.CancelAsync().ConfigureAwait(false);
                    await ExpectAsync<OperationCanceledException>(writing, trace).ConfigureAwait(false);
                    // The receive direction survives both cancellations.
                    await remote.WriteAsync(new byte[] { 43 }, true, token).ConfigureAwait(false);
                    Assert.That(await Within(ReadAll(native, token), trace).ConfigureAwait(false), Is.EqualTo(new byte[] { 43 }));
                    break;
                case 1:
                    native.Dispose();
                    await ExpectAsync<ObjectDisposedException>(reading, trace).ConfigureAwait(false);
                    await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                    await LatePeerWrite(remote, token).ConfigureAwait(false);
                    break;
                case 2:
                    await Within((Task)Call(connection, "ShutdownAsync", 0x100L), trace).ConfigureAwait(false);
                    await ExpectAsync<IOException>(reading, trace).ConfigureAwait(false);
                    await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                    break;
                case 3:
                    connection.Dispose();
                    await ExpectAsync<IOException>(reading, trace).ConfigureAwait(false);
                    await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                    Assert.That(connection.IsClosed, Is.False, trace + ": a live stream retains its connection.");
                    native.Dispose();
                    Assert.That(connection.IsClosed, Is.True, trace + ": the last stream releases its connection.");
                    break;
                case 4:
                    remote.Abort(QuicAbortDirection.Both, 0x1ab);
                    await ExpectAsync<QuicException>(reading, trace, error => error.ApplicationErrorCode == 0x1ab).ConfigureAwait(false);
                    await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                    break;
                default:
                    await Within((Task)Call(server.Listener, "StopAsync"), trace).ConfigureAwait(false);
                    server.Listener.Dispose();
                    await Task.Delay(50, token).ConfigureAwait(false);
                    Assert.That(reading.IsCompleted || writing.IsCompleted, Is.False, trace + ": listener closure must not reach accepted streams.");
                    native.Dispose();
                    await ExpectAsync<ObjectDisposedException>(reading, trace).ConfigureAwait(false);
                    await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                    break;
            }
            trace.Phase = "pending-" + ending.ToString(CultureInfo.InvariantCulture) + "-sibling";
            if (ending == 2 || ending == 3)
            {
                var (fresh, freshPeer) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                await using (freshPeer)
                using (fresh)
                using (await Within(PeerEcho(fresh, freshPeer, token), trace).ConfigureAwait(false)) { }
            }
            else
            {
                using var sibling = await Within(PeerEcho(connection, peer, token), trace).ConfigureAwait(false);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task LatePeerWrite(QuicStream remote, CancellationToken token)
        {
            // Bytes that reach a disposed native stream exercise late callbacks.
            try { await remote.WriteAsync(new byte[4096], token).ConfigureAwait(false); }
            catch (QuicException) { }
        }

        // Endings: 0 dispose the stream, 1 shut the connection down, 2 peer
        // reset followed by stream disposal.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task PartialReceiveOnce(int ending, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "partial-" + ending.ToString(CultureInfo.InvariantCulture) + "-setup";
            using var server = Server.Start(8, 8, false);
            var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            await using var peer = client;
            using var owned = connection;
            await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
            await remote.WriteAsync(Pattern(65536, 13), token).ConfigureAwait(false);
            var native = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
            var one = new byte[1];
            Assert.That(await Read(native, one, token).ConfigureAwait(false), Is.EqualTo(1));
            Assert.That(one[0], Is.EqualTo(Pattern(1, 13)[0]));
            trace.Phase = "partial-" + ending.ToString(CultureInfo.InvariantCulture) + "-ending";
            if (ending == 1) await Within((Task)Call(connection, "ShutdownAsync", 0x100L), trace).ConfigureAwait(false);
            if (ending == 2)
            {
                remote.Abort(QuicAbortDirection.Write, 0x1ac);
                await Within(WaitFaulted(NativeDirection(native, "ReadsClosed")), trace).ConfigureAwait(false);
            }
            trace.Phase = "partial-" + ending.ToString(CultureInfo.InvariantCulture) + "-dispose";
            await Within(Task.Run(native.Dispose, token), trace).ConfigureAwait(false);
            Assert.That(native.IsClosed, Is.True, trace.ToString());
            if (ending != 1)
            {
                trace.Phase = "partial-" + ending.ToString(CultureInfo.InvariantCulture) + "-sibling";
                using var sibling = await Within(PeerEcho(connection, peer, token), trace).ConfigureAwait(false);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task StreamOverflowOnce(Trace trace)
        {
            const int Admitted = 128;
            const int Excess = 8;
            using var deadline = new CancellationTokenSource(Step * 3);
            var token = deadline.Token;
            trace.Phase = "stream-overflow-setup";
            using var server = Server.Start(Admitted + Excess + 8, 0, false);
            var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            await using var peer = client;
            using var owned = connection;
            var remotes = new List<QuicStream>();
            var natives = new List<SafeHandle>();
            try
            {
                trace.Phase = "stream-overflow-open";
                for (var i = 0; i < Admitted + Excess; i++)
                {
                    var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                    remotes.Add(remote);
                    await remote.WriteAsync(new byte[] { (byte)i }, token).ConfigureAwait(false);
                }
                // Unclaimed admission is bounded at 128; the excess is rejected
                // with H3_REQUEST_CANCELLED instead of growing the queue.
                trace.Phase = "stream-overflow-rejection";
                var rejected = await Within(WaitForRejections(remotes, Excess, token), trace).ConfigureAwait(false);
                Assert.That(rejected.Count, Is.EqualTo(Excess), trace.ToString());
                foreach (var remote in rejected)
                    Assert.That((remote.ReadsClosed.Exception?.InnerException as QuicException)?.ApplicationErrorCode, Is.EqualTo(0x10c), trace.ToString());
                trace.Phase = "stream-overflow-drain";
                var expected = remotes.Except(rejected).ToDictionary(remote => remote.Id);
                for (var i = 0; i < Admitted; i++)
                {
                    var native = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
                    natives.Add(native);
                    var id = (long)(native.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(native)
                        ?? throw new AssertionException("Missing native stream ID."));
                    Assert.That(expected.Remove(id, out var remote), Is.True, trace + ": unexpected admitted stream " + id);
                    var one = new byte[1];
                    Assert.That(await Read(native, one, token).ConfigureAwait(false), Is.EqualTo(1));
                    Assert.That(remote?.Id, Is.EqualTo(id));
                }
                Assert.That(expected, Is.Empty, trace.ToString());
                trace.Phase = "stream-overflow-recovery";
                using var later = await Within(PeerEcho(connection, peer, token), trace).ConfigureAwait(false);
            }
            finally
            {
                foreach (var native in natives) native.Dispose();
                foreach (var remote in remotes) await remote.DisposeAsync().ConfigureAwait(false);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<List<QuicStream>> WaitForRejections(List<QuicStream> remotes, int expected, CancellationToken token)
        {
            while (true)
            {
                var faulted = remotes.Where(remote => remote.ReadsClosed.IsFaulted).ToList();
                if (faulted.Count >= expected)
                {
                    // Give an over-rejection the chance to appear before counting.
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    return remotes.Where(remote => remote.ReadsClosed.IsFaulted).ToList();
                }
                if (token.IsCancellationRequested) throw new AssertionException("Hang: only " + faulted.Count.ToString(CultureInfo.InvariantCulture) + " rejected streams.");
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ConnectionOverflowOnce(Trace trace)
        {
            const int Capacity = 256;
            const int Excess = 4;
            using var deadline = new CancellationTokenSource(Step * 4);
            var token = deadline.Token;
            trace.Phase = "connection-overflow-setup";
            using var server = Server.Start(8, 8, false);
            var queued = new List<Task<QuicConnection>>();
            var accepted = new List<SafeHandle>();
            try
            {
                for (var i = 0; i < Capacity; i++)
                    queued.Add(QuicConnection.ConnectAsync(ClientOptions(server, 8, 30), token).AsTask());
                trace.Phase = "connection-overflow-fill";
                while (QueuedConnections(server.Listener) < Capacity)
                {
                    if (token.IsCancellationRequested) throw new AssertionException("Hang: " + trace + " with " + QueuedConnections(server.Listener).ToString(CultureInfo.InvariantCulture) + " queued.");
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                }
                trace.Phase = "connection-overflow-refusal";
                var refused = new List<Task<QuicConnection>>();
                for (var i = 0; i < Excess; i++) refused.Add(QuicConnection.ConnectAsync(ClientOptions(server, 8, 10), token).AsTask());
                await Within(Task.WhenAll(refused.Select(Settled)), trace).ConfigureAwait(false);
                foreach (var refusal in refused)
                    Assert.That((refusal.Exception?.InnerException as QuicException)?.QuicError, Is.EqualTo(QuicError.ConnectionRefused),
                        trace + ": " + refusal.Exception?.InnerException);
                trace.Phase = "connection-overflow-drain";
                // Drained connections are refused unconfigured. Shutdown completes
                // before close; ConnectionOverflowDrainByDisposal covers Dispose alone.
                var closing = new List<Task>();
                for (var i = 0; i < Capacity; i++)
                {
                    var accept = (Task)Call(server.Listener, "AcceptAsync", token);
                    await Within(accept, trace).ConfigureAwait(false);
                    var connection = Handle(accept);
                    accepted.Add(connection);
                    closing.Add(((Task)Call(connection, "ShutdownAsync", 0x100L)).ContinueWith(_ => connection.Dispose(), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default));
                }
                await Within(Task.WhenAll(closing), trace).ConfigureAwait(false);
                await Within(Task.WhenAll(queued.Select(Settled)), trace).ConfigureAwait(false);
                Assert.That(QueuedConnections(server.Listener), Is.Zero, trace + ": refused peers must not be readmitted.");
                Assert.That(queued.Count(client => client.IsCompletedSuccessfully), Is.Zero, trace.ToString());
                trace.Phase = "connection-overflow-recovery";
                var (healthy, peer) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                await using (peer)
                using (healthy)
                using (await Within(PeerEcho(healthy, peer, token), trace).ConfigureAwait(false)) { }
                Assert.That(accepted.All(connection => connection.IsClosed), Is.True, trace.ToString());
            }
            finally
            {
                foreach (var connection in accepted) connection.Dispose();
                foreach (var client in queued)
                {
                    await Settled(client).ConfigureAwait(false);
                    if (client.IsCompletedSuccessfully) await client.Result.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        // Dispose alone (no awaited ShutdownAsync) must still send the
        // application close: every peer sees ConnectionAborted with 0x100.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task DisposeEstablishedOnce(int count, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            using var server = Server.Start(8, 8, false);
            var peers = new List<QuicConnection>();
            var natives = new List<SafeHandle>();
            try
            {
                trace.Phase = "established-connect";
                for (var i = 0; i < count; i++)
                {
                    var (connection, peer) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                    natives.Add(connection); peers.Add(peer);
                }
                var waits = peers.Select(peer => peer.AcceptInboundStreamAsync(token).AsTask()).ToList();
                trace.Phase = "established-dispose";
                foreach (var connection in natives) connection.Dispose();
                trace.Phase = "established-peer-notified";
                foreach (var wait in waits)
                    await ExpectAsync<QuicException>(wait.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None), trace,
                        error => error.QuicError == QuicError.ConnectionAborted && error.ApplicationErrorCode == 0x100).ConfigureAwait(false);
            }
            finally
            {
                foreach (var connection in natives) connection.Dispose();
                foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Disposing queued unconfigured connections must refuse each peer
        // once; a silent close lets retransmitted Initials be readmitted.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task DisposeQueuedOnce(int count, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            using var server = Server.Start(8, 8, false);
            var clients = new List<Task<QuicConnection>>();
            var admitted = 0;
            try
            {
                for (var i = 0; i < count; i++) clients.Add(QuicConnection.ConnectAsync(ClientOptions(server, 8, 30), token).AsTask());
                trace.Phase = "queued-fill";
                await Eventually(() => QueuedConnections(server.Listener) == count, trace + ": the queue did not fill.").ConfigureAwait(false);
                trace.Phase = "queued-dispose";
                var settled = Task.WhenAll(clients.Select(Settled));
                var clock = Stopwatch.StartNew();
                while (!settled.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    using var poll = new CancellationTokenSource(50);
                    var accept = (Task)Call(server.Listener, "AcceptAsync", poll.Token);
                    await Settled(accept).ConfigureAwait(false);
                    if (!accept.IsCompletedSuccessfully) continue;
                    Handle(accept).Dispose();
                    admitted++;
                }
                Assert.That(settled.IsCompleted, Is.True, trace + ": peers were not refused within 10 s.");
                Assert.That(admitted, Is.EqualTo(count), trace + ": retransmitted Initials were readmitted as new connections.");
                Assert.That(clients.Count(client => client.IsCompletedSuccessfully), Is.Zero, trace.ToString());
            }
            finally
            {
                foreach (var client in clients)
                {
                    await Settled(client).ConfigureAwait(false);
                    if (client.IsCompletedSuccessfully) await client.Result.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task FinThenDisposeOnce(int length, int trials, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            using var server = Server.Start(8, 8, false);
            var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            await using var peer = client;
            using var owned = connection;
            var payload = Pattern(length, 19);
            var outcomes = new List<string>();
            for (var i = 0; i < trials; i++)
            {
                trace.Iteration = i;
                trace.Phase = "fin-dispose-write";
                await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                await remote.WriteAsync(new byte[] { 1 }, true, token).ConfigureAwait(false);
                var native = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
                Assert.That(await Within(ReadAll(native, token), trace).ConfigureAwait(false), Is.EqualTo(new byte[] { 1 }));
                await Within(Write(native, payload, true, token).AsTask(), trace).ConfigureAwait(false);
                native.Dispose();
                trace.Phase = "fin-dispose-peer-read";
                try
                {
                    var received = await Within(ReadAll(remote, token), trace).ConfigureAwait(false);
                    outcomes.Add(received.AsSpan().SequenceEqual(payload) ? "complete" : "truncated " + received.Length.ToString(CultureInfo.InvariantCulture));
                }
                catch (QuicException error) { outcomes.Add(error.QuicError + " " + error.ApplicationErrorCode?.ToString(CultureInfo.InvariantCulture)); }
            }
            Assert.That(outcomes, Is.All.EqualTo("complete"), trace + ": peer outcomes " + string.Join(", ", outcomes));
        }

        // Leaves one stream and its connection to their finalizers while the
        // peer stays connected; returns weak references to the parents.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<(List<WeakReference<SafeHandle>> Parents, QuicConnection Peer, QuicStream Remote)> AbandonOnce(Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            var server = Server.Start(8, 8, false);
            var (connection, peer) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
            await remote.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
            var stream = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
            Assert.That(await Read(stream, new byte[1], token).ConfigureAwait(false), Is.EqualTo(1));
            await Within((Task)Call(server.Listener, "StopAsync"), trace).ConfigureAwait(false);
            server.Listener.Dispose();
            server.Configuration.Dispose();
            server.Registration.Dispose();
            server.Api.Dispose();
            server.Certificate.Dispose();
            Assert.That(server.Registration.IsClosed || server.Api.IsClosed, Is.False, trace + ": the live connection retains its parents.");
            trace.Phase = "abandoned";
            return (new List<WeakReference<SafeHandle>> { new(server.Registration), new(server.Api) }, peer, remote);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ConcurrentDisposeOnce(Random random, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "concurrent-setup";
            var server = Server.Start(16, 8, true);
            QuicConnection? peer = null;
            var remotes = new List<QuicStream>();
            var natives = new List<SafeHandle>();
            var pending = new List<Task>();
            var pumps = new List<Task>();
            using var stop = new CancellationTokenSource();
            try
            {
                var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                peer = client;
                var count = random.Next(2, 7);
                var pumping = new List<(QuicStream Remote, SafeHandle Native)>();
                for (var i = 0; i < count; i++)
                {
                    var remote = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                    remotes.Add(remote);
                    await remote.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                    var native = await AcceptDiagnosed(connection, stop, pumps, trace).ConfigureAwait(false);
                    natives.Add(native);
                    Assert.That(await Read(native, new byte[1], token).ConfigureAwait(false), Is.EqualTo(1));
                    // Mix pending reads, blocked writes and peers that keep sending.
                    switch (random.Next(3))
                    {
                        case 0: pending.Add(Read(native, new byte[64], CancellationToken.None).AsTask()); break;
                        case 1: pending.Add(Write(native, new byte[4 * 1024 * 1024], false, CancellationToken.None).AsTask()); break;
                        default: pumping.Add((remote, native)); break;
                    }
                }
                // Pumps start only after every stream is accepted: MsQuic's default
                // FIFO stream scheduling lets a saturating stream starve later ones.
                foreach (var (remote, native) in pumping) { pumps.Add(Pump(remote, stop.Token)); pending.Add(Drain(native)); }
                await Task.Delay(random.Next(0, 30), token).ConfigureAwait(false);
                trace.Phase = "concurrent-dispose";
                var actions = new List<Action>();
                foreach (var native in natives) { actions.Add(native.Dispose); actions.Add(native.Dispose); }
                actions.Add(connection.Dispose);
                actions.Add(() => Settled((Task)Call(connection, "ShutdownAsync", 0x100L)).GetAwaiter().GetResult());
                actions.Add(server.Listener.Dispose);
                actions.Add(server.Configuration.Dispose);
                actions.Add(server.Registration.Dispose);
                actions.Add(server.Api.Dispose);
                if (random.Next(2) == 0) actions.Add(() => client.DisposeAsync().AsTask().GetAwaiter().GetResult());
                var order = actions.OrderBy(_ => random.Next()).Select(action => Task.Run(() => IgnoreDisposed(action), token)).ToArray();
                await Within(Task.WhenAll(order), trace).ConfigureAwait(false);
                trace.Phase = "concurrent-pending";
                await Within(Task.WhenAll(pending.Select(Settled)), trace).ConfigureAwait(false);
                foreach (var task in pending)
                    Assert.That(task.IsFaulted || task.IsCanceled || task.IsCompletedSuccessfully, Is.True, trace.ToString());
                await stop.CancelAsync().ConfigureAwait(false);
                await Within(Task.WhenAll(pumps), trace).ConfigureAwait(false);
                await Eventually(() => server.Api.IsClosed, trace + ": the API owner was not released.").ConfigureAwait(false);
                // Final native shutdown releases child leases asynchronously.
                // The existing parent-close wait proves every lease is released.
                Assert.That(natives.All(native => native.IsClosed) && connection.IsClosed, Is.True, trace.ToString());
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                // After a failure, observe abandoned operations so their faults
                // cannot surface as unobserved exceptions in a later case.
                foreach (var task in pending.Concat(pumps)) _ = task.ContinueWith(done => done.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var remote in remotes) await remote.DisposeAsync().ConfigureAwait(false);
                if (peer != null) await peer.DisposeAsync().ConfigureAwait(false);
                server.Dispose();
            }
        }

        // A stream not accepted within the step is waited for after the peer
        // pumps stop, so the failure states whether it was late or lost.
        private static async Task<SafeHandle> AcceptDiagnosed(SafeHandle connection, CancellationTokenSource stop, List<Task> pumps, Trace trace)
        {
            var accepting = AcceptStream(connection, CancellationToken.None);
            if (await Task.WhenAny(accepting, Task.Delay(Step, CancellationToken.None)).ConfigureAwait(false) == accepting) return await accepting.ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            await stop.CancelAsync().ConfigureAwait(false);
            var stopping = Task.WhenAll(pumps);
            var pumpsStopped = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false) == stopping;
            var late = await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None)).ConfigureAwait(false) == accepting;
            var outcome = late ? "arrived " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s after the step expired"
                : "still not accepted 60 s after the step expired";
            if (late) await Settled(accepting).ConfigureAwait(false);
            throw new AssertionException("Hang: " + trace + " stream acceptance exceeded " + Step.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " s with " + pumps.Count.ToString(CultureInfo.InvariantCulture) + " peer pump(s) running; " + outcome
                + (pumpsStopped ? "" : " (pumps did not stop)") + ". " + HangEvidence(trace));
        }

        private static void IgnoreDisposed(Action action)
        {
            try { action(); }
            catch (ObjectDisposedException) { }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Pump(QuicStream remote, CancellationToken token)
        {
            var chunk = new byte[16384];
            try { while (!token.IsCancellationRequested) await remote.WriteAsync(chunk, token).ConfigureAwait(false); }
            catch (QuicException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task Drain(SafeHandle native)
        {
            var buffer = new byte[16384];
            while (await Read(native, buffer, CancellationToken.None).ConfigureAwait(false) > 0) { }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ParentRetentionOnce(Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "retention-setup";
            var server = Server.Start(8, 8, false);
            QuicConnection? peer = null;
            try
            {
                var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
                peer = client;
                await using var remote = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                await remote.WriteAsync(new byte[] { 1, 2, 3 }, token).ConfigureAwait(false);
                var stream = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
                trace.Phase = "retention-parents-disposed";
                await Within((Task)Call(server.Listener, "StopAsync"), trace).ConfigureAwait(false);
                server.Listener.Dispose();
                server.Configuration.Dispose();
                server.Registration.Dispose();
                server.Api.Dispose();
                Assert.That(server.Listener.IsClosed && server.Configuration.IsClosed, Is.True, trace + ": unretained owners release immediately.");
                Assert.That(server.Registration.IsClosed || server.Api.IsClosed, Is.False, trace + ": a live connection retains its registration and API.");
                trace.Phase = "retention-child-active";
                // The connection and stream still reach native code through their leases.
                var input = new byte[8];
                var total = 0;
                while (total < 3) total += await Within(Read(stream, input, token).AsTask(), trace).ConfigureAwait(false);
                await Within(Write(stream, new byte[] { 4, 5 }, true, token).AsTask(), trace).ConfigureAwait(false);
                Assert.That(await Within(ReadAll(remote, token), trace).ConfigureAwait(false), Is.EqualTo(new byte[] { 4, 5 }));
                connection.Dispose();
                Assert.That(connection.IsClosed || server.Registration.IsClosed || server.Api.IsClosed, Is.False, trace + ": a live stream retains every parent.");
                Assert.That(References(connection), Is.EqualTo(1), trace + ": exactly the stream lease remains.");
                trace.Phase = "retention-release";
                await Within(Task.Run(stream.Dispose, token), trace).ConfigureAwait(false);
                await Eventually(() => server.Registration.IsClosed && server.Api.IsClosed, trace + ": the last child releases every retained parent.").ConfigureAwait(false);
                Assert.That(stream.IsClosed && connection.IsClosed, Is.True, trace + ": the last child releases its connection.");
            }
            finally
            {
                if (peer != null) await peer.DisposeAsync().ConfigureAwait(false);
                server.Dispose();
            }
        }

        // Variant 0: local read abort, then write abort, with both pending.
        // Variant 1: canceled pending read, then graceful FIN after a
        // committed flow-blocked write; the peer must receive every byte.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task DirectionOnce(int variant, Trace trace)
        {
            using var deadline = new CancellationTokenSource(Step * 2);
            var token = deadline.Token;
            trace.Phase = "direction-" + variant.ToString(CultureInfo.InvariantCulture) + "-setup";
            using var server = Server.Start(8, 8, true);
            var (connection, client) = await Within(Connect(server, 8, token), trace).ConfigureAwait(false);
            await using var peer = client;
            using var owned = connection;
            await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
            await remote.WriteAsync(new byte[] { 42 }, token).ConfigureAwait(false);
            using var native = await Within(AcceptStream(connection, token), trace).ConfigureAwait(false);
            Assert.That(await Read(native, new byte[1], token).ConfigureAwait(false), Is.EqualTo(1));
            var payload = Pattern(variant == 0 ? 8 * 1024 * 1024 : 1024 * 1024, 17);
            using var readCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var reading = Read(native, new byte[16], readCancel.Token).AsTask();
            var writing = Write(native, payload, false, token).AsTask();
            await Task.Delay(50, token).ConfigureAwait(false);
            Assert.That(reading.IsCompleted || writing.IsCompleted, Is.False, trace + ": both operations must be pending.");
            var abort = Capabilities.Abort ?? throw new AssertionException("Missing native Abort.");
            trace.Phase = "direction-" + variant.ToString(CultureInfo.InvariantCulture) + "-ending";
            if (variant == 0)
            {
                Invoke(abort, native, QuicAbortDirection.Read, 0x1ad);
                await ExpectAsync<IOException>(reading, trace).ConfigureAwait(false);
                await Within(WaitFaulted(remote.WritesClosed), trace).ConfigureAwait(false);
                Assert.That((remote.WritesClosed.Exception?.InnerException as QuicException)?.ApplicationErrorCode, Is.EqualTo(0x1ad), trace.ToString());
                Assert.That(writing.IsCompleted, Is.False, trace + ": a read abort must not end the send direction.");
                Invoke(abort, native, QuicAbortDirection.Write, 0x1ae);
                await ExpectAsync<IOException>(writing, trace).ConfigureAwait(false);
                await ExpectAsync<QuicException>(ReadAll(remote, token), trace, error => error.ApplicationErrorCode == 0x1ae).ConfigureAwait(false);
            }
            else
            {
                await readCancel.CancelAsync().ConfigureAwait(false);
                await ExpectAsync<OperationCanceledException>(reading, trace).ConfigureAwait(false);
                Invoke(Capabilities.CompleteWrites ?? throw new AssertionException("Missing native CompleteWrites."), native);
                var received = await Within(ReadAll(remote, token), trace).ConfigureAwait(false);
                await Within(writing, trace).ConfigureAwait(false);
                Assert.That(received.Length, Is.EqualTo(payload.Length), trace.ToString());
                Assert.That(received, Is.EqualTo(payload), trace.ToString());
                await Within(NativeDirection(native, "WritesClosed"), trace).ConfigureAwait(false);
                await remote.WriteAsync(new byte[] { 44 }, true, token).ConfigureAwait(false);
                Assert.That(await Within(ReadAll(native, token), trace).ConfigureAwait(false), Is.EqualTo(new byte[] { 44 }));
            }
        }

        private static void Invoke(MethodInfo method, object owner, params object[] arguments)
        {
            try { method.Invoke(owner, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static class Capabilities
        {
            private static readonly Type? Stream = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicNativeStream");
            internal static readonly MethodInfo? Abort = Stream?.GetMethod("Abort", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(QuicAbortDirection), typeof(long) }, null);
            internal static readonly MethodInfo? CompleteWrites = Stream?.GetMethod("CompleteWrites", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            internal static readonly bool Directions = Stream?.GetProperty("ReadsClosed", BindingFlags.Instance | BindingFlags.NonPublic) != null;
        }

        private sealed class Server : IDisposable
        {
            internal readonly SafeHandle Api;
            internal readonly SafeHandle Registration;
            internal readonly SafeHandle Configuration;
            internal readonly SafeHandle Listener;
            internal readonly X509Certificate2 Certificate;
            internal readonly IPEndPoint Endpoint;
            private Server(SafeHandle api, SafeHandle registration, SafeHandle configuration, SafeHandle listener, X509Certificate2 certificate, IPEndPoint endpoint)
            { Api = api; Registration = registration; Configuration = configuration; Listener = listener; Certificate = certificate; Endpoint = endpoint; }
            internal static Server Start(ushort bidirectional, ushort unidirectional, bool unbuffered)
            {
                var owners = new Stack<IDisposable>();
                try
                {
                    var api = OpenApi(); owners.Push(api);
                    var registration = (SafeHandle)Call(api, "CreateRegistration"); owners.Push(registration);
                    var configuration = (SafeHandle)Call(registration, unbuffered ? "CreateUnbufferedStreamConfiguration" : "CreateStreamConfiguration", H3, bidirectional, unidirectional);
                    owners.Push(configuration);
                    var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable); owners.Push(certificate);
                    Call(configuration, "LoadServerCertificate", certificate);
                    var listener = (SafeHandle)Call(registration, "CreateListener"); owners.Push(listener);
                    Call(listener, "EnableAcceptance");
                    Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
                    var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
                    RememberPort(endpoint.Port, "listener");
                    return new Server(api, registration, configuration, listener, certificate, endpoint);
                }
                catch
                {
                    while (owners.Count > 0) owners.Pop().Dispose();
                    throw;
                }
            }
            public void Dispose()
            {
                Listener.Dispose();
                Configuration.Dispose();
                Registration.Dispose();
                Api.Dispose();
                Certificate.Dispose();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<(SafeHandle Connection, QuicConnection Peer)> Connect(Server server, int peerCredit, CancellationToken token)
        {
            var connecting = QuicConnection.ConnectAsync(ClientOptions(server, peerCredit, 10), token).AsTask();
            SafeHandle? connection = null;
            try
            {
                var accept = (Task)Call(server.Listener, "AcceptAsync", token);
                try { await accept.WaitAsync(Step - TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // Record why no connection reached the queue before failing.
                    await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
                    var admission = server.Listener.GetType().GetProperty("LastAdmissionFailure", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server.Listener);
                    throw new TimeoutException("No native connection was accepted within " + Step.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " s; client " + (connecting.IsCompletedSuccessfully ? "connected" : connecting.Exception?.InnerException?.ToString() ?? "pending")
                        + "; listener admission failure " + (admission?.ToString() ?? "none") + "; queued " + QueuedConnections(server.Listener).ToString(CultureInfo.InvariantCulture)
                        + "; endpoint " + server.Endpoint + "; " + PortHistory(server.Endpoint.Port) + ".");
                }
                connection = Handle(accept);
                Call(connection, "EnableStreamAcceptance");
                Call(connection, "Configure", server.Configuration);
                var peer = await connecting.WaitAsync(token).ConfigureAwait(false);
                RememberPort(peer.LocalEndPoint.Port, "client");
                await ((Task)(connection.GetType().GetProperty("Connected", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(connection)
                    ?? throw new AssertionException("Missing handshake completion."))).WaitAsync(token).ConfigureAwait(false);
                return (connection, peer);
            }
            catch
            {
                connection?.Dispose();
                await Settled(connecting).ConfigureAwait(false);
                if (connecting.IsCompletedSuccessfully) await connecting.Result.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static QuicClientConnectionOptions ClientOptions(Server server, int peerCredit, int handshakeSeconds) => new()
        {
            RemoteEndPoint = server.Endpoint,
            MaxInboundBidirectionalStreams = peerCredit,
            MaxInboundUnidirectionalStreams = peerCredit,
            DefaultCloseErrorCode = 0x100,
            DefaultStreamErrorCode = 0x10c,
            HandshakeTimeout = TimeSpan.FromSeconds(handshakeSeconds),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString() == server.Certificate.GetCertHashString(),
            },
        };

        private static async Task<SafeHandle> AcceptStream(SafeHandle connection, CancellationToken token)
            => await Result(Call(connection, "AcceptStreamAsync", token)).WaitAsync(token).ConfigureAwait(false);

        private static async Task<SafeHandle> Result(object task)
        {
            await ((Task)task).ConfigureAwait(false);
            return Handle((Task)task);
        }

        private static SafeHandle Handle(Task task)
            => (SafeHandle)(task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new AssertionException("Missing native handle result."));

        private static ValueTask<int> Read(SafeHandle stream, byte[] buffer, CancellationToken token)
            => (ValueTask<int>)Call(stream, "ReadAsync", buffer.AsMemory(), token);

        private static ValueTask Write(SafeHandle stream, byte[] payload, bool fin, CancellationToken token)
            => (ValueTask)Call(stream, "WriteAsync", (ReadOnlyMemory<byte>)payload, fin, token);

        private static async Task<byte[]> ReadAll(SafeHandle stream, CancellationToken token)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8191];
            int count;
            while ((count = await Read(stream, buffer, token).ConfigureAwait(false)) > 0) output.Write(buffer, 0, count);
            return output.ToArray();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<byte[]> ReadAll(QuicStream stream, CancellationToken token)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8191];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) output.Write(buffer, 0, count);
            return output.ToArray();
        }

        private static Task NativeDirection(SafeHandle stream, string name)
            => stream.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(stream) as Task
                ?? throw new AssertionException("Missing native direction completion " + name + ".");

        private static async Task WaitFaulted(Task task)
        {
            await Settled(task).ConfigureAwait(false);
            Assert.That(task.IsFaulted, Is.True, "The direction must complete with its abort.");
        }

        private static async Task Settled(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private static async Task Eventually(Func<bool> condition, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > Step) throw new AssertionException(message);
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + seed);
            return bytes;
        }

        private static int QueuedConnections(SafeHandle listener)
        {
            var queue = listener.GetType().GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener)
                ?? throw new AssertionException("Missing native accept queue.");
            var reader = queue.GetType().GetProperty("Reader")?.GetValue(queue) ?? throw new AssertionException("Missing accept queue reader.");
            return (int)(reader.GetType().GetProperty("Count")?.GetValue(reader) ?? throw new AssertionException("Missing accept queue count."));
        }

        // SafeHandle keeps its reference count above two state bits; it is
        // read only for diagnostics and the parent-retention assertion.
        private static int References(SafeHandle handle)
            => typeof(SafeHandle).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(handle) is int state
                ? (int)((uint)state >> 2) : -1;

        private static async Task<T> Within<T>(Task<T> task, Trace trace)
        {
            await Within((Task)task, trace).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        private static async Task Within(Task task, Trace trace)
        {
            try { await task.WaitAsync(Step).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                throw new AssertionException("Hang: " + trace + " exceeded " + Step.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s. "
                    + error.Message + " " + HangEvidence(trace), error);
            }
        }

        // Records MsQuic queue counters and, on macOS, a native stack sample of
        // every thread, so a blocked MsQuic worker is visible in the evidence.
        private static string HangEvidence(Trace trace)
        {
            var directory = Environment.GetEnvironmentVariable("EMBEDIO_NATIVE_QUIC_CAMPAIGN_OUTPUT") ?? Path.Combine("TestResults", "native-quic-lifetime");
            Directory.CreateDirectory(directory);
            var file = Path.GetFullPath(Path.Combine(directory, "hang-" + trace.Campaign + "-" + trace.Iteration.ToString(CultureInfo.InvariantCulture)
                + "-" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture)));
            string counters;
            try
            {
                using var probe = Probe.Open();
                var sample = probe.Sample("hang", trace.Iteration);
                counters = sample + " " + probe.QueueDepths();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { counters = "counters unavailable: " + error.Message; }
            File.WriteAllText(file + ".txt", trace + Environment.NewLine + counters + Environment.NewLine);
            if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sample"))
            {
                using var sampler = Process.Start(new ProcessStartInfo("/usr/bin/sample",
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " 2 -file \"" + file + "-sample.txt\"")
                { UseShellExecute = false });
                sampler?.WaitForExit(60000);
            }
            return counters + "; evidence " + file;
        }

        private static async Task ExpectAsync<T>(Task task, Trace trace, Func<T, bool>? condition = null) where T : Exception
        {
            try { await task.WaitAsync(Step).ConfigureAwait(false); }
            catch (TimeoutException) { throw new AssertionException("Hang: " + trace + " waiting for " + typeof(T).Name + "."); }
            catch (T error) when (condition == null || condition(error)) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new AssertionException(trace + ": expected " + typeof(T).Name + " but observed " + error, error); }
            throw new AssertionException(trace + ": expected " + typeof(T).Name + " but the operation succeeded.");
        }

        private sealed class Trace
        {
            internal readonly string Campaign;
            internal readonly int Seed;
            internal int Iteration;
            internal string Phase = "start";
            internal Trace(string campaign, int seed) { Campaign = campaign; Seed = seed; }
            public override string ToString() => Campaign + " seed " + Seed.ToString(CultureInfo.InvariantCulture)
                + " iteration " + Iteration.ToString(CultureInfo.InvariantCulture) + " phase " + Phase;
        }

        private static SafeHandle OpenApi()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi")
                ?? throw new IgnoreException("The retained asset does not include the native provider.");
            try
            {
                return (SafeHandle)(type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
                    ?? throw new AssertionException("Missing native API owner."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static object Call(object owner, string method, params object[] arguments)
        {
            try
            {
                var member = owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing native member " + method + ".");
                var result = member.Invoke(owner, arguments);
                return member.ReturnType == typeof(void) ? typeof(void) : result ?? throw new AssertionException("Missing result from " + method + ".");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private sealed record Sample(string Label, int Iteration, double Seconds, long NativeConnections, long NativeStreams,
            long ConnectionsCreated, long ApplicationRejected, int Descriptors, int UdpSockets, int Threads,
            long ManagedBytes, long HeapBytes, long ResidentBytes, int UnobservedNative);

        // Process and MsQuic resource probe. Native counters are MsQuic's
        // process-wide QUIC_PARAM_GLOBAL_PERF_COUNTERS (client and server).
        private sealed class Probe : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint GetParameter(IntPtr handle, uint parameter, ref uint size, IntPtr buffer);
            private readonly SafeHandle _api;
            private readonly GetParameter _get;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly int _unobservedStart;
            private Probe(SafeHandle api)
            {
                _api = api;
                _get = Marshal.GetDelegateForFunctionPointer<GetParameter>(Marshal.ReadIntPtr(api.DangerousGetHandle(), 4 * IntPtr.Size));
                _unobservedStart = Unobserved.Count;
            }
            internal static Probe Open()
            {
                if (Interlocked.Exchange(ref _tracking, 1) == 0)
                {
                    TaskScheduler.UnobservedTaskException += (_, args) =>
                    {
                        foreach (var error in args.Exception.Flatten().InnerExceptions)
                            if (error.Message.Contains("native", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("MsQuic", StringComparison.Ordinal))
                                Unobserved.Enqueue(error.GetType().FullName + ": " + error.Message);
                    };
                }
                return new Probe(OpenApi());
            }
            public void Dispose() => _api.Dispose();
            private static bool Failed(uint status) => OperatingSystem.IsWindows() ? unchecked((int)status) < 0 : unchecked((int)status) > 0;
            private byte[] Parameter(uint parameter, int capacity)
            {
                var buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    var size = (uint)capacity;
                    var status = _get(IntPtr.Zero, parameter, ref size, buffer);
                    if (Failed(status) || size > capacity) throw new IOException("MsQuic global parameter 0x" + parameter.ToString("X8", CultureInfo.InvariantCulture) + " failed.");
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            private long[] Counters()
            {
                var bytes = Parameter(0x01000003, 64 * 8);
                var counters = new long[bytes.Length / 8];
                for (var i = 0; i < counters.Length; i++) counters[i] = BitConverter.ToInt64(bytes, i * 8);
                return counters;
            }
            // CONN_QUEUE_DEPTH, CONN_OPER_QUEUE_DEPTH, WORK_OPER_QUEUE_DEPTH and
            // LISTEN_QUEUE_DEPTH from the public QUIC_PERFORMANCE_COUNTERS order.
            internal string QueueDepths()
            {
                var counters = Counters();
                return "connQueue " + counters[20].ToString(CultureInfo.InvariantCulture) + " connOperQueue " + counters[21].ToString(CultureInfo.InvariantCulture)
                    + " workOperQueue " + counters[24].ToString(CultureInfo.InvariantCulture)
                    + (counters.Length > 32 ? " listenQueue " + counters[32].ToString(CultureInfo.InvariantCulture) : "");
            }
            private static void Collect()
            {
                for (var i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                GC.Collect();
            }
            internal Sample Sample(string label, int iteration)
            {
                Collect();
                var counters = Counters();
                using var process = Process.GetCurrentProcess();
                return new Sample(label, iteration, _clock.Elapsed.TotalSeconds, counters[4], counters[8], counters[0], counters[2],
                    Descriptors(), UdpSockets(), process.Threads.Count, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes,
                    process.WorkingSet64, Unobserved.Count - _unobservedStart);
            }
            // Wait, bounded, for MsQuic to finish asynchronous connection/stream
            // frees and for Darwin's deferred socket-context close.
            internal Sample Settle(string? label)
            {
                var start = Stopwatch.StartNew();
                long[] previous = Counters();
                while (start.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(50);
                    var current = Counters();
                    if (current[4] == previous[4] && current[8] == previous[8] && current[20] == 0) break;
                    previous = current;
                }
                return Sample(label ?? "baseline", 0);
            }
            internal void AssertReturned(Sample baseline, Trace trace)
            {
                Sample current = Sample("check", trace.Iteration);
                var waited = Stopwatch.StartNew();
                while ((current.NativeConnections > baseline.NativeConnections || current.NativeStreams > baseline.NativeStreams
                    || (current.UdpSockets >= 0 && current.UdpSockets > baseline.UdpSockets)) && waited.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(100);
                    current = Sample("check", trace.Iteration);
                }
                var description = trace + ": baseline " + baseline + " current " + current;
                Assert.That(current.NativeConnections, Is.LessThanOrEqualTo(baseline.NativeConnections), "Native connections remain allocated. " + description);
                Assert.That(current.NativeStreams, Is.LessThanOrEqualTo(baseline.NativeStreams), "Native streams remain allocated. " + description);
                if (current.UdpSockets >= 0) Assert.That(current.UdpSockets, Is.LessThanOrEqualTo(baseline.UdpSockets), "UDP sockets remain open. " + description);
                Assert.That(current.UnobservedNative, Is.EqualTo(baseline.UnobservedNative), "Unobserved native task exceptions: "
                    + string.Join(" | ", Unobserved.Skip(_unobservedStart)) + ". " + description);
            }
            private static int Descriptors()
            {
                var directory = OperatingSystem.IsMacOS() ? "/dev/fd" : OperatingSystem.IsLinux() ? "/proc/self/fd" : null;
                if (directory == null) { using var process = Process.GetCurrentProcess(); return process.HandleCount; }
                return Directory.GetFileSystemEntries(directory).Length;
            }
            private static int UdpSockets()
            {
                if (OperatingSystem.IsLinux())
                {
                    var inodes = new HashSet<string>();
                    foreach (var table in new[] { "/proc/self/net/udp", "/proc/self/net/udp6" })
                    {
                        if (!File.Exists(table)) continue;
                        foreach (var line in File.ReadLines(table).Skip(1))
                        {
                            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (fields.Length > 9) inodes.Add("socket:[" + fields[9] + "]");
                        }
                    }
                    var count = 0;
                    foreach (var entry in Directory.GetFileSystemEntries("/proc/self/fd"))
                    {
                        try { if (new FileInfo(entry).LinkTarget is { } target && inodes.Contains(target)) count++; }
                        catch (IOException) { }
                    }
                    return count;
                }
                if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/sbin/lsof")) return -1;
                var start = new ProcessStartInfo("/usr/sbin/lsof", "-nP -a -p " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " -i UDP -F f")
                { RedirectStandardOutput = true, UseShellExecute = false };
                using var lsof = Process.Start(start) ?? throw new IOException("lsof did not start.");
                var output = lsof.StandardOutput.ReadToEnd();
                if (!lsof.WaitForExit(10000)) { lsof.Kill(); return -1; }
                return output.Split('\n').Count(line => line.StartsWith('f'));
            }
            internal void Write(string directory, string campaign, int seed, int iterations, Trace trace, List<Sample> samples, string? failure, TimeSpan elapsed)
            {
                Directory.CreateDirectory(directory);
                var version = Parameter(0x01000004, 16);
                var hash = Encoding.ASCII.GetString(Parameter(0x01000008, 64)).TrimEnd('\0');
                var (path, digest) = LoadedLibrary();
                var steady = samples.Where(sample => sample.Label.StartsWith("iteration-", StringComparison.Ordinal)).ToList();
                var evidence = new
                {
                    campaign,
                    seed,
                    iterations,
                    completedIterations = failure == null ? iterations : trace.Iteration,
                    failure,
                    failurePhase = failure == null ? null : trace.ToString(),
                    elapsedSeconds = elapsed.TotalSeconds,
                    source = Environment.GetEnvironmentVariable("EMBEDIO_SOURCE_SHA"),
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.OSArchitecture.ToString(),
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription,
                    processors = Environment.ProcessorCount,
                    msquic = new
                    {
                        path,
                        sha256 = digest,
                        version = string.Join('.', Enumerable.Range(0, version.Length / 4).Select(i => BitConverter.ToUInt32(version, i * 4).ToString(CultureInfo.InvariantCulture))),
                        gitHash = hash,
                    },
                    capabilities = new { abort = Capabilities.Abort != null, completeWrites = Capabilities.CompleteWrites != null, directions = Capabilities.Directions },
                    // Per-iteration growth after the first sample; plateaus are
                    // pooling, persistent positive slopes are accumulation.
                    growthPerIteration = steady.Count < 3 ? null : new
                    {
                        managedBytes = Slope(steady, sample => sample.ManagedBytes),
                        heapBytes = Slope(steady, sample => sample.HeapBytes),
                        residentBytes = Slope(steady, sample => sample.ResidentBytes),
                        descriptors = Slope(steady, sample => sample.Descriptors),
                        threads = Slope(steady, sample => sample.Threads),
                        nativeConnections = Slope(steady, sample => sample.NativeConnections),
                        nativeStreams = Slope(steady, sample => sample.NativeStreams),
                    },
                    unobserved = Unobserved.Skip(_unobservedStart).ToArray(),
                    samples,
                };
                var file = Path.Combine(directory, campaign + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.Out.WriteLine("Native lifetime evidence: " + Path.GetFullPath(file));
            }
            private static double Slope(List<Sample> samples, Func<Sample, double> value)
            {
                var points = samples.Skip(1).ToList();
                var meanX = points.Average(sample => (double)sample.Iteration);
                var meanY = points.Average(value);
                var numerator = points.Sum(sample => (sample.Iteration - meanX) * (value(sample) - meanY));
                var denominator = points.Sum(sample => (sample.Iteration - meanX) * (sample.Iteration - meanX));
                return denominator == 0 ? 0 : numerator / denominator;
            }
            private static (string? Path, string? Sha256) LoadedLibrary()
            {
                string? path = null;
                if (OperatingSystem.IsMacOS())
                {
                    var count = NativeMethods.ImageCount();
                    for (uint index = 0; index < count && path == null; index++)
                    {
                        var name = Marshal.PtrToStringUTF8(NativeMethods.ImageName(index));
                        if (name != null && System.IO.Path.GetFileName(name).StartsWith("libmsquic", StringComparison.Ordinal)) path = name;
                    }
                }
                else if (OperatingSystem.IsLinux() && File.Exists("/proc/self/maps"))
                    path = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Where(fields => fields.Length > 5 && fields[5].Contains("libmsquic", StringComparison.Ordinal)).Select(fields => fields[5]).FirstOrDefault();
                else
                {
                    using var process = Process.GetCurrentProcess();
                    path = process.Modules.Cast<ProcessModule>().Select(module => module.FileName)
                        .FirstOrDefault(name => System.IO.Path.GetFileName(name).StartsWith("msquic", StringComparison.OrdinalIgnoreCase));
                }
                if (path == null) return (null, null);
                var canonical = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
                return (canonical, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonical))));
            }
        }

        [SupportedOSPlatform("macos")]
        private static class NativeMethods
        {
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
            internal static extern uint ImageCount();
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
            internal static extern IntPtr ImageName(uint index);
        }
    }
}
