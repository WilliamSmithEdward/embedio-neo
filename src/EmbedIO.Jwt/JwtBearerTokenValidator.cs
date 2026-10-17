using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EmbedIO.Jwt
{
    /// <summary>Validates signed JWTs against a fixed issuer, audience, key set and algorithm allowlist.</summary>
    /// <remarks>Pass ValidateAsync as the bearer module validator. Claims retain their original names.
    /// This class does not issue tokens, fetch keys or accept unsigned or encrypted tokens.</remarks>
    public sealed class JwtBearerTokenValidator
    {
        private readonly JsonWebTokenHandler _handler;
        private readonly TokenValidationParameters _parameters;
        private readonly int _maximumSize;

        /// <summary>Creates a validator with a snapshot of explicit trust settings.</summary>
        /// <param name="options">Required issuer, audience, signing keys and allowed algorithms.</param>
        public JwtBearerTokenValidator(JwtBearerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var issuer = Required(options.Issuer, nameof(options.Issuer));
            var audience = Required(options.Audience, nameof(options.Audience));
            var nameClaim = Required(options.NameClaimType, nameof(options.NameClaimType));
            var roleClaim = Required(options.RoleClaimType, nameof(options.RoleClaimType));
            if (options.ClockSkew < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Clock skew must not be negative.");
            if (options.MaxTokenSizeInBytes < 1) throw new ArgumentOutOfRangeException(nameof(options), "Token size must be positive.");
            if (options.SigningKeys.Count == 0 || options.AllowedAlgorithms.Count == 0)
                throw new ArgumentException("At least one trusted key and one signing algorithm are required.", nameof(options));
            var algorithms = new List<string>();
            foreach (var algorithm in options.AllowedAlgorithms)
            {
                if (algorithm is not ("HS256" or "HS384" or "HS512" or "RS256" or "RS384" or "RS512"
                    or "PS256" or "PS384" or "PS512" or "ES256" or "ES384" or "ES512"))
                    throw new ArgumentException("Unsupported signing algorithm in the allowlist.", nameof(options));
                if (!algorithms.Contains(algorithm)) algorithms.Add(algorithm);
            }
            var tokenTypes = new List<string>();
            foreach (var type in options.AllowedTokenTypes)
            {
                Required(type, nameof(options.AllowedTokenTypes));
                if (!tokenTypes.Contains(type)) tokenTypes.Add(type);
            }
            var keys = new List<SecurityKey>();
            foreach (var key in options.SigningKeys)
            {
                if (key == null) throw new ArgumentException("Signing keys must not contain null.", nameof(options));
                if (key is not (JsonWebKey or RsaSecurityKey or ECDsaSecurityKey or SymmetricSecurityKey))
                    throw new ArgumentException("Supply an RSA, EC, symmetric or JSON Web Key directly.", nameof(options));
                var jwk = key as JsonWebKey ?? JsonWebKeyConverter.ConvertFromSecurityKey(key);
                // Copy only verification components; never retain mutable input keys or private asymmetric material.
                var copy = new JsonWebKey
                {
                    Kty = jwk.Kty,
                    Kid = jwk.Kid,
                    Alg = jwk.Alg,
                    Use = jwk.Use,
                    N = jwk.N,
                    E = jwk.E,
                    Crv = jwk.Crv,
                    X = jwk.X,
                    Y = jwk.Y,
                    K = jwk.K,
                };
                if (copy.Kty is not ("oct" or "RSA" or "EC"))
                    throw new ArgumentException("Only RSA, EC and symmetric verification keys are supported.", nameof(options));
                if (copy.Kty == "oct") Required(copy.K, nameof(options.SigningKeys));
                else if (copy.Kty == "RSA")
                {
                    Required(copy.N, nameof(options.SigningKeys));
                    Required(copy.E, nameof(options.SigningKeys));
                }
                else
                {
                    Required(copy.Crv, nameof(options.SigningKeys));
                    Required(copy.X, nameof(options.SigningKeys));
                    Required(copy.Y, nameof(options.SigningKeys));
                }
                if (!string.IsNullOrEmpty(copy.Use) && copy.Use != "sig")
                    throw new ArgumentException("Signing keys must permit signature verification.", nameof(options));
                if (jwk.KeyOps.Count > 0 && !jwk.KeyOps.Contains("verify"))
                    throw new ArgumentException("Signing keys must permit verification.", nameof(options));
                keys.Add(copy);
            }
            _maximumSize = options.MaxTokenSizeInBytes;
            _handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = _maximumSize };
            _parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                RequireAudience = true,
                ValidAudience = audience,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys.ToArray(),
                RequireSignedTokens = true,
                ValidAlgorithms = algorithms.ToArray(),
                ValidTypes = tokenTypes.Count == 0 ? null : tokenTypes.ToArray(),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = options.ClockSkew,
                AuthenticationType = "Bearer",
                NameClaimType = nameClaim,
                RoleClaimType = roleClaim,
                SaveSigninToken = false,
                IncludeTokenOnFailedValidation = false,
            };
        }

        /// <summary>Returns an authenticated principal for a valid token, or null for invalid tokens.</summary>
        /// <param name="token">The encoded JWT without the Bearer scheme prefix.</param>
        /// <param name="cancellationToken">Cancellation checked before and after local validation.</param>
        /// <returns>A validated principal, or null.</returns>
        /// <remarks>Local cryptographic work is not interruptible. Cancellation prevents starting validation
        /// or publishing its result and is never converted into authentication failure.</remarks>
        public async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Length == 0 || token.Length > _maximumSize || Encoding.UTF8.GetByteCount(token) > _maximumSize
                || !_handler.CanReadToken(token)) return null;
            var separators = 0;
            foreach (var character in token) if (character == '.') separators++;
            if (separators != 2) return null;
            var result = await _handler.ValidateTokenAsync(token, _parameters).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.IsValid && result.ClaimsIdentity != null ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
        }

        private static string Required(string value, string parameter)
            => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A nonempty value is required.", parameter) : value;
    }
}
