using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Quic
{
    // RFC 9000 section 13.2.3: discarded ACK history raises a permanent acceptance floor.
    internal sealed class PacketNumberWindow
    {
        internal const ulong Maximum = (1UL << 62) - 1;
        internal const ulong Width = 4096;
        internal const int MaximumRanges = 128;
        private readonly List<(ulong Low, ulong High)> _ranges = [];

        internal ulong Minimum { get; private set; }

        internal bool Add(ulong number)
        {
            if (number > Maximum) throw new InvalidDataException("Invalid packet number.");

            if (number < Minimum) return false;

            var index = _ranges.Count;

            while (index > 0 && number < _ranges[index - 1].Low) index--;

            if (index > 0 && number <= _ranges[index - 1].High) return false;

            var left = index > 0 && _ranges[index - 1].High + 1 == number;
            var right = index < _ranges.Count && number + 1 == _ranges[index].Low;

            if (left && right)
            {
                _ranges[index - 1] = (_ranges[index - 1].Low, _ranges[index].High);
                _ranges.RemoveAt(index);
            }

            else if (left) _ranges[index - 1] = (_ranges[index - 1].Low, number);

            else if (right) _ranges[index] = (number, _ranges[index].High);

            else _ranges.Insert(index, (number, number));

            var largest = _ranges[^1].High;

            if (largest >= Width) Minimum = Math.Max(Minimum, largest - Width + 1);

            while (_ranges[0].High < Minimum) _ranges.RemoveAt(0);

            if (_ranges[0].Low < Minimum) _ranges[0] = (Minimum, _ranges[0].High);

            if (_ranges.Count > MaximumRanges)
            {
                Minimum = Math.Max(Minimum, _ranges[0].High + 1);
                _ranges.RemoveAt(0);
            }

            return true;
        }

        internal IReadOnlyList<(ulong Low, ulong High)> Descending()
        {
            var result = new (ulong Low, ulong High)[_ranges.Count];

            for (var index = 0; index < result.Length; index++) result[index] = _ranges[^(index + 1)];

            return result;
        }
    }
}
