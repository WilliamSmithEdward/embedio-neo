using System;
using System.Text;

namespace EmbedIO.Net.Internal
{
    // RFC 9218 parameters carried in an RFC 9651 Dictionary. Unknown values
    // are validated and skipped without materializing a dictionary or lists.
    internal readonly struct HttpPriority
    {
        internal HttpPriority(int urgency, bool incremental) { Urgency = urgency; Incremental = incremental; }
        public int Urgency { get; }
        public bool Incremental { get; }
        internal static bool TryParse(string field, out HttpPriority priority)
        {
            priority = new HttpPriority(3, false);
            if (field == null || field.Length > 16384) return false;
            var reader = new Reader(field);
            if (!reader.Dictionary(out var urgency, out var incremental)) return false;
            priority = new HttpPriority(urgency, incremental);
            return true;
        }

        private enum Kind { Other, Integer, Boolean }
        private struct Reader
        {
            private static readonly UTF8Encoding StrictUtf8 = new(false, true);
            private readonly string _text;
            private int _position;
            internal Reader(string text) { _text = text; _position = 0; }
            private char Current => _position < _text.Length ? _text[_position] : '\0';
            private bool Take(char c) { if (Current != c) return false; _position++; return true; }
            private void Space(bool tabs = false) { while (Current == ' ' || (tabs && Current == '\t')) _position++; }
            private static bool Digit(char c) => c >= '0' && c <= '9';
            private static bool Lower(char c) => c >= 'a' && c <= 'z';
            private static bool Alpha(char c) => Lower(c) || (c >= 'A' && c <= 'Z');
            private bool Key(out int start, out int length)
            {
                start = _position; length = 0;
                if (!Lower(Current) && Current != '*') return false;
                do { _position++; } while (Lower(Current) || Digit(Current) || Current is '_' or '-' or '.' or '*');
                length = _position - start;
                return true;
            }
            internal bool Dictionary(out int urgency, out bool incremental)
            {
                urgency = 3; incremental = false;
                Space();
                if (_position == _text.Length) return true;
                while (true)
                {
                    if (!Key(out var start, out var length)) return false;
                    var kind = Kind.Boolean; long value = 1;
                    if (Take('='))
                    {
                        if (Take('('))
                        {
                            kind = Kind.Other;
                            while (true)
                            {
                                Space();
                                if (Take(')')) break;
                                if (!Bare(out _, out _) || !Parameters()) return false;
                                if (Current != ' ' && Current != ')') return false;
                            }
                        }
                        else if (!Bare(out kind, out value)) return false;
                    }
                    if (!Parameters()) return false;
                    // Dictionary duplicate keys replace the whole prior member,
                    // including replacement with an ignored type/range.
                    if (length == 1 && _text[start] == 'u') urgency = kind == Kind.Integer && value >= 0 && value <= 7 ? (int)value : 3;
                    if (length == 1 && _text[start] == 'i') incremental = kind == Kind.Boolean && value != 0;
                    Space(true);
                    if (_position == _text.Length) return true;
                    if (!Take(',')) return false;
                    Space(true);
                    if (_position == _text.Length) return false;
                }
            }
            private bool Parameters()
            {
                while (Take(';'))
                {
                    Space();
                    if (!Key(out _, out _)) return false;
                    if (Take('=') && !Bare(out _, out _)) return false;
                }
                return true;
            }
            private bool Bare(out Kind kind, out long value)
            {
                kind = Kind.Other; value = 0;
                if (Current == '-' || Digit(Current)) return Number(out kind, out value);
                if (Take('?'))
                {
                    kind = Kind.Boolean;
                    if (Take('1')) { value = 1; return true; }
                    return Take('0');
                }
                if (Take('@')) return Number(out var dateKind, out _) && dateKind == Kind.Integer;
                if (Take('"')) return Quoted();
                if (Take('%')) return Take('"') && Display();
                if (Take(':')) return Bytes();
                if (!Alpha(Current) && Current != '*') return false;
                do { _position++; } while (Alpha(Current) || Digit(Current) || Current is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~' or ':' or '/');
                return true;
            }
            private bool Number(out Kind kind, out long value)
            {
                kind = Kind.Integer; value = 0;
                var negative = Take('-');
                var start = _position;
                while (Digit(Current))
                {
                    if (_position - start == 15) return false;
                    value = value * 10 + Current - '0'; _position++;
                }
                var digits = _position - start;
                if (digits == 0) return false;
                if (Take('.'))
                {
                    kind = Kind.Other;
                    if (digits > 12) return false;
                    start = _position;
                    while (Digit(Current)) _position++;
                    if (_position - start < 1 || _position - start > 3) return false;
                }
                if (negative) value = -value;
                return true;
            }
            private bool Quoted()
            {
                while (_position < _text.Length)
                {
                    var c = _text[_position++];
                    if (c == '"') return true;
                    if (c < 32 || c > 126) return false;
                    if (c == '\\')
                    {
                        if (!Take('"') && !Take('\\')) return false;
                    }
                }
                return false;
            }
            private bool Bytes()
            {
                var count = 0; var padding = 0;
                while (_position < _text.Length && Current != ':')
                {
                    var c = _text[_position++];
                    if (c == '=') { if (++padding > 2) return false; }
                    else
                    {
                        if (padding != 0 || (!Alpha(c) && !Digit(c) && c != '+' && c != '/')) return false;
                        count++;
                    }
                }
                if (!Take(':') || count % 4 == 1) return false;
                // Missing base64 padding is permitted; present padding must fit.
                return padding == 0 || (count + padding) % 4 == 0;
            }
            private static int Hex(char c) => Digit(c) ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
            private bool Display()
            {
                var end = _position;
                while (end < _text.Length && _text[end] != '"') end++;
                if (end == _text.Length) return false;
                var bytes = new byte[end - _position];
                var count = 0;
                while (_position < _text.Length)
                {
                    var c = _text[_position++];
                    if (c == '"')
                    {
                        try { _ = StrictUtf8.GetCharCount(bytes, 0, count); return true; }
                        catch (DecoderFallbackException) { return false; }
                    }
                    if (c < 32 || c > 126) return false;
                    if (c == '%')
                    {
                        if (_text.Length - _position < 2) return false;
                        var high = Hex(_text[_position++]); var low = Hex(_text[_position++]);
                        if (high < 0 || low < 0) return false;
                        bytes[count++] = (byte)((high << 4) | low);
                    }
                    else bytes[count++] = (byte)c;
                }
                return false;
            }
        }
    }
}
