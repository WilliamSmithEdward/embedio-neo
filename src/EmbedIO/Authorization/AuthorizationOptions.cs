using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Utilities;

namespace EmbedIO.Authorization
{
    /// <summary>Named asynchronous authorization policies. Configure before registering controllers.</summary>
    /// <remarks>Each protected action requires an authenticated user. All applicable attributes and policies
    /// must succeed; roles within one attribute are alternatives. Applications own the concurrency and
    /// cancellation behavior of policy callbacks. Exceptions propagate rather than granting access.</remarks>
    public sealed class AuthorizationOptions
    {
        private readonly Dictionary<string, Func<IHttpContext, CancellationToken, Task<bool>>> _policies;
        private readonly bool _snapshot;

        /// <summary>Creates an empty policy configuration.</summary>
        public AuthorizationOptions() =>
            _policies = new Dictionary<string, Func<IHttpContext, CancellationToken, Task<bool>>>(
                StringComparer.Ordinal
            );

        private AuthorizationOptions(AuthorizationOptions source)
        {
            _policies = new Dictionary<string, Func<IHttpContext, CancellationToken, Task<bool>>>(
                source._policies,
                StringComparer.Ordinal
            );
            _snapshot = true;
        }

        /// <summary>Adds a named policy. Names are compared ordinally and case-sensitively.</summary>
        /// <param name="name">The nonempty policy name.</param>
        /// <param name="policy">Returns true to permit the current authenticated user.</param>
        /// <returns>This configuration.</returns>
        public AuthorizationOptions AddPolicy(string name, Func<IHttpContext, CancellationToken, Task<bool>> policy)
        {
            if (_snapshot) throw new InvalidOperationException("The authorization configuration is a snapshot.");

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A policy name is required.", nameof(name));

            _policies.Add(name, Utilities.Validate.NotNull(nameof(policy), policy));
            return this;
        }

        internal static AuthorizationOptions Empty { get; } = new AuthorizationOptions().Snapshot();

        internal AuthorizationOptions Snapshot() => new(this);

        internal void Validate(AuthorizationMetadata metadata)
        {
            // Anonymous overrides are explicit, but an unknown policy must still
            // fail at registration rather than become a latent unprotected route.
            foreach (var requirement in metadata.Requirements)
                if (requirement.Policy != null && !_policies.ContainsKey(requirement.Policy))
                    throw new ArgumentException(
                        "Unknown authorization policy: " + requirement.Policy,
                        nameof(metadata)
                    );
        }

        internal async Task AuthorizeAsync(IHttpContext context, AuthorizationMetadata metadata)
        {
            if (metadata.AllowAnonymous || metadata.Requirements.Count == 0) return;

            var cancellation = context.CancellationToken;
            cancellation.ThrowIfCancellationRequested();
            var authenticated = context.User is ClaimsPrincipal claims
                ? claims.Identities.Any(identity => identity.IsAuthenticated)
                : context.User.Identity?.IsAuthenticated == true;

            if (!authenticated) BearerAuthenticationModule.Challenge(context);

            foreach (var requirement in metadata.Requirements)
            {
                if (
                    requirement.Roles.Count > 0
                    && !requirement.Roles.Any(
                        role => context.User is ClaimsPrincipal principal
                            ? principal.Identities.Any(
                                identity => identity.IsAuthenticated && identity.HasClaim(identity.RoleClaimType, role)
                            )
                            : context.User.IsInRole(role)
                    )
                )
                    throw HttpException.Forbidden();

                if (requirement.Policy != null)
                {
                    var permitted = await _policies[requirement.Policy](context, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();

                    if (!permitted) throw HttpException.Forbidden();
                }
            }

            cancellation.ThrowIfCancellationRequested();
        }
    }
}
