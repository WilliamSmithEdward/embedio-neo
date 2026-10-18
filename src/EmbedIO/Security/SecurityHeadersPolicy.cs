using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EmbedIO.Security
{
    /// <summary>An immutable, opt-in response security-header policy. Nonce templates use the literal {nonce}.</summary>
    public sealed class SecurityHeadersPolicy
    {
        private static readonly string[] ReferrerPolicies = [
            "no-referrer", "no-referrer-when-downgrade", "origin", "origin-when-cross-origin", "same-origin",
            "strict-origin", "strict-origin-when-cross-origin", "unsafe-url"
        ];
        internal static readonly string[] ManagedHeaders = [
            "Content-Security-Policy", "Content-Security-Policy-Report-Only", "X-Content-Type-Options",
            "Referrer-Policy", "X-Frame-Options", "Permissions-Policy", "Strict-Transport-Security"
        ];
        internal readonly KeyValuePair<string, string>[] Fields;
        internal readonly string? Hsts;
        internal readonly KeyValuePair<string, string[]>[] NonceFields;

        /// <summary>Creates a policy. CSP and permissions directives are application-selected; no CSP is invented.</summary>
        /// <param name="contentSecurityPolicy">Enforced CSP; use 'nonce-{nonce}' inside a source list for a per-response nonce.</param>
        /// <param name="contentSecurityPolicyReportOnly">Independent report-only CSP; it can use the same nonce template.</param>
        /// <param name="noSniff">Emit nosniff; true by default when a policy is installed.</param>
        /// <param name="referrerPolicy">A supported referrer policy, or null to omit it.</param>
        /// <param name="frameOptions">DENY, SAMEORIGIN, or null. Coordinate with CSP frame-ancestors.</param>
        /// <param name="permissionsPolicy">Application-selected permissions directives, or null.</param>
        /// <param name="hstsMaxAge">Explicit HSTS lifetime, zero to clear, or null to omit. Emitted only on an actually secure connection.</param>
        /// <param name="hstsIncludeSubdomains">Explicit HSTS subdomain inclusion.</param>
        /// <param name="hstsPreload">Explicit preload directive; requires subdomains and at least one year. Does not submit a domain.</param>
        public SecurityHeadersPolicy(
            string? contentSecurityPolicy = null,
            string? contentSecurityPolicyReportOnly = null,
            bool noSniff = true,
            string? referrerPolicy = "strict-origin-when-cross-origin",
            string? frameOptions = "DENY",
            string? permissionsPolicy = null,
            TimeSpan? hstsMaxAge = null,
            bool hstsIncludeSubdomains = false,
            bool hstsPreload = false
        )
        {
            var fields = new List<KeyValuePair<string, string>>();
            Add("Content-Security-Policy", contentSecurityPolicy);
            Add("Content-Security-Policy-Report-Only", contentSecurityPolicyReportOnly);

            if (noSniff) Add("X-Content-Type-Options", "nosniff");

            if (referrerPolicy != null && !ReferrerPolicies.Contains(referrerPolicy, StringComparer.Ordinal))
                throw new ArgumentException("Unknown referrer policy.", nameof(referrerPolicy));

            Add("Referrer-Policy", referrerPolicy);

            if (frameOptions != null && frameOptions != "DENY" && frameOptions != "SAMEORIGIN")
                throw new ArgumentException("Use DENY, SAMEORIGIN or null.", nameof(frameOptions));

            Add("X-Frame-Options", frameOptions);
            Add("Permissions-Policy", permissionsPolicy);

            if (hstsMaxAge.HasValue)
            {
                var seconds = hstsMaxAge.Value.TotalSeconds;

                if (seconds < 0 || seconds > 315360000 || seconds != Math.Truncate(seconds))
                    throw new ArgumentOutOfRangeException(nameof(hstsMaxAge));

                if (hstsPreload && (!hstsIncludeSubdomains || seconds < 31536000))
                    throw new ArgumentException(
                        "Preload requires subdomains and at least one year.",
                        nameof(hstsPreload)
                    );

                Hsts = "max-age="
                    + ((long)seconds).ToString(CultureInfo.InvariantCulture)
                    + (hstsIncludeSubdomains ? "; includeSubDomains" : "")
                    + (hstsPreload ? "; preload" : "");
            }

            else if (hstsIncludeSubdomains || hstsPreload)
                throw new ArgumentException("HSTS directives require an explicit lifetime.", nameof(hstsMaxAge));

            if (fields.Sum(p => p.Value.Length) > 16384) throw new ArgumentException("Policy fields exceed 16 KiB.");

            NonceFields =
            [
                .. fields
                    .Where(IsNonceField)
                    .Select(
                        p => new KeyValuePair<string, string[]>(
                            p.Key,
                            p.Value.Split(["{nonce}"], StringSplitOptions.None)
                        )
                    )
            ];
            Fields =
            [
                // Static fields need no per-response template formatting.
                .. fields.Where(p => !IsNonceField(p))
            ];
            UsesNonce = HasNonce(contentSecurityPolicy) || HasNonce(contentSecurityPolicyReportOnly);
            ValidateCsp(contentSecurityPolicy);
            ValidateCsp(contentSecurityPolicyReportOnly);

            void Add(string name, string? value)
            {
                if (value == null) return;

                if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(c => c < 32 || c > 126))
                    throw new ArgumentException(
                        "Policy fields must be nonempty visible ASCII, at most 4096 characters.",
                        name
                    );

                fields.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        /// <summary>Gets whether this policy requires a fresh cryptographic nonce and prohibits response storage.</summary>
        public bool UsesNonce { get; }

        /// <summary>Selects this policy before response commitment. A selected policy owns all managed fields.</summary>
        /// <param name="context">The current request.</param>
        public void Apply(IHttpContext context) => SecurityHeadersState.Select(context, this);

        /// <summary>Custom response adapters must call this immediately before final headers reach the wire.
        /// EmbedIO listeners and TestWebServer do so automatically.</summary>
        /// <param name="context">The current request.</param>
        public static void PrepareResponse(IHttpContext context)
        {
            context = global::EmbedIO.Utilities.Validate.NotNull(nameof(context), context);

            SecurityHeadersState
                .Find(context)
                ?.Commit();
        }

        private static bool IsNonceField(KeyValuePair<string, string> field) =>
            (field.Key == "Content-Security-Policy" || field.Key == "Content-Security-Policy-Report-Only")
                && HasNonce(field.Value);

        private static bool HasNonce(string? value) => value?.IndexOf("{nonce}", StringComparison.Ordinal) >= 0;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2249:Use string.Contains",
            Justification = "The shared implementation targets netstandard2.0, which has no string.Contains overload with StringComparison."
        )]
        private static void ValidateCsp(string? value)
        {
            if (value == null) return;

            var rest = string.Concat(value.Split(["'nonce-{nonce}'"], StringSplitOptions.None));

            if (
                rest.IndexOf("'nonce-", StringComparison.OrdinalIgnoreCase) >= 0
                || global::EmbedIO.Internal.StringOperations.IndexOfOrdinal(rest, '{') >= 0
                || global::EmbedIO.Internal.StringOperations.IndexOfOrdinal(rest, '}') >= 0
            )
                throw new ArgumentException(
                    "Use only the exact quoted source 'nonce-{nonce}'; fixed or malformed nonce sources are forbidden.",
                    nameof(value)
                );
        }
    }
}
