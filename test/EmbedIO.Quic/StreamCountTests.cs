namespace EmbedIO.Quic
{
    internal static class StreamCountTests
    {
        internal static void Run()
        {
            var window = new StreamCountWindow();

            for (ulong ordinal = 0; ordinal < 100000; ordinal++)
            {
                QuicFuzzChecks.Require(window.Open(ordinal), "count-opens-next-stream");
                QuicFuzzChecks.Require(window.Retire(ordinal) && !window.Retire(ordinal), "count-retires-once");
                QuicFuzzChecks.Require(
                    !window.Open(ordinal) && window.Active == 0 && window.RangeCount == 1,
                    "count-bounded-sequential-history"
                );
            }

            window = new StreamCountWindow(4);
            QuicFuzzChecks.Require(window.Open(3) && window.Active == 4, "higher-id-opens-lower-streams");
            Reject(() => window.Open(4));
            window.Retire(1);
            QuicFuzzChecks.Require(window.Open(4) && window.Active == 4, "out-of-order-retirement-credit");
            window.Retire(3);
            window.Retire(2);
            window.Retire(0);
            window.Retire(4);
            QuicFuzzChecks.Require(window.Active == 0 && window.RangeCount == 1, "count-ranges-merge");
            Reject(() => window.Open(StreamCountWindow.MaximumCount));
            Mutate(
                Enumerable
                    .Range(0, 128)
                    .Select(i => (byte)i)
                    .ToArray()
            );
        }

        private static void Reject(Action operation)
        {
            try
            {
                operation();
            }

            catch (TransportException error) when (error.ErrorCode == 0x04)
            {
                return;
            }

            throw new QuicFuzzFailure("excess-stream-count-accepted");
        }

        internal static void Mutate(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return;

            var capacity = 1 + bytes[0] % 8;
            var window = new StreamCountWindow(capacity);
            var closed = new HashSet<ulong>();
            ulong opened = 0;

            foreach (var command in bytes[..Math.Min(bytes.Length, 128)])
            {
                var maximum = (ulong)(closed.Count + capacity);
                var ordinal = (ulong)command % (maximum + 2);

                if ((command & 1) == 0)
                {
                    var rejected = false;
                    var accepted = false;

                    try
                    {
                        accepted = window.Open(ordinal);
                    }

                    catch (TransportException error) when (error.ErrorCode == 0x04)
                    {
                        rejected = true;
                    }

                    QuicFuzzChecks.Require(rejected == (ordinal >= maximum), "count-model-admission");

                    if (!rejected)
                    {
                        opened = Math.Max(opened, ordinal + 1);
                        QuicFuzzChecks.Require(accepted == !closed.Contains(ordinal), "count-model-no-revival");
                    }
                }

                else if (opened != 0)
                {
                    ordinal %= opened;
                    QuicFuzzChecks.Require(window.Retire(ordinal) == closed.Add(ordinal), "count-model-retirement");
                }

                QuicFuzzChecks.Require(
                    window.Maximum == (ulong)(closed.Count + capacity)
                        && window.Active == opened - (ulong)closed.Count
                        && window.Active <= (ulong)capacity
                        && window.RangeCount <= capacity + 1,
                    "count-model-resource-bounds"
                );
            }
        }
    }
}
