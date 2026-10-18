using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Caching;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        private OutputCache? _outputCache;
        private Dictionary<string, OutputCachePolicy>? _outputCachePolicies;

        /// <summary>Configures opt-in output caching before registering controllers.</summary>
        /// <param name="cache">Shared, bounded storage; retain it for invalidation.</param>
        /// <param name="policies">Named immutable policies copied during configuration.</param>
        public void ConfigureOutputCaching(OutputCache cache, IDictionary<string, OutputCachePolicy> policies)
        {
            EnsureConfigurationNotLocked();
            if (ControllerCount != 0) throw new InvalidOperationException("Configure output caching before registering controllers.");
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (policies == null) throw new ArgumentNullException(nameof(policies));
            var snapshot = new Dictionary<string, OutputCachePolicy>(StringComparer.Ordinal);
            foreach (var pair in policies)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) throw new ArgumentException("Each policy needs a nonempty name and value.", nameof(policies));
                snapshot.Add(pair.Key, pair.Value);
            }
            _outputCache = cache; _outputCachePolicies = snapshot;
        }

        // Chosen at registration time: uncached invokers keep their original state machines.
        private static RouteHandlerCallback CreateCachedControllerHandler(Type controllerType,
            Func<IHttpContext, WebApiController?> factory, Func<IHttpContext, WebApiController, Task> release,
            Func<WebApiController, IHttpContext, RouteMatch, Task> invoke, BeforeHandlerPlan? before,
            OutputCacheRegistration caching)
            => async (context, route) =>
            {
                var controller = factory(context) ?? throw new InvalidOperationException($"The factory for {controllerType.FullName} returned null.");
                Func<Task>? complete = null;
                try
                {
                    if (!controllerType.IsInstanceOfType(controller))
                        throw new InvalidOperationException($"The factory for {controllerType.FullName} returned {controller.GetType().FullName}.");
                    controller.HttpContext = context;
                    controller.Route = route;
                    controller.OutputCaching = caching;
                    complete = controller.ConfigureOutputCacheRelease(release, context);
                    if (before != null) await before.ExecuteAsync(controller, context.CancellationToken).ConfigureAwait(false);
                    await invoke(controller, context, route).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (complete != null) await complete().ConfigureAwait(false);
                        else await release(context, controller).ConfigureAwait(false);
                    }
                    finally { controller.OutputCaching = null; }
                }
            };

        private OutputCacheRegistration? ResolveOutputCache(string? policy)
        {
            if (policy == null) return null;
            if (_outputCache == null || _outputCachePolicies == null || !_outputCachePolicies.TryGetValue(policy, out var selected))
                throw new InvalidOperationException("Unknown or unconfigured output-cache policy: " + policy);
            return new OutputCacheRegistration(_outputCache, selected);
        }

        private static string? ReadOutputCache(Type controllerType, MethodInfo method)
            => (method.GetCustomAttribute<OutputCacheAttribute>(true) ?? controllerType.GetCustomAttribute<OutputCacheAttribute>(true))?.Policy;
    }

    internal sealed class OutputCacheRegistration
    {
        internal readonly OutputCache Cache;
        internal readonly OutputCachePolicy Policy;
        internal OutputCacheRegistration(OutputCache cache, OutputCachePolicy policy) { Cache = cache; Policy = policy; }
    }
}
