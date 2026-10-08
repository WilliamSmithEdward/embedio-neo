using System;

namespace EmbedIO.Security
{
    /// <summary>An immutable snapshot of a temporary client-key ban.</summary>
    public sealed class ClientBanInfo
    {
        internal ClientBanInfo(string clientKey, DateTimeOffset expiresAt)
        {
            ClientKey = clientKey;
            ExpiresAt = expiresAt;
        }

        /// <summary>Gets the application-selected, ordinal client key.</summary>
        public string ClientKey { get; }

        /// <summary>Gets the UTC expiration of this ban.</summary>
        public DateTimeOffset ExpiresAt { get; }
    }
}
