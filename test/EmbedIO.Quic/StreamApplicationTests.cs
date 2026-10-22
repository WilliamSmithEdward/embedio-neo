namespace EmbedIO.Quic
{
    internal static class StreamApplicationTests
    {
        private sealed class Application(StreamOperations operations) : StreamApplication
        {
            internal StreamOperations Operations => operations;
            internal List<(ulong Id, byte[] Bytes, bool Fin)> Received { get; } = [];
            internal List<(ulong Id, ulong Code)> Resets { get; } = [];
            internal int Opened { get; private set; }

            internal override void Open(ulong id) => Opened++;

            internal override void Stream(ulong id, byte[] bytes, bool fin) => Received.Add((id, bytes, fin));

            internal override void Reset(ulong id, ulong error, ulong reliableSize) => Resets.Add((id, error));

            internal override void ReceiveDatagram(byte[] bytes) { }

            internal override bool CanRetire(ulong id) => false;

            internal override void Retire(ulong id) { }

            public override void Dispose() { }
        }

        private sealed class Peer : IDisposable
        {
            internal List<Delivery> Sent { get; } = [];
            internal ConnectionStreams Streams { get; }

            internal Peer(bool echo = false) =>
                Streams = new ConnectionStreams(
                    (bytes, reliable) =>
                    {
                        var item = new Delivery(2, bytes, reliable);
                        Sent.Add(item);
                        return item;
                    },
                    1UL << 32,
                    65536,
                    65536,
                    65536,
                    1UL << 20,
                    16,
                    1200,
                    true,
                    createApplication: operations => echo
                        ? new StreamEchoApplication(operations)
                        : new Application(operations)
                );

            internal void Ack()
            {
                foreach (var item in Sent) item.Acknowledged = true;

                Sent.Clear();
                Streams.Prune();
            }

            public void Dispose() => Streams.Dispose();
        }

        internal static void Run()
        {
            using (var peer = new Peer())
            {
                var app = (Application)peer.Streams.Protocol;
                QuicFuzzChecks.Require(peer.Sent.Count == 0, "generic-no-http-startup");
                peer.Streams.Receive(0, 0, [], false);
                peer.Streams.Receive(0, 0, [], false);
                QuicFuzzChecks.Require(app.Opened == 1 && app.Received.Count == 0, "generic-empty-stream-open-once");
                app.Operations.Write(3, [1, 2, 3], false);
                peer.Streams.Stop(3, 999);
                var reader = new WireReader(peer.Sent.Last().Bytes);
                QuicFuzzChecks.Require(
                    reader.Integer() == 0x24
                        && reader.Integer() == 3
                        && reader.Integer() == 999
                        && reader.Integer() == 3
                        && reader.Integer() == 0
                        && reader.Remaining == 0,
                    "generic-no-implicit-http-prefix-or-critical-stream"
                );
                peer.Streams.Reset(0, 123456, 8, 4, true);
                peer.Streams.Receive(0, 2, [3, 4], false);
                QuicFuzzChecks.Require(app.Resets.Count == 0, "generic-reset-awaits-reliable-prefix");
                peer.Streams.Receive(0, 0, [1, 2], false);
                peer.Streams.Reset(0, 123456, 8, 4, true);
                QuicFuzzChecks.Require(
                    app.Resets.SequenceEqual([(0UL, 123456UL)])
                        && app.Received
                            .Single()
                            .Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "generic-reset-code-and-prefix-once"
                );
                peer.Dispose();
                peer.Dispose();
                RejectDisposed(() => peer.Streams.Receive(4, 0, [1], true));
                RejectDisposed(() => app.Operations.Write(7, [1], true));
                RejectDisposed(() => app.Operations.Consume(0, 0));
                RejectDisposed(() => app.Operations.Datagram([1]));
                RejectDisposed(() => app.Operations.Abort(0, 0));
                RejectDisposed(peer.Streams.Prune);
            }

            using (var peer = new Peer())
            {
                var app = (Application)peer.Streams.Protocol;

                for (ulong index = 0; index < 4; index++) peer.Streams.Receive(index * 4, 0, new byte[65536], false);

                QuicFuzzChecks.Require(
                    peer.Streams.ReceiveCreditUpdates == 0,
                    "generic-delivery-does-not-release-credit"
                );

                for (ulong index = 0; index < 4; index++) app.Operations.Consume(index * 4, 32768);

                QuicFuzzChecks.Require(
                    peer.Streams.ReceiveCreditUpdates == 5,
                    "generic-consumption-releases-stream-and-connection-credit"
                );

                for (ulong index = 0; index < 4; index++)
                    peer.Streams.Receive(index * 4, 65536, new byte[32768], false);

                QuicFuzzChecks.Require(peer.Streams.ReceivedData == 393216, "generic-sliding-credit-receives-again");
            }

            Mutate([0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3]);
        }

        internal static void Mutate(ReadOnlySpan<byte> steps)
        {
            using var peer = new Peer(true);
            using var other = new Peer(true);
            var expected = 0;
            ulong bidi = 0, uni = 2;

            foreach (var command in steps[..Math.Min(steps.Length, 16)])
            {
                var unidirectional = (command & 1) != 0;
                var id = unidirectional ? uni : bidi;

                if (unidirectional) uni += 4;

                else bidi += 4;

                peer.Streams.Receive(id, 0, [command, 0, 255], true);
                expected += unidirectional ? 2 : 1;
                QuicFuzzChecks.Require(peer.Streams.RetiredStreams < expected, "generic-retirement-awaits-ack");
                peer.Ack();
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == expected
                        && peer.Streams.IncomingCount == 0
                        && peer.Streams.OutgoingCount == 0
                        && ((StreamEchoApplication)peer.Streams.Protocol).Retained == 0,
                    "generic-ack-retires-both-halves"
                );
                QuicFuzzChecks.Require(
                    other.Streams.RetiredStreams == 0 && other.Sent.Count == 0,
                    "generic-owner-isolation"
                );
            }
        }

        private static void RejectDisposed(Action operation)
        {
            try
            {
                operation();
            }

            catch (ObjectDisposedException)
            {
                return;
            }

            throw new InvalidOperationException("Disposed stream owner accepted an operation.");
        }
    }
}
