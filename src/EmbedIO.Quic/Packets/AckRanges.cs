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
            var largest = reader.Integer();

            if (largest >= sentCount) throw new InvalidDataException("ACK for unsent packet.");

            delay = reader.Integer() * Math.Pow(2, exponent) / 1000;
            var ranges = reader.Length(128);
            var acknowledged = new List<(ulong Low, ulong High)>();
            var range = reader.Integer();

            if (range > largest) throw new InvalidDataException("Invalid ACK range.");

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
