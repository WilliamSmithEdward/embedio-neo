using System;

namespace EmbedIO.Net.Internal
{
    internal sealed class ListenerPrefix
    {
        public ListenerPrefix(string uri)
        {
            // Uri does not accept HttpListener's wildcard hosts. Parse the rest
            // with a valid placeholder, retaining the wildcard for routing.
            var hostStart = uri.IndexOf("://", StringComparison.Ordinal) + 3;
            var wildcard = hostStart >= 3 && hostStart + 1 < uri.Length
                && (uri[hostStart] == '*' || uri[hostStart] == '+')
                && (uri[hostStart + 1] == ':' || uri[hostStart + 1] == '/');
            var parsed = new Uri(wildcard
                ? uri.Substring(0, hostStart) + "localhost" + uri.Substring(hostStart + 1)
                : uri);

            Secure = parsed.Scheme == Uri.UriSchemeHttps;
            Host = wildcard ? uri[hostStart].ToString() : parsed.Host;
            Port = parsed.Port;
            Path = parsed.PathAndQuery + parsed.Fragment;
        }

        public HttpListener? Listener { get; set; }

        public bool Secure { get; }

        public string Host { get; }

        public int Port { get; }

        public string Path { get; }

        public static void CheckUri(string uri)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (!uri.StartsWith("http://", StringComparison.Ordinal) && !uri.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new ArgumentException("Only 'http' and 'https' schemes are supported.");
            }

            var length = uri.Length;
            var startHost = uri.IndexOf(':') + 3;

            if (startHost >= length)
            {
                throw new ArgumentException("No host specified.");
            }

            var root = uri.IndexOf('/', startHost, length - startHost);
            if (root == -1)
                throw new ArgumentException("No path specified.");

            // A port separator belongs to the authority, outside IPv6 brackets.
            // Colons in the address or path must not be interpreted as a port.
            var colon = uri.LastIndexOf(':', root - 1, root - startHost);
            if (colon <= uri.LastIndexOf(']', root - 1, root - startHost))
                colon = -1;

            if (startHost == colon)
            {
                throw new ArgumentException("No host specified.");
            }

            if (colon > 0)
            {
                if (!int.TryParse(uri.Substring(colon + 1, root - colon - 1), out var p) || p <= 0 || p >= 65536)
                {
                    throw new ArgumentException("Invalid port.");
                }
            }

            if (uri[uri.Length - 1] != '/')
            {
                throw new ArgumentException("The prefix must end with '/'");
            }
        }

        public bool IsValid() => Path.IndexOf('%') == -1 && Path.IndexOf("//", StringComparison.Ordinal) == -1;

        public override string ToString() => $"{Host}:{Port} ({(Secure ? "Secure" : "Insecure")}";
    }
}
