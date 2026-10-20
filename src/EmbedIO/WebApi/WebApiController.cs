using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Sessions;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// Inherit from this class and define your own Web API methods
    /// You must RegisterController in the Web API Module to make it active.
    /// </summary>
    public abstract class WebApiController
    {
        // Request activation initializes these properties after construction.
        // Earlier access reports that the controller has not been activated.

        private IHttpContext? _httpContext;
        private RouteMatch? _route;

        // Cache state lives only on explicitly opted-in requests. Ordinary controller
        // instances retain their original size, and unused lazy Items stays unallocated.
        private static readonly object OutputCacheKey = new();

        private sealed class OutputCacheState
        {
            internal readonly OutputCacheRegistration Registration;
            internal bool Disabled;
            internal Func<Task>? Completion;

            internal OutputCacheState(OutputCacheRegistration registration) => Registration = registration;
        }

        private OutputCacheState? CacheState =>
            _httpContext == null || _httpContext is ILazyItems { HasItems: false }
                ? null
                : _httpContext.Items.TryGetValue(OutputCacheKey, out var value) ? value as OutputCacheState : null;

        internal OutputCacheRegistration? OutputCaching
        {
            get => CacheState?.Registration;
            set
            {
                if (value != null) HttpContext.Items[OutputCacheKey] = new OutputCacheState(value);

                else if (CacheState != null) HttpContext.Items.Remove(OutputCacheKey);
            }
        }

        internal bool OutputCacheDisabled
        {
            get => CacheState?.Disabled == true;
            set
            {
                if (CacheState is { } state) state.Disabled = value;
            }
        }

        internal Func<Task>? OutputCacheCompletion => CacheState?.Completion;

        internal Func<Task> ConfigureOutputCacheRelease(
            Func<IHttpContext, WebApiController, Task> release,
            IHttpContext context
        )
        {
            var state = CacheState ?? throw new InvalidOperationException("No output-cache invocation is configured.");
            Task? finished = null;
            // The async wrapper retains synchronous callback faults in the same task,
            // so outer cleanup can await completion without invoking release twice.
            Task once() => finished ??= ReleaseAsync();
            state.Completion = once;
            return once;
            async Task ReleaseAsync() => await release(context, this).ConfigureAwait(false);
        }

        /// <summary>
        /// <para>Gets the HTTP context.</para>
        /// <para>This property is automatically initialized upon controller creation.</para>
        /// </summary>
        public IHttpContext HttpContext
        {
            get =>
                _httpContext
                    ?? throw new System.InvalidOperationException(
                        "The controller has not been initialized for a request."
                    );
            internal set => _httpContext = value;
        }

        internal void UseCapturedContext(IHttpContext context) => _httpContext = context;

        /// <summary>
        /// <para>Gets the resolved route.</para>
        /// <para>This property is automatically initialized upon controller creation.</para>
        /// </summary>
        public RouteMatch Route
        {
            get =>
                _route
                    ?? throw new System.InvalidOperationException(
                        "The controller has not been initialized for a request."
                    );
            internal set => _route = value;
        }

        /// <summary>
        /// Gets the <see cref="CancellationToken" /> used to cancel processing of the request.
        /// </summary>
        public CancellationToken CancellationToken => HttpContext.CancellationToken;

        /// <summary>
        /// Gets the HTTP request.
        /// </summary>
        public IHttpRequest Request => HttpContext.Request;

        /// <summary>
        /// Gets the HTTP response object.
        /// </summary>
        public IHttpResponse Response => HttpContext.Response;

        /// <summary>
        /// Gets the user.
        /// </summary>
        public IPrincipal? User => HttpContext.User;

        /// <summary>
        /// Gets the session proxy associated with the HTTP context.
        /// </summary>
        public ISessionProxy Session
        {
            get
            {
                OutputCacheDisabled = true;
                return HttpContext.Session;
            }
        }

        /// <summary>
        /// <para>This method is meant to be called internally by EmbedIO.</para>
        /// <para>Derived classes can override the <see cref="OnBeforeHandler"/> method
        /// to perform common operations before any handler gets called.</para>
        /// </summary>
        /// <seealso cref="OnBeforeHandler"/>
        public void PreProcessRequest() => OnBeforeHandler();

        /// <summary>
        /// <para>Called before a handler to perform common operations.</para>
        /// <para>The default behavior is to set response headers
        /// in order to prevent caching of the response.</para>
        /// </summary>
        protected virtual void OnBeforeHandler()
        {
            if (OutputCaching == null || !Caching.OutputCache.Eligible(HttpContext))
                HttpContext.Response.DisableCaching();
        }
    }
}
