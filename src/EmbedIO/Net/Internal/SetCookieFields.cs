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
                if (current != null && SetAttribute(current, name, content, equals < 0, separator, ref cursor)) continue;
                if (current != null) result.Add(current);
                current = new Cookie(name, equals < 0 ? string.Empty : piece.Substring(equals + 1).TrimStart(' '));
            }
            if (current != null) result.Add(current);
            return result;
        }

        // Applies one attribute to the cookie being read. False when the piece is
        // not a known attribute, so it starts the next cookie instead.
        private static bool SetAttribute(Cookie cookie, string name, string? content, bool flagOnly, char separator, ref HeaderValueCursor cursor)
        {
            if (Is(name, "version")) cookie.Version = Number(content);
            else if (Is(name, "expires")) SetExpires(cookie, content, separator, ref cursor);
            else if (Is(name, "max-age")) cookie.Expires = DateTime.Now.AddSeconds(Number(content));
            else if (Is(name, "path")) cookie.Path = content;
            else if (Is(name, "domain")) cookie.Domain = content;
            else if (Is(name, "port")) cookie.Port = flagOnly ? "\"\"" : content;
            else if (Is(name, "commenturl")) cookie.CommentUri = UriUtility.StringToUri(content == null ? null : HeaderValueCursor.Unquote(content));
            else if (Is(name, "comment")) cookie.Comment = WebUtility.UrlDecode(content);
            else if (Is(name, "discard")) cookie.Discard = true;
            else if (Is(name, "secure")) cookie.Secure = true;
            else if (Is(name, "httponly")) cookie.HttpOnly = true;
            else return false;
            return true;
        }

        // The cursor splits an Expires date at its comma; rejoin it before parsing.
        private static void SetExpires(Cookie cookie, string? content, char separator, ref HeaderValueCursor cursor)
        {
            var date = content ?? string.Empty;
            if (separator == ',' && cursor.Next(out var continuation, out _)) date += ", " + continuation.Trim();
            if (!HttpDate.TryParse(date, out var expires)) expires = DateTimeOffset.Now;
            if (cookie.Expires == DateTime.MinValue) cookie.Expires = expires.LocalDateTime;
        }

        private static bool Is(string name, string attribute) => name.Equals(attribute, StringComparison.OrdinalIgnoreCase);

        private static int Number(string? value)
            => int.Parse(value == null ? throw new ArgumentNullException("s") : HeaderValueCursor.Unquote(value), CultureInfo.InvariantCulture);
    }
}
