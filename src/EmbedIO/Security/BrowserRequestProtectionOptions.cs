using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbedIO.Security
{
    /// <summary>Immutable exact origin allow-lists and token lifetime for browser request protection.</summary>
    public sealed class BrowserRequestProtectionOptions
    {
        /// <summary>Creates a policy. No wildcard, suffix, path, user-info or opaque origins are accepted.</summary>
        /// <param name="serverOrigins">One to 64 allowed physical HTTP(S) server origins, including ports.</param>
        /// <param name="requestOrigins">Allowed browser origins; defaults to serverOrigins. One to 64 entries.</param>
        /// <param name="tokenLifetime">Token lifetime, greater than zero and at most one day; default 20 minutes.</param>
        public BrowserRequestProtectionOptions(
            string[] serverOrigins,
            string[]? requestOrigins = null,
            TimeSpan? tokenLifetime = null
        )
        {
            ServerOrigins = Copy(serverOrigins, nameof(serverOrigins));
            RequestOrigins = Copy(requestOrigins ?? serverOrigins, nameof(requestOrigins));
            TokenLifetime = tokenLifetime ?? TimeSpan.FromMinutes(20);

            if (TokenLifetime <= TimeSpan.Zero || TokenLifetime > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(tokenLifetime));
        }

        /// <summary>Gets the allowed physical server origins. Forwarding headers are not consulted.</summary>
        public IReadOnlyList<string> ServerOrigins { get; }

        /// <summary>Gets the allowed browser origins.</summary>
        public IReadOnlyList<string> RequestOrigins { get; }

        /// <summary>Gets the maximum token age. Server clock rollback fails closed.</summary>
        public TimeSpan TokenLifetime { get; }

        internal static string? OriginKey(string? value, bool configuration = false)
        {
            if (
                value == null
                || value.Length == 0
                || value.Length > 2048
                || value.Any(c => c <= 32 || c >= 127 || c == '\\' || c == '%' || c == ',' || c == '*')
            ) return null;

            if (
                !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || uri.UserInfo.Length != 0
                || uri.Host.Length == 0
                || uri.Host.EndsWith(".", StringComparison.Ordinal)
                || uri.Query.Length != 0
                || uri.Fragment.Length != 0
            ) return null;

            var prefix = uri.Scheme.Length + 3;

            if (value.Length < prefix || value.Substring(uri.Scheme.Length, 3) != "://") return null;

            var authority = value.Substring(prefix);

            if (configuration && authority.EndsWith("/", StringComparison.Ordinal))
                authority = authority.Substring(0, authority.Length - 1);

            if (
                authority.Length == 0
                || EmbedIO.Internal.StringOperations.IndexOfOrdinal(authority, '/') >= 0
                || authority.EndsWith(":", StringComparison.Ordinal)
            ) return null;

            return uri.Scheme
                + "://"
                + uri.IdnHost.ToUpperInvariant()
                + ":"
                + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static System.Collections.ObjectModel.ReadOnlyCollection<string> Copy(string[] values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);

            if (values.Length < 1 || values.Length > 64) throw new ArgumentOutOfRangeException(name);

            var copy = (string[])values.Clone();

            if (copy.Any(value => OriginKey(value, true) == null))
                throw new ArgumentException("Expected exact HTTP(S) origins.", name);

            return Array.AsReadOnly(copy);
        }
    }
}
