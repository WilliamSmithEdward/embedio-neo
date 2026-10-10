using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Native QUIC datagram ownership (RFC 9221) over the internal MsQuic provider.
    // Synthetic-event cases drive the documented v2.6.2 event layouts directly;
    // native cases use a real MsQuic server connection.
    public sealed class MsQuicNativeDatagramTest
    {
        private static readonly Assembly Core = typeof(WebServer).Assembly;
        private static readonly uint Pending = OperatingSystem.IsWindows() ? 0x000703E5u : unchecked((uint)-2);
        private static readonly uint InvalidParameter = OperatingSystem.IsWindows() ? 0x80070057u : 22u;
        private static readonly uint InvalidState = OperatingSystem.IsWindows() ? 0x8007139Fu : 1u;
        private static readonly uint OutOfMemory = OperatingSystem.IsWindows() ? 0x8007000Eu : 12u;
        private static readonly byte[] H3 = { (byte)'h', (byte)'3' };

        [Test]
        public void SendsWaitForNegotiationAndRespectTheNegotiatedLimit()
        {
            var native = new FakeNative();
            using var datagrams = Create(native);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
            Assert.That(SendNegotiated(datagrams).IsCompleted, Is.False);
            using (var state = NativeEvent.StateChanged(true, 1200)) Deliver(datagrams, state);
            Assert.That(SendNegotiated(datagrams).Result, Is.True);
            Assert.That(Get<int>(datagrams, "MaxSendLength"), Is.EqualTo(1200));
            Assert.That(TrySend(datagrams, new byte[1201], out var rejected), Is.EqualTo("TooLarge"));
            Assert.That(rejected, Is.Null);
            Assert.That(native.Calls, Is.Empty, "Locally rejected datagrams must never reach MsQuic.");
            Assert.That(TrySend(datagrams, new byte[1200], out var accepted), Is.EqualTo("Queued"));
            Assert.That(accepted, Is.Not.Null);
            Assert.That(native.Calls.Single().Length, Is.EqualTo(1200));
            // A smaller path MTU later lowers the limit.
            using (var state = NativeEvent.StateChanged(true, 1100)) Deliver(datagrams, state);
            Assert.That(TrySend(datagrams, new byte[1101], out _), Is.EqualTo("TooLarge"));
            using (var state = NativeEvent.StateChanged(false, 0)) Deliver(datagrams, state);
            Assert.That(Get<int>(datagrams, "MaxSendLength"), Is.Zero);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
            Complete(datagrams, native.Calls[0].Context, 6);
        }

        [Test]
        public void PeerWithoutDatagramSupportReportsUnavailable()
        {
            var native = new FakeNative();
            using var datagrams = Create(native);
            using (var state = NativeEvent.StateChanged(false, 0)) Deliver(datagrams, state);
            Assert.That(SendNegotiated(datagrams).Result, Is.False);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
            Assert.That(native.Calls, Is.Empty);
        }

        // A server processes the peer's transport parameters before it owns the
        // connection, so MsQuic may raise no DATAGRAM_STATE_CHANGED at all.
        [Test]
        public void ConnectedQueryEstablishesSupportWhenMsQuicIndicatedNoLimit()
        {
            var native = new FakeNative();
            using var datagrams = Create(native);
            Call(datagrams, "OnConnected", (bool?)true);
            Assert.That(SendNegotiated(datagrams).Result, Is.True);
            Assert.That(Get<bool>(datagrams, "MaxSendLengthIndicated"), Is.False);
            Assert.That(Get<int>(datagrams, "MaxSendLength"), Is.EqualTo(65535), "Until MsQuic indicates a limit, only the protocol ceiling applies.");
            Assert.That(TrySend(datagrams, new byte[65536], out _), Is.EqualTo("TooLarge"));
            Assert.That(native.Calls, Is.Empty);
            native.Status = InvalidParameter; // MsQuic's own path limit is authoritative.
            Assert.That(TrySend(datagrams, new byte[4000], out _), Is.EqualTo("TooLarge"));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
            using (var mtu = NativeEvent.StateChanged(true, 1452)) Deliver(datagrams, mtu);
            Assert.That(Get<bool>(datagrams, "MaxSendLengthIndicated"), Is.True);
            Assert.That(Get<int>(datagrams, "MaxSendLength"), Is.EqualTo(1452));
        }

        [TestCase(false)]
        [TestCase(null)]
        public void ConnectedQueryWithoutSupportOrAnswerReportsUnavailable(bool? queried)
        {
            var native = new FakeNative();
            using var datagrams = Create(native);
            Call(datagrams, "OnConnected", queried);
            Assert.That(SendNegotiated(datagrams).Result, Is.False);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
            Assert.That(native.Calls, Is.Empty);
        }

        [Test]
        public void AnIndicatedLimitIsNotReplacedByTheConnectedQuery()
        {
            using var datagrams = Create(new FakeNative());
            using (var state = NativeEvent.StateChanged(true, 1200)) Deliver(datagrams, state);
            Call(datagrams, "OnConnected", (bool?)true);
            Assert.That(Get<int>(datagrams, "MaxSendLength"), Is.EqualTo(1200));
            Assert.That(Get<bool>(datagrams, "MaxSendLengthIndicated"), Is.True);
        }

        [Test]
        public void SubmittedBytesAreCopiedFromTheCallersSliceIntoOwnedNativeMemory()
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200);
            var source = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            Assert.That(TrySend(datagrams, new ReadOnlyMemory<byte>(source, 5, 17), out var completion), Is.EqualTo("Queued"));
            Array.Fill(source, (byte)0xEE); // The caller may reuse its buffer immediately.
            var call = native.Calls.Single();
            Assert.That(call.Count, Is.EqualTo(1));
            Assert.That(call.Context, Is.Not.EqualTo(IntPtr.Zero), "MsQuic suppresses discard indications for a null context.");
            Assert.That(call.Bytes, Is.EqualTo(Enumerable.Range(5, 17).Select(i => (byte)i).ToArray()));
            Assert.That(ReadNative(call.Buffers), Is.EqualTo(call.Bytes), "The descriptor and payload must stay valid until a final state.");
            Complete(datagrams, call.Context, 4);
            Assert.That(Outcome(Required(completion)), Is.EqualTo("Acknowledged"));
        }

        [Test]
        public void EmptyDatagramsAreSubmittedWithAZeroLengthBuffer()
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200);
            Assert.That(TrySend(datagrams, ReadOnlyMemory<byte>.Empty, out var completion), Is.EqualTo("Queued"));
            Assert.That(native.Calls.Single().Length, Is.Zero);
            Complete(datagrams, native.Calls[0].Context, 6);
            Assert.That(Outcome(Required(completion)), Is.EqualTo("Canceled"));
        }

        [TestCase(3, "Lost")]
        [TestCase(4, "Acknowledged")]
        [TestCase(5, "Acknowledged")]
        [TestCase(6, "Canceled")]
        [TestCase(7, "Lost")]
        public void OwnershipReturnsOnlyAtAFinalSendState(int final, string outcome)
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            Assert.That(TrySend(datagrams, bytes, out var completion), Is.EqualTo("Queued"));
            var call = native.Calls.Single();
            foreach (var intermediate in new[] { 0, 1, 2 })
            {
                using var progress = NativeEvent.SendState(call.Context, intermediate);
                Deliver(datagrams, progress);
                Assert.That(progress.Context, Is.EqualTo(call.Context), "A non-final state leaves the context for later indications.");
                Assert.That(Required(completion).IsCompleted, Is.False);
                Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.EqualTo(1));
                Assert.That(ReadNative(call.Buffers), Is.EqualTo(bytes), "SENT and LOST_SUSPECT must not release native memory.");
            }
            using (var terminal = NativeEvent.SendState(call.Context, final))
            {
                Deliver(datagrams, terminal);
                Assert.That(terminal.Context, Is.EqualTo(IntPtr.Zero), "The in/out context is cleared so MsQuic cannot indicate it again.");
            }
            Assert.That(Outcome(Required(completion)), Is.EqualTo(outcome));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
            Assert.That(Get<long>(datagrams, "AbandonedSends"), Is.Zero);
        }

        [Test]
        public void UnknownContextsAreIgnored()
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200);
            using var empty = NativeEvent.SendState(IntPtr.Zero, 4);
            Deliver(datagrams, empty);
            var foreign = GCHandle.Alloc(new object());
            try
            {
                using var other = NativeEvent.SendState(GCHandle.ToIntPtr(foreign), 4);
                Deliver(datagrams, other);
                Assert.That(other.Context, Is.EqualTo(GCHandle.ToIntPtr(foreign)));
            }
            finally { foreign.Free(); }
        }

        [Test]
        public void OutstandingSendsAreBoundedUntilAFinalStateReturnsASlot()
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200, sendCapacity: 2);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Queued"));
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Queued"));
            Assert.That(TrySend(datagrams, new byte[1], out var refused), Is.EqualTo("QueueFull"));
            Assert.That(refused, Is.Null);
            Assert.That(native.Calls, Has.Count.EqualTo(2));
            using (var sent = NativeEvent.SendState(native.Calls[0].Context, 1)) Deliver(datagrams, sent);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("QueueFull"), "SENT is not final; the slot stays owned.");
            Complete(datagrams, native.Calls[0].Context, 3);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Queued"));
            Complete(datagrams, native.Calls[1].Context, 4);
            Complete(datagrams, native.Calls[2].Context, 4);
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
        }

        [TestCase("parameter", "TooLarge")]
        [TestCase("state", "Unavailable")]
        public void SynchronousNativeRefusalReleasesTheSubmission(string failure, string status)
        {
            var native = new FakeNative { Status = failure == "parameter" ? InvalidParameter : InvalidState };
            using var datagrams = Negotiated(native, 1200);
            Assert.That(TrySend(datagrams, new byte[8], out var completion), Is.EqualTo(status));
            Assert.That(completion, Is.Null);
            Assert.That(native.Calls, Has.Count.EqualTo(1));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
            native.Status = Pending;
            Assert.That(TrySend(datagrams, new byte[8], out _), Is.EqualTo("Queued"));
            Complete(datagrams, native.Calls[1].Context, 4);
        }

        [Test]
        public void UnexpectedNativeFailureThrowsAfterReleasingTheSubmission()
        {
            var native = new FakeNative { Status = OutOfMemory };
            using var datagrams = Negotiated(native, 1200);
            Assert.Throws<IOException>(() => TrySend(datagrams, new byte[8], out _));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
        }

        [Test]
        public void ClosedConnectionHandleRefusesWithoutANativeCall()
        {
            var native = new FakeNative();
            var connection = new FakeConnection();
            using var datagrams = Negotiated(native, 1200, connection: connection);
            connection.Dispose();
            Assert.That(TrySend(datagrams, new byte[8], out var completion), Is.EqualTo("Closed"));
            Assert.That(completion, Is.Null);
            Assert.That(native.Calls, Is.Empty);
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
        }

        [Test]
        public async Task ReceivedDatagramsAreCopiedBeforeTheBorrowedBufferIsReclaimed()
        {
            using var datagrams = Create(new FakeNative());
            var first = new byte[] { 10, 20, 30 };
            using (var received = NativeEvent.Received(first))
            {
                Deliver(datagrams, received);
                received.Scribble(); // MsQuic reclaims the payload after the callback.
            }
            using (var empty = NativeEvent.Received(Array.Empty<byte>())) Deliver(datagrams, empty);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var one = await Receive(datagrams, cancellation.Token);
            Assert.That(Payload(Required(one)), Is.EqualTo(first));
            using var two = await Receive(datagrams, cancellation.Token);
            Assert.That(Payload(Required(two)), Is.Empty);
            Assert.That(Get<int>(datagrams, "QueuedReceiveCount"), Is.Zero);
        }

        [Test]
        public async Task ReceiveQueueDropsArrivalsBeyondItsCountOrByteBound()
        {
            using var datagrams = Create(new FakeNative(), receiveCapacity: 2, receiveByteLimit: 10);
            foreach (var size in new[] { 4, 4, 1 })
            {
                using var received = NativeEvent.Received(new byte[size]);
                Deliver(datagrams, received);
            }
            Assert.That(Get<int>(datagrams, "QueuedReceiveCount"), Is.EqualTo(2));
            Assert.That(Get<long>(datagrams, "ReceiveDropped"), Is.EqualTo(1), "Count bound.");
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Required(await Receive(datagrams, token.Token)).Dispose();
            using (var tooLarge = NativeEvent.Received(new byte[7])) Deliver(datagrams, tooLarge);
            Assert.That(Get<long>(datagrams, "ReceiveDropped"), Is.EqualTo(2), "Byte bound: 4 queued + 7 exceeds 10.");
            using (var fits = NativeEvent.Received(new byte[6])) Deliver(datagrams, fits);
            Assert.That(Get<int>(datagrams, "QueuedReceiveCount"), Is.EqualTo(2));
            using var a = await Receive(datagrams, token.Token);
            using var b = await Receive(datagrams, token.Token);
            Assert.That(new[] { Get<int>(Required(a), "Length"), Get<int>(Required(b), "Length") }, Is.EqualTo(new[] { 4, 6 }));
        }

        [Test]
        public async Task DisposingAReceivedDatagramClearsItsPooledStorage()
        {
            using var datagrams = Create(new FakeNative());
            using (var received = NativeEvent.Received(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A })) Deliver(datagrams, received);
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var datagram = Required(await Receive(datagrams, token.Token));
            var memory = Get<ReadOnlyMemory<byte>>(datagram, "Payload");
            Assert.That(MemoryMarshal.TryGetArray(memory, out var storage), Is.True);
            datagram.Dispose();
            datagram.Dispose();
            // Another renter could reuse the array, but never restores this pattern.
            Assert.That(Required(storage.Array).Take(4).ToArray(), Is.Not.EqualTo(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A }), "Sensitive received bytes are cleared before pool reuse.");
            Assert.Throws<TargetInvocationException>(() => datagram.GetType().GetProperty("Payload", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(datagram));
        }

        [Test]
        public async Task CanceledReceiveLeavesTheQueueIntact()
        {
            using var datagrams = Create(new FakeNative());
            using var cancellation = new CancellationTokenSource();
            var pending = Receive(datagrams, cancellation.Token);
            Assert.That(pending.IsCompleted, Is.False);
            cancellation.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            using (var received = NativeEvent.Received(new byte[] { 7 })) Deliver(datagrams, received);
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var datagram = await Receive(datagrams, token.Token);
            Assert.That(Payload(Required(datagram)), Is.EqualTo(new byte[] { 7 }));
        }

        [Test]
        public async Task ConnectionShutdownDrainsQueuedDatagramsThenEndsReceiveAndSend()
        {
            var native = new FakeNative();
            using var datagrams = Create(native);
            using (var received = NativeEvent.Received(new byte[] { 1, 2 })) Deliver(datagrams, received);
            Call(datagrams, "OnConnectionShutdownComplete");
            Assert.That(SendNegotiated(datagrams).Result, Is.False, "Negotiation never observed before shutdown.");
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Closed"));
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var queued = await Receive(datagrams, token.Token);
            Assert.That(Payload(Required(queued)), Is.EqualTo(new byte[] { 1, 2 }), "Owned copies stay readable after shutdown.");
            Assert.That(await Receive(datagrams, token.Token), Is.Null);
            Assert.That(native.Calls, Is.Empty);
        }

        [Test]
        public async Task PendingReceiveCompletesAtConnectionShutdown()
        {
            using var datagrams = Create(new FakeNative());
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = Receive(datagrams, token.Token);
            Assert.That(pending.IsCompleted, Is.False);
            Call(datagrams, "OnConnectionShutdownComplete");
            Assert.That(await pending, Is.Null);
        }

        [Test]
        public async Task DisposalDiscardsUnreadDatagramsButLateSendStatesStillReleaseOwnership()
        {
            var native = new FakeNative();
            var datagrams = Negotiated(native, 1200);
            using (var received = NativeEvent.Received(new byte[] { 0x33, 0x33 })) Deliver(datagrams, received);
            Assert.That(TrySend(datagrams, new byte[] { 9, 9 }, out var completion), Is.EqualTo("Queued"));
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = Receive(datagrams, CancellationToken.None);
            // The queued datagram satisfies the first receive; a second waits.
            Required(await pending).Dispose();
            var waiting = Receive(datagrams, token.Token);
            Assert.That(waiting.IsCompleted, Is.False);
            datagrams.Dispose();
            datagrams.Dispose();
            await Assert.ThatAsync(async () => await waiting, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Closed"));
            using (var late = NativeEvent.Received(new byte[] { 1 })) Deliver(datagrams, late);
            Assert.That(Get<long>(datagrams, "ReceiveDropped"), Is.EqualTo(1));
            Assert.That(Get<int>(datagrams, "QueuedReceiveCount"), Is.Zero);
            Assert.That(Required(completion).IsCompleted, Is.False, "Disposal never frees memory MsQuic still owns.");
            Assert.That(ReadNative(native.Calls[0].Buffers), Is.EqualTo(new byte[] { 9, 9 }));
            Complete(datagrams, native.Calls[0].Context, 3);
            Assert.That(Outcome(Required(completion)), Is.EqualTo("Lost"));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
        }

        [Test]
        public void ConnectionCloseCountsAndReleasesSendsMsQuicNeverFinalized()
        {
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200);
            Assert.That(TrySend(datagrams, new byte[4], out var completion), Is.EqualTo("Queued"));
            Call(datagrams, "ReleaseAfterConnectionClose");
            Assert.That(Outcome(Required(completion)), Is.EqualTo("Canceled"));
            Assert.That(Get<long>(datagrams, "AbandonedSends"), Is.EqualTo(1));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Closed"));
        }

        [Test]
        public void ConnectionCloseCannotReleaseASendWhileItsPayloadIsBeingPrepared()
        {
            var native = new FakeNative();
            using var connection = new FakeConnection();
            using var datagrams = Negotiated(native, 1200, connection: connection);
            connection.OnRelease = () => Call(datagrams, "ReleaseAfterConnectionClose");
            using var payload = new ClosingPayload(connection);
            var memory = payload.Memory;
            payload.Armed = true;

            Assert.Throws<IOException>(() => TrySend(datagrams, memory, out _));
            Assert.That(payload.ReleasedDuringCopy, Is.False,
                "ConnectionClose must wait for payload preparation and synchronous-failure cleanup.");
            Assert.That(connection.Released, Is.True);
            Assert.That(native.Calls, Is.Empty);
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
            Assert.That(Get<long>(datagrams, "AbandonedSends"), Is.Zero,
                "A send that never reached MsQuic must be released before ConnectionClose inspects pending sends.");
        }

        [Test]
        public async Task ConcurrentSubmissionAndCompletionNeverExceedTheSendBound()
        {
            const int capacity = 8;
            var native = new FakeNative();
            using var datagrams = Negotiated(native, 1200, sendCapacity: capacity);
            var completions = new System.Collections.Concurrent.ConcurrentBag<Task>();
            var maxPending = 0;
            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                if (TrySend(datagrams, new byte[] { (byte)i }, out var completion) == "Queued")
                {
                    completions.Add(Required(completion));
                    InterlockedMax(ref maxPending, Get<int>(datagrams, "PendingSendCount"));
                }
                // A concurrent "connection thread" finalizes whatever is outstanding,
                // possibly before TrySend returns. Claims are by submission index:
                // a released GCHandle value can be reused by a later submission.
                FakeNative.Call[] snapshot;
                lock (native.Calls) snapshot = native.Calls.ToArray();
                for (var index = 0; index < snapshot.Length; index++)
                    if (native.TryClaim(index)) Complete(datagrams, snapshot[index].Context, i % 2 == 0 ? 4 : 3);
            });
            var remaining = native.Calls.ToArray();
            for (var index = 0; index < remaining.Length; index++)
                if (native.TryClaim(index)) Complete(datagrams, remaining[index].Context, 6);
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(maxPending, Is.LessThanOrEqualTo(capacity));
            Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
        }

        [TestCase(0, 1, 1)]
        [TestCase(1, 0, 1)]
        [TestCase(1, 1, 0)]
        public void InvalidBoundsAreRejected(int receiveCapacity, int receiveByteLimit, int sendCapacity)
            => Assert.Throws<ArgumentOutOfRangeException>(() => Create(new FakeNative(), receiveCapacity, receiveByteLimit, sendCapacity));

        // Real MsQuic: a System.Net.Quic peer never advertises max_datagram_frame_size.
        [Test]
        public async Task NativeServerReportsUnsupportedNegotiationFromAPeerWithoutDatagrams()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeUnsupportedCore();
        }

        [Test]
        public async Task NativeDatagramEnablementIsRefusedAfterConfigurationOrTwice()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeEnablementCore();
        }

        // Independent peer: pinned aioquic from test/EmbedIO.Conformance/drivers.
        // Set EMBEDIO_DATAGRAM_PEER_PYTHON to a Python with drivers/requirements.txt.
        [TestCase("echo")]
        [TestCase("unsupported")]
        public async Task NativeDatagramsInteroperateWithAnIndependentAioquicPeer(string mode)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            var python = Environment.GetEnvironmentVariable("EMBEDIO_DATAGRAM_PEER_PYTHON");
            if (string.IsNullOrEmpty(python)) { Assert.Ignore("EMBEDIO_DATAGRAM_PEER_PYTHON is not set."); return; }
            await NativeAioquicCore(python, mode);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeUnsupportedCore()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var server = await NativeServer.StartAsync();
            var connecting = QuicConnection.ConnectAsync(server.ClientOptions(), deadline.Token).AsTask();
            using var connection = await server.AcceptAsync(deadline.Token);
            var datagrams = (IDisposable)Call(connection, "EnableDatagrams");
            Call(connection, "Configure", server.Configuration);
            await using var peer = await connecting.WaitAsync(deadline.Token);
            await Connected(connection).WaitAsync(deadline.Token);
            Assert.That(await SendNegotiated(datagrams).WaitAsync(deadline.Token), Is.False);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
            var pending = Receive(datagrams, deadline.Token);
            Assert.That(pending.IsCompleted, Is.False);
            await ((Task)Call(connection, "ShutdownAsync", 0x100L)).WaitAsync(deadline.Token);
            Assert.That(await pending.WaitAsync(deadline.Token), Is.Null);
            Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Closed"));
            connection.Dispose();
            Assert.That(Get<long>(datagrams, "AbandonedSends"), Is.Zero);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeEnablementCore()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var server = await NativeServer.StartAsync();
            for (var configuredFirst = 0; configuredFirst < 2; configuredFirst++)
            {
                var connecting = QuicConnection.ConnectAsync(server.ClientOptions(), deadline.Token).AsTask();
                using var connection = await server.AcceptAsync(deadline.Token);
                if (configuredFirst == 1)
                {
                    Call(connection, "Configure", server.Configuration);
                    Assert.Throws<InvalidOperationException>(() => Call(connection, "EnableDatagrams"));
                    Assert.Throws<InvalidOperationException>(() => Call(connection, "EnableDatagrams"), "A refused enablement leaves no owner behind.");
                }
                else
                {
                    var datagrams = Call(connection, "EnableDatagrams");
                    Assert.Throws<InvalidOperationException>(() => Call(connection, "EnableDatagrams"));
                    Call(connection, "Configure", server.Configuration);
                    Assert.That(datagrams, Is.Not.Null);
                }
                // Either way the connection itself stays healthy.
                await using var peer = await connecting.WaitAsync(deadline.Token);
                await Connected(connection).WaitAsync(deadline.Token);
                await ((Task)Call(connection, "ShutdownAsync", 0x100L)).WaitAsync(deadline.Token);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeAioquicCore(string python, string mode)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var server = await NativeServer.StartAsync();
            var certificate = Path.Combine(Path.GetTempPath(), "embedio-datagram-" + Guid.NewGuid().ToString("N") + ".pem");
            File.WriteAllText(certificate, server.Certificate.ExportCertificatePem());
            try
            {
                var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in new[] { "-I", DriverPath(), mode, "--port", server.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--cafile", certificate, "--sha256", server.Certificate.GetCertHashString(HashAlgorithmName.SHA256) })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new AssertionException("The aioquic peer did not start.");
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                using var connection = await server.AcceptAsync(deadline.Token);
                var datagrams = (IDisposable)Call(connection, "EnableDatagrams");
                Call(connection, "Configure", server.Configuration);
                await Connected(connection).WaitAsync(deadline.Token);
                var negotiated = await SendNegotiated(datagrams).WaitAsync(deadline.Token);
                var completions = new List<Task>();
                var echoed = 0;
                if (mode == "echo")
                {
                    Assert.That(negotiated, Is.True);
                    var limit = Get<int>(datagrams, "MaxSendLength");
                    Assert.That(limit, Is.InRange(1000, 65535));
                    Assert.That(TrySend(datagrams, new byte[limit + 1], out _), Is.EqualTo("TooLarge"));
                    Assert.That(TrySend(datagrams, System.Text.Encoding.ASCII.GetBytes("embedio-native-datagram-hello"), out var greeting), Is.EqualTo("Queued"));
                    completions.Add(Required(greeting));
                    while (await Receive(datagrams, deadline.Token) is { } datagram)
                    {
                        using (datagram)
                        {
                            Assert.That(TrySend(datagrams, Payload(datagram), out var reply), Is.EqualTo("Queued"));
                            completions.Add(Required(reply));
                            echoed++;
                        }
                    }
                }
                else
                {
                    Assert.That(negotiated, Is.False);
                    Assert.That(TrySend(datagrams, new byte[1], out _), Is.EqualTo("Unavailable"));
                    Assert.That(await Receive(datagrams, deadline.Token), Is.Null);
                }
                await process.WaitForExitAsync(deadline.Token);
                var lines = await output;
                TestContext.Out.WriteLine(lines);
                TestContext.Out.WriteLine(await errors);
                Assert.That(process.ExitCode, Is.Zero, lines);
                Assert.That(lines, Does.Contain("\"event\": \"done\""));
                await Task.WhenAll(completions).WaitAsync(deadline.Token);
                var outcomes = completions.Select(Outcome).ToArray();
                TestContext.Out.WriteLine("echoed=" + echoed + " outcomes=" + string.Join(",", outcomes.GroupBy(o => o).Select(g => g.Key + ":" + g.Count())));
                if (mode == "echo")
                {
                    Assert.That(echoed, Is.GreaterThanOrEqualTo(7));
                    Assert.That(outcomes, Has.Some.EqualTo("Acknowledged"));
                }
                Assert.That(Get<int>(datagrams, "PendingSendCount"), Is.Zero);
                connection.Dispose();
                Assert.That(Get<long>(datagrams, "AbandonedSends"), Is.Zero);
            }
            finally { File.Delete(certificate); }
        }

        private static string DriverPath()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "test", "EmbedIO.Conformance", "drivers", "quic_datagram_peer.py");
                if (File.Exists(candidate)) return candidate;
            }
            throw new AssertionException("Missing test/EmbedIO.Conformance/drivers/quic_datagram_peer.py.");
        }

        private sealed class NativeServer : IAsyncDisposable
        {
            private readonly SafeHandle _api;
            private readonly SafeHandle _registration;
            private readonly SafeHandle _listener;
            internal SafeHandle Configuration { get; }
            internal X509Certificate2 Certificate { get; }
            internal IPEndPoint Endpoint { get; }
            private NativeServer(SafeHandle api, SafeHandle registration, SafeHandle configuration, SafeHandle listener, X509Certificate2 certificate, IPEndPoint endpoint)
            { _api = api; _registration = registration; Configuration = configuration; _listener = listener; Certificate = certificate; Endpoint = endpoint; }

            internal static Task<NativeServer> StartAsync()
            {
                var type = Core.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi") ?? throw new IgnoreException("The retained asset does not include the native provider.");
                var api = (SafeHandle)(type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
                    ?? throw new AssertionException("Missing native API owner."));
                var registration = (SafeHandle)Call(api, "CreateRegistration");
                var configuration = (SafeHandle)Call(registration, "CreateConfiguration", (object)H3);
                var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
                Call(configuration, "LoadServerCertificate", certificate);
                var listener = (SafeHandle)Call(registration, "CreateListener");
                Call(listener, "EnableAcceptance");
                Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
                return Task.FromResult(new NativeServer(api, registration, configuration, listener, certificate, (IPEndPoint)Call(listener, "LocalEndPoint")));
            }

            internal async Task<SafeHandle> AcceptAsync(CancellationToken token)
            {
                var accept = (Task)Call(_listener, "AcceptAsync", token);
                await accept.WaitAsync(token);
                return (SafeHandle)Required(TaskResult(accept));
            }

            [SupportedOSPlatform("windows")]
            [SupportedOSPlatform("linux")]
            [SupportedOSPlatform("macos")]
            internal QuicClientConnectionOptions ClientOptions() => new()
            {
                RemoteEndPoint = Endpoint,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString() == Certificate.GetCertHashString(),
                },
            };

            public async ValueTask DisposeAsync()
            {
                await ((Task)Call(_listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(10));
                _listener.Dispose();
                Configuration.Dispose();
                _registration.Dispose();
                _api.Dispose();
                Certificate.Dispose();
            }
        }

        private sealed class FakeConnection : SafeHandle
        {
            public FakeConnection() : base(IntPtr.Zero, true) => SetHandle((IntPtr)1);
            public override bool IsInvalid => false;
            internal Action? OnRelease;
            internal bool Released;
            protected override bool ReleaseHandle()
            {
                Released = true;
                OnRelease?.Invoke();
                return true;
            }
        }

        private sealed class ClosingPayload : MemoryManager<byte>
        {
            private readonly FakeConnection _connection;
            private readonly byte[] _bytes = new byte[4];
            internal bool Armed;
            internal bool ReleasedDuringCopy;
            internal ClosingPayload(FakeConnection connection) => _connection = connection;
            public override Span<byte> GetSpan()
            {
                if (Armed)
                {
                    _connection.Dispose();
                    ReleasedDuringCopy = _connection.Released;
                    // Never write through the old code's prematurely freed native pointer.
                    throw new IOException("Controlled payload-copy failure.");
                }
                return _bytes;
            }
            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
            public override void Unpin() { }
            protected override void Dispose(bool disposing) { }
        }

        private sealed class FakeNative
        {
            internal sealed record Call(IntPtr Buffers, uint Count, IntPtr Context, int Length, byte[] Bytes);
            internal readonly List<Call> Calls = new();
            private readonly HashSet<int> _claimed = new();
            internal uint Status;
            internal FakeNative() => Status = Pending;
            internal uint Send(IntPtr connection, IntPtr buffers, uint count, uint flags, IntPtr context)
            {
                Assert.That(connection, Is.EqualTo((IntPtr)1));
                Assert.That(flags, Is.Zero);
                var bytes = ReadNative(buffers);
                lock (Calls) Calls.Add(new Call(buffers, count, context, bytes.Length, bytes));
                return Status;
            }
            internal bool TryClaim(int index) { lock (_claimed) return _claimed.Add(index); }
        }

        // QUIC_CONNECTION_EVENT on 64-bit layouts: uint32 Type, union at offset 8.
        private sealed class NativeEvent : IDisposable
        {
            internal IntPtr Pointer { get; }
            private IntPtr _buffer;
            private IntPtr _payload;
            private int _length;
            private NativeEvent(int type)
            {
                Pointer = Marshal.AllocHGlobal(64);
                Marshal.Copy(new byte[64], 0, Pointer, 64);
                Marshal.WriteInt32(Pointer, type);
            }
            internal int Type => Marshal.ReadInt32(Pointer);
            internal IntPtr Context => Marshal.ReadIntPtr(Pointer, 8);
            internal static NativeEvent StateChanged(bool enabled, ushort maxLength)
            {
                var result = new NativeEvent(10);
                Marshal.WriteByte(result.Pointer, 8, enabled ? (byte)1 : (byte)0);
                Marshal.WriteInt16(result.Pointer, 10, unchecked((short)maxLength));
                return result;
            }
            internal static NativeEvent Received(byte[] payload)
            {
                var result = new NativeEvent(11);
                result._length = payload.Length;
                result._payload = Marshal.AllocHGlobal(Math.Max(payload.Length, 1));
                Marshal.Copy(payload, 0, result._payload, payload.Length);
                result._buffer = Marshal.AllocHGlobal(16);
                Marshal.WriteInt32(result._buffer, payload.Length);
                Marshal.WriteIntPtr(result._buffer, IntPtr.Size, result._payload);
                Marshal.WriteIntPtr(result.Pointer, 8, result._buffer);
                return result;
            }
            internal static NativeEvent SendState(IntPtr context, int state)
            {
                var result = new NativeEvent(12);
                Marshal.WriteIntPtr(result.Pointer, 8, context);
                Marshal.WriteInt32(result.Pointer, 8 + IntPtr.Size, state);
                return result;
            }
            internal void Scribble()
            {
                for (var i = 0; i < _length; i++) Marshal.WriteByte(_payload, i, 0xCC);
            }
            public void Dispose()
            {
                if (_payload != IntPtr.Zero) Marshal.FreeHGlobal(_payload);
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                Marshal.FreeHGlobal(Pointer);
            }
        }

        private static byte[] ReadNative(IntPtr descriptor)
        {
            var length = Marshal.ReadInt32(descriptor);
            var bytes = new byte[length];
            if (length != 0) Marshal.Copy(Marshal.ReadIntPtr(descriptor, IntPtr.Size), bytes, 0, length);
            return bytes;
        }

        private static IDisposable Create(FakeNative native, int receiveCapacity = 16, int receiveByteLimit = 65536, int sendCapacity = 16, SafeHandle? connection = null)
        {
            var type = Core.GetType("EmbedIO.Net.Internal.Http3.MsQuicNativeDatagrams")
                ?? throw new IgnoreException("The retained asset does not include the native provider.");
            var sendType = Core.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi+DatagramFunctions+SendDatagram")
                ?? throw new AssertionException("Missing native datagram send binding.");
            var send = Delegate.CreateDelegate(sendType, native, nameof(FakeNative.Send));
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            try { return (IDisposable)constructor.Invoke(new object[] { connection ?? new FakeConnection(), send, receiveCapacity, receiveByteLimit, sendCapacity }); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static IDisposable Negotiated(FakeNative native, ushort limit, int sendCapacity = 16, SafeHandle? connection = null)
        {
            var datagrams = Create(native, sendCapacity: sendCapacity, connection: connection);
            using var state = NativeEvent.StateChanged(true, limit);
            Deliver(datagrams, state);
            return datagrams;
        }

        private static void Deliver(object datagrams, NativeEvent native) => Call(datagrams, "OnConnectionEvent", native.Type, native.Pointer);

        private static void Complete(object datagrams, IntPtr context, int state)
        {
            using var terminal = NativeEvent.SendState(context, state);
            Deliver(datagrams, terminal);
        }

        private static string TrySend(object datagrams, ReadOnlyMemory<byte> payload, out Task? completion)
        {
            var arguments = new object?[] { payload, null };
            var status = Call(datagrams, "TrySend", arguments);
            completion = (Task?)arguments[1];
            return $"{status}";
        }

        private static T Required<T>(T? value) where T : class
            => value ?? throw new AssertionException("Missing expected value.");

        private static object? TaskResult(Task task) => task.GetType().GetProperty("Result")?.GetValue(task);

        private static string Outcome(Task completion) => $"{Required(TaskResult(completion))}";

        private static Task<bool> SendNegotiated(object datagrams) => Get<Task<bool>>(datagrams, "SendNegotiated");

        private static async Task<IDisposable?> Receive(object datagrams, CancellationToken token)
        {
            var pending = Call(datagrams, "ReceiveAsync", token);
            var task = (Task)(pending.GetType().GetMethod("AsTask")?.Invoke(pending, null)
                ?? throw new AssertionException("Missing ValueTask conversion."));
            await task;
            return (IDisposable?)TaskResult(task);
        }

        private static byte[] Payload(object datagram) => Get<ReadOnlyMemory<byte>>(datagram, "Payload").ToArray();

        private static Task Connected(SafeHandle connection) => Get<Task>(connection, "Connected");

        private static T Get<T>(object owner, string property)
            => (T)(owner.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new AssertionException("Missing property " + property + "."));

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }

        private static object Call(object owner, string method, params object?[] arguments)
        {
            try
            {
                var member = owner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .SingleOrDefault(m => m.Name == method && m.GetParameters().Length == arguments.Length)
                    ?? throw new AssertionException("Missing member " + method + ".");
                var result = member.Invoke(owner, arguments);
                return member.ReturnType == typeof(void) ? typeof(void) : result ?? throw new AssertionException("Missing result from " + method + ".");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
