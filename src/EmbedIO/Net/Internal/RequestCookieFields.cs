using System;
using System.Globalization;
using System.Net;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal static class RequestCookieFields
    {
        internal static CookieList Read(string field)
        {
            var result = new CookieList();
            Cookie? cookie = null;
            var version = 0;
            for (var offset = 0; offset <= field.Length;)
            {
                var end = field.IndexOf(';', offset);
                if (end < 0) end = field.Length;
                var part = field.Substring(offset, end - offset).Trim();
                var equals = StringOperations.IndexOfOrdinal(part, '=');
                var name = (equals < 0 ? part : part.Substring(0, equals)).Trim();
                var value = equals < 0 ? string.Empty : part.Substring(equals + 1).Trim();
                if (part.StartsWith("$Version", StringComparison.Ordinal))
                    version = int.Parse(HeaderValueCursor.Unquote(equals < 0 ? part : part.Substring(equals + 1)), CultureInfo.InvariantCulture);
                else if (cookie != null && part.StartsWith("$Path", StringComparison.Ordinal)) cookie.Path = value;
                else if (cookie != null && part.StartsWith("$Domain", StringComparison.Ordinal)) cookie.Domain = value;
                else if (cookie != null && part.StartsWith("$Port", StringComparison.Ordinal)) cookie.Port = "\"" + value + "\"";
                else if (part.Length != 0)
                {
                    if (cookie != null) result.Add(cookie);
                    cookie = null;
                    try { cookie = new Cookie(name, value) { Version = version }; }
                    catch (Exception error) when (error is CookieException or ArgumentException) { }
                }
                if (end == field.Length) break;
                offset = end + 1;
            }
            if (cookie != null) result.Add(cookie);
            return result;
        }
    }
}
