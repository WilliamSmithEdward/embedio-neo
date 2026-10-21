using System;
using System.Collections.Generic;

namespace EmbedIO.Quic
{
    // Closed ranges compact around at most Capacity live/implicitly opened
    // streams. Cumulative credit advances without retaining each completed ID.
    internal sealed class StreamCountWindow(int capacity = 16)
    {
        internal const ulong MaximumCount = 1UL << 60;
        private readonly int _capacity = Validate(capacity);
        private readonly List<(ulong Low, ulong High)> _closed = [];
        private ulong _opened;
        private ulong _completed;

        internal int Capacity => _capacity;
        internal ulong Maximum => Math.Min(MaximumCount, _completed + (ulong)_capacity);
        internal ulong Opened => _opened;
        internal ulong Active => _opened - _completed;
        internal int RangeCount => _closed.Count;

        private static int Validate(int value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024);
            return value;
        }

        internal bool Open(ulong ordinal)
        {
            if (ordinal >= Maximum) throw new TransportException(0x04, "Incoming stream count exceeded.");

            _opened = Math.Max(_opened, ordinal + 1);
            return !IsClosed(ordinal);
        }

        internal bool IsClosed(ulong ordinal)
        {
            var index = Find(ordinal);
            return index < _closed.Count && _closed[index].Low <= ordinal;
        }

        internal bool Retire(ulong ordinal)
        {
            if (ordinal >= _opened) throw new InvalidOperationException("Cannot retire an unopened stream.");

            var index = Find(ordinal);

            if (index < _closed.Count && _closed[index].Low <= ordinal) return false;

            var left = index > 0 && _closed[index - 1].High + 1 == ordinal;
            var right = index < _closed.Count && _closed[index].Low == ordinal + 1;

            if (left && right)
            {
                _closed[index - 1] = (_closed[index - 1].Low, _closed[index].High);
                _closed.RemoveAt(index);
            }

            else if (left) _closed[index - 1] = (_closed[index - 1].Low, ordinal);

            else if (right) _closed[index] = (ordinal, _closed[index].High);

            else _closed.Insert(index, (ordinal, ordinal));

            _completed++;
            return true;
        }

        private int Find(ulong ordinal)
        {
            var low = 0;
            var high = _closed.Count;

            while (low < high)
            {
                var middle = low + (high - low) / 2;

                if (_closed[middle].High < ordinal) low = middle + 1;

                else high = middle;
            }

            return low;
        }
    }
}
