namespace EmbedIO.Quic
{
    internal static class AsyncStreamTests
    {
        private sealed class Peer : IDisposable
        {
            internal bool CanQueue = true;
            internal ulong? ClosedCode;

            internal List<Delivery> Sent { get; } = [];
            internal ConnectionStreams Streams { get; }
            internal AsyncStreamApplication Application => (AsyncStreamApplication)Streams.Protocol;

            internal Peer(
                ulong uni = 32,
                ulong bidi = 32,
                ulong datagram = 1200,
                ulong data = 1UL << 32,
                int inboundBidi = 16,
                int inboundUni = 16
            ) =>
                Streams = new ConnectionStreams(
                    (bytes, reliable) =>
                    {
                        var delivery = new Delivery(2, bytes, reliable);
                        Sent.Add(delivery);
                        return delivery;
                    },
                    data,
                    65536,
                    65536,
                    65536,
                    uni,
                    bidi,
                    datagram,
                    true,
                    operations => new AsyncStreamApplication(operations),
                    _ => CanQueue,
                    code => ClosedCode = code,
                    inboundBidi,
                    inboundUni
                );

            internal void Pump() => Streams.Prune();

            internal void Ack()
            {
                foreach (var item in Sent) item.Acknowledged = true;

                Sent.Clear();
                Pump();
            }

            public void Dispose() => Streams.Dispose();
        }

        internal static void Run()
        {
            ConfiguredCapacity(128, 16, true);
            ConfiguredCapacity(16, 128, true);
            ConfiguredCapacity(1, 1, false);
            ReadWrite();
            ResetCredit();
            ResetDiscard();
            FlowBackpressure();
            Cancellation();
            LocalStreams();
            Datagrams();
            Closure();

            for (var index = 0; index < 100; index++) ReadRace();

            Mutate([0, 1, 2, 7]);
            Mutate([8, 9, 10, 15]);
            ConnectionClosure([0, 17]);
            ConnectionClosure([1, 29]);
            ConnectionClosure([2, 43]);
            CheckNotificationsAsync()
                .GetAwaiter()
                .GetResult();
        }

        internal static bool ConfiguredCapacity(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || (data[0] & 31) != 0) return false;

            ConfiguredCapacity(33 + data[1] % 4, 1, false);
            return true;
        }

        private static void ConfiguredCapacity(int inboundBidi, int inboundUni, bool localStreams)
        {
            using var peer = new Peer(uni: 64, bidi: 64, inboundBidi: inboundBidi, inboundUni: inboundUni);
            var total = inboundBidi + inboundUni;

            for (var index = 0; index < inboundBidi; index++) peer.Streams.Receive((ulong)index * 4, 0, [], true);

            for (var index = 0; index < inboundUni; index++) peer.Streams.Receive((ulong)index * 4 + 2, 0, [], true);

            Check(
                peer.Application.Retained == total && peer.Streams.RetiredStreams == 0,
                "configured-unaccepted-retention"
            );
            var bidiFailure = Expect<TransportException>(
                () => peer.Streams.Receive((ulong)inboundBidi * 4, 0, [], true)
            );
            var uniFailure = Expect<TransportException>(
                () => peer.Streams.Receive((ulong)inboundUni * 4 + 2, 0, [], true)
            );
            Check(
                bidiFailure.ErrorCode == 4 && uniFailure.ErrorCode == 4 && peer.Application.Retained == total,
                "configured-overflow-preserves-owners"
            );

            var outbound = new List<AsyncQuicStream>();

            if (localStreams)
            {
                for (var index = 0; index < 32; index++)
                    foreach (var uni in new[] { false, true })
                    {
                        var opening = peer.Application
                            .OpenOutboundStreamAsync(uni)
                            .AsTask();
                        peer.Pump();
                        outbound.Add(Await(opening));
                    }

                Check(peer.Application.Retained == total + 64, "configured-inbound-and-local-capacity");
                using var canceled = new CancellationTokenSource();
                var blockedBidi = peer.Application
                    .OpenOutboundStreamAsync(false, canceled.Token)
                    .AsTask();
                var blockedUni = peer.Application
                    .OpenOutboundStreamAsync(true, canceled.Token)
                    .AsTask();
                peer.Pump();
                Check(!blockedBidi.IsCompleted && !blockedUni.IsCompleted, "configured-local-capacity-backpressure");
                canceled.Cancel();
                Expect<OperationCanceledException>(() => Await(blockedBidi));
                Expect<OperationCanceledException>(() => Await(blockedUni));
                peer.Pump();
                Check(peer.Application.PendingOpens == 0, "configured-canceled-opens-retired");
            }

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Expect<OperationCanceledException>(
                    () => Await(
                        peer.Application
                            .AcceptInboundStreamAsync(canceled.Token)
                            .AsTask()
                    )
                );
                Check(
                    peer.Application.Retained == total + outbound.Count,
                    "configured-canceled-accept-preserves-queue"
                );
            }

