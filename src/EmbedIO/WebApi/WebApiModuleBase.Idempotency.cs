using System;
using System.Collections.Generic;
using System.Reflection;
using EmbedIO.Idempotency;
using EmbedIO.Routing;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        private IdempotencyCoordinator? _idempotency;
        private Dictionary<string, IdempotencyPolicy>? _idempotencyPolicies;

        /// <summary>Configures finite write idempotency before controller registration. Security guards execute before replay.</summary>
        /// <param name="coordinator">Shared coordinator/store.</param>
        /// <param name="policies">Named immutable policies, copied during configuration.</param>
        public void ConfigureIdempotency(
            IdempotencyCoordinator coordinator,
            IDictionary<string, IdempotencyPolicy> policies
        )
        {
            EnsureConfigurationNotLocked();

            if (ControllerCount != 0)
                throw new InvalidOperationException("Configure idempotency before registering controllers.");

            coordinator = Utilities.Validate.NotNull(nameof(coordinator), coordinator);

            if (policies == null) throw new ArgumentNullException(nameof(policies));

            var copy = new Dictionary<string, IdempotencyPolicy>(StringComparer.Ordinal);

            foreach (var pair in policies)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                    throw new ArgumentException("Each policy needs a nonempty name and value.", nameof(policies));

                copy.Add(pair.Key, pair.Value);
            }

            _idempotency = coordinator;
            _idempotencyPolicies = copy;
        }

        private IdempotencyPolicy? ResolveIdempotency(
            string? policy,
            HttpVerbs verb,
            Type? resultType,
            string? outputCache
        )
        {
            if (policy == null) return null;

            if (
                !IdempotencyCoordinator.WriteVerb(verb)
                || resultType != null && IsStreamingResultType(resultType)
                || outputCache != null
            )
                throw new InvalidOperationException("Idempotency requires finite write routes without output caching.");

            if (
                _idempotency == null
                || _idempotencyPolicies == null
                || !_idempotencyPolicies.TryGetValue(policy, out var selected)
            )
                throw new InvalidOperationException("Unknown or unconfigured idempotency policy: " + policy);

            return selected;
        }

        private RouteHandlerCallback WrapIdempotency(RouteHandlerCallback handler, IdempotencyPolicy? policy) =>
            policy == null
                ? handler
                : (_idempotency ?? throw new InvalidOperationException("No idempotency coordinator.")).Protect(
                    policy,
                    handler
                );

        private static string? ReadIdempotency(Type controllerType, MethodInfo method) =>
            (method.GetCustomAttribute<IdempotencyAttribute>(true)
                ?? controllerType.GetCustomAttribute<IdempotencyAttribute>(true))?.Policy;
    }
}
