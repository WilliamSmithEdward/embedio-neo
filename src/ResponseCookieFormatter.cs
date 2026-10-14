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
            // System.Net.Cookie does not refuse control characters in a value, and these parts
            // are written into the Set-Cookie field as they stand, so they are held to the
            // field value's rule here, where the text is produced for every writer.
            Check(cookie.Name);
            Check(cookie.Value);
            Check(cookie.Path);
            Check(cookie.Domain);
            Check(cookie.Port);
            if (cookie.Version > 0) target.Append("Version=").Append(cookie.Version).Append("; ");
            target.Append(cookie.Name).Append('=').Append(cookie.Value);
            if (cookie.Expires != DateTime.MinValue) Attribute(target, "Expires", HttpDate.Format(cookie.Expires));
            if (!string.IsNullOrEmpty(cookie.Path)) Attribute(target, "Path", Quote(cookie, cookie.Path));
            if (!string.IsNullOrEmpty(cookie.Domain)) Attribute(target, "Domain", Quote(cookie, cookie.Domain));
            if (!string.IsNullOrEmpty(cookie.Port)) Attribute(target, "Port", cookie.Port);
            if (cookie.Secure) target.Append("; Secure");
            if (cookie.HttpOnly) target.Append("; HttpOnly");
        }
        private static void Check(string? value)
        {
            if (value != null && !HttpTokenSyntax.IsValidFieldValue(value))
                throw new ArgumentException("A cookie carries no control characters.", nameof(value));
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

        // RFC 9110, Section 5.5: a field value carries no control characters other than HTAB.
        // The HTTP/1 head is encoded as ISO-8859-1, so nothing above 255 can be represented
        // there either. Http2RequestHeaders.ValidateField holds HTTP/2, HTTP/3, informational
        // sections and trailers to this same rule, and this is the one place HTTP/1 shares it.
        //
        // The head is written once per response, so this runs on the request path. Searching
        // for the first character outside the permitted set is vectorized on the modern target,
        // which is why it is a search rather than a loop; the older target keeps the loop.
#if NET10_0_OR_GREATER
        private static readonly System.Buffers.SearchValues<char> FieldValueCharacters =
            System.Buffers.SearchValues.Create(PermittedFieldValueCharacters());

        private static string PermittedFieldValueCharacters()
        {
            // HTAB, the printable range, and the rest of ISO-8859-1 above DEL.
            var permitted = new char[1 + ('\u007f' - ' ') + (256 - 128)];
            var next = 0;
            permitted[next++] = '\t';
            for (var character = ' '; character < '\u007f'; character++) permitted[next++] = character;
            for (var character = '\u0080'; character < 'Ā'; character++) permitted[next++] = character;
            return new string(permitted, 0, next);
        }

        internal static bool IsValidFieldValue(string value)
            => value.AsSpan().IndexOfAnyExcept(FieldValueCharacters) < 0;
#else
        internal static bool IsValidFieldValue(string value)
        {
            foreach (var character in value)
                if ((character < ' ' && character != '\t') || character == '\u007f' || character > 'ÿ')
                    return false;

            return true;
        }
#endif

        // A reason phrase carries the same characters as a field value (RFC 9112, Section 4).
        // Refusing it where it is assigned matches System.Net.HttpListenerResponse and reports
        // the mistake at the line that made it, rather than when the head reaches the wire.
        internal static string ValidatedReasonPhrase(string? value, string parameterName)
        {
            if (value == null) return string.Empty;
            if (!IsValidFieldValue(value))
                throw new ArgumentException("A reason phrase carries no control characters.", parameterName);

            return value;
        }
    }
}
