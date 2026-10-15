using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http2
{
    // RFC 7541 section 5.1. The same prefix integer representation is used by
    // QPACK. Restrict values to Int32 and at most five continuation octets;
    // callers must additionally enforce their field-specific resource limits.
    internal static class PrefixInteger
    {
        internal static int Read(byte[] source, ref int offset, int end, int prefixBits)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (prefixBits < 1 || prefixBits > 8) throw new ArgumentOutOfRangeException(nameof(prefixBits));
            if (offset < 0 || end < offset || end > source.Length) throw new ArgumentOutOfRangeException(nameof(end));
            if (offset == end) throw new EndOfStreamException("Missing prefix integer.");
            var mask = (1 << prefixBits) - 1;
            var result = source[offset++] & mask;
            if (result < mask) return result;
            for (var shift = 0; shift <= 28; shift += 7)
            {
                if (offset == end) throw new EndOfStreamException("Incomplete prefix integer.");
                var next = source[offset++];
                var expanded = result + ((long)(next & 127) << shift);
                if (expanded > int.MaxValue) throw new InvalidDataException("Prefix integer exceeds implementation limit.");
                result = (int)expanded;
                if ((next & 128) == 0) return result;
            }
            throw new InvalidDataException("Prefix integer has too many continuation octets.");
        }

        internal static void Write(Stream destination, int value, int prefixBits, byte flags)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (prefixBits < 1 || prefixBits > 8) throw new ArgumentOutOfRangeException(nameof(prefixBits));
            var mask = (1 << prefixBits) - 1;
            if ((flags & mask) != 0) throw new ArgumentException("Flags overlap the integer prefix.", nameof(flags));
            if (value < mask)
            {
                destination.WriteByte((byte)(flags | value));
                return;
            }
            destination.WriteByte((byte)(flags | mask));
            value -= mask;
            while (value >= 128)
            {
                destination.WriteByte((byte)((value & 127) | 128));
                value >>= 7;
            }
            destination.WriteByte((byte)value);
        }
    }
}
