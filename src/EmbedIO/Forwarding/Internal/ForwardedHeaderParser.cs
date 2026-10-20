using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EmbedIO.Forwarding.Internal
{
    // One proxy's record of the request it received: who sent it, and the scheme and host it was sent to.
    internal sealed class ForwardedHop
    {
        internal ForwardedHop(IPAddress? client, string? proto, string? host)
        {
            Client = client;
            Proto = proto;
            Host = host;
        }

        // Null when the hop names no client, or an "unknown" or obfuscated one.
        internal IPAddress? Client { get; }
        internal string? Proto { get; }
        internal string? Host { get; }
    }

    // Parses forwarding headers into hops, client first. Any value that does not parse makes the
    // whole result null: a malformed chain is not partially trusted.
    internal static class ForwardedHeaderParser
    {
        internal const int MaxHops = 32;

        // RFC 7239: Forwarded = 1#forwarded-element; forwarded-element = [ pair ] *( ";" [ pair ] ).
        internal static List<ForwardedHop>? ParseForwarded(string header)
        {
            var elements = SplitOutsideQuotes(header, ',');

            if (elements == null || elements.Count > MaxHops)
                return null;

            var hops = new List<ForwardedHop>(elements.Count);

            foreach (var element in elements)
            {
                var hop = ParseElement(element);

                if (hop == null)
                    return null;

                hops.Add(hop);
            }

            return hops;
        }

        // One element's pairs; null when a pair is malformed, repeats a parameter or names a
        // value the module does not accept.
        private static ForwardedHop? ParseElement(string element)
        {
            var pairs = SplitOutsideQuotes(element, ';');

            if (pairs == null)
                return null;

            var fields = default(ForwardedElement);

            foreach (var rawPair in pairs)
            {
                var pair = rawPair.Trim();

                if (pair.Length == 0)
                    continue;

                if (!TryReadPair(pair, out var name, out var value) || !fields.TryApply(name, value))
                    return null;
            }

            return fields.ToHop();
        }

        // pair = token "=" value; value = token / quoted-string.
        private static bool TryReadPair(string pair, out string name, out string value)
        {
            name = string.Empty;
            value = string.Empty;
            var equals = EmbedIO.Internal.StringOperations.IndexOfOrdinal(pair, '=');

            if (equals <= 0)
                return false;

            name = pair.Substring(0, equals);

            if (!EmbedIO.Utilities.Validate.IsRfc2616Token(name))
                return false;

            var unquoted = Unquote(pair.Substring(equals + 1));

            if (unquoted == null)
                return false;

            value = unquoted;
            return true;
        }

        // X-Forwarded-For, -Proto and -Host each list one value per hop, client first. They are aligned
        // from the nearest proxy, so a list shorter than the others has nothing for the outermost hops.
        internal static List<ForwardedHop>? ParseXForwarded(string? forHeader, string? protoHeader, string? hostHeader)
        {
            var fors = forHeader == null ? [] : SplitList(forHeader);
            var protos = protoHeader == null ? [] : SplitList(protoHeader);
            var hosts = hostHeader == null ? [] : SplitList(hostHeader);
            var count = Math.Max(fors.Count, Math.Max(protos.Count, hosts.Count));

            if (count > MaxHops)
                return null;

            var hops = new ForwardedHop[count];

            for (var fromEnd = 1; fromEnd <= count; fromEnd++)
            {
                IPAddress? client = null;
                var hasFor = fromEnd <= fors.Count;

                if (hasFor && !TryParseNode(fors[fors.Count - fromEnd], true, out client))
                    return null;

                string? proto = null;

                if (fromEnd <= protos.Count && (proto = Scheme(protos[protos.Count - fromEnd])) == null)
                    return null;

                string? host = null;

                if (fromEnd <= hosts.Count && (host = Authority(hosts[hosts.Count - fromEnd])) == null)
                    return null;

                hops[count - fromEnd] = new ForwardedHop(client, proto, host);
            }

            return [.. hops];
        }

        // "http" or "https", lowercased; anything else is not a scheme the module accepts.
        internal static string? Scheme(string value)
        {
            if (string.Equals(value, "http", StringComparison.OrdinalIgnoreCase))
                return "http";

            return string.Equals(value, "https", StringComparison.OrdinalIgnoreCase) ? "https" : null;
        }

        // A host with an optional port, normalized to lower case; null if it is not one.
        internal static string? Authority(string value)
        {
            if (value.Length == 0 || value.Length > 255)
                return null;

            string hostPart;
            string? port = null;

            if (value[0] == '[')
            {
                var close = EmbedIO.Internal.StringOperations.IndexOfOrdinal(value, ']');

                if (
                    close < 0
                    || !IPAddress.TryParse(value.Substring(1, close - 1), out var v6)
                    || v6.AddressFamily != AddressFamily.InterNetworkV6
                )
                    return null;

                hostPart = value.Substring(0, close + 1);
                var rest = value.Substring(close + 1);

                if (rest.Length > 0)
                {
                    if (rest[0] != ':')
                        return null;

                    port = rest.Substring(1);
                }
            }

            else
            {
                var colon = EmbedIO.Internal.StringOperations.IndexOfOrdinal(value, ':');

                if (colon >= 0)
                {
                    if (value.IndexOf(":", colon + 1, StringComparison.Ordinal) >= 0)
                        return null;

                    port = value.Substring(colon + 1);
                    hostPart = value.Substring(0, colon);
                }

                else
                {
                    hostPart = value;
                }

                var kind = Uri.CheckHostName(hostPart);

                if (kind != UriHostNameType.Dns && kind != UriHostNameType.IPv4)
                    return null;
            }

            if (port != null && !IsPort(port))
                return null;

            // Host names compare case-insensitively; only ASCII letters occur in a valid authority.
            return EmbedIO.Internal.StringOperations.ToLowerAscii(port == null ? hostPart : hostPart + ":" + port);
        }

        // RFC 7239 node: an IPv4 address, a bracketed IPv6 address, "unknown" or an obfuscated
        // identifier, each with an optional port. X-Forwarded-For also carries bare IPv6 addresses.
        private static bool TryParseNode(string value, bool allowBareIPv6, out IPAddress? address)
        {
            address = null;

            if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) || IsObfuscatedNode(value))
                return true;

            var parsed = value.Length > 0 && value[0] == '['
                ? TryParseBracketed(value, out address, out var port)
                : TryParseUnbracketed(value, allowBareIPv6, out address, out port);

            if (!parsed || (port != null && !IsPort(port) && !IsObfuscatedNode(port)))
                return false;

            if (address != null && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            return true;
        }

        // "[" IPv6 "]" with an optional ":" port; the address may not carry a zone.
        private static bool TryParseBracketed(string value, out IPAddress? address, out string? port)
        {
            address = null;
            port = null;
            var close = EmbedIO.Internal.StringOperations.IndexOfOrdinal(value, ']');
            return close >= 0
                && TrySplitPort(value.Substring(close + 1), out port)
                && TryParseIPv6(value.Substring(1, close - 1), out address);
        }

        // A bare IPv6 address where allowed, or canonical dotted-decimal IPv4 with an optional ":" port.
        private static bool TryParseUnbracketed(
            string value,
            bool allowBareIPv6,
            out IPAddress? address,
            out string? port
        )
        {
            address = null;
            port = null;
            var colons = Count(value, ':');

            if (colons > 1)
                return allowBareIPv6 && TryParseIPv6(value, out address);

            var colon = colons == 1 ? EmbedIO.Internal.StringOperations.IndexOfOrdinal(value, ':') : -1;

            if (colon >= 0)
                port = value.Substring(colon + 1);

            return TryParseCanonicalIPv4(colon >= 0 ? value.Substring(0, colon) : value, out address);
        }

        // Nothing after the address, or ":" and a port to validate.
        private static bool TrySplitPort(string rest, out string? port)
        {
            port = null;

            if (rest.Length == 0)
                return true;

            if (rest[0] != ':')
                return false;

            port = rest.Substring(1);
            return true;
        }

        private static bool TryParseIPv6(string text, out IPAddress? address)
        {
            address = null;

            if (
                EmbedIO.Internal.StringOperations.IndexOfOrdinal(text, '%') >= 0
                || !IPAddress.TryParse(text, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetworkV6
            )
                return false;

            address = parsed;
            return true;
        }

        // Only canonical dotted decimal: IPAddress.TryParse also accepts "1" or "0x7f.1".
        private static bool TryParseCanonicalIPv4(string text, out IPAddress? address)
        {
            address = null;

            if (
                !IPAddress.TryParse(text, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork
                || parsed.ToString() != text
            )
                return false;

            address = parsed;
            return true;
        }

        private static bool IsObfuscatedNode(string value) =>
            value.Length > 1 && value[0] == '_' && IsObfuscated(value);

        private static bool IsPort(string value) =>
            value.Length > 0
                && value.Length <= 5
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port > 0
                && port <= 65535;

        // obfnode = "_" 1*( ALPHA / DIGIT / "." / "_" / "-" ).
        private static bool IsObfuscated(string value)
        {
            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];

                if (
                    !(c is >= 'a'
                        and <= 'z' || c is >= 'A'
                        and <= 'Z' || c is >= '0'
                        and <= '9' || c == '.' || c == '_' || c == '-')
                )
                    return false;
            }

            return true;
        }

        // A token, or a quoted-string with its escapes resolved; null if it is neither.
        private static string? Unquote(string value)
        {
            if (value.Length == 0)
                return null;

            if (value[0] != '"')
                return EmbedIO.Utilities.Validate.IsRfc2616Token(value) ? value : null;

            if (value.Length < 2 || value[value.Length - 1] != '"')
                return null;

            var result = new StringBuilder(value.Length);

            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];

                if (c == '\\')
                {
                    if (++i >= value.Length - 1)
                        return null;

                    c = value[i];
                }

                else if (c == '"')
                {
                    return null;
                }

                if (c < ' ' && c != '\t')
                    return null;

                result.Append(c);
            }

            return result.ToString();
        }

        // Splits on a separator outside quoted strings; null when a quote is left open.
        private static List<string>? SplitOutsideQuotes(string value, char separator)
        {
            var parts = new List<string>();
            var start = 0;
            var quoted = false;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];

                if (quoted && c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    quoted = !quoted;
                }

                else if (c == separator && !quoted)
                {
                    parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (quoted)
                return null;

            parts.Add(value.Substring(start));

            for (var i = 0; i < parts.Count; i++)
                parts[i] = parts[i].Trim();

            return parts;
        }

        private static List<string> SplitList(string value)
        {
            var parts = new List<string>();

            foreach (var part in value.Split(','))
                parts.Add(part.Trim());

            return parts;
        }

        private static int Count(string value, char c)
        {
            var count = 0;

            foreach (var ch in value)
            {
                if (ch == c)
                    count++;
            }

            return count;
        }

        // The fields one forwarded-element names, each at most once.
        private struct ForwardedElement
        {
            private IPAddress? _client;
            private string? _proto;
            private string? _host;
            private bool _hasFor;
            private bool _hasProto;
            private bool _hasHost;

            // False when a parameter repeats or its value is not one the module accepts.
            internal bool TryApply(string name, string value)
            {
                switch (name.ToUpperInvariant())
                {
                    case "FOR":
                        if (_hasFor || !TryParseNode(value, false, out _client))
                            return false;

                        _hasFor = true;
                        return true;

                    case "PROTO":
                        if (_hasProto || (_proto = Scheme(value)) == null)
                            return false;

                        _hasProto = true;
                        return true;

                    case "HOST":
                        if (_hasHost || (_host = Authority(value)) == null)
                            return false;

                        _hasHost = true;
                        return true;

                    default:
                        // "by" and RFC 7239 extension parameters carry nothing this module uses.
                        return true;
                }
            }

            internal readonly ForwardedHop ToHop() => new(_client, _proto, _host);
        }
    }
}
