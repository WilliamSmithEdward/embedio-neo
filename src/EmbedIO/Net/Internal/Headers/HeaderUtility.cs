using System;
using System.Text;

namespace EmbedIO.Net.Internal
{
    internal static class HeaderUtility
    {
        public static Encoding GetEncoding(string? contentType)
        {
            var charset = GetCharset(contentType);

            if (string.IsNullOrEmpty(charset)) return WebServer.DefaultEncoding;

            try
            {
                return Encoding.GetEncoding(charset);
            }

            catch (ArgumentException)
            {
                return WebServer.DefaultEncoding;
            }
        }

        public static string? GetCharset(string? contentType)
        {
            if (contentType == null) return null;

            var start = 0;

            while (true)
            {
                var separator = contentType.IndexOf(';', start);
                var partStart = start;
                var partEnd = separator < 0 ? contentType.Length : separator;

                // Match String.Trim's Unicode whitespace rules without creating discarded segments.
                while (partStart < partEnd && char.IsWhiteSpace(contentType[partStart])) partStart++;

                while (partEnd > partStart && char.IsWhiteSpace(contentType[partEnd - 1])) partEnd--;

                if (
                    partEnd - partStart >= 7
                    && string.Compare(contentType, partStart, "charset", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
                )
                    return GetAttributeValue(contentType.Substring(partStart, partEnd - partStart));

                if (separator < 0) return null;

                start = separator + 1;
            }
        }

        public static string? GetAttributeValue(string nameAndValue)
        {
            var idx = nameAndValue.IndexOf("=", System.StringComparison.Ordinal);

            return idx < 0 || idx == nameAndValue.Length - 1
                ? null
                : HeaderValueCursor.Unquote(nameAndValue.Substring(idx + 1));
        }
    }
}
