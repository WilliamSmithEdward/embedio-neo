using System;

namespace EmbedIO.Net.Internal
{
    // Original prefix view: configured syntax is checked separately from URI
    // canonicalization so wildcard routing and validation timing stay compatible.
    internal sealed class ListenerPrefix
    {
        public ListenerPrefix(string uri)
        {
            var scheme = uri.IndexOf(Uri.SchemeDelimiter, StringComparison.Ordinal);
            var authority = scheme + Uri.SchemeDelimiter.Length;
            var alias = authority >= 3
                && authority + 1 < uri.Length
                && uri[authority] is '*' or '+'
                && uri[authority + 1] is ':' or '/';
            var canonical = new Uri(
                alias ? uri.Substring(0, authority) + "localhost" + uri.Substring(authority + 1) : uri
            );
            Host = alias ? uri[authority].ToString() : canonical.Host;
            Secure = canonical.Scheme == Uri.UriSchemeHttps;
            Port = canonical.Port;
            Path = canonical.PathAndQuery + canonical.Fragment;
        }

        public string Host { get; }
        public int Port { get; }
        public bool Secure { get; }
        public string Path { get; }

        public static void CheckUri(string uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            var begin = uri.StartsWith("http://", StringComparison.Ordinal)
                ? 7
                : uri.StartsWith("https://", StringComparison.Ordinal) ? 8 : -1;

            if (begin < 0) throw new ArgumentException("Only 'http' and 'https' schemes are supported.");

            if (begin >= uri.Length) throw new ArgumentException("No host specified.");

            var path = uri.IndexOf('/', begin);

            if (path < 0) throw new ArgumentException("No path specified.");

            var portStart = -1;
            var bracketEnd = -1;

            for (var position = begin; position < path; position++)
            {
                if (uri[position] == ':') portStart = position;

                else if (uri[position] == ']') bracketEnd = position;
            }

            if (portStart <= bracketEnd) portStart = -1;

            if (portStart == begin) throw new ArgumentException("No host specified.");

            if (
                portStart >= 0
                && (!int.TryParse(uri.Substring(portStart + 1, path - portStart - 1), out var port)
                    || port is < 1 or > 65535)
            )
                throw new ArgumentException("Invalid port.");

            if (uri[uri.Length - 1] != '/') throw new ArgumentException("The prefix must end with '/'");
        }

        public bool IsValid() =>
            Path.IndexOf("%", StringComparison.Ordinal) < 0 && Path.IndexOf("//", StringComparison.Ordinal) < 0;

        public override string ToString() => Host + ":" + Port + " (" + (Secure ? "Secure" : "Insecure");
    }
}
