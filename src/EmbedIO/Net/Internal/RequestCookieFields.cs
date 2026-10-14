using System;
using System.Globalization;
using System.Net;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    internal static class RequestCookieFields
    {
        internal static CookieList Read(string field)
        {
            var result = new CookieList();
            Cookie? cookie = null;
            var version = 0;
            var offset = 0;
            int end;
            do
            {
                end = field.IndexOf(';', offset);
                if (end < 0) end = field.Length;
                ReadPart(field.Substring(offset, end - offset).Trim(), result, ref cookie, ref version);
                offset = end + 1;
            }
            while (end < field.Length);
            if (cookie != null) result.Add(cookie);
            return result;
        }

        // "$Version" applies to the cookies after it; "$Path", "$Domain" and "$Port" to the cookie
        // before them; any other non-empty part starts a cookie, an invalid one leaving none.
        private static void ReadPart(string part, CookieList result, ref Cookie? cookie, ref int version)
        {
            var equals = StringOperations.IndexOfOrdinal(part, '=');
            var name = (equals < 0 ? part : part.Substring(0, equals)).Trim();
            var value = equals < 0 ? string.Empty : part.Substring(equals + 1).Trim();
            if (part.StartsWith("$Version", StringComparison.Ordinal))
                version = int.Parse(HeaderValueCursor.Unquote(equals < 0 ? part : part.Substring(equals + 1)), CultureInfo.InvariantCulture);
            else if (cookie != null && part.StartsWith("$Path", StringComparison.Ordinal)) cookie.Path = value;
            else if (cookie != null && part.StartsWith("$Domain", StringComparison.Ordinal)) cookie.Domain = value;
            else if (cookie != null && part.StartsWith("$Port", StringComparison.Ordinal)) cookie.Port = "\"" + value + "\"";
            else if (part.Length != 0) cookie = StartCookie(result, cookie, name, value, version);
        }

        // Keeps the finished cookie and starts the next; an invalid name or value leaves none in progress.
        private static Cookie? StartCookie(CookieList result, Cookie? finished, string name, string value, int version)
        {
            if (finished != null) result.Add(finished);
            try { return new Cookie(name, value) { Version = version }; }
            catch (Exception error) when (error is CookieException or ArgumentException) { return null; }
        }
    }
}
