using System;
using System.Collections.Generic;
using System.Reflection;
using EmbedIO.Routing;
using EmbedIO.Security;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        private Dictionary<string, SecurityHeadersPolicy>? _securityHeaderPolicies;
        private SecurityHeadersPolicy? _defaultSecurityHeaderPolicy;

        /// <summary>Configures named immutable response policies before controller registration. Method attributes override class attributes.</summary>
        /// <param name="policies">Policy names and immutable policies, copied on configuration.</param>
        /// <param name="defaultPolicy">Optional module default; null inherits the server or preceding middleware.</param>
        public void ConfigureSecurityHeaders(
            IDictionary<string, SecurityHeadersPolicy> policies,
            string? defaultPolicy = null
        )
        {
            EnsureConfigurationNotLocked();

            if (ControllerCount != 0)
                throw new InvalidOperationException("Configure security headers before registering controllers.");

            policies = global::EmbedIO.Utilities.Validate.NotNull(nameof(policies), policies);

            var snapshot = new Dictionary<string, SecurityHeadersPolicy>(StringComparer.Ordinal);

            foreach (var pair in policies)
            {
                _ = new SecurityHeadersAttribute(pair.Key);

                if (pair.Value == null) throw new ArgumentException("Every policy needs a value.", nameof(policies));

                snapshot.Add(pair.Key, pair.Value);
            }

            if (defaultPolicy != null && !snapshot.ContainsKey(defaultPolicy))
                throw new ArgumentException("Unknown default policy.", nameof(defaultPolicy));

            _securityHeaderPolicies = snapshot;
            _defaultSecurityHeaderPolicy = defaultPolicy == null ? null : snapshot[defaultPolicy];
        }

        private static string? ReadSecurityHeaders(Type controller, MethodInfo method) =>
            (method.GetCustomAttribute<SecurityHeadersAttribute>(true)
                ?? controller.GetCustomAttribute<SecurityHeadersAttribute>(true))?.Policy;

        private SecurityHeadersPolicy? ResolveSecurityHeaders(string? name, string? idempotency)
        {
            var policy = _defaultSecurityHeaderPolicy;

            if (
                name != null
                && (_securityHeaderPolicies == null || !_securityHeaderPolicies.TryGetValue(name, out policy))
            )
                throw new InvalidOperationException("Unknown or unconfigured security-header policy: " + name);

            if (policy?.UsesNonce == true && idempotency != null)
                throw new InvalidOperationException(
                    "Nonce policies cannot be combined with idempotent response recording."
                );

            return policy;
        }

        private static RouteHandlerCallback WrapSecurityHeaders(
            RouteHandlerCallback handler,
            SecurityHeadersPolicy? policy
        )
        {
            if (policy == null) return handler;

            return (context, route) =>
            {
                policy.Apply(context);
                return handler(context, route);
            };
        }
    }
}
