using System;
using System.Globalization;
using System.Net;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal static class SetCookieFields
    {
        internal static CookieList Read(string value)
        {
            var result = new CookieList();
            var cursor = new HeaderValueCursor(value, true);
            Cookie? current = null;
            while (cursor.Next(out var piece, out var separator))
            {
                piece = piece.Trim();
                if (piece.Length == 0) continue;
                var equals = StringOperations.IndexOfOrdinal(piece, '=');
                var name = equals < 0 ? piece : piece.Substring(0, equals).TrimEnd(' ');
                var content = equals < 0 || equals == piece.Length - 1 ? null : piece.Substring(equals + 1).Trim();
                if (current != null && name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    current.Version = Number(content);
                else if (current != null && name.Equals("expires", StringComparison.OrdinalIgnoreCase))
                {
                    var date = content ?? string.Empty;
                    if (separator == ',' && cursor.Next(out var continuation, out _)) date += ", " + continuation.Trim();
                    if (!HttpDate.TryParse(date, out var expires)) expires = DateTimeOffset.Now;
                    if (current.Expires == DateTime.MinValue) current.Expires = expires.LocalDateTime;
                }
                else if (current != null && name.Equals("max-age", StringComparison.OrdinalIgnoreCase)) current.Expires = DateTime.Now.AddSeconds(Number(content));
                else if (current != null && name.Equals("path", StringComparison.OrdinalIgnoreCase)) current.Path = content;
                else if (current != null && name.Equals("domain", StringComparison.OrdinalIgnoreCase)) current.Domain = content;
                else if (current != null && name.Equals("port", StringComparison.OrdinalIgnoreCase)) current.Port = equals < 0 ? "\"\"" : content;
                else if (current != null && name.Equals("commenturl", StringComparison.OrdinalIgnoreCase)) current.CommentUri = UriUtility.StringToUri(content == null ? null : HeaderValueCursor.Unquote(content));
                else if (current != null && name.Equals("comment", StringComparison.OrdinalIgnoreCase)) current.Comment = WebUtility.UrlDecode(content);
                else if (current != null && name.Equals("discard", StringComparison.OrdinalIgnoreCase)) current.Discard = true;
                else if (current != null && name.Equals("secure", StringComparison.OrdinalIgnoreCase)) current.Secure = true;
                else if (current != null && name.Equals("httponly", StringComparison.OrdinalIgnoreCase)) current.HttpOnly = true;
                else
                {
                    if (current != null) result.Add(current);
                    current = new Cookie(name, equals < 0 ? string.Empty : piece.Substring(equals + 1).TrimStart(' '));
                }
            }
            if (current != null) result.Add(current);
            return result;
        }
        private static int Number(string? value)
            => int.Parse(value == null ? throw new ArgumentNullException("s") : HeaderValueCursor.Unquote(value), CultureInfo.InvariantCulture);
    }
}
