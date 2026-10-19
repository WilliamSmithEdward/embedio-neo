using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.WebApi;

namespace EmbedIO.Endpoints
{
    /// <summary>A reusable generated endpoint binding. Obtain it from a class marked with GeneratedEndpointBinding.</summary>
    public sealed class EndpointHandler
    {
        internal readonly Func<WebApiModuleBase, RouteMatcher, WebApiRouteInvoker<WebApiController>> CreateInvoker;
        internal readonly Authorization.AuthorizationMetadata? Authorization;
        internal readonly BrowserProtectionMode? Browser;
        internal readonly string? Cache;
        internal readonly string? Idempotency;
        internal readonly string? SecurityHeaders;

        /// <summary>Creates a generated endpoint descriptor; intended for source-generated code.</summary>
        /// <param name="createInvoker">Registration-time binding plan factory.</param>
        /// <param name="operation">Generated operation description.</param>
        /// <param name="browser">Optional browser token policy.</param>
        /// <param name="cache">Optional output-cache policy.</param>
        /// <param name="idempotency">Optional idempotency policy.</param>
        /// <param name="securityHeaders">Optional security-header policy.</param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public EndpointHandler(
            Func<WebApiRouteInvoker<WebApiController>> createInvoker,
            WebApiOperation operation,
            BrowserProtectionMode? browser = null,
            string? cache = null,
            string? idempotency = null,
            string? securityHeaders = null
        )
        {
            createInvoker = global::EmbedIO.Utilities.Validate.NotNull(nameof(createInvoker), createInvoker);

            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            CreateInvoker = (_, _) => createInvoker();
            Authorization = operation.Authorization;
            Browser = browser;
            Cache = cache;
            Idempotency = idempotency;
            SecurityHeaders = securityHeaders;
        }

        /// <summary>Prepares a reusable delegate descriptor. Binding is compiled once for each mapped route.</summary>
        /// <param name="handler">A single application-owned delegate. Captured targets must support concurrent requests.</param>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        public static EndpointHandler FromDelegate(Delegate handler) => WebApiModuleBase.DescribeEndpoint(handler);

        internal EndpointHandler(
            Func<WebApiModuleBase, RouteMatcher, WebApiRouteInvoker<WebApiController>> create,
            WebApiOperation operation,
            Authorization.AuthorizationMetadata? authorization,
            BrowserProtectionMode? browser,
            string? cache,
            string? idempotency,
            string? securityHeaders
        )
        {
            CreateInvoker = create;
            Operation = operation;
            Authorization = authorization;
            Browser = browser;
            Cache = cache;
            Idempotency = idempotency;
            SecurityHeaders = securityHeaders;
        }

        /// <summary>Gets the handler's generated or reflected input/output description.</summary>
        public WebApiOperation Operation { get; }
    }

    /// <summary>Generates reflection-free endpoint descriptors for public static methods on a partial class.
    /// Each method M receives a generated MEndpoint property. Private helpers are ignored.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class GeneratedEndpointBindingAttribute : Attribute
    {
        /// <summary>Gets or sets the System.Text.Json source-generated context for JSON request bodies.</summary>
        public Type? JsonSerializerContext { get; set; }
    }
}
