using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace EmbedIO.Endpoints
{
    /// <summary>Registers exact minimal endpoints on an existing server without changing OnGet/controller APIs.</summary>
    public static class EndpointExtensions
    {
        private static readonly object Gate = new object();

        /// <summary>Gets the server's endpoint module, creating it at the current module-order position.</summary>
        public static EndpointModule GetEndpointModule(this IWebServer server)
        {
            server = global::EmbedIO.Utilities.Validate.NotNull(nameof(server), server);

            lock (Gate)
            {
                var registered = server.Modules
                    .OfType<EndpointModule>()
                    .ToArray();

                if (registered.Length > 1)
                    throw new InvalidOperationException(
                        "Use a specific module's Root when several endpoint modules are registered."
                    );

                var module = registered.Length == 1 ? registered[0] : new EndpointModule();

                if (registered.Length == 0) server.Modules.Add(null, module);

                return module;
            }
        }

        /// <summary>Creates a group whose prefix and policies apply to its exact endpoints.</summary>
        public static EndpointRouteGroup MapGroup(this IWebServer server, string prefix) =>
            server
                .GetEndpointModule()
                .Root.MapGroup(prefix);

        /// <summary>Maps several explicit methods to one generated endpoint.</summary>
        public static EndpointBuilder MapMethods(
            this IWebServer server,
            string route,
            EndpointHandler handler,
            params HttpVerbs[] methods
        ) =>
            server
                .GetEndpointModule()
                .Root.MapMethods(route, handler, methods);

        /// <summary>Maps several explicit methods using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapMethods(
            this IWebServer server,
            string route,
            Delegate handler,
            params HttpVerbs[] methods
        ) =>
            server
                .GetEndpointModule()
                .Root.MapMethods(route, handler, methods);

        /// <summary>Maps an exact GET generated endpoint.</summary>
        public static EndpointBuilder MapGet(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapGet(route, handler);

        /// <summary>Maps an exact GET delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapGet(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapGet(route, handler);

        /// <summary>Maps an exact POST generated endpoint.</summary>
        public static EndpointBuilder MapPost(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapPost(route, handler);

        /// <summary>Maps an exact POST delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapPost(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapPost(route, handler);

        /// <summary>Maps an exact PUT generated endpoint.</summary>
        public static EndpointBuilder MapPut(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapPut(route, handler);

        /// <summary>Maps an exact PUT delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapPut(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapPut(route, handler);

        /// <summary>Maps an exact PATCH generated endpoint.</summary>
        public static EndpointBuilder MapPatch(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapPatch(route, handler);

        /// <summary>Maps an exact PATCH delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapPatch(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapPatch(route, handler);

        /// <summary>Maps an exact DELETE generated endpoint.</summary>
        public static EndpointBuilder MapDelete(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapDelete(route, handler);

        /// <summary>Maps an exact DELETE delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapDelete(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapDelete(route, handler);

        /// <summary>Maps an exact HEAD generated endpoint.</summary>
        public static EndpointBuilder MapHead(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapHead(route, handler);

        /// <summary>Maps an exact HEAD delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapHead(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapHead(route, handler);

        /// <summary>Maps an exact OPTIONS generated endpoint.</summary>
        public static EndpointBuilder MapOptions(this IWebServer server, string route, EndpointHandler handler) =>
            server
                .GetEndpointModule()
                .Root.MapOptions(route, handler);

        /// <summary>Maps an exact OPTIONS delegate endpoint using registration-time reflection.</summary>
        [RequiresUnreferencedCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApi.WebApiModuleBase.ReflectionMessage)]
        public static EndpointBuilder MapOptions(this IWebServer server, string route, Delegate handler) =>
            server
                .GetEndpointModule()
                .Root.MapOptions(route, handler);
    }
}
