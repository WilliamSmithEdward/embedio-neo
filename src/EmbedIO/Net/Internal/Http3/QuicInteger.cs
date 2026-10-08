using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http3
{
    // RFC 9000 section 16; HTTP/3 permits all four widths, including non-minimal encodings.
    internal static class QuicInteger
    {
        internal const long Maximum = (1L << 62) - 1;

        internal static long Read(byte[] bytes, ref int offset, int end)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || end < offset || end > bytes.Length) throw new ArgumentOutOfRangeException(nameof(end));
            if (offset == end) throw new EndOfStreamException("Missing QUIC integer.");
            var length = 1 << (bytes[offset] >> 6);
            if (length > end - offset) throw new EndOfStreamException("Truncated QUIC integer.");
            long value = bytes[offset++] & 63;
            for (var i = 1; i < length; i++) value = (value << 8) | bytes[offset++];
            return value;
        }

        internal static int Write(byte[] bytes, int offset, long value)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (value < 0 || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            var length = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
            if (offset < 0 || offset > bytes.Length - length) throw new ArgumentOutOfRangeException(nameof(offset));
            var remaining = value;
            for (var i = length - 1; i >= 0; i--) { bytes[offset + i] = (byte)remaining; remaining >>= 8; }
            bytes[offset] |= (byte)(length == 1 ? 0 : length == 2 ? 64 : length == 4 ? 128 : 192);
            return length;
        }
    }
}
