using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Authorization;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// <para>The routes of a Web API controller type, with binding and invocation code
    /// produced at build time by the EmbedIO-Neo source generator.</para>
    /// <para>Obtain instances from a class marked with <see cref="GeneratedControllerBindingAttribute"/>,
    /// and register them with <see cref="WebApiModule.RegisterController{TController}(WebApiControllerBinding{TController})"/>
    /// or <see cref="WebApiModuleExtensions.WithController{TController}(WebApiModule, WebApiControllerBinding{TController})"/>.
    /// Registration through a binding uses no reflection or runtime code generation,
    /// so it works in trimmed and Native AOT applications.</para>
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    public sealed class WebApiControllerBinding<TController>
        where TController : WebApiController
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiControllerBinding{TController}"/> class.
        /// This constructor supports generated code and is not intended to be called directly.
        /// </summary>
        /// <param name="routes">The controller's routes, in registration order.</param>
        /// <exception cref="ArgumentNullException"><paramref name="routes"/> or one of its elements is <see langword="null"/>.</exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public WebApiControllerBinding(params WebApiRouteBinding<TController>[] routes)
        {
            routes = Validate.NotNull(nameof(routes), routes);
            var copy = new WebApiRouteBinding<TController>[routes.Length];
            for (var i = 0; i < routes.Length; i++)
                copy[i] = routes[i] ?? throw new ArgumentNullException(nameof(routes), "A route binding is null.");

            Routes = copy;
        }

        /// <summary>
        /// Gets the controller's routes, in registration order.
        /// </summary>
        public IReadOnlyList<WebApiRouteBinding<TController>> Routes { get; }
    }

    /// <summary>
    /// One route of a controller method, as produced by the EmbedIO-Neo source generator.
    /// This type supports generated code and is not intended to be used directly.
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class WebApiRouteBinding<TController>
        where TController : WebApiController
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiRouteBinding{TController}"/> class.
        /// </summary>
        /// <param name="verb">The HTTP verb of the route attribute.</param>
        /// <param name="route">The route of the route attribute.</param>
        /// <param name="isBaseRoute"><see langword="true"/> for a base route.</param>
        /// <param name="handlerId">A string that identifies the controller method, used to tell
        /// aliases of one method from routes of different methods.</param>
        /// <param name="createInvoker">Creates the invoker for one registration.</param>
        /// <exception cref="ArgumentNullException"><paramref name="route"/>, <paramref name="handlerId"/>
        /// or <paramref name="createInvoker"/> is <see langword="null"/>.</exception>
        public WebApiRouteBinding(
            HttpVerbs verb,
            string route,
            bool isBaseRoute,
            string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiRouteBinding{TController}"/> class,
        /// with a description of the controller method for OpenAPI documents.
        /// </summary>
        /// <param name="verb">The HTTP verb of the route attribute.</param>
        /// <param name="route">The route of the route attribute.</param>
        /// <param name="isBaseRoute"><see langword="true"/> for a base route.</param>
        /// <param name="handlerId">A string that identifies the controller method, used to tell
        /// aliases of one method from routes of different methods.</param>
        /// <param name="createInvoker">Creates the invoker for one registration.</param>
        /// <param name="operation">The controller method's description, or <see langword="null"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="route"/>, <paramref name="handlerId"/>
        /// or <paramref name="createInvoker"/> is <see langword="null"/>.</exception>
        public WebApiRouteBinding(
            HttpVerbs verb,
            string route,
            bool isBaseRoute,
            string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker,
            WebApiOperation? operation)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, operation, operation?.Authorization)
        {
        }

        /// <summary>Creates a generated route binding with immutable authorization metadata.</summary>
        /// <param name="verb">The HTTP verb.</param>
        /// <param name="route">The route template.</param>
        /// <param name="isBaseRoute">Whether this is a base route.</param>
        /// <param name="handlerId">The method identity.</param>
        /// <param name="createInvoker">The invoker factory.</param>
        /// <param name="operation">Optional OpenAPI metadata.</param>
        /// <param name="authorization">Optional authorization metadata.</param>
        public WebApiRouteBinding(HttpVerbs verb, string route, bool isBaseRoute, string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker, WebApiOperation? operation, AuthorizationMetadata? authorization)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, operation, authorization, true)
        {
        }

        /// <summary>Creates a route binding with explicit controller preprocessing ownership.</summary>
        /// <param name="verb">The HTTP verb.</param>
        /// <param name="route">The route template.</param>
        /// <param name="isBaseRoute">Whether this is a base route.</param>
        /// <param name="handlerId">The method identity.</param>
        /// <param name="createInvoker">The invoker factory.</param>
        /// <param name="operation">Optional OpenAPI metadata.</param>
        /// <param name="authorization">Optional authorization metadata.</param>
        /// <param name="useControllerPreprocessing">Whether the runtime calls the legacy controller hook.
        /// False means the generated invoker owns all preprocessing.</param>
        public WebApiRouteBinding(HttpVerbs verb, string route, bool isBaseRoute, string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker, WebApiOperation? operation, AuthorizationMetadata? authorization,
            bool useControllerPreprocessing)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, operation, authorization, useControllerPreprocessing, null)
        {
        }

        /// <summary>Creates a route binding with explicit browser token policy metadata.</summary>
        /// <param name="verb">The HTTP verb.</param>
        /// <param name="route">The route template.</param>
        /// <param name="isBaseRoute">Whether this is a base route.</param>
        /// <param name="handlerId">The method identity.</param>
        /// <param name="createInvoker">The invoker factory.</param>
        /// <param name="operation">Optional OpenAPI metadata.</param>
        /// <param name="authorization">Optional authorization metadata.</param>
        /// <param name="useControllerPreprocessing">Whether the runtime invokes controller preprocessing.</param>
        /// <param name="browserProtection">Optional per-action token requirement; null uses the configured default.</param>
        public WebApiRouteBinding(HttpVerbs verb, string route, bool isBaseRoute, string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker, WebApiOperation? operation, AuthorizationMetadata? authorization,
            bool useControllerPreprocessing, Security.BrowserProtectionMode? browserProtection)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, operation, authorization, useControllerPreprocessing, browserProtection, null)
        {
        }

        /// <summary>Creates a route binding with opt-in named output caching.</summary>
        /// <param name="verb">The HTTP verb.</param>
        /// <param name="route">The route template.</param>
        /// <param name="isBaseRoute">Whether this is a base route.</param>
        /// <param name="handlerId">The method identity.</param>
        /// <param name="createInvoker">The invoker factory.</param>
        /// <param name="operation">Optional OpenAPI metadata.</param>
        /// <param name="authorization">Optional authorization metadata.</param>
        /// <param name="useControllerPreprocessing">Whether the runtime invokes controller preprocessing.</param>
        /// <param name="browserProtection">Optional browser policy.</param>
        /// <param name="outputCachePolicy">Optional named output cache policy.</param>
        public WebApiRouteBinding(HttpVerbs verb, string route, bool isBaseRoute, string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker, WebApiOperation? operation, AuthorizationMetadata? authorization,
            bool useControllerPreprocessing, Security.BrowserProtectionMode? browserProtection, string? outputCachePolicy)
            : this(verb, route, isBaseRoute, handlerId, createInvoker, operation, authorization, useControllerPreprocessing, browserProtection, outputCachePolicy, null)
        {
        }

        /// <summary>Creates a route with optional output-cache and finite write-idempotency declarations.</summary>
        /// <param name="verb">HTTP verb.</param>
        /// <param name="route">Route template.</param>
        /// <param name="isBaseRoute">Whether the route is a base route.</param>
        /// <param name="handlerId">Method identity.</param>
        /// <param name="createInvoker">Invoker factory.</param>
        /// <param name="operation">Operation metadata.</param>
        /// <param name="authorization">Authorization declaration.</param>
        /// <param name="useControllerPreprocessing">Whether runtime preprocessing is required.</param>
        /// <param name="browserProtection">Browser protection declaration.</param>
        /// <param name="outputCachePolicy">Output-cache policy.</param>
        /// <param name="idempotencyPolicy">Finite write-idempotency policy.</param>
        public WebApiRouteBinding(HttpVerbs verb, string route, bool isBaseRoute, string handlerId,
            Func<WebApiRouteInvoker<TController>> createInvoker, WebApiOperation? operation, AuthorizationMetadata? authorization,
            bool useControllerPreprocessing, Security.BrowserProtectionMode? browserProtection, string? outputCachePolicy, string? idempotencyPolicy)
        {
            IdempotencyPolicy = idempotencyPolicy;
            OutputCachePolicy = outputCachePolicy;
            if (browserProtection.HasValue && browserProtection != Security.BrowserProtectionMode.RequireToken
                && browserProtection != Security.BrowserProtectionMode.IgnoreToken) throw new ArgumentOutOfRangeException(nameof(browserProtection));
            BrowserProtection = browserProtection;
            UseControllerPreprocessing = useControllerPreprocessing;
            if (operation != null && !ReferenceEquals(operation.Authorization, authorization))
                throw new ArgumentException("Route and operation must share the same authorization metadata.", nameof(authorization));
            Verb = verb;
            Route = Validate.NotNull(nameof(route), route);
            IsBaseRoute = isBaseRoute;
            HandlerId = Validate.NotNull(nameof(handlerId), handlerId);
            CreateInvoker = Validate.NotNull(nameof(createInvoker), createInvoker);
            Operation = operation;
            Authorization = authorization;
        }

        /// <summary>Gets the explicitly selected output-cache policy, or null.</summary>
        public string? OutputCachePolicy { get; }

        /// <summary>Gets the explicitly selected idempotency policy, or null.</summary>
        public string? IdempotencyPolicy { get; }

        /// <summary>Gets whether the runtime calls the legacy controller hook before the invoker.</summary>
        public bool UseControllerPreprocessing { get; }

        /// <summary>Gets the token requirement applied before controller activation; null uses the module default.</summary>
        public Security.BrowserProtectionMode? BrowserProtection { get; }

        /// <summary>Gets authorization enforced before controller activation.</summary>
        public AuthorizationMetadata? Authorization { get; }

        /// <summary>Gets the controller method's description for OpenAPI documents, if the generator provided one.</summary>
        public WebApiOperation? Operation { get; }

        /// <summary>Gets the HTTP verb of the route.</summary>
        public HttpVerbs Verb { get; }

        /// <summary>Gets the route.</summary>
        public string Route { get; }

        /// <summary>Gets a value indicating whether the route is a base route.</summary>
        public bool IsBaseRoute { get; }

        /// <summary>Gets the string that identifies the controller method.</summary>
        public string HandlerId { get; }

        /// <summary>Gets the factory of the route's invoker.</summary>
        public Func<WebApiRouteInvoker<TController>> CreateInvoker { get; }
    }

    /// <summary>
    /// Binds a request to a controller method's parameters, calls the method and starts
    /// serializing its result. This delegate supports generated code and is not intended to be used directly.
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    /// <param name="controller">The controller, already initialized for the request.</param>
    /// <param name="context">The HTTP context.</param>
    /// <param name="route">The matched route.</param>
    /// <param name="serializer">The module's response serializer.</param>
    /// <returns>A task that completes once the parameters are bound and the method has been called;
    /// its result is the task returned by the method, or the task serializing the method's result.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate Task<Task> WebApiRouteInvoker<in TController>(
        TController controller,
        IHttpContext context,
        RouteMatch route,
        ResponseSerializerCallback serializer)
        where TController : WebApiController;
}
