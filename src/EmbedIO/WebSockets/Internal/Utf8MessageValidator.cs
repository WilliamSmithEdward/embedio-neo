using System;

namespace EmbedIO.WebSockets.Internal
{
    // Validate RFC 3629 bytes without materializing UTF-16 or renting a character
    // buffer. Carry only the allowed continuation range across frame boundaries.
    internal struct Utf8MessageValidator
    {
#if NETSTANDARD2_0
        private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
#endif
        private int _remaining;
        private byte _minimum;
        private byte _maximum;

        internal bool Validate(byte[] bytes, bool final)
        {
            var offset = 0;
            while (_remaining != 0 && offset < bytes.Length)
                if (!Continue(bytes[offset++])) return false;
            if (_remaining != 0) return !final;
            var end = bytes.Length - offset >= 16 ? CompletePrefixEnd(bytes, offset) : bytes.Length;
#if NET10_0_OR_GREATER
            if (System.Text.Unicode.Utf8.IsValid(bytes.AsSpan(offset, end - offset))) offset = end;
#else
            // Keep the runtime's optimized complete-span path for substantial
            // legacy chunks. Tiny fragments use the byte state without decoder
            // exceptions or character buffers on every incomplete scalar.
            if (end - offset >= 16)
            {
                try { _ = StrictUtf8.GetCharCount(bytes, offset, end - offset); offset = end; }
                catch (System.Text.DecoderFallbackException)
                {
                    // The byte state distinguishes an incomplete trailing scalar
                    // from an illegal sequence, including before the final frame.
                }
            }
#endif
            for (; offset < bytes.Length; offset++)
            {
                var value = bytes[offset];
                if (_remaining != 0)
                {
                    if (!Continue(value)) return false;
                    continue;
                }
                if (value <= 0x7F) continue;
                _minimum = 0x80;
                _maximum = 0xBF;
                if (value >= 0xC2 && value <= 0xDF) _remaining = 1;
                else if (value >= 0xE0 && value <= 0xEF)
                {
                    _remaining = 2;
                    if (value == 0xE0) _minimum = 0xA0;
                    else if (value == 0xED) _maximum = 0x9F;
                }
                else if (value >= 0xF0 && value <= 0xF4)
                {
                    _remaining = 3;
                    if (value == 0xF0) _minimum = 0x90;
                    else if (value == 0xF4) _maximum = 0x8F;
                }
                else return false;
            }
            return !final || _remaining == 0;
        }

        // Leave only a potentially incomplete trailing scalar for the byte path;
        // large fragments can still use the runtime validator up to that boundary.
        private static int CompletePrefixEnd(byte[] bytes, int start)
        {
            var end = bytes.Length;
            var tail = end;
            while (tail > start && end - tail < 3 && bytes[tail - 1] >= 0x80 && bytes[tail - 1] <= 0xBF) tail--;
            if (tail == start) return end;
            var lead = bytes[tail - 1];
            var width = lead >= 0xC2 && lead <= 0xDF ? 2
                : lead >= 0xE0 && lead <= 0xEF ? 3
                : lead >= 0xF0 && lead <= 0xF4 ? 4 : 0;
            return width > end - tail + 1 ? tail - 1 : end;
        }

        private bool Continue(byte value)
        {
            if (value < _minimum || value > _maximum) return false;
            _remaining--;
            _minimum = 0x80;
            _maximum = 0xBF;
            return true;
        }
    }
}
