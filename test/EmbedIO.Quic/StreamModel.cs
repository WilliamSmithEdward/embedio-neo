namespace EmbedIO.Quic
{
    // Dictionary oracle uses absolute positions, never the implementation's ring indexing.
    internal static class StreamModel
    {
        private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
            actual.SequenceEqual(expected);

        internal static void SelfTest()
        {
            QuicFuzzChecks.Require(
                !Matches([1, 2, 3], [1, 9, 3]) && !Matches([1, 2], [1, 2, 3]) && !Matches([1, 2, 3, 3], [1, 2, 3]),
                "stream-negative-oracle"
            );

            for (byte profile = 0; profile < 4; profile++) PageBoundaries([0, profile]);
        }

        private static void PageBoundaries(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || (data[0] & 31) != 0) return;

            var profile = data[1] % 4;
            var capacity = profile switch
            {
                0 => 1023,
                1 => 1024,
                2 => 1025,
                _ => 65536
            };
            using var buffer = new StreamReassembly(capacity);
            var bytes = new byte[capacity];

            for (var index = 0; index < bytes.Length; index++) bytes[index] = data[index % data.Length];

            for (var cycle = 0; cycle < 3; cycle++)
            {
                var offset = buffer.Delivered;
                var split = 1 + data[(cycle + 1) % data.Length] % (capacity - 1);
                buffer.Add(offset + (ulong)split, bytes.AsSpan(split));
                QuicFuzzChecks.Require(buffer.Consume().Length == 0, "stream-page-gap");
                buffer.Add(offset, bytes.AsSpan(0, split));
                QuicFuzzChecks.Require(
                    Matches(buffer.Consume(offset + (ulong)split), bytes.AsSpan(0, split)),
                    "stream-page-prefix"
                );
                QuicFuzzChecks.Require(
                    Matches(buffer.Consume(), bytes.AsSpan(split)) && buffer.RetainedCapacity == 0,
                    "stream-page-remainder"
                );
                buffer.Add(offset, bytes);
                QuicFuzzChecks.Require(
                    buffer.Consume().Length == 0 && buffer.RetainedCapacity == 0,
                    "stream-page-retired"
                );
            }

            buffer.Add(buffer.Delivered, [], true);
            QuicFuzzChecks.Require(buffer.Complete, "stream-page-final");
        }

        internal static void Check(ReadOnlySpan<byte> data)
        {
            PageBoundaries(data);
            StreamDiscardTests.Mutate(data);
            var buffer = new StreamReassembly(32);
            var pending = new SortedDictionary<ulong, byte>();
            ulong delivered = 0, extent = 0, retentionLimit = StreamReassembly.MaximumOffset;
            ulong? final = null;

            try
            {
                for (var cursor = 0; cursor + 3 < data.Length && cursor < 512; cursor += 4)
                {
                    var delta = data[cursor] % 48;
                    var offset = (data[cursor + 1] & 1) != 0 && delivered > 0
                        ? delivered - Math.Min(delivered, (ulong)delta)
                        : delivered + (ulong)delta;

                    if ((data[cursor + 3] & 128) != 0)
                    {
                        retentionLimit = Math.Min(retentionLimit, offset);

                        foreach (var position in pending.Keys
                            .Where(position => position >= retentionLimit)
                            .ToArray())
                            pending.Remove(position);

                        buffer.DiscardFrom(offset);
                    }

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
                        buffer.Dispose();
                        buffer = new StreamReassembly(32);
                        pending.Clear();
                        delivered = extent = 0;
                        retentionLimit = StreamReassembly.MaximumOffset;
                        final = null;
                        continue;
                    }

                    QuicFuzzChecks.Require(!invalid, "stream-accepted-invalid");

                    if (fin) final = end;

                    extent = Math.Max(extent, end);

                    for (var index = 0; index < bytes.Length; index++)
                        if (offset + (ulong)index >= delivered && offset + (ulong)index < retentionLimit)
                            pending[offset + (ulong)index] = bytes[index];

                    var maximum = delivered + (ulong)(data[cursor + 3] % 33);
                    var expected = new List<byte>();

                    while (
                        delivered < maximum && delivered < retentionLimit && pending.Remove(delivered, out var value)
                    )
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
            }

            finally
            {
                buffer.Dispose();
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
