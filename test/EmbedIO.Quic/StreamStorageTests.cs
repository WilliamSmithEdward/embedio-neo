using System.Buffers;

namespace EmbedIO.Quic
{
    internal static class StreamStorageTests
    {
        internal static void Run()
        {
            DiscardedStorage();
            var pool = new TrackingPool();

            using (var empty = new StreamReassembly(65536, pool))
            {
                empty.Add(0, [], true);
                Check(empty.Complete && empty.Consume().Length == 0 && pool.Rents == 0, "empty FIN does not rent");
            }

            using (var buffer = new StreamReassembly(65536, pool))
            {
                buffer.Add(2, [3]);
                Check(buffer.RetainedCapacity < 4096 && buffer.Consume().Length == 0, "sparse byte occupies one page");
                buffer.Add(0, [1, 2]);
                Check(
                    buffer
                        .Consume(2)
                        .AsSpan()
                        .SequenceEqual((ReadOnlySpan<byte>)[1, 2])
                        && pool.Active == 1,
                    "partial read retains unread bytes"
                );
                Check(
                    buffer
                        .Consume()
                        .AsSpan()
                        .SequenceEqual((ReadOnlySpan<byte>)[3])
                        && pool.Active == 0,
                    "last byte releases page"
                );
                buffer.Add(9, [4]);
                Reject(() => buffer.Add(8, [5, 6]));
                buffer.Dispose();
                buffer.Dispose();
                Check(pool.Active == 0, "overlap rejection and repeated disposal release once");
                CheckDisposed(() => buffer.Add(0, []));
                CheckDisposed(() => buffer.Consume());
            }

            foreach (var capacity in new[] { 16, 1023, 1024, 1025, 65536 })
            {
                using var buffer = new StreamReassembly(capacity, pool);
                var bytes = Enumerable
                    .Range(0, capacity)
                    .Select(value => (byte)value)
                    .ToArray();

                for (var cycle = 0; cycle < 4; cycle++)
                {
                    var offset = buffer.Delivered;
                    var split = capacity / 2;
                    buffer.Add(offset + (ulong)split, bytes.AsSpan(split));
                    Check(buffer.Consume().Length == 0, "hole prevents dirty pooled data delivery");
                    buffer.Add(offset, bytes.AsSpan(0, split));
                    Check(
                        buffer
                            .Consume(offset + (ulong)split)
                            .AsSpan()
                            .SequenceEqual(bytes.AsSpan(0, split)),
                        "bounded read crosses pages"
                    );
                    Check(
                        buffer
                            .Consume()
                            .AsSpan()
                            .SequenceEqual(bytes.AsSpan(split)),
                        "wrap preserves remainder"
                    );
                    Check(pool.Active == 0 && buffer.RetainedCapacity == 0, "completed read returns every page");
                    buffer.Add(offset, bytes);
                    Check(buffer.Consume().Length == 0 && pool.Active == 0, "retired duplicates do not rent");
                }

                buffer.Add(buffer.Delivered, [], true);
                Check(buffer.Complete, "FIN preserved after pooled wrap");
            }

            using (var pending = new StreamReassembly(65536, pool))
            {
                pending.Add(1024, [1]);
                pending.Add(65535, [2]);
                Check(pool.Active == 2, "separated bytes occupy distinct pages");
            }

            Check(pool.Active == 0 && pool.Rents == pool.Returns, "disposal releases sparse pages");
        }

        private static void DiscardedStorage()
        {
            var pool = new TrackingPool();

            using (var buffer = new StreamReassembly(1024, pool))
            {
                buffer.Add(0, [1, 2, 3, 4, 5]);
                buffer.DiscardFrom(3);
                pool.CheckCleared(3, 2);
                Reject(() => buffer.Add(4, [], true));
                Reject(() => buffer.Add(1025, []));
                buffer.Add(5, [], true);
                buffer.DiscardFrom(4);
                Check(
                    buffer
                        .Consume()
                        .AsSpan()
                        .SequenceEqual((ReadOnlySpan<byte>)[1, 2, 3]),
                    "discard limit cannot be increased"
                );
                Check(
                    buffer.Delivered == 3 && buffer.FinalSize == 5 && !buffer.Complete,
                    "discard preserves delivered count and final size"
                );
                var rents = pool.Rents;
                buffer.Add(3, [9, 9], true);
                Check(pool.Rents == rents && buffer.Consume().Length == 0, "discarded suffix never rents again");
                buffer.Dispose();
                CheckDisposed(() => buffer.DiscardFrom(0));
            }

            foreach (var capacity in new[] { 16, 1023, 1024, 1025, 65536 })
            {
                using var buffer = new StreamReassembly(capacity, pool);
                var bytes = Enumerable
                    .Range(0, capacity)
                    .Select(value => (byte)value)
                    .ToArray();
                buffer.Add(0, bytes.AsSpan(0, capacity - 1));
                Check(buffer.Consume().Length == capacity - 1, "prepare wrapped discard");
                var start = buffer.Delivered;
                buffer.Add(start, bytes, true);
                var prefix = capacity / 2;
                buffer.DiscardFrom(start + (ulong)prefix);
                buffer.DiscardFrom(start + (ulong)prefix - 1);
                buffer.DiscardFrom(start + (ulong)prefix);
                Check(buffer.Delivered == start, "discard never advances delivery");
                Check(
                    buffer
                        .Consume()
                        .AsSpan()
                        .SequenceEqual(bytes.AsSpan(0, prefix - 1)),
                    "wrapped discard preserves reliable prefix"
                );
                Check(pool.Active == 0 && buffer.RetainedCapacity == 0, "wrapped discard and consume return all pages");
                var rents = pool.Rents;
                buffer.Add(start, bytes, true);
                Check(pool.Rents == rents && buffer.Consume().Length == 0, "wrapped late data stays discarded");
                Reject(() => buffer.Add(start + (ulong)capacity, [1]));
            }

            using (var empty = new StreamReassembly(0, pool))
            {
                empty.DiscardFrom(0);
                empty.Add(0, [], true);
                Check(empty.Complete && empty.RetainedCapacity == 0, "empty discard remains allocation-free");
            }

            Check(pool.Active == 0 && pool.Rents == pool.Returns, "all discarded pool ownership released once");
        }

        private sealed class TrackingPool : ArrayPool<byte>
        {
            private readonly HashSet<byte[]> _active = [];

            internal int Rents { get; private set; }
            internal int Returns { get; private set; }
            internal int Active => _active.Count;

            internal void CheckCleared(int offset, int length) =>
                Check(
                    _active
                        .Single()
                        .AsSpan(offset, length)
                        .IndexOfAnyExcept((byte)0)
                        < 0,
                    "discarded bytes cleared while page remains owned"
                );

            public override byte[] Rent(int minimumLength)
            {
                var bytes = new byte[minimumLength + 16];
                Array.Fill(bytes, (byte)0xa5);
                _active.Add(bytes);
                Rents++;
                return bytes;
            }

            public override void Return(byte[] array, bool clearArray = false)
            {
                Check(_active.Remove(array), "pool owns every returned page once");
                Check(clearArray, "payload cleared before pooled reuse");
                Array.Clear(array);
                Returns++;
            }
        }

        private static void Reject(Action action)
        {
            try
            {
                action();
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException("Expected conflicting overlap rejection.");
        }

        private static void CheckDisposed(Action action)
        {
            try
            {
                action();
            }

            catch (ObjectDisposedException)
            {
                return;
            }

            throw new InvalidOperationException("Disposed receive buffer remained usable.");
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Stream storage regression: " + name);
        }
    }
}
