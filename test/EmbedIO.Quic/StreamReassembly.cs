namespace EmbedIO.Quic
{
    // A fixed receive window. Retired bytes are forgotten; retransmissions still
    // buffered in the window must agree, and final-size history is never forgotten.
    internal sealed class StreamReassembly(int capacity)
    {
        internal const ulong MaximumOffset = (1UL << 62) - 1;
        private readonly byte[] _bytes = new byte[capacity];
        private readonly bool[] _present = new bool[capacity];
        private ulong _extent;

        internal ulong Delivered { get; private set; }
        internal ulong? FinalSize { get; private set; }
        internal bool Complete => FinalSize == Delivered;

        internal void Add(ulong offset, ReadOnlySpan<byte> bytes, bool final = false)
        {
            if (offset > MaximumOffset || (ulong)bytes.Length > MaximumOffset - offset)
                throw new InvalidDataException("Invalid stream offset.");

            var end = offset + (ulong)bytes.Length;

            if (FinalSize.HasValue && (end > FinalSize.Value || final && end != FinalSize.Value))
                throw new InvalidDataException("Conflicting final size.");

            if (final && end < _extent) throw new InvalidDataException("Final size precedes received bytes.");

            if (end > Delivered && end - Delivered > (ulong)capacity)
                throw new InvalidDataException("Stream receive window exceeded.");

            if (final) FinalSize = end;

            _extent = Math.Max(_extent, end);

            for (var i = 0; i < bytes.Length; i++)
            {
                var position = offset + (ulong)i;

                if (position < Delivered) continue;

                var index = (int)(position % (ulong)capacity);

                if (_present[index] && _bytes[index] != bytes[i])
                    throw new InvalidDataException("Conflicting overlapping data.");

                _bytes[index] = bytes[i];
                _present[index] = true;
            }
        }

        internal byte[] Consume(ulong maximum = MaximumOffset)
        {
            var count = 0;

            while (
                count < capacity
                && Delivered + (ulong)count < maximum
                && _present[(int)((Delivered + (ulong)count) % (ulong)capacity)]
            ) count++;

            var result = new byte[count];

            for (var i = 0; i < count; i++)
            {
                var index = (int)((Delivered + (ulong)i) % (ulong)capacity);
                result[i] = _bytes[index];
                _bytes[index] = 0;
                _present[index] = false;
            }

            Delivered += (ulong)count;
            return result;
        }
    }
}
