using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Quic
{
    internal static class AckRangeTests
    {
        internal static void Run()
        {
            foreach (var additional in new[] { 0, 1, 127, 128, 129, 255, 511, 1024, 32700 })
            {
                var bytes = Alternating(additional);
                var reader = new WireReader(bytes);
                var ranges = AckRanges.Read(ref reader, (ulong)(2 * additional + 1), 3, out var delay);
                Check(ranges.Count == additional + 1 && reader.Remaining == 0 && delay == 0, "complete ACK frame");

                for (var index = 0; index < ranges.Count; index++)
                {
                    var expected = (ulong)(2 * (additional - index));
                    Check(ranges[index] == (expected, expected), "descending ACK ranges");
                }
            }

            var truncated = Alternating(129)[..^1];
            Reject(truncated, 259, 7, "truncated ACK range");
            Reject(Wire.Frame(2, 0, 0, 0), 2, 10, "ACK for unsent packet");
            Reject(Wire.Frame(2, 0, 1, 0), 3, 7, "missing ACK range");
            Reject(Wire.Frame(2, 0, 0, 3), 3, 7, "ACK range underflow");
            Reject(Wire.Frame(2, 0, 1, 0, 2, 0), 3, 7, "ACK gap underflow");
            Mutate([255, 3, 0, 1]);
            Mutate([254, 1, 1, 2]);
            RecoveryLookup();
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 3) return;

            var additional = (data[0] & 1) == 0 ? data[0] / 2 : 129 + data[0];
            var firstRange = (ulong)(data[1] & 3);
            var largest = firstRange;

            for (var index = 0; index < additional; index++)
                largest += (ulong)(data[(index + 2) % data.Length] & 3) + 2;

            using var frame = new MemoryStream();
            Wire.Integer(frame, largest);
            Wire.Integer(frame, 0);
            Wire.Integer(frame, (ulong)additional);
            Wire.Integer(frame, firstRange);

            for (var index = 0; index < additional; index++)
            {
                Wire.Integer(frame, (ulong)(data[(index + 2) % data.Length] & 3));
                Wire.Integer(frame, 0);
            }

            var reader = new WireReader(frame.ToArray());
            var ranges = AckRanges.Read(ref reader, largest + 1, 3, out var delay);
            QuicFuzzChecks.Require(
                ranges.Count == additional + 1 && reader.Remaining == 0 && delay == 0,
                "ack-mutation-count"
            );
            var high = largest;
            var low = high - firstRange;
            QuicFuzzChecks.Require(ranges[0] == (low, high), "ack-mutation-first");

            for (var index = 0; index < additional; index++)
            {
                high = low - (ulong)(data[(index + 2) % data.Length] & 3) - 2;
                low = high;
                QuicFuzzChecks.Require(ranges[index + 1] == (low, high), "ack-mutation-range");
            }
        }

        private static void RecoveryLookup()
        {
            const int additional = 32700;
            var values = new (ulong Low, ulong High)[additional + 1];

            for (var index = 0; index < values.Length; index++)
            {
                var number = (ulong)(2 * (additional - index));
                values[index] = (number, number);
            }

            var ranges = new CountedRanges(values);
            var recovery = new Recovery(static (_, _) => { });
            var deliveries = new Delivery[Recovery.MaximumActivePackets];

            for (var index = 0; index < deliveries.Length; index++)
            {
                var delivery = new Delivery(2, [1]);
                deliveries[index] = delivery;
                recovery.SentPacket(2, (ulong)(61305 + index), 1, 0, delivery, false);
            }

            recovery.Acknowledge(2, ranges, 0, 1);
            Check(ranges.Reads < 350000, "large ACK lookup work bound");

            for (var index = 0; index < deliveries.Length; index++)
                Check(deliveries[index].Acknowledged == ((61305 + index) % 2 == 0), "large ACK ownership");

            recovery = new Recovery(static (_, _) => { });
            deliveries = new Delivery[5];

            for (var index = 0; index < deliveries.Length; index++)
            {
                deliveries[index] = new Delivery(2, [1]);
                recovery.SentPacket(2, (ulong)index, 1, 0, deliveries[index], false);
            }

            recovery.Acknowledge(
                2,
                [
                    (0, 0),
                    (2, 2),
                    (1, 1),
                    (3, 3),
                    (4, 4)
                ],
                0,
                1
            );
            Check(Array.TrueForAll(deliveries, static delivery => delivery.Acknowledged), "unordered model ranges");
        }

        private sealed class CountedRanges((ulong Low, ulong High)[] values) : IReadOnlyList<(ulong Low, ulong High)>
        {
            internal int Reads { get; private set; }
            public int Count => values.Length;

            public (ulong Low, ulong High) this[int index]
            {
                get
                {
                    Reads++;
                    return values[index];
                }
            }

            public IEnumerator<(ulong Low, ulong High)> GetEnumerator() =>
                ((IEnumerable<(ulong Low, ulong High)>)values).GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static byte[] Alternating(int additional)
        {
            using var frame = new MemoryStream();
            Wire.Integer(frame, (ulong)(2 * additional));
            Wire.Integer(frame, 0);
            Wire.Integer(frame, (ulong)additional);
            Wire.Integer(frame, 0);

            for (var index = 0; index < additional; index++)
            {
                Wire.Integer(frame, 0);
                Wire.Integer(frame, 0);
            }

            return frame.ToArray();
        }

        private static void Reject(byte[] bytes, ulong sentCount, ulong errorCode, string name)
        {
            var reader = new WireReader(bytes);

            try
            {
                AckRanges.Read(ref reader, sentCount, 3, out _);
            }

            catch (TransportException error) when (error.ErrorCode == errorCode)
            {
                return;
            }

            throw new InvalidOperationException("ACK regression: " + name);
        }

        private static void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("ACK regression: " + name);
        }
    }
}
