using System;
using System.Diagnostics.CodeAnalysis;
using EmbedIO.Routing;

namespace EmbedIO.Endpoints
{
    /// <summary>A route prefix and inherited endpoint settings. Merely declaring a group does not claim requests.</summary>
    public sealed class EndpointRouteGroup : EndpointOptions<EndpointRouteGroup>
    {
        internal EndpointRouteGroup(EndpointModule owner, EndpointRouteGroup? parent, string prefix)
            : base(owner, parent) => Prefix = prefix;

        /// <summary>Gets the full application-relative route prefix.</summary>
        public string Prefix { get; }

        /// <summary>Gets the module for configuring authorization, browser protection, caches and security-header policy registries.</summary>
        public EndpointModule Module => Owner;

        /// <summary>Creates a nested group. Prefix placeholders participate in endpoint binding.</summary>
        public EndpointRouteGroup MapGroup(string prefix)
        {
            Check();
            return new EndpointRouteGroup(Owner, this, Join(Prefix, prefix));
        }

        /// <summary>Maps an exact GET route with a generated binding.</summary>
        public EndpointBuilder MapGet(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Get, handler);

        /// <summary>Maps an exact POST route with a generated binding.</summary>
        public EndpointBuilder MapPost(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Post, handler);

        /// <summary>Maps an exact PUT route with a generated binding.</summary>
        public EndpointBuilder MapPut(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Put, handler);

        /// <summary>Maps an exact PATCH route with a generated binding.</summary>
        public EndpointBuilder MapPatch(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Patch, handler);

        /// <summary>Maps an exact DELETE route with a generated binding.</summary>
        public EndpointBuilder MapDelete(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Delete, handler);

        /// <summary>Maps an exact HEAD route with a generated binding.</summary>
        public EndpointBuilder MapHead(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Head, handler);

        /// <summary>Maps an exact OPTIONS route with a generated binding.</summary>
        public EndpointBuilder MapOptions(string route, EndpointHandler handler) =>
            MapMethods(route, HttpVerbs.Options, handler);

        /// <summary>Maps one or more methods to an exact route.</summary>
        public EndpointBuilder MapMethods(string route, HttpVerbs methods, EndpointHandler handler)
        {
            Check();
            return Owner.Add(
                this,
                Join(Prefix, route),
                methods,
                handler ?? throw new ArgumentNullException(nameof(handler))
            );
        }

        /// <summary>Maps several explicit HTTP methods to one endpoint with shared fluent settings.</summary>
        public EndpointBuilder MapMethods(string route, EndpointHandler handler, params HttpVerbs[] methods)
        {
            Check();
            return Owner.Add(
                this,
                Join(Prefix, route),
                methods,
                handler ?? throw new ArgumentNullException(nameof(handler))
            );
        }

        /// <summary>Maps several methods using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapMethods(string route, Delegate handler, params HttpVerbs[] methods) =>
            MapMethods(route, EndpointHandler.FromDelegate(handler), methods);

        /// <summary>Maps a delegate using registration-time reflection and compilation. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapMethods(string route, HttpVerbs methods, Delegate handler) =>
            MapMethods(route, methods, WebApi.WebApiModuleBase.DescribeEndpoint(handler));

        /// <summary>Maps an exact GET delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapGet(string route, Delegate handler) => MapMethods(route, HttpVerbs.Get, handler);

        /// <summary>Maps an exact POST delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapPost(string route, Delegate handler) => MapMethods(route, HttpVerbs.Post, handler);

        /// <summary>Maps an exact PUT delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapPut(string route, Delegate handler) => MapMethods(route, HttpVerbs.Put, handler);

        /// <summary>Maps an exact PATCH delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapPatch(string route, Delegate handler) => MapMethods(route, HttpVerbs.Patch, handler);

        /// <summary>Maps an exact DELETE delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapDelete(string route, Delegate handler) =>
            MapMethods(route, HttpVerbs.Delete, handler);

        /// <summary>Maps an exact HEAD delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapHead(string route, Delegate handler) => MapMethods(route, HttpVerbs.Head, handler);

        /// <summary>Maps an exact OPTIONS delegate endpoint. Use a generated descriptor for Native AOT.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public EndpointBuilder MapOptions(string route, Delegate handler) =>
            MapMethods(route, HttpVerbs.Options, handler);

        internal static string Join(string prefix, string route)
        {
            route = global::EmbedIO.Utilities.Validate.NotNull(nameof(route), route);

            if ((route.Length == 0 || route[0] != '/') || route.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException(
                    "Routes must be application-relative paths beginning with a single slash.",
                    nameof(route)
                );

            foreach (var character in route)
                if (character == '\\' || character == '#' || char.IsControl(character))
                    throw new ArgumentException(
                        "Routes cannot contain backslashes, fragments or control characters.",
                        nameof(route)
                    );

            var combined = prefix.TrimEnd('/') + (route == "/" ? string.Empty : route);

            if (combined.Length == 0) combined = "/";

            if (combined.Length > 4096) throw new ArgumentException("Route exceeds 4096 characters.", nameof(route));

            RouteMatcher.Parse(combined, false);
            return combined;
        }
    }
}
