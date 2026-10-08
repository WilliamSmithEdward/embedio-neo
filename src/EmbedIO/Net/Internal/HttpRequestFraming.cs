using System;

namespace EmbedIO.Net.Internal
{
    internal static class HttpRequestFraming
    {
        internal static bool IsTokenCharacter(char c)
            => c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
                || c == '!' || c == '#' || c == '$' || c == '%' || c == '&' || c == '\''
                || c == '*' || c == '+' || c == '-' || c == '.' || c == '^' || c == '_'
                || c == '`' || c == '|' || c == '~';

        internal static bool IsValidPathAndQuery(string value, int start = 0)
        {
            for (var i = start; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '%')
                {
                    if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
                    i += 2;
                }
                else if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9')
                    && EmbedIO.Internal.StringOperations.IndexOfOrdinal("-._~!$&'()*+,;=:@/?", c) < 0) return false;
            }
            return true;
        }

        internal static bool IsValidHost(string value)
        {
            foreach (var c in value)
                if (c <= 32 || c >= 127 || c == '/' || c == '\\' || c == '?' || c == '#' || c == '@') return false;

            var colon = value.LastIndexOf(':');
            var bracket = value.LastIndexOf(']');
            if (bracket >= 0 && (value[0] != '[' || (bracket + 1 < value.Length && value[bracket + 1] != ':'))) return false;
            if (colon > bracket)
            {
                if (bracket < 0 && value.IndexOf(":", StringComparison.Ordinal) != colon) return false;
                // RFC 3986 permits an empty port; a present port contains only digits.
                for (var i = colon + 1; i < value.Length; i++)
                    if (value[i] < '0' || value[i] > '9') return false;
            }
            return true;
        }

        internal static bool TryContentLength(string value, out long length)
        {
            length = 0;
            if (value.Length == 0) return false;
            foreach (var c in value)
            {
                if (c < '0' || c > '9' || length > (long.MaxValue - (c - '0')) / 10) return false;
                length = length * 10 + c - '0';
            }
            return true;
        }
    }
}
