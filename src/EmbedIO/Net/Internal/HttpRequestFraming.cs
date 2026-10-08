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
