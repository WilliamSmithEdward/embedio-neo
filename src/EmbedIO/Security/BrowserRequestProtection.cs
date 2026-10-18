using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using EmbedIO.Utilities;

namespace EmbedIO.Security
{
    /// <summary>Application-owned browser origin checks and expiring session/identity-bound anti-forgery tokens.</summary>
    /// <remarks>Register explicitly and dispose after all users stop. A new instance invalidates old tokens.
    /// This does not authenticate clients, prevent XSS, make safe methods safe to mutate, or authorize actions.
    /// Tokens are reusable until expiry; regenerate sessions at login/logout. No token registry or timer is retained.</remarks>
    public sealed class BrowserRequestProtection : IDisposable
    {
        /// <summary>The only request header searched for an anti-forgery token. Bodies, cookies and URLs are not searched.</summary>
        public const string TokenHeaderName = "X-CSRF-Token";
        private const string SessionMarker = "__EmbedIO.BrowserRequestProtection";
        private const int PayloadLength = 57;
        private const int TokenLength = 89;
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0330:Use 'System.Threading.Lock'",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock."
        )]
        private readonly object _sync = new();
        private readonly byte[] _key = new byte[32];
        private readonly HashSet<string> _servers;
        private readonly HashSet<string> _origins;
        private readonly TimeSpan _lifetime;
        private bool _disposed;

        /// <summary>Creates an independent policy with a cryptographically random process-local signing key.</summary>
        /// <param name="options">Immutable origin and lifetime configuration.</param>
        public BrowserRequestProtection(BrowserRequestProtectionOptions options)
        {
            options = Validate.NotNull(nameof(options), options);

            _servers = new HashSet<string>(
                options.ServerOrigins.Select(
                    value => BrowserRequestProtectionOptions.OriginKey(value, true)
                        ?? throw new ArgumentException("Invalid server origin.", nameof(options))
                ),
                StringComparer.Ordinal
            );
            _origins = new HashSet<string>(
                options.RequestOrigins.Select(
                    value => BrowserRequestProtectionOptions.OriginKey(value, true)
                        ?? throw new ArgumentException("Invalid request origin.", nameof(options))
                ),
                StringComparer.Ordinal
            );
            _lifetime = options.TokenLifetime;
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(_key);
        }

        /// <summary>Issues a token on a GET request, creating/resolving its session and marking the response no-store.</summary>
        /// <param name="context">An authenticated, if required, context with a configured session manager.</param>
        /// <returns>A 119-character opaque token to return in a response body and send in X-CSRF-Token.</returns>
        public string GetToken(IHttpContext context)
        {
            context = ValidateContext(context);

            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal))
                throw new InvalidOperationException("Issue browser tokens through a GET endpoint.");

            ValidateOrigin(context);
            context.Response.Headers.Set(HttpHeaderNames.CacheControl, "no-store");
            context.Response.Headers.Set("Pragma", "no-cache");
            // Keep a newly issued session nonempty: the built-in manager purges empty sessions.
            context.Session[SessionMarker] = true;
            var token = new byte[TokenLength];
            token[0] = 1;
            var ticks = DateTime.UtcNow.Ticks;

            for (var i = 8; i > 0; i--)
            {
                token[i] = (byte)ticks;
                ticks >>= 8;
            }

            var nonce = new byte[16];

            using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);

            Buffer.BlockCopy(nonce, 0, token, 9, nonce.Length);
            var binding = Binding(context);
            Buffer.BlockCopy(binding, 0, token, 25, binding.Length);
            var signature = Sign(0, token, PayloadLength);
            Buffer.BlockCopy(signature, 0, token, PayloadLength, signature.Length);
            return Encode(token);
        }

        /// <summary>Rejects untrusted target/origin or invalid unsafe-method tokens with a generic 403.</summary>
        /// <param name="context">The request. Authentication must run first when tokens bind an authenticated identity.</param>
        /// <param name="requireToken">False explicitly exempts token checks, but never host, supplied Origin or WebSocket Origin checks.</param>
        /// <remarks>GET, HEAD and OPTIONS do not require a token unless X-HTTP-Method-Override selects an unsafe or ambiguous method.
        /// An override never exempts an unsafe physical method. WebSocket handshakes require a trusted Origin,
        /// including on GET, and use application authentication rather than a custom token header.
        /// A missing Origin on other requests is allowed only subject to their normal token requirement.</remarks>
        public void ValidateRequest(IHttpContext context, bool requireToken = true)
        {
            context = ValidateContext(context);
            ValidateOrigin(context);
            var method = context.Request.HttpMethod;
            var overrides = context.Request.Headers.GetValues("X-HTTP-Method-Override");
            // Middleware runs before handlers interpret method overrides. Require a token whenever
            // either interpretation can mutate; never downgrade an unsafe physical method.
            var safeOverride = overrides == null || (overrides.Length == 1 && IsSafeMethod(overrides[0]));

            if (!requireToken || (safeOverride && (context.Request.IsWebSocketRequest || IsSafeMethod(method)))) return;

            var values = context.Request.Headers.GetValues(TokenHeaderName);

            if (values == null || values.Length != 1 || values[0] == null || values[0].Length != 119) throw Denied();

            var value = values[0];

            for (var i = 0; i < value.Length; i++)
                if (
                    !(value[i] >= 'a' && value[i] <= 'z')
                    && !(value[i] >= 'A' && value[i] <= 'Z')
                    && !(value[i] >= '0' && value[i] <= '9')
                    && value[i] != '-'
                    && value[i] != '_'
                ) throw Denied();

            byte[] token;

            try
            {
                token = Convert.FromBase64String(
                    value
                        .Replace('-', '+')
                        .Replace('_', '/')
                        + "="
                );
            }

            catch (FormatException)
            {
                throw Denied();
            }

            if (token.Length != TokenLength || token[0] != 1 || Encode(token) != value) throw Denied();

            // Authenticate the bounded token before touching sessions, so forged tokens create no session state.
            if (!Equal(Sign(0, token, PayloadLength), token, PayloadLength)) throw Denied();

            long issued = 0;

            for (var i = 1; i <= 8; i++) issued = (issued << 8) | token[i];

            var now = DateTime.UtcNow.Ticks;

            if (issued < 0 || issued > now || now - issued >= _lifetime.Ticks) throw Denied();

            if (!Equal(Binding(context), token, 25)) throw Denied();
        }

        /// <summary>Erases the signing key and prevents further issuance or validation.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                Array.Clear(_key, 0, _key.Length);
            }
        }

        private static bool IsSafeMethod(string method) => method == "GET" || method == "HEAD" || method == "OPTIONS";

        private IHttpContext ValidateContext(IHttpContext context)
        {
            context = Validate.NotNull(nameof(context), context);
            context.CancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                EnsureOpen();
            }

            return context;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        private void EnsureOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BrowserRequestProtection));
        }

        private void ValidateOrigin(IHttpContext context)
        {
            var request = context.Request;
            var target = BrowserRequestProtectionOptions.OriginKey(request.Url.GetLeftPart(UriPartial.Authority));

            if (target == null || !_servers.Contains(target)) throw Denied();

            var hosts = request.Headers.GetValues(HttpHeaderNames.Host);

            if (
                hosts != null
                && (hosts.Length != 1
                    || BrowserRequestProtectionOptions.OriginKey(request.Url.Scheme + "://" + hosts[0]) != target)
            ) throw Denied();

            var origins = request.Headers.GetValues("Origin");

            if (origins == null)
            {
                if (request.IsWebSocketRequest) throw Denied();

                return;
            }

            if (origins.Length != 1) throw Denied();

            var origin = BrowserRequestProtectionOptions.OriginKey(origins[0]);

            if (origin == null || !_origins.Contains(origin)) throw Denied();
        }

        private byte[] Binding(IHttpContext context)
        {
            using var stream = new MemoryStream();

            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                Write(context.Session.Id);
                var count = 0;

                if (context.User is ClaimsPrincipal principal)
                {
                    foreach (var identity in principal.Identities)
                        if (identity.IsAuthenticated)
                        {
                            if (++count > 16)
                                throw new InvalidOperationException("Too many identities for browser protection.");

                            WriteIdentity(identity);
                        }
                }

                else if (context.User.Identity is { IsAuthenticated: true } identity)
                {
                    count++;
                    WriteIdentity(identity);
                }

                writer.Write(count);

                void WriteIdentity(IIdentity identity)
                {
                    Write(identity.AuthenticationType);
                    Write(identity.Name);
                    var claims = identity as ClaimsIdentity;
                    WriteClaim(claims?.FindFirst(claims.NameClaimType));
                    WriteClaim(claims?.FindFirst(ClaimTypes.NameIdentifier));
                }

                void WriteClaim(Claim? claim)
                {
                    // Subject/name values are scoped to their issuing authority, including delegated identities.
                    Write(claim?.Type);
                    Write(claim?.Value);
                    Write(claim?.Issuer);
                    Write(claim?.OriginalIssuer);
                }

                void Write(string? value)
                {
                    if (value != null && value.Length > 1024)
                        throw new InvalidOperationException(
                            "Session/identity values exceed browser protection bounds."
                        );

                    writer.Write(value != null);

                    if (value != null) writer.Write(value);
                }
            }

            var bytes = stream.ToArray();
            return Sign(1, bytes, bytes.Length);
        }

        private byte[] Sign(byte domain, byte[] value, int length)
        {
            var bytes = new byte[length + 1];
            bytes[0] = domain;
            Buffer.BlockCopy(value, 0, bytes, 1, length);
            HMACSHA256 hmac;

            lock (_sync)
            {
                EnsureOpen();

                hmac = new HMACSHA256(_key);
            }

            // The constructor copies the key; independent requests compute outside the key-lifetime lock.
            using (hmac) return hmac.ComputeHash(bytes);
        }

        private static bool Equal(byte[] expected, byte[] actual, int offset)
        {
#if NET10_0_OR_GREATER
            return CryptographicOperations.FixedTimeEquals(expected, actual.AsSpan(offset, expected.Length));
#else
            var difference = 0;

            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ actual[offset + i];

            return difference == 0;
#endif
        }

        private static string Encode(byte[] bytes) =>
            Convert
                .ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        private static HttpException Denied() =>
            HttpException.Forbidden("Browser request protection rejected the request.");
    }
}
