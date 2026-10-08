using System;
using System.Collections.Specialized;
using System.Text;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2RequestHeaders
    {
        private Http2RequestHeaders() { }
        public string Method { get; private set; } = "";
        public string Scheme { get; private set; } = "";
        public string Authority { get; private set; } = "";
        public string Path { get; private set; } = "";
        public string? Protocol { get; private set; }
        public long? ContentLength { get; private set; }
        public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal static Http2RequestHeaders Parse(Http2HeaderBlock block, bool extendedConnectEnabled = false)
        {
            var result = new Http2RequestHeaders();
            var regular = false;
            var seen = 0;
            string? host = null;
            StringBuilder? cookies = null;
            foreach (var field in block.Fields)
            {
                ValidateField(field, block.StreamId);
                if (field.Name[0] == ':')
                {
                    if (regular) throw Invalid(block.StreamId, "Pseudo-header follows regular headers.");
                    int flag;
                    switch (field.Name)
                    {
                        case ":method": flag = 1; result.Method = field.Value; break;
                        case ":scheme": flag = 2; result.Scheme = field.Value; break;
                        case ":authority": flag = 4; result.Authority = field.Value; break;
                        case ":path": flag = 8; result.Path = field.Value; break;
                        case ":protocol": flag = 16; result.Protocol = field.Value; break;
                        default: throw Invalid(block.StreamId, "Unknown request pseudo-header.");
                    }
                    if ((seen & flag) != 0) throw Invalid(block.StreamId, "Duplicate pseudo-header.");
                    seen |= flag;
                    continue;
                }
                regular = true;
                ValidateConnectionField(field, block.StreamId);
                if (field.Name == "host")
                {
                    if (host != null) throw Invalid(block.StreamId, "Duplicate Host.");
                    host = field.Value;
                }
                if (field.Name == "content-length")
                    result.ContentLength = ParseLength(field.Value, result.ContentLength, block.StreamId);
                if (field.Name == "cookie")
                {
                    if (cookies == null) cookies = new StringBuilder(field.Value);
                    else cookies.Append("; ").Append(field.Value);
                }
                else result.Headers.Add(field.Name, field.Value);
            }
            if (!Token(result.Method)) throw Invalid(block.StreamId, "Missing or invalid method.");
            var connect = result.Method == "CONNECT";
            if (result.Protocol != null && (!connect || !extendedConnectEnabled || !Token(result.Protocol)))
                throw Invalid(block.StreamId, "Extended CONNECT was not negotiated or is invalid.");
            if (connect && result.Protocol != null && (seen & 4) == 0) throw Invalid(block.StreamId, "Extended CONNECT requires authority.");
            if (connect && result.Protocol == null)
            {
                if ((seen & 10) != 0 || (seen & 4) == 0) throw Invalid(block.StreamId, "CONNECT requires authority and forbids scheme/path.");
                ValidateAuthority(result.Authority, "http", block.StreamId, true);
            }
            else
            {
                if ((seen & 10) != 10 || !SchemeValid(result.Scheme) || result.Path.Length == 0)
                    throw Invalid(block.StreamId, "Missing or invalid scheme/path.");
                if (result.Path[0] != '/' && !(result.Method == "OPTIONS" && result.Path == "*"))
                    throw Invalid(block.StreamId, "Invalid request path.");
                foreach (var ch in result.Path)
                    if (ch <= 32 || ch >= 127 || ch == '#' || ch == '\\') throw Invalid(block.StreamId, "Invalid request target character.");
                if ((seen & 4) == 0) result.Authority = host ?? "";
                if (result.Authority.Length != 0 || string.Equals(result.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(result.Scheme, "https", StringComparison.OrdinalIgnoreCase) || connect)
                    ValidateAuthority(result.Authority, result.Scheme, block.StreamId, false);
            }
            if (host != null)
            {
                var scheme = result.Scheme.Length == 0 ? "http" : result.Scheme;
                var hostUri = ValidateAuthority(host, scheme, block.StreamId, false);
                var authorityUri = ValidateAuthority(result.Authority, scheme, block.StreamId, false);
                if (!string.Equals(hostUri.IdnHost, authorityUri.IdnHost, StringComparison.OrdinalIgnoreCase) || hostUri.Port != authorityUri.Port)
                    throw Invalid(block.StreamId, "Host and authority identify different endpoints.");
            }
            if (block.EndStream && result.ContentLength.GetValueOrDefault() != 0)
                throw Invalid(block.StreamId, "Content-Length exceeds the completed request body.");
            if (cookies != null) result.Headers.Set("cookie", cookies.ToString());
            if (result.Authority.Length != 0) result.Headers.Set("host", result.Authority);
            return result;
        }

        internal static void ValidateTrailers(Http2HeaderBlock block)
        {
            if (!block.EndStream) throw Invalid(block.StreamId, "Trailers must end the request.");
            foreach (var field in block.Fields)
            {
                ValidateField(field, block.StreamId);
                if (field.Name[0] == ':' || field.Name == "content-length" || field.Name == "host")
                    throw Invalid(block.StreamId, "Invalid trailer field.");
                ValidateConnectionField(field, block.StreamId);
            }
        }

        private static void ValidateField(HpackField field, int id)
        {
            var start = field.Name.Length > 0 && field.Name[0] == ':' ? 1 : 0;
            if (field.Name.Length == start) throw Invalid(id, "Empty field name.");
            for (var i = start; i < field.Name.Length; i++)
                if (!TokenChar(field.Name[i]) || (field.Name[i] >= 'A' && field.Name[i] <= 'Z')) throw Invalid(id, "Invalid lowercase field name.");
            if (field.Value.Length > 0 && (Whitespace(field.Value[0]) || Whitespace(field.Value[field.Value.Length - 1])))
                throw Invalid(id, "Field value has surrounding whitespace.");
            foreach (var ch in field.Value)
                if ((ch < 32 && ch != '\t') || ch == 127 || ch > 255) throw Invalid(id, "Invalid field value.");
        }

        private static void ValidateConnectionField(HpackField field, int id)
        {
            switch (field.Name)
            {
                case "connection":
                case "proxy-connection":
                case "keep-alive":
                case "transfer-encoding":
                case "upgrade":
                    throw Invalid(id, "Connection-specific field in HTTP/2.");
                case "te":
                    if (!string.Equals(field.Value, "trailers", StringComparison.OrdinalIgnoreCase)) throw Invalid(id, "TE must be trailers.");
                    break;
            }
        }

        private static long ParseLength(string value, long? previous, int id)
        {
            var i = 0;
            do
            {
                while (i < value.Length && Whitespace(value[i])) i++;
                var start = i;
                long length = 0;
                while (i < value.Length && value[i] >= '0' && value[i] <= '9')
                {
                    var digit = value[i++] - '0';
                    if (length > (long.MaxValue - digit) / 10) throw Invalid(id, "Content-Length overflow.");
                    length = length * 10 + digit;
                }
                if (i == start) throw Invalid(id, "Invalid Content-Length.");
                while (i < value.Length && Whitespace(value[i])) i++;
                if (previous.HasValue && previous.Value != length) throw Invalid(id, "Conflicting Content-Length.");
                previous = length;
                if (i == value.Length) return length;
                if (value[i++] != ',' || i == value.Length) throw Invalid(id, "Invalid Content-Length list.");
            } while (true);
        }

        private static Uri ValidateAuthority(string value, string scheme, int id, bool requirePort)
        {
            if (value.Length == 0) throw Invalid(id, "Empty authority.");
            foreach (var ch in value)
                if (ch <= 32 || ch >= 127 || ch == '/' || ch == '\\' || ch == '?' || ch == '#' || ch == '@') throw Invalid(id, "Invalid authority.");
            var colon = value.LastIndexOf(':');
            var bracket = value.LastIndexOf(']');
            var hasPort = colon >= 0 && colon > bracket;
            if (requirePort && !hasPort) throw Invalid(id, "CONNECT requires an explicit port.");
            if (hasPort)
            {
                if (colon == value.Length - 1) throw Invalid(id, "Empty authority port.");
                for (var i = colon + 1; i < value.Length; i++)
                    if (value[i] < '0' || value[i] > '9') throw Invalid(id, "Invalid authority port.");
            }
            if (!Uri.TryCreate(scheme + "://" + value + "/", UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
                throw Invalid(id, "Invalid authority URI.");
            return uri;
        }

        private static bool SchemeValid(string value)
        {
            if (value.Length == 0 || !Letter(value[0])) return false;
            foreach (var ch in value) if (!Letter(ch) && !(ch >= '0' && ch <= '9') && ch != '+' && ch != '-' && ch != '.') return false;
            return true;
        }
        private static bool Letter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
        private static bool Whitespace(char ch) => ch == ' ' || ch == '\t';
        private static bool Token(string value)
        {
            if (value.Length == 0) return false;
            foreach (var ch in value) if (!TokenChar(ch)) return false;
            return true;
        }
        private static bool TokenChar(char ch) => Letter(ch) || (ch >= '0' && ch <= '9') || "!#$%&'*+-.^_`|~".IndexOf(ch) >= 0;
        private static Http2ProtocolException Invalid(int id, string reason) => new(1, reason, id);
    }
}
