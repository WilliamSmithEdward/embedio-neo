using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http3
{
    // QPACK counters and stream IDs need the full 62-bit range. Unlike QUIC's
    // width-tagged integers, these use the HPACK continuation representation.
    internal static class QpackInteger
    {
        internal static long Read(byte[] bytes, ref int offset, int end, int prefixBits)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (prefixBits < 1 || prefixBits > 8) throw new ArgumentOutOfRangeException(nameof(prefixBits));
            if (offset < 0 || end < offset || end > bytes.Length) throw new ArgumentOutOfRangeException(nameof(end));
            var cursor = offset;
            if (cursor == end) throw new EndOfStreamException("Missing QPACK integer.");
            var mask = (1 << prefixBits) - 1;
            long value = bytes[cursor++] & mask;
            if (value < mask) { offset = cursor; return value; }
            for (var shift = 0; shift <= 56; shift += 7)
            {
                if (cursor == end) throw new EndOfStreamException("Truncated QPACK integer.");
                var next = bytes[cursor++];
                var part = next & 127;
                if (part > ((QuicInteger.Maximum - value) >> shift)) throw new InvalidDataException("QPACK integer exceeds 62 bits.");
                value += (long)part << shift;
                if ((next & 128) == 0) { offset = cursor; return value; }
            }
            throw new InvalidDataException("QPACK integer has too many continuation bytes.");
        }

        internal static void Write(Stream output, long value, int prefixBits, byte flags)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (value < 0 || value > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            if (prefixBits < 1 || prefixBits > 8) throw new ArgumentOutOfRangeException(nameof(prefixBits));
            var mask = (1 << prefixBits) - 1;
            if ((flags & mask) != 0) throw new ArgumentException("Flags overlap the integer prefix.", nameof(flags));
            if (value < mask) { output.WriteByte((byte)(flags | value)); return; }
            output.WriteByte((byte)(flags | mask));
            value -= mask;
            while (value >= 128) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
            output.WriteByte((byte)value);
        }
    }
}
