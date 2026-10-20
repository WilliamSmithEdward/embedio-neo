using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Authentication
{
    /// <summary>Authenticates Authorization: Bearer credentials before subsequent modules run.</summary>
    /// <remarks>Missing credentials are allowed by default so controller authorization can select protected routes.
    /// Invalid supplied credentials are rejected. Register before protected modules and use HTTPS for bearer credentials.
    /// Query strings and request bodies are never searched for tokens.</remarks>
    /// <remarks>Creates an authentication module.</remarks>
    /// <param name="baseRoute">The base path on which credentials are processed.</param>
    /// <param name="validator">Application-owned asynchronous token validation.</param>
    public sealed class BearerAuthenticationModule(string baseRoute, BearerTokenValidator validator) : WebModuleBase(
        baseRoute
    )
    {
        private static readonly object ChallengeKey = new();
        private readonly BearerTokenValidator _validator = Validate.NotNull(nameof(validator), validator);
        private int _maximumTokenLength = 16384;
        private bool _requireAuthentication;
        private string? _realm;

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <summary>Gets or sets whether requests without bearer credentials are rejected. Defaults to false.</summary>
        public bool RequireAuthentication
        {
            get => _requireAuthentication;
            set
            {
                EnsureConfigurationNotLocked();
                _requireAuthentication = value;
            }
        }

        /// <summary>Gets or sets the maximum credential length, in ASCII characters. Defaults to 16384.</summary>
        public int MaximumTokenLength
        {
            get => _maximumTokenLength;
            set
            {
                EnsureConfigurationNotLocked();

                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));

                _maximumTokenLength = value;
            }
        }

        /// <summary>Gets or sets an optional printable ASCII realm for challenges.</summary>
        public string? Realm
        {
            get => _realm;
            set
            {
                EnsureConfigurationNotLocked();

                if (value != null && value.Any(character => character < 32 || character > 126))
                    throw new ArgumentException(
                        "The realm must contain only printable ASCII characters.",
                        nameof(value)
                    );

                _realm = value;
            }
        }

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            if (context == null) throw new NullReferenceException();

            context.CancellationToken.ThrowIfCancellationRequested();
            // Identity belongs to this request. It must never be inherited from another
            // request or an earlier authentication attempt in the same pipeline.
            context.GetImplementation().User = new ClaimsPrincipal(new ClaimsIdentity());
            var challenge = _realm == null
                ? "Bearer"
                : "Bearer realm=\""
                    + Internal.StringOperations.ReplaceOrdinal(
                        Internal.StringOperations.ReplaceOrdinal(_realm, "\\", "\\\\"),
                        "\"",
                        "\\\""
                    )
                    + "\"";
            context.Items[ChallengeKey] = challenge;
            var values = context.Request.Headers.GetValues(HttpHeaderNames.Authorization);

            if (values == null || values.Length == 0)
            {
                if (_requireAuthentication) Reject(context, challenge, 401, null);

                return;
            }

            if (values.Length != 1) Reject(context, challenge, 400, "invalid_request");

            var value = values[0] ?? string.Empty;
            var separator = Internal.StringOperations.IndexOfOrdinal(value, ' ');
            var scheme = separator < 0 ? value : value.Substring(0, separator);

            if (!string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                if (_requireAuthentication) Reject(context, challenge, 401, null);

                return;
            }

            var start = separator;

            while (start >= 0 && start < value.Length && value[start] == ' ') start++;

            if (start < 0 || start == value.Length || value.Length - start > _maximumTokenLength)
                Reject(context, challenge, 400, "invalid_request");

            var padding = false;

            for (var index = start; index < value.Length; index++)
            {
                var character = value[index];

                if (character == '=')
                {
                    padding = true;
                    continue;
                }

                if (padding || !IsCredentialCharacter(character)) Reject(context, challenge, 400, "invalid_request");
            }

            if (value[start] == '=') Reject(context, challenge, 400, "invalid_request");

            var principal = await _validator(value.Substring(start), context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();

            if (principal == null || !principal.Identities.Any(identity => identity.IsAuthenticated))
                Reject(context, challenge, 401, "invalid_token");

            // Clone identities so a validator returning a cached principal cannot give
            // separate requests the same mutable ClaimsIdentity objects.
            context.GetImplementation().User = new ClaimsPrincipal(
                (principal ?? throw new InvalidOperationException("Missing validated principal.")).Identities
                    .Where(identity => identity.IsAuthenticated)
                    .Select(identity => identity.Clone())
            );
        }

        internal static void Challenge(IHttpContext context)
        {
            var challenge = context.Items.TryGetValue(ChallengeKey, out var value) && value is string text
                ? text
                : "Bearer";
            Reject(context, challenge, 401, null);
        }

        private static bool IsCredentialCharacter(char character) =>
            character is >= 'A'
                and <= 'Z'
                or >= 'a'
                and <= 'z'
                or >= '0'
                and <= '9'
                or '-'
                or '.'
                or '_'
                or '~'
                or '+'
                or '/';

        private static void Reject(IHttpContext context, string challenge, int status, string? error)
        {
            context.Response.Headers.Set(
                HttpHeaderNames.WWWAuthenticate,
                error == null ? challenge : challenge + (challenge == "Bearer" ? " " : ", ") + "error=\"" + error + "\""
            );
            throw new HttpException(status);
        }
    }
}
