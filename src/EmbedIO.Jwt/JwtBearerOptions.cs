using System;
using System.Collections.Generic;
using Microsoft.IdentityModel.Tokens;

namespace EmbedIO.Jwt
{
    /// <summary>Explicit trust and claim settings for a JWT bearer validator.</summary>
    /// <remarks>The validator snapshots settings and supported verification keys at construction.
    /// Signature, expiration, issuer and audience checks cannot be disabled. No remote discovery
    /// is performed. Construct a new validator to change the trusted keys.</remarks>
    public sealed class JwtBearerOptions
    {
        /// <summary>Gets or sets the required exact issuer.</summary>
        public string Issuer { get; set; } = string.Empty;
        /// <summary>Gets or sets the required audience. Trailing slashes are significant.</summary>
        public string Audience { get; set; } = string.Empty;
        /// <summary>Gets the trusted RSA, EC or symmetric signing keys.</summary>
        public IList<SecurityKey> SigningKeys { get; } = new List<SecurityKey>();
        /// <summary>Gets the required explicit JOSE signing-algorithm allowlist, for example RS256.</summary>
        public IList<string> AllowedAlgorithms { get; } = new List<string>();
        /// <summary>Gets or sets the lifetime clock skew, zero by default.</summary>
        public TimeSpan ClockSkew { get; set; } = TimeSpan.Zero;
        /// <summary>Gets or sets the maximum encoded token size in UTF-8 bytes, 16,384 by default.</summary>
        public int MaxTokenSizeInBytes { get; set; } = 16384;
        /// <summary>Gets or sets the name claim type, sub by default.</summary>
        public string NameClaimType { get; set; } = "sub";
        /// <summary>Gets or sets the role claim type, role by default.</summary>
        public string RoleClaimType { get; set; } = "role";
    }
}
