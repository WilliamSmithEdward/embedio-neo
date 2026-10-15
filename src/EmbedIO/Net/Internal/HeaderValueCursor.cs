using System;

namespace EmbedIO.Net.Internal
{
    // A forward-only view over delimiter boundaries; quoted text remains intact.
    internal struct HeaderValueCursor
    {
        private readonly string _text;
        private readonly bool _semicolons;
        private int _offset;
        internal HeaderValueCursor(string text, bool semicolons)
        { _text = text; _semicolons = semicolons; _offset = 0; }
        internal bool Next(out string segment, out char separator)
        {
            segment = string.Empty;
            separator = '\0';
            if (_offset >= _text.Length) return false;
            var begin = _offset;
            var quoted = false;
            var slashes = 0;
            while (_offset < _text.Length)
            {
                var character = _text[_offset++];
                if (character == '"' && (slashes & 1) == 0) quoted = !quoted;
                if (!quoted && (character == ',' || (_semicolons && character == ';')))
                {
                    segment = _text.Substring(begin, _offset - begin - 1);
                    separator = character;
                    return true;
                }
                slashes = character == '\\' ? slashes + 1 : 0;
            }
            segment = _text.Substring(begin);
            return true;
        }
        internal static string Unquote(string value)
        {
            var opening = value.IndexOf("\"", StringComparison.Ordinal);
            if (opening < 0) return value.Trim();
            // Attribute consumers historically use the closing quote's absolute
            // offset as the retained length. Preserve malformed-input behavior,
            // including the range error for a lone quote at the start.
            var retainedLength = value.LastIndexOf('"') - 1;
            return value.Substring(opening + 1, retainedLength).Trim();
        }
    }
}
