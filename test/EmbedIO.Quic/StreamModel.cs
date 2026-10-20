namespace EmbedIO.Quic
{
    // Dictionary oracle uses absolute positions, never the implementation's ring indexing.
    internal static class StreamModel
    {
        private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
            actual.SequenceEqual(expected);

        internal static void SelfTest() =>
            QuicFuzzChecks.Require(
                !Matches([1, 2, 3], [1, 9, 3]) && !Matches([1, 2], [1, 2, 3]) && !Matches([1, 2, 3, 3], [1, 2, 3]),
                "stream-negative-oracle"
            );

        internal static void Check(ReadOnlySpan<byte> data)
        {
            var buffer = new StreamReassembly(32);
            var pending = new SortedDictionary<ulong, byte>();
            ulong delivered = 0, extent = 0;
            ulong? final = null;

            for (var cursor = 0; cursor + 3 < data.Length && cursor < 512; cursor += 4)
            {
                var delta = data[cursor] % 48;
                var offset = (data[cursor + 1] & 1) != 0 && delivered > 0
                    ? delivered - Math.Min(delivered, (ulong)delta)
                    : delivered + (ulong)delta;
                var bytes = Enumerable
                    .Repeat(data[cursor + 2], data[cursor + 1] % 9)
                    .ToArray();
                var end = offset + (ulong)bytes.Length;
                var fin = (data[cursor + 3] & 7) == 0;
                var invalid = end > delivered + 32
                    || (final.HasValue && (end > final || fin && end != final))
                    || fin && end < extent;

                for (var index = 0; index < bytes.Length; index++)
                    if (
                        offset + (ulong)index >= delivered
                        && pending.TryGetValue(offset + (ulong)index, out var old)
                        && old != bytes[index]
                    ) invalid = true;

                try
                {
                    buffer.Add(offset, bytes, fin);
                }

                catch (InvalidDataException)
                {
                    QuicFuzzChecks.Require(invalid, "stream-unexpected-rejection");
                    // A rejected frame closes its connection; never reuse partial failed state.
                    buffer = new StreamReassembly(32);
                    pending.Clear();
                    delivered = extent = 0;
                    final = null;
                    continue;
                }

                QuicFuzzChecks.Require(!invalid, "stream-accepted-invalid");

                if (fin) final = end;

                extent = Math.Max(extent, end);

                for (var index = 0; index < bytes.Length; index++)
                    if (offset + (ulong)index >= delivered) pending[offset + (ulong)index] = bytes[index];

                var maximum = delivered + (ulong)(data[cursor + 3] % 33);
                var expected = new List<byte>();

                while (delivered < maximum && pending.Remove(delivered, out var value))
                {
                    expected.Add(value);
                    delivered++;
                }

                QuicFuzzChecks.Require(
                    Matches(buffer.Consume(maximum), expected.ToArray())
                        && buffer.Delivered == delivered
                        && buffer.FinalSize == final
                        && buffer.Complete == (final == delivered),
                    "stream-delivery-model"
                );
            }

            // The TLS CRYPTO reassembler has a fixed lifetime window. Fragment and
            // reverse arbitrary bytes, then verify its one-time contiguous delivery.
            var source = data[..Math.Min(data.Length, 256)];
            var crypto = new Reassembly(256);
            var split = source.Length / 2;
            crypto.Add((ulong)split, source[split..]);
            crypto.Add(0, source[..split]);
            QuicFuzzChecks.Require(
                crypto
                    .Consume()
                    .AsSpan()
                    .SequenceEqual(source)
                    && crypto.Consume().Length == 0,
                "crypto-reassembly-order"
            );
            crypto.Add(0, source);
            QuicFuzzChecks.Require(crypto.Consume().Length == 0, "crypto-duplicate-delivery");
        }
    }
}
