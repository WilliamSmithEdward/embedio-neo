namespace EmbedIO.Quic
{
    internal static class PacketNumberWindowTests
    {
        internal static void Run()
        {
            var window = new PacketNumberWindow();

            for (ulong number = 0; number < 100000; number++)
            {
                QuicFuzzChecks.Require(window.Add(number) && !window.Add(number), "packet-history-once-only");
                var ranges = window.Descending();
                QuicFuzzChecks.Require(
                    ranges.Count == 1
                        && ranges[0].High == number
                        && ranges[0].High - ranges[0].Low < PacketNumberWindow.Width,
                    "packet-history-window"
                );
            }

            QuicFuzzChecks.Require(
                !window.Add(0) && window.Minimum == 100000 - PacketNumberWindow.Width,
                "packet-history-retired-floor"
            );
            window = new PacketNumberWindow();

            for (ulong number = 0; number < 600; number += 2) window.Add(number);

            QuicFuzzChecks.Require(
                window.Descending().Count == PacketNumberWindow.MaximumRanges && !window.Add(0),
                "packet-history-range-budget"
            );
            window = new PacketNumberWindow();
            window.Add(10);
            window.Add(12);
            window.Add(11);
            window.Add(9);
            window.Add(13);
            var merged = window.Descending();
            QuicFuzzChecks.Require(merged.Count == 1 && merged[0] == (9UL, 13UL), "packet-history-merge");
            window.Add(PacketNumberWindow.Maximum);
            QuicFuzzChecks.Require(
                window.Descending().Count == 1 && !window.Add(13) && !window.Add(PacketNumberWindow.Maximum),
                "packet-history-large-jump"
            );

            try
            {
                window.Add(PacketNumberWindow.Maximum + 1);
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new QuicFuzzFailure("packet-number-overflow");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, 512)];
            var window = new PacketNumberWindow();
            var model = new HashSet<ulong>();
            ulong floor = 0;
            ulong largest = 0;

            for (var index = 0; index < data.Length; index += 2)
            {
                var value = data[index];
                var delta = index + 1 < data.Length ? data[index + 1] : 0;
                var number = (value & 3) switch
                {
                    0 => largest + (ulong)delta + 1,
                    1 => largest > (ulong)delta ? largest - (ulong)delta : 0,
                    2 => largest + 4096 + (ulong)delta,
                    _ => (ulong)delta
                };
                var fresh = number >= floor && model.Add(number);
                QuicFuzzChecks.Require(window.Add(number) == fresh, "packet-history-model-deduplication");

                if (fresh)
                {
                    largest = Math.Max(largest, number);
                    floor = Math.Max(
                        floor,
                        largest >= PacketNumberWindow.Width ? largest - PacketNumberWindow.Width + 1 : 0
                    );
                    model.RemoveWhere(packet => packet < floor);
                    var groups = Groups(model);

                    if (groups.Count > PacketNumberWindow.MaximumRanges)
                    {
                        floor = groups[PacketNumberWindow.MaximumRanges].High + 1;
                        model.RemoveWhere(packet => packet < floor);
                    }
                }

                QuicFuzzChecks.Require(
                    window.Minimum == floor
                        && window
                            .Descending()
                            .SequenceEqual(Groups(model)),
                    "packet-history-model-ranges"
                );
            }
        }

        private static List<(ulong Low, ulong High)> Groups(HashSet<ulong> values)
        {
            var ranges = new List<(ulong Low, ulong High)>();

            foreach (var number in values.OrderDescending())
            {
                if (ranges.Count != 0 && number + 1 == ranges[^1].Low) ranges[^1] = (number, ranges[^1].High);

                else ranges.Add((number, number));
            }

            return ranges;
        }
    }
}
