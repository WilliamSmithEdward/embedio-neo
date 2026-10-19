using System;
using System.Collections.Specialized;
using System.Net;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Parsed wire facts are separate from the application's mutable request views.
    // This owner never reads a body or borrows a transport.
    internal sealed class Http1RequestHead
    {
        internal NameValueCollection Fields { get; } = [];
        internal string Method { get; private set; } = string.Empty;
        internal string Target { get; private set; } = string.Empty;
        internal Version Version { get; private set; } = HttpVersion.Version11;
        internal Uri Address { get; private set; } = WebServer.NullUri;
        internal bool Chunked { get; private set; }
        internal bool Framed { get; private set; }
        internal long Length { get; private set; }

        internal string? ReadLine(string line)
        {
            var methodEnd = StringOperations.IndexOfOrdinal(line, ' ');
            var targetEnd = methodEnd < 0 ? -1 : line.IndexOf(' ', methodEnd + 1);

            if (methodEnd < 1 || targetEnd <= methodEnd + 1 || line.Length - targetEnd != 9)
                return "Invalid request line.";

            for (var position = 0; position < methodEnd; position++)
                if (!HttpRequestFraming.IsTokenCharacter(line[position])) return "Invalid method.";

            for (var position = methodEnd + 1; position < targetEnd; position++)
            {
                var character = line[position];

                if (character <= ' ' || character == 127 || character is '#' or '\\') return "Invalid request target.";

                if (
                    character == '%'
                    && (position + 2 >= targetEnd
                        || !Uri.IsHexDigit(line[position + 1])
                        || !Uri.IsHexDigit(line[position + 2]))
                )
                    return "Invalid request target.";
            }

            Method = line.Substring(0, methodEnd);
            Target = line.Substring(methodEnd + 1, targetEnd - methodEnd - 1);

            if (
                string.CompareOrdinal(line, targetEnd + 1, "HTTP/1.", 0, 7) != 0
                || line[targetEnd + 8] is < '0' or > '9'
            )
                return "Unsupported HTTP version.";

            Version = line[targetEnd + 8] switch
            {
                '0' => HttpVersion.Version10,
                '1' => HttpVersion.Version11,
                var minor => new Version(1, minor - '0')
            };
            return null;
        }

        internal string? ReadField(string line, out string name, out string value)
        {
            name = value = string.Empty;
            var separator = StringOperations.IndexOfOrdinal(line, ':');

            if (separator < 1) return "Bad Request";

            for (var index = 0; index < separator; index++)
                if (!HttpRequestFraming.IsTokenCharacter(line[index])) return "Invalid header name.";

            for (var index = separator + 1; index < line.Length; index++)
                if ((line[index] < 32 && line[index] != '\t') || line[index] == 127) return "Invalid header value.";

            name = line.Substring(0, separator);
            var valueStart = separator + 1;
            var valueEnd = line.Length;

            while (valueStart < valueEnd && line[valueStart] is ' ' or '\t') valueStart++;

            while (valueEnd > valueStart && line[valueEnd - 1] is ' ' or '\t') valueEnd--;

            value = line.Substring(valueStart, valueEnd - valueStart);
            var preceding = Fields[name];

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (
                    !HttpRequestFraming.TryContentLength(value, out var length)
                    || (preceding != null
                        && (!HttpRequestFraming.TryContentLength(preceding, out var prior) || length != prior))
                )
                    return "Invalid or conflicting Content-Length.";
            }

            else if (preceding != null)
            {
                if (
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                )
                    return "Duplicate framing header.";

                if (
                    name.Equals(HttpHeaderNames.SecWebSocketKey, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(HttpHeaderNames.SecWebSocketVersion, StringComparison.OrdinalIgnoreCase)
                )
                {
                    Fields.Add(name, value);
                    return null;
                }

                // RFC 9110, Section 5.3: repeated field lines combine in order; keeping only the last would
                // change the field. Cookie lines join with "; ", as HTTP/2 joins cookie fields (RFC 9113, 8.2.3).
                // The out value stays this line's own, so header budgets count each line once.
                Fields.Set(
                    name,
                    preceding
                        + (name.Equals(HttpHeaderNames.Cookie, StringComparison.OrdinalIgnoreCase) ? "; " : ", ")
                        + value
                );
                return null;
            }

            Fields.Set(name, value);
            return null;
        }

        internal string? Complete(IPEndPoint local, bool secure)
        {
            var transfer = Fields[HttpHeaderNames.TransferEncoding];
            var declared = Fields[HttpHeaderNames.ContentLength];

            if (transfer != null)
            {
                if (
                    declared != null
                    || Version < HttpVersion.Version11
                    || !transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase)
                )
                    return "Unsupported or ambiguous request framing.";

                Chunked = true;
                Length = -1;
            }

            else if (declared != null)
            {
                if (!HttpRequestFraming.TryContentLength(declared, out var count)) return "Invalid Content-Length.";

                Length = count;
            }

            Framed = true;
            var authority = Fields[HttpHeaderNames.Host];

            if (
                (Version > HttpVersion.Version10 && string.IsNullOrEmpty(authority))
                || (!string.IsNullOrEmpty(authority) && !HttpRequestFraming.IsValidHost(authority))
            ) return "Invalid host name";

            var connect = Method == "CONNECT";

            if (connect)
            {
                var portStart = Target.LastIndexOf(':');

                if (
                    portStart <= 0
                    || portStart <= Target.LastIndexOf(']')
                    || portStart == Target.Length - 1
                    || !HttpRequestFraming.IsValidHost(Target)
                    || !Uri.TryCreate("http://" + Target + "/", UriKind.Absolute, out var destination)
                    || destination.Host.Length == 0
                    || destination.Port == 0
                )
                    return "CONNECT requires a valid authority and explicit port.";

                authority = Target;
            }

            var pathStart = 0;

            if (Target.Length == 0) return "Invalid request target form.";

            if (Target[0] != '/')
            {
                var schemeEnd = Target.IndexOf("://", StringComparison.Ordinal);

                if (schemeEnd >= 0)
                {
                    pathStart = schemeEnd + 3;

                    while (pathStart < Target.Length && Target[pathStart] is not '/' and not '?')
                    {
                        if (Target[pathStart] == '@') return "Userinfo is not allowed in a request target.";

                        pathStart++;
                    }
                }
            }

            if (!connect && !HttpRequestFraming.IsValidPathAndQuery(Target, pathStart))
                return "Invalid request target syntax.";

            var absolute = UriUtility.StringToAbsoluteUri(Target);

            if (
                !connect
                && Target[0] != '/'
                && Target != "*"
                && (absolute == null
                    || (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                    || Target.IndexOf("://", StringComparison.Ordinal) < 0)
            ) return "Invalid request target form.";

            if (Target == "*" && Method != "OPTIONS") return "Asterisk-form requires OPTIONS.";

            var path = connect || Target == "*" ? "/" : absolute?.PathAndQuery ?? Target;

            if (!connect && absolute != null) authority = absolute.Host;

            if (string.IsNullOrEmpty(authority)) authority = absolute?.Host ?? local.ToString();

            var port = authority.LastIndexOf(':');

            if (port >= 0 && port > authority.LastIndexOf(']')) authority = authority.Substring(0, port);

            var address = (secure ? "https" : "http") + "://" + authority + ":" + local.Port + path;

            if (!Uri.TryCreate(address, UriKind.Absolute, out var resolved))
                return WebUtility.HtmlEncode("Invalid url: " + address);

            Address = resolved;
            return null;
        }
    }
}
