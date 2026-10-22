using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Quic
{
    internal static class AckRanges
    {
        internal static List<(ulong Low, ulong High)> Read(
            ref WireReader reader,
            ulong sentCount,
            int exponent,
            out double delay
        )
        {
            try
            {
                return ReadCore(ref reader, sentCount, exponent, out delay);
            }

            catch (InvalidDataException error)
            {
                throw new TransportException(0x07, error.Message);
            }
        }

        private static List<(ulong Low, ulong High)> ReadCore(
            ref WireReader reader,
            ulong sentCount,
            int exponent,
            out double delay
        )
        {
            var largest = reader.Integer();

            if (largest >= sentCount) throw new TransportException(0x0a, "ACK for unsent packet.");

            delay = reader.Integer() * Math.Pow(2, exponent) / 1000;
            // Each extra range requires at least a gap and a range varint. A
            // received packet cannot contain more than one datagram's bytes.
            var ranges = reader.Length(Math.Min(reader.Remaining / 2, ushort.MaxValue / 2));
            List<(ulong Low, ulong High)> acknowledged = new(ranges + 1);
            var range = reader.Integer();

            if (range > largest) throw new InvalidDataException("Invalid ACK range.");

            // Even one-packet ranges with the smallest legal gap consume two
            // packet numbers per additional range.
            if ((ulong)ranges > largest / 2) throw new InvalidDataException("Invalid ACK range count.");

            var smallest = largest - range;
            acknowledged.Add((smallest, largest));

            for (var i = 0; i < ranges; i++)
            {
                var gap = reader.Integer();
                range = reader.Integer();

                if (gap + 2 > smallest || range > smallest - gap - 2)
                    throw new InvalidDataException("Invalid ACK gap.");

                var high = smallest - gap - 2;
                smallest = high - range;
                acknowledged.Add((smallest, high));
            }

            return acknowledged;
        }
    }
}
