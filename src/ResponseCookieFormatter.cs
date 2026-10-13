using System;
using System.Net;
using System.Text;
using EmbedIO.Utilities;

namespace EmbedIO.Internal
{
    // Compiled into core and the in-process testing adapter to keep cookie wire values identical.
    internal static class ResponseCookieFormatter
    {
        internal static void Append(StringBuilder target, Cookie cookie)
        {
            if (cookie.Version > 0) target.Append("Version=").Append(cookie.Version).Append("; ");
            target.Append(cookie.Name).Append('=').Append(cookie.Value);
            if (cookie.Expires != DateTime.MinValue) Attribute(target, "Expires", HttpDate.Format(cookie.Expires));
            if (!string.IsNullOrEmpty(cookie.Path)) Attribute(target, "Path", Quote(cookie, cookie.Path));
            if (!string.IsNullOrEmpty(cookie.Domain)) Attribute(target, "Domain", Quote(cookie, cookie.Domain));
            if (!string.IsNullOrEmpty(cookie.Port)) Attribute(target, "Port", cookie.Port);
            if (cookie.Secure) target.Append("; Secure");
            if (cookie.HttpOnly) target.Append("; HttpOnly");
        }
        private static void Attribute(StringBuilder target, string name, string value)
            => target.Append("; ").Append(name).Append('=').Append(value);
        private static string Quote(Cookie cookie, string value)
        {
            if (cookie.Version == 0) return value;
            foreach (var character in value)
                if (!HttpTokenSyntax.IsTokenCharacter(character))
                    return "\"" + StringOperations.ReplaceOrdinal(value, "\"", "\\\"") + "\"";
            return value;
        }
    }
    internal static class HttpTokenSyntax
    {
        internal static bool IsTokenCharacter(char c)
            => c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
                || c == '!' || c == '#' || c == '$' || c == '%' || c == '&' || c == '\''
                || c == '*' || c == '+' || c == '-' || c == '.' || c == '^' || c == '_'
                || c == '`' || c == '|' || c == '~';
    }
}