            var expected = new HashSet<ulong>();

            for (var index = 0; index < inboundBidi; index++) expected.Add((ulong)index * 4);

            for (var index = 0; index < inboundUni; index++) expected.Add((ulong)index * 4 + 2);

            for (var index = 0; index < total; index++)
            {
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                Check(expected.Remove(stream.Id), "configured-accepted-identity");
                Check(
                    Await(
                        stream
                            .ReadAsync(new byte[1])
                            .AsTask()
                    )
                        == 0,
                    "configured-accepted-final-size"
                );

                if (stream.CanWrite)
                {
                    var write = stream
                        .WriteOwnedAsync(ReadOnlyMemory<byte>.Empty, true, default)
                        .AsTask();
                    peer.Pump();
                    Await(write);
                    peer.Ack();
                }

                else peer.Pump();
            }

            Check(expected.Count == 0 && peer.Application.Retained == outbound.Count, "configured-inbound-retirement");

            foreach (var stream in outbound)
            {
                using (stream)
                {
                    if (stream.CanRead)
                    {
                        peer.Streams.Receive(stream.Id, 0, [], true);
                        Check(
                            Await(
                                stream
                                    .ReadAsync(new byte[1])
                                    .AsTask()
                            )
                                == 0,
                            "configured-local-eof"
                        );
                    }

                    var write = stream
                        .WriteOwnedAsync(ReadOnlyMemory<byte>.Empty, true, default)
                        .AsTask();
                    peer.Pump();
                    Await(write);
                    peer.Ack();
                }
            }

