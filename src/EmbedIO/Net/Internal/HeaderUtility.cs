using System;

namespace EmbedIO.Net.Internal
{
    internal static class HeaderUtility
    {
        public static string? GetCharset(string? contentType)
        {
            if (contentType == null) return null;
            var start = 0;
            while (true)
            {
                var separator = contentType.IndexOf(';', start);
                var part = contentType.Substring(start, (separator < 0 ? contentType.Length : separator) - start).Trim();
                if (part.StartsWith("charset", StringComparison.OrdinalIgnoreCase))
                    return GetAttributeValue(part);
                if (separator < 0) return null;
                start = separator + 1;
            }
        }

        public static string? GetAttributeValue(string nameAndValue)
        {
            var idx = nameAndValue.IndexOf("=", System.StringComparison.Ordinal);

            return idx < 0 || idx == nameAndValue.Length - 1 ? null : nameAndValue.Substring(idx + 1).Trim().Unquote();
        }
    }
}
