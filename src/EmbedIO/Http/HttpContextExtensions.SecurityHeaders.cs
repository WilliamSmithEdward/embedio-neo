using System;
using EmbedIO.Security;

namespace EmbedIO
{
    public static partial class HttpContextExtensions
    {
        /// <summary>Gets this response's 256-bit CSP nonce. Use only in trusted script/style elements, HTML-encode attributes,
        /// and never interpolate a nonce into attacker-supplied markup. Nonce responses cannot be cached or recorded for idempotency.</summary>
        /// <param name="this">The current request with a nonce policy selected before response commitment.</param>
        /// <returns>A stable nonce for this response, distinct across requests.</returns>
        public static string GetSecurityHeadersNonce(this IHttpContext @this)
        {
            @this = global::EmbedIO.Utilities.Validate.NotNull(nameof(@this), @this);

            if (SecurityHeadersState.IsRecording(@this))
                throw new InvalidOperationException("Nonce responses cannot be recorded for idempotency.");

            return (SecurityHeadersState.Find(@this)
                ?? throw new InvalidOperationException("No security-header policy is selected.")).Nonce();
        }
    }
}
