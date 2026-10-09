using System;
using System.IO;
using System.Text;

namespace EmbedIO.Net.Internal
{
    internal enum Http1HeadReadResult { NeedMoreData, RequestLine, Header, Complete }

    // Owns only incremental HTTP/1 head framing. Returned consumption excludes
    // body/pipeline bytes; request semantics and transport lifetime live elsewhere.
    internal struct Http1HeadReader
    {
        private const int MaximumBytes = 32768;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        private StringBuilder? _partial;
        private int _bytes;
        private bool _pendingCr;
        private bool _hasRequestLine;
        private bool _complete;
        private bool _failed;

        internal void Reset() => this = default;

        internal Http1HeadReadResult Read(byte[] input, int offset, int count, out int consumed, out string? line)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (offset < 0 || count < 0 || offset > input.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
            consumed = 0;
            line = null;
            if (_failed) throw new InvalidDataException("Request head reader requires reset after failure.");
            if (_complete) return Http1HeadReadResult.Complete;
            while (consumed < count)
            {
                line = ReadLine(input, offset + consumed, count - consumed, out var used);
                consumed += used;
                if (line == null) return Http1HeadReadResult.NeedMoreData;
                if (line.Length == 0)
                {
                    if (!_hasRequestLine) continue;
                    _complete = true;
                    return Http1HeadReadResult.Complete;
                }
                if (_hasRequestLine) return Http1HeadReadResult.Header;
                _hasRequestLine = true;
                return Http1HeadReadResult.RequestLine;
            }
            line = null;
            return Http1HeadReadResult.NeedMoreData;
        }

        private string? ReadLine(byte[] input, int offset, int count, out int used)
        {
            used = 0;
            if (_partial == null && !_pendingCr)
            {
                var cr = Array.IndexOf(input, (byte)13, offset, count);
                var prefixLength = cr < 0 ? count : cr - offset;
                if (Array.IndexOf(input, (byte)10, offset, prefixLength) >= 0)
                    throw Fail("Bare LF in request head.");
                if (cr >= 0 && cr + 1 < offset + count)
                {
                    if (input[cr + 1] != 10) throw Fail("Invalid request head line ending.");
                    used = prefixLength + 2;
                    Charge(used);
                    return Latin1.GetString(input, offset, prefixLength);
                }
            }
            for (var i = offset; i < offset + count; ++i)
            {
                Charge(1);
                ++used;
                var value = input[i];
                if (_pendingCr)
                {
                    if (value != 10) throw Fail("Invalid request head line ending.");
                    _pendingCr = false;
                    var line = _partial?.ToString() ?? string.Empty;
                    _partial = null;
                    return line;
                }
                if (value == 10) throw Fail("Bare LF in request head.");
                if (value == 13) _pendingCr = true;
                else (_partial ??= new StringBuilder(128)).Append((char)value);
            }
            return null;
        }

        private void Charge(int count)
        {
            if (count > MaximumBytes - _bytes) throw Fail("Request headers exceed 32768 bytes.");
            _bytes += count;
        }

        private InvalidDataException Fail(string message)
        {
            _failed = true;
            _partial = null;
            return new InvalidDataException(message);
        }
    }
}