            Check(
                peer.Application.Retained == 0
                    && peer.Streams.IncomingCount == 0
                    && peer.Streams.OutgoingCount == 0
                    && peer.Streams.RetiredStreams == total + outbound.Count,
                "configured-all-owners-retired"
            );
            peer.Streams.Receive((ulong)inboundBidi * 4, 0, [], false);
            peer.Streams.Receive((ulong)inboundUni * 4 + 2, 0, [], false);
            Check(peer.Application.Retained == 2, "configured-stream-credit-replenished");
            peer.Dispose();
            Check(peer.Application.Retained == 0, "configured-queued-owners-disposed");
        }

        private static void ReadWrite()
        {
            using var peer = new Peer();
            peer.Streams.Receive(0, 0, [], false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            var memory = new byte[10];
            var read = stream
                .ReadAsync(memory)
                .AsTask();
            Expect<InvalidOperationException>(
                () => Await(
                    stream
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
            );
            peer.Streams.Receive(0, 2, [3, 4], true);
            Check(!read.IsCompleted, "read-awaits-gap");
            peer.Streams.Receive(0, 0, [1, 2], false);
            Check(
                Await(read) == 4
                    && memory
                        .AsSpan(0, 4)
                        .SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "read-orders-bytes"
            );
            Await(stream.ReadsClosed);
            Check(
                Await(
                    stream
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
                    == 0,
                "read-eof"
            );
            var wake = peer.Application.WaitForCommandsAsync(default);
            var source = new byte[] { 8, 9 };
            var write = stream
                .WriteOwnedAsync(source, true, default)
                .AsTask();
            Check(Await(wake), "application-write-wakes-owner");
            peer.Pump();
            Await(write);
            Array.Fill(source, (byte)255);
            var frame = new WireReader(peer.Sent.Last().Bytes);
            Check(
                frame.Integer() == 15
                    && frame.Integer() == 0
                    && frame.Integer() == 0
                    && frame.Integer() == 2
                    && frame
                        .Take(2)
                        .SequenceEqual(new byte[] { 8, 9 }),
                "write-releases-caller-memory-after-copy"
            );
            Check(!stream.WritesClosed.IsCompleted, "write-close-awaits-fin-ack");
            var disposal = stream
                .DisposeAsync()
                .AsTask();
            Check(!disposal.IsCompleted, "async-dispose-preserves-fin-until-ack");
            peer.Ack();
            Await(disposal);
            Check(peer.Streams.RetiredStreams == 1 && peer.Application.Retained == 0, "async-bidi-retired");
            Check(!peer.Sent.Any(item => item.Bytes[0] is 4 or 0x24), "graceful-dispose-does-not-reset");
            Expect<ObjectDisposedException>(
                () => Await(
                    stream
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
            );

            using var empty = new Peer();
            empty.Streams.Receive(2, 0, [], true);
            Check(
                empty.Streams.RetiredStreams == 0 && empty.Application.Retained == 1,
                "unaccepted-empty-fin-keeps-stream-credit"
            );
            using var input = Await(
                empty.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            Check(
                Await(
                    input
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
                    == 0,
                "empty-accepted-eof"
            );
            empty.Pump();
            Check(
                empty.Streams.RetiredStreams == 1 && empty.Application.Retained == 0,
                "empty-accepted-stream-retires"
            );
        }

        private static void ResetCredit()
        {
            using var peer = new Peer();
            var bytes = Enumerable
                .Range(0, 32768)
                .Select(index => (byte)index)
                .ToArray();
            peer.Streams.Receive(2, 0, bytes, false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            peer.Streams.Reset(2, 909, 65536, 32768, true);
            Check(
                peer.Streams.ReleasedData == 32768 && stream.Buffered == 32768 && peer.Streams.RetiredStreams == 0,
                "reliable-reset-keeps-unread-prefix-credit"
            );
            var prefix = new byte[16384];
            Check(
                Await(
                    stream
                        .ReadAsync(prefix)
                        .AsTask()
                )
                    == prefix.Length
                    && prefix
                        .AsSpan()
                        .SequenceEqual(bytes.AsSpan(0, prefix.Length)),
                "reset-prefix-first-half"
            );
            peer.Pump();
            Check(
                peer.Streams.ReleasedData == 49152 && peer.Streams.RetiredStreams == 0,
                "reset-only-consumed-prefix-credit"
            );
            Check(
                Await(
                    stream
                        .ReadAsync(prefix)
                        .AsTask()
                )
                    == prefix.Length
                    && prefix
                        .AsSpan()
                        .SequenceEqual(bytes.AsSpan(prefix.Length)),
                "reset-prefix-second-half"
            );
            peer.Pump();
            Check(peer.Streams.ReleasedData == 65536 && peer.Streams.RetiredStreams == 1, "reset-credit-released-once");
            var failure = Expect<QuicStreamFailure>(
                () => Await(
                    stream
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
            );
            Check(failure.ErrorCode == 909 && failure.Remote, "reset-original-application-code");
            Expect<QuicStreamFailure>(() => Await(stream.ReadsClosed));
        }

        private static void ResetDiscard()
        {
            using (var peer = new Peer())
            {
                peer.Streams.Receive(2, 0, new byte[32768], false);
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                var closed = stream.ReadsClosed;
                peer.Streams.Reset(2, 901, 32768, 0, false);
                var failure = Expect<QuicStreamFailure>(() => Await(closed));
                Check(
                    failure.ErrorCode == 901
                        && stream.Buffered == 0
                        && peer.Streams.ReleasedData == 32768
                        && peer.Streams.RetiredStreams == 1,
                    "ordinary-reset-discards-unread-body-and-signals-immediately"
                );
                Expect<QuicStreamFailure>(
                    () => Await(
                        stream
                            .ReadAsync(new byte[1])
                            .AsTask()
                    )
                );
            }

            using (var peer = new Peer())
            {
                var bytes = Enumerable
                    .Range(0, 64)
                    .Select(index => (byte)index)
                    .ToArray();
                peer.Streams.Receive(2, 0, bytes, false);
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                var first = new byte[8];
                Check(
                    Await(
                        stream
                            .ReadAsync(first)
                            .AsTask()
                    )
                        == 8,
                    "reset-trim-initial-consumption"
                );
                peer.Streams.Reset(2, 902, 100, 16, true);
                Check(
                    stream.Buffered == 8 && peer.Streams.ReleasedData == 92,
                    "reset-keeps-only-unread-required-prefix"
                );
                Check(
                    Await(
                        stream
                            .ReadAsync(first)
                            .AsTask()
                    )
                        == 8
                        && first
                            .AsSpan()
                            .SequenceEqual(bytes.AsSpan(8, 8)),
                    "reset-retained-prefix-bytes"
                );
                peer.Pump();
                Check(
                    peer.Streams.ReleasedData == 100 && peer.Streams.RetiredStreams == 1,
                    "reset-trim-and-gap-credit-release-once"
                );
                Expect<QuicStreamFailure>(() => Await(stream.ReadsClosed));
            }
        }

        private static void FlowBackpressure()
        {
            using var peer = new Peer(data: 0);
            peer.Streams.Receive(0, 0, [], true);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            var write = stream
                .WriteOwnedAsync(new byte[32768], true, default)
                .AsTask();
            peer.Pump();
            peer.Pump();
            Await(write);
            Check(peer.Sent.Count == 0 && !stream.WritesClosed.IsCompleted, "flow-blocked-write-copies-within-budget");
            peer.CanQueue = false;
            peer.Streams.UpdateData(65536);
            Check(peer.Sent.Count == 0, "credit-update-respects-packet-queue-budget");
            peer.CanQueue = true;
            peer.Pump();
            Check(peer.Sent.Count > 0, "owner-pump-resumes-queued-flow-write");
            peer.Ack();
            Await(stream.WritesClosed);
            Check(peer.Application.Retained == 0, "backpressured-stream-retires");
        }

        private static void Cancellation()
        {
            using (var peer = new Peer())
            {
                peer.Streams.Receive(0, 0, [], false);
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                using var stop = new CancellationTokenSource();
                var memory = new byte[8];
                var read = stream
                    .ReadOwnedAsync(memory, 0x100, stop.Token)
                    .AsTask();
                stop.Cancel();
                Expect<OperationCanceledException>(() => Await(read));
                Array.Fill(memory, (byte)77);
                peer.Streams.Receive(0, 0, [1, 2, 3, 4], false);
                Check(memory.All(value => value == 77), "cancel-read-releases-borrowed-memory");
                var stopped = peer.Sent.First(item => item.Bytes[0] == 5);
                var frame = new WireReader(stopped.Bytes);
                Check(
                    frame.Integer() == 5 && frame.Integer() == 0 && frame.Integer() == 0x100,
                    "read-cancel-explicit-no-error-code"
                );
                peer.Streams.Reset(0, 0, 20, 0, false);
                var write = stream
                    .WriteOwnedAsync(new byte[] { 9 }, true, default)
                    .AsTask();
                peer.Pump();
                Await(write);
                peer.Ack();
                Check(
                    peer.Streams.RetiredStreams == 1 && peer.Streams.ReleasedData == 20,
                    "canceled-upload-preserves-response-half"
                );
            }

            using (var peer = new Peer())
            {
                peer.Streams.Receive(0, 0, [], true);
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                using var stop = new CancellationTokenSource();
                peer.CanQueue = false;
                var write = stream
                    .WriteOwnedAsync(new byte[32], true, stop.Token)
                    .AsTask();
                peer.Pump();
                stop.Cancel();
                Expect<OperationCanceledException>(() => Await(write));
                peer.CanQueue = true;
                peer.Pump();
                Check(peer.Sent.Count == 0, "write-cancel-before-copy-has-no-wire-side-effect");
                write = stream
                    .WriteOwnedAsync(new byte[] { 5 }, true, default)
                    .AsTask();
                peer.Pump();
                Await(write);
                peer.Ack();
                Check(peer.Streams.RetiredStreams == 1, "write-after-unadmitted-cancellation");
            }

            using (var peer = new Peer())
            {
                peer.Streams.Receive(0, 0, [], true);
                using var stream = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                using var stop = new CancellationTokenSource();
                var memory = Enumerable
                    .Repeat((byte)42, 32768)
                    .ToArray();
                var write = stream
                    .WriteOwnedAsync(memory, false, stop.Token)
                    .AsTask();
                peer.Pump();
                Check(!write.IsCompleted, "partial-write-remains-pending");
                stop.Cancel();
                Expect<OperationCanceledException>(() => Await(write));
                Array.Fill(memory, (byte)255);
                peer.Pump();
                var frame = new WireReader(peer.Sent.Last().Bytes);
                Check(
                    frame.Integer() == 0x24
                        && frame.Integer() == 0
                        && frame.Integer() == 0x10c
                        && frame.Integer() == 16384
                        && frame.Integer() == 0,
                    "partial-write-cancel-resets-only-written-extent"
                );
                Expect<QuicStreamFailure>(
                    () => Await(
                        stream
                            .WriteAsync(new byte[1])
                            .AsTask()
                    )
                );
                peer.Ack();
                Check(peer.Application.Retained == 0, "partial-write-cancel-retires-after-reset-ack");
            }
        }

        private static void LocalStreams()
        {
            using var peer = new Peer(0, 1);
            using var canceled = new CancellationTokenSource();
            var canceledOpen = peer.Application
                .OpenOutboundStreamAsync(true, canceled.Token)
                .AsTask();
            canceled.Cancel();
            Expect<OperationCanceledException>(() => Await(canceledOpen));
            var uni = peer.Application
                .OpenOutboundStreamAsync(true)
                .AsTask();
            var bidi = peer.Application
                .OpenOutboundStreamAsync(false)
                .AsTask();
            peer.Pump();
            using var output = Await(bidi);
            Check(output.Id == 1 && !uni.IsCompleted, "bidi-open-bypasses-blocked-uni");
            var accept = peer.Application
                .AcceptInboundStreamAsync()
                .AsTask();
            Check(!accept.IsCompleted, "local-stream-is-not-inbound-accept");
            peer.Streams.UpdateCount(true, 1);
            peer.Pump();
            using var sendOnly = Await(uni);
            Check(sendOnly.Id == 3 && !sendOnly.CanRead && sendOnly.CanWrite, "outbound-uni-direction");
            Expect<NotSupportedException>(
                () => Await(
                    sendOnly
                        .ReadAsync(new byte[1])
                        .AsTask()
                )
            );
            var write = output
                .WriteOwnedAsync(new byte[] { 7, 8 }, true, default)
                .AsTask();
            peer.Pump();
            Await(write);
            peer.Streams.Receive(1, 0, [4, 5], true);
            var response = new byte[2];
            Check(
                Await(
                    output
                        .ReadAsync(response)
                        .AsTask()
                )
                    == 2
                    && response.SequenceEqual(new byte[] { 4, 5 }),
                "local-bidi-response"
            );
            peer.Pump();
            peer.Ack();
            Check(!peer.Sent.Any(item => item.Bytes[0] == 0x12), "local-bidi-retirement-does-not-grant-peer-streams");
            write = sendOnly
                .WriteOwnedAsync(new byte[] { 6 }, true, default)
                .AsTask();
            peer.Pump();
            Await(write);
            peer.Ack();
            Check(peer.Streams.RetiredStreams == 2 && peer.Application.Retained == 0, "outbound-streams-retire");
            peer.Application.ConnectionClosed(123, false);
            Expect<QuicConnectionFailure>(() => Await(accept));
        }

        private static void Datagrams()
        {
            using var peer = new Peer(datagram: 64);
            Check(peer.Application.MaximumDatagramPayload == 62, "datagram-payload-includes-framing");
            peer.CanQueue = false;

            for (var index = 0; index < 32; index++)
                Check(peer.Application.TrySendDatagram([(byte)index]), "bounded-datagram-enqueue");

            Check(!peer.Application.TrySendDatagram([99]), "datagram-queue-full");
            Expect<ArgumentOutOfRangeException>(() => peer.Application.TrySendDatagram(new byte[63]));
            peer.Pump();
            Check(peer.Sent.Count == 0, "datagram-backpressure");
            peer.CanQueue = true;

            for (var index = 0; index < 4; index++) peer.Pump();

            Check(peer.Sent.Count == 32 && peer.Sent.All(item => !item.Reliable), "datagrams-never-retransmitted");

            for (var index = 0; index < 40; index++) peer.Streams.Datagram([(byte)index]);

            Check(
                Await(
                    peer.Application
                        .ReceiveDatagramAsync()
                        .AsTask()
                ).SequenceEqual(new byte[] { 8 }),
                "received-datagrams-have-bounded-lossy-queue"
            );
            using var unsupported = new Peer(datagram: 0);
            Expect<NotSupportedException>(() => unsupported.Application.TrySendDatagram([]));
        }

        private static void Closure()
        {
            using var peer = new Peer(0, 0);
            peer.Streams.Receive(0, 0, [], false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            peer.CanQueue = false;
            var read = stream
                .ReadAsync(new byte[8])
                .AsTask();
            var write = stream
                .WriteAsync(new byte[8])
                .AsTask();
            var open = peer.Application
                .OpenOutboundStreamAsync(true)
                .AsTask();
            var accept = peer.Application
                .AcceptInboundStreamAsync()
                .AsTask();
            var datagram = peer.Application
                .ReceiveDatagramAsync()
                .AsTask();
            peer.Application.ConnectionClosed(321, true);

            foreach (var pending in new Task[] { read, write, open, accept, datagram })
            {
                var failure = Expect<QuicConnectionFailure>(() => Await(pending));
                Check(
                    failure.ErrorCode == 321 && failure.Application && failure.Remote,
                    "connection-close-fails-pending-with-original-code"
                );
            }

            peer.Dispose();
            peer.Dispose();
            Check(
                peer.Streams.IncomingCount == 0 && peer.Streams.OutgoingCount == 0 && peer.Application.Retained == 0,
                "connection-close-releases-all-owners"
            );
            using var local = new Peer();
            var close = local.Application
                .CloseAsync(777)
                .AsTask();
            Check(!close.IsCompleted, "close-awaits-owner");
            local.Pump();
            Await(close);
            Check(local.ClosedCode == 777 && local.Application.IsClosed, "local-close-owner-code");
        }

        private static void ReadRace()
        {
            using var peer = new Peer();
            peer.Streams.Receive(0, 0, [], false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            using var stop = new CancellationTokenSource();
            var memory = new byte[16];
            var read = stream
                .ReadAsync(memory, stop.Token)
                .AsTask();
            var cancel = Task.Run(stop.Cancel);
            peer.Streams.Receive(0, 0, [1, 2], false);
            Await(cancel);

            try
            {
                Check(Await(read) == 2, "racing-read-completion");
            }

            catch (OperationCanceledException) { }

            Array.Fill(memory, (byte)77);
            peer.Streams.Receive(0, 2, [3, 4], false);
            Check(memory.All(value => value == 77), "racing-cancellation-releases-memory-before-return");
        }

        internal static void Mutate(ReadOnlySpan<byte> steps)
        {
            using var peer = new Peer();
            using var sibling = new Peer();
            ulong bidi = 0, uni = 2, localUni = 3;
            var retired = 0;

            foreach (var command in steps[..Math.Min(steps.Length, 4)])
            {
                var unidirectional = (command & 1) != 0;
                var id = unidirectional ? uni : bidi;

                if (unidirectional) uni += 4;

                else bidi += 4;

                peer.Streams.Receive(id, 0, [], false);
                using var input = Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                );
                var payload = Enumerable
                    .Range(0, 2 + command % 14)
                    .Select(index => (byte)(index ^ command))
                    .ToArray();
                var destination = new byte[payload.Length];
                using var stop = new CancellationTokenSource();
                var read = input
                    .ReadOwnedAsync(destination, 0x100, stop.Token)
                    .AsTask();

                if ((command & 2) != 0)
                {
                    stop.Cancel();
                    Expect<OperationCanceledException>(() => Await(read));
                    Array.Fill(destination, (byte)77);
                    peer.Streams.Receive(id, 0, payload, true);
                    Check(destination.All(value => value == 77), "model-canceled-buffer-is-unborrowed");
                }

                else
                {
                    var split = payload.Length / 2;
                    peer.Streams.Receive(id, (ulong)split, payload[split..], true);
                    Check(!read.IsCompleted, "model-gap-awaits-prefix");
                    peer.Streams.Receive(id, 0, payload[..split], false);
                    Check(
                        Await(read) == payload.Length && destination.SequenceEqual(payload),
                        "model-read-exact-prefix"
                    );
                }

                var output = input;

                if (unidirectional)
                {
                    var opened = peer.Application
                        .OpenOutboundStreamAsync(true)
                        .AsTask();
                    peer.Pump();
                    output = Await(opened);
                    Check(output.Id == localUni, "model-local-uni-id");
                    localUni += 4;
                }

                using (output)
                {
                    using var writeStop = new CancellationTokenSource();
                    var write = output
                        .WriteOwnedAsync(payload, true, writeStop.Token)
                        .AsTask();

                    if ((command & 4) != 0)
                    {
                        writeStop.Cancel();
                        Expect<OperationCanceledException>(() => Await(write));
                        write = output
                            .WriteOwnedAsync(payload, true, default)
                            .AsTask();
                    }

                    peer.Pump();
                    Await(write);
                    var frame = new WireReader(peer.Sent.Last().Bytes);
                    Check(
                        frame.Integer() == 15
                            && frame.Integer() == output.Id
                            && frame.Integer() == 0
                            && frame.Integer() == (ulong)payload.Length
                            && frame
                                .Take(payload.Length)
                                .SequenceEqual(payload),
                        "model-wire-output-matches-independent-bytes"
                    );
                    peer.Ack();
                    Await(output.WritesClosed);
                    retired += unidirectional ? 2 : 1;
                    Check(
                        peer.Streams.RetiredStreams == retired
                            && peer.Application.Retained == 0
                            && peer.Streams.IncomingCount == 0
                            && peer.Streams.OutgoingCount == 0,
                        "model-ack-retires-all-records"
                    );
                    Check(
                        sibling.Sent.Count == 0
                            && sibling.Streams.ReceivedData == 0
                            && sibling.Application.Retained == 0,
                        "model-owner-isolation"
                    );
                }
            }

            if (steps.Length != 0 && (steps[0] & 8) != 0)
            {
                var opened = peer.Application
                    .OpenOutboundStreamAsync(false)
                    .AsTask();
                peer.Pump();
                using var stream = Await(opened);
                Check(stream.Id == 1, "model-local-bidi-id");
                var write = stream
                    .WriteOwnedAsync(new byte[] { 17, 29 }, true, default)
                    .AsTask();
                peer.Pump();
                Await(write);
                peer.Ack();
                Await(stream.WritesClosed);
                Check(peer.Application.Retained == 1, "model-local-bidi-keeps-incomplete-receive-half");
                peer.Streams.Stop(1, 99);
                Check(
                    peer.Sent.Count == 0 && stream.WritesClosed.IsCompletedSuccessfully,
                    "model-late-stop-cannot-reopen-acknowledged-send-half"
                );
                peer.Streams.Receive(1, 0, [31, 43], true);
                var response = new byte[2];
                Check(
                    Await(
                        stream
                            .ReadAsync(response)
                            .AsTask()
                    )
                        == 2
                        && response.SequenceEqual(new byte[] { 31, 43 }),
                    "model-local-bidi-response-direction"
                );
                peer.Pump();
                retired++;
                Check(
                    peer.Streams.RetiredStreams == retired
                        && peer.Application.Retained == 0
                        && peer.Streams.IncomingCount == 0
                        && peer.Streams.OutgoingCount == 0,
                    "model-local-bidi-final-retirement"
                );
            }

            if (steps.Length != 0 && (steps[0] & 64) != 0) ConnectionClosure(steps);

            if (steps.Length != 0 && (steps[0] & 16) != 0) ResetModel(steps);
        }

        private static void ResetModel(ReadOnlySpan<byte> steps)
        {
            using var peer = new Peer();
            var command = steps[0];
            var bytes = Enumerable
                .Range(0, 16 + command % 16)
                .Select(index => (byte)(command ^ index))
                .ToArray();
            var partial = (command & 32) != 0;
            var reliable = partial ? (ulong)(steps.Length > 1 ? steps[1] % (bytes.Length + 1) : 0) : 0;
            var consumed = steps.Length > 2 ? steps[2] % bytes.Length : 0;
            var final = (ulong)bytes.Length + (ulong)(command % 32);
            var code = 900UL + command;
            peer.Streams.Receive(2, 0, bytes, false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );

            if (consumed != 0)
            {
                var prefix = new byte[consumed];
                Check(
                    Await(
                        stream
                            .ReadAsync(prefix)
                            .AsTask()
                    )
                        == consumed
                        && prefix
                            .AsSpan()
                            .SequenceEqual(bytes.AsSpan(0, consumed)),
                    "reset-model-already-consumed-prefix"
                );
            }

            var closed = stream.ReadsClosed;
            peer.Streams.Reset(2, code, final, reliable, partial);
            var keep = reliable > (ulong)consumed ? (int)reliable - consumed : 0;
            Check(
                stream.Buffered == keep && peer.Streams.ReleasedData == final - (ulong)keep,
                "reset-model-retains-only-required-credit"
            );
            Check(closed.IsCompleted == (keep == 0), "reset-model-close-follows-required-prefix");

            if (keep != 0)
            {
                var prefix = new byte[keep];
                Check(
                    Await(
                        stream
                            .ReadAsync(prefix)
                            .AsTask()
                    )
                        == keep
                        && prefix
                            .AsSpan()
                            .SequenceEqual(bytes.AsSpan(consumed, keep)),
                    "reset-model-required-prefix-exact-bytes"
                );
            }

            var failure = Expect<QuicStreamFailure>(() => Await(closed));
            Check(failure.ErrorCode == code && failure.Remote, "reset-model-original-failure");
            peer.Pump();
            Check(
                peer.Streams.ReleasedData == final
                    && peer.Streams.RetiredStreams == 1
                    && peer.Application.Retained == 0,
                "reset-model-once-only-final-credit-and-retirement"
            );
        }

        internal static void ConnectionClosure(ReadOnlySpan<byte> steps)
        {
            using var peer = new Peer(0, 0);
            peer.Streams.Receive(0, 0, [], false);
            using var stream = Await(
                peer.Application
                    .AcceptInboundStreamAsync()
                    .AsTask()
            );
            peer.CanQueue = false;
            var readMemory = new byte[8];
            var writeMemory = new byte[8];
            var read = stream
                .ReadAsync(readMemory)
                .AsTask();
            var write = stream
                .WriteAsync(writeMemory)
                .AsTask();
            var open = peer.Application
                .OpenOutboundStreamAsync(true)
                .AsTask();
            var accept = peer.Application
                .AcceptInboundStreamAsync()
                .AsTask();
            var datagram = peer.Application
                .ReceiveDatagramAsync()
                .AsTask();
            var command = steps.Length == 0 ? 0 : steps[0];
            var code = (ulong)(steps.Length < 2 ? 1 : steps[1]) + 256;

            if (command % 3 == 0) peer.Application.ConnectionClosed(code, (command & 4) != 0);

            else if (command % 3 == 1)
            {
                var close = peer.Application
                    .CloseAsync(code)
                    .AsTask();
                peer.Pump();
                Await(close);
                Check(peer.ClosedCode == code, "closure-model-local-error-code");
            }

            else peer.Dispose();

            foreach (var task in new Task[] { read, write, open, accept, datagram })
            {
                var failure = Expect<IOException>(() => Await(task));

                if (command % 3 != 2)
                    Check(
                        failure is QuicConnectionFailure closed
                            && closed.ErrorCode == code
                            && closed.Remote == (command % 3 == 0),
                        "closure-model-original-failure-scope"
                    );
            }

            Array.Fill(readMemory, (byte)77);
            Array.Fill(writeMemory, (byte)88);
            peer.Dispose();
            Check(
                peer.Application.Retained == 0
                    && peer.Application.PendingOpens == 0
                    && peer.Streams.IncomingCount == 0
                    && peer.Streams.OutgoingCount == 0
                    && stream.Buffered == 0,
                "closure-model-all-pending-ownership-released"
            );
        }

        private static async Task CheckNotificationsAsync()
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
            var start = new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(AsyncStreamTests).Assembly.Location);

            start.ArgumentList.Add("notification-self-test");
            using var process = new System.Diagnostics.Process { StartInfo = start };
            Check(process.Start(), "notification-process-start");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            try
            {
                await process
                    .WaitForExitAsync(stop.Token)
                    .ConfigureAwait(false);
            }

            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process
                        .WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(3))
                        .ConfigureAwait(false);
                }

                await Task
                    .WhenAll(output, errors)
                    .ConfigureAwait(false);
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    await output.ConfigureAwait(false) + await errors.ConfigureAwait(false)
                );
        }

        internal static async Task<int> NotificationSelfTestAsync()
        {
            var notifications = 0;

            void Observe(object? sender, UnobservedTaskExceptionEventArgs error)
            {
                Interlocked.Increment(ref notifications);
                error.SetObserved();
            }

            TaskScheduler.UnobservedTaskException += Observe;

            try
            {
                NotificationCases();
                await Task
                    .Delay(100)
                    .ConfigureAwait(false);

                for (var index = 0; index < 3; index++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                Console.WriteLine("unobserved-notifications=" + notifications);
                return notifications == 0 ? 0 : 1;
            }

            finally
            {
                TaskScheduler.UnobservedTaskException -= Observe;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void NotificationCases()
        {
            for (var index = 0; index < 30; index++) Mutate([84, 91, 64, 127]);
        }

        private static void Check(bool value, string name) => QuicFuzzChecks.Require(value, "async-" + name);

        private static void Await(Task task) =>
            task
                .WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();

        private static T Await<T>(Task<T> task) =>
            task
                .WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();

        private static T Expect<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }

            catch (T error)
            {
                return error;
            }

            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
