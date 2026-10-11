using System;
using System.Net;
using System.Text;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal static class ResponseCookieFields
    {
        internal static void AppendLine(StringBuilder target, Cookie cookie, IHttpResponse response)
        {
            if (cookie.Name.Length == 0) return;
            target.Append("Set-Cookie: ");
            Append(target, cookie, response);
            target.Append("\r\n");
        }
        internal static void Append(StringBuilder target, Cookie cookie, IHttpResponse? response)
        {
            if (cookie.Version > 0) target.Append("Version=").Append(cookie.Version).Append("; ");
            target.Append(cookie.Name).Append('=').Append(cookie.Value);
            if (cookie.Expires != DateTime.MinValue) Attribute(target, "Expires", HttpDate.Format(cookie.Expires));
            if (!string.IsNullOrEmpty(cookie.Path)) Attribute(target, "Path", Quote(cookie, cookie.Path));
            if (!string.IsNullOrEmpty(cookie.Domain)) Attribute(target, "Domain", Quote(cookie, cookie.Domain));
            if (!string.IsNullOrEmpty(cookie.Port)) Attribute(target, "Port", cookie.Port);
            if (cookie.Secure) target.Append("; Secure");
            if (cookie.HttpOnly) target.Append("; HttpOnly");
            if (response != null) CookieSameSiteStore.Append(target, response, cookie);
        }
        private static void Attribute(StringBuilder target, string name, string value)
            => target.Append("; ").Append(name).Append('=').Append(value);
        private static string Quote(Cookie cookie, string value)
        {
            if (cookie.Version == 0) return value;
            foreach (var character in value)
                if (!HttpRequestFraming.IsTokenCharacter(character))
                    return "\"" + StringOperations.ReplaceOrdinal(value, "\"", "\\\"") + "\"";
            return value;
        }
    }
}
