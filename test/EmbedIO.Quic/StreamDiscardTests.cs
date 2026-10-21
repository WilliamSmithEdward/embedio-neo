namespace EmbedIO.Quic
{
    internal static class StreamDiscardTests
    {
        private sealed class Application(StreamOperations operations) : StreamApplication
        {
            internal StreamOperations Operations { get; } = operations;
            internal List<byte> Received { get; } = [];
            internal List<(ulong Error, ulong Reliable)> Resets { get; } = [];

            internal override void Stream(ulong id, byte[] bytes, bool fin) => Received.AddRange(bytes);

            internal override void Reset(ulong id, ulong error, ulong reliableSize) =>
                Resets.Add((error, reliableSize));

            internal override void ReceiveDatagram(byte[] bytes) { }

            internal override bool CanRetire(ulong id) => false;

            internal override void Retire(ulong id) =>
                throw new InvalidOperationException("Application still owns this stream.");

            public override void Dispose() { }
        }

        private sealed class Peer : IDisposable
        {
            internal ConnectionStreams Streams { get; } = new(
                (bytes, reliable) => new Delivery(2, bytes, reliable),
                1UL << 32,
                65536,
                65536,
                65536,
                32,
                32,
                1200,
                true,
                operations => new Application(operations)
            );
            internal Application App => (Application)Streams.Protocol;

            public void Dispose() => Streams.Dispose();
        }

        internal static void Run()
        {
            LateConsumption();
            ReliablePrefix([0, 0, 0, 7]);
            ReliablePrefix([0, 255, 255, 127]);
            FinalSizeHistory();

            foreach (var reset in new[] { false, true })
            {
                using var peer = new Peer();
                peer.Streams.Receive(0, 32768, new byte[32768], false);
                Check(peer.Streams.RetainedReceiveCapacity > 0, "sparse bytes are initially owned");

                if (reset) peer.Streams.Reset(0, 16, 65536, 0, true);

                else peer.App.Operations.AbortRead(0, 16);

                Check(peer.Streams.RetainedReceiveCapacity == 0, "discarded pages release before retirement");
                Check(
                    peer.Streams.IncomingCount == 1 && peer.Streams.ReleasedData == 65536,
                    "metadata remains while discarded credit releases"
                );
                peer.Streams.Receive(0, 32768, new byte[32768], false);
                Check(
                    peer.Streams.RetainedReceiveCapacity == 0 && peer.App.Received.Count == 0,
                    "late discarded bytes do not rent or deliver"
                );
            }
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 || (data[0] & 31) != 0) return;

            ReliablePrefix(data);
        }

        private static void LateConsumption()
        {
            using var peer = new Peer();
            peer.Streams.Receive(0, 0, new byte[32], false);
            peer.Streams.Receive(0, 1024, [1], false);
            peer.App.Operations.AbortRead(0, 16);
            Check(peer.Streams.ReleasedData == 993, "already delivered bytes retain separate consumption ownership");
            peer.App.Operations.Consume(0, 32);
            Check(
                peer.Streams.ReleasedData == 1025 && peer.Streams.ReleasedData == peer.Streams.ReceivedData,
                "late consumption returns credit once"
            );

            try
            {
                peer.App.Operations.Consume(0, 1);
            }

            catch (InvalidOperationException)
            {
                return;
            }

            throw new InvalidOperationException("Application consumed a discarded byte.");
        }

        private static void ReliablePrefix(ReadOnlySpan<byte> data)
        {
            using var peer = new Peer();
            var gap = 1024 + data[1];
            var extent = gap + 4096;
            var reliable = gap + 1 + data[2] * 4;
            var error = (ulong)data[3];
            var bytes = new byte[extent];

            for (var index = 0; index < bytes.Length; index++) bytes[index] = data[index % data.Length];

            peer.Streams.Receive(
                0,
                (ulong)gap,
                bytes
                    .AsSpan(gap)
                    .ToArray(),
                false
            );
            var before = peer.Streams.RetainedReceiveCapacity;
            peer.Streams.Reset(0, error, (ulong)extent, (ulong)(gap + 2048), true);
            Check(peer.Streams.RetainedReceiveCapacity < before, "unused reset tail releases while prefix is pending");
            Check(
                peer.App.Received.Count == 0 && peer.App.Resets.Count == 0,
                "reset waits for its missing reliable prefix"
            );
            peer.Streams.Reset(0, error, (ulong)extent, (ulong)reliable, true);
            peer.Streams.Receive(
                0,
                0,
                bytes
                    .AsSpan(0, gap)
                    .ToArray(),
                false
            );
            Check(
                peer.App.Received
                    .ToArray()
                    .AsSpan()
                    .SequenceEqual(bytes.AsSpan(0, reliable)),
                "shrunk reliable prefix preserves exact bytes"
            );
            Check(
                peer.App.Resets.Count == 1 && peer.App.Resets[0] == (error, (ulong)reliable),
                "reset notifies once after prefix delivery"
            );
            Check(
                peer.Streams.RetainedReceiveCapacity == 0 && peer.Streams.ReleasedData == (ulong)(extent - reliable),
                "only undelivered reset suffix returns immediate credit"
            );
            peer.App.Operations.Consume(0, reliable);
            Check(peer.Streams.ReleasedData == (ulong)extent, "reset prefix consumption completes credit once");
            peer.Streams.Receive(0, 0, bytes, true);
            peer.Streams.Reset(0, error, (ulong)extent, 0, true);
            Check(
                peer.Streams.RetainedReceiveCapacity == 0
                    && peer.App.Resets.Count == 1
                    && peer.Streams.ReleasedData == (ulong)extent,
                "late frames cannot revive reset storage or credit"
            );
            RejectFrame(() => peer.Streams.Receive(0, (ulong)extent, [1], false));
        }

        private static void FinalSizeHistory()
        {
            using (var peer = new Peer())
            {
                peer.Streams.Receive(0, 64, new byte[64], false);
                peer.App.Operations.AbortRead(0, 16);
                RejectFrame(() => peer.Streams.Receive(0, 127, [], true));
            }

            using (var peer = new Peer())
            {
                peer.Streams.Receive(0, 64, new byte[64], false);
                peer.App.Operations.AbortRead(0, 16);
                peer.Streams.Receive(0, 128, [], true);
                peer.Streams.Receive(0, 64, new byte[64], true);
                Check(
                    peer.Streams.RetainedReceiveCapacity == 0 && peer.Streams.ReleasedData == 128,
                    "stopped FIN keeps metadata without payload"
                );
                RejectFrame(() => peer.Streams.Receive(0, 128, [1], false));
            }
        }

        private static void RejectFrame(Action action)
        {
            try
            {
                action();
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException("Discarded stream lost its final-size validation.");
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Stream discard regression: " + name);
        }
    }
}
