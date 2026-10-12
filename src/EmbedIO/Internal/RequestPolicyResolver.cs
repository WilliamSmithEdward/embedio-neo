using System;
using EmbedIO.Net.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Internal
{
    // One immutable effective policy per locked owner/route. Requests borrow the
    // same object at listener admission and pipeline deadline application.
    internal sealed class RequestPolicySnapshot
    {
        internal RequestPolicySnapshot(WebServerOptionsBase options, RequestLimitPolicy? route)
        {
            var maximum = RequestLimitPolicy.Resolve(route?.MaximumRequestBodyBytes, options.MaximumRequestBodyBytes);
            BodyPolicy = maximum is long bytes
                ? new RequestBodyPolicy(bytes, options.MaximumRequestHeaderBytes, options.MaximumRequestHeaderCount) : null;
            BodyReadTimeout = RequestLimitPolicy.Resolve(route?.RequestBodyReadTimeout, options.RequestBodyReadTimeout);
            RequestTimeout = RequestLimitPolicy.Resolve(route?.RequestTimeout, options.RequestTimeout);
        }
        internal RequestBodyPolicy? BodyPolicy { get; }
        internal TimeSpan? BodyReadTimeout { get; }
        internal TimeSpan? RequestTimeout { get; }
        internal static RequestPolicySnapshot? CapturedBy(IHttpRequest request)
            => request switch
            {
                HttpListenerRequest http1 => http1.CapturedRequestPolicy,
                MultiplexedRequest multiplexed => multiplexed.CapturedRequestPolicy,
                _ => null,
            };
    }

    internal sealed class RequestPolicyResolver
    {
        private readonly WebServerOptionsBase _options;
        private readonly RequestPolicySnapshot _default;
        private readonly (RequestLimitPolicy Route, RequestPolicySnapshot Effective)[] _routes;
        private RequestPolicyResolver(WebServerOptionsBase options)
        {
            if (!options.ConfigurationLocked) throw new InvalidOperationException("Request policies require locked options.");
            _options = options;
            _default = new RequestPolicySnapshot(options, null);
            _routes = new (RequestLimitPolicy, RequestPolicySnapshot)[options.RouteRequestLimits.Count];
            for (var i = 0; i < _routes.Length; i++)
            {
                var route = options.RouteRequestLimits[i];
                _routes[i] = (route, new RequestPolicySnapshot(options, route));
            }
        }
        internal static RequestPolicyResolver? Create(WebServerOptionsBase options)
            => options.MaximumRequestBodyBytes == null && options.RouteRequestLimits.Count == 0
                ? null : new RequestPolicyResolver(options);

        private RequestPolicySnapshot Resolve(string path)
        {
            // Use the accepted matcher and first-match order, not a second route
            // interpretation. Locked policies cannot change between these stages.
            var route = _options.FindRouteRequestLimits(path);
            if (route == null) return _default;
            foreach (var captured in _routes)
                if (ReferenceEquals(captured.Route, route)) return captured.Effective;
            throw new InvalidOperationException("The locked request policy snapshot changed.");
        }
        internal void Apply(IHttpContext context)
        {
            // WebServer later establishes RequestedPath using this exact
            // normalization. Admission precedes that public Route assignment.
            var effective = Resolve(UrlPath.Normalize(context.Request.Url.AbsolutePath, false));
            if (context.Request is HttpListenerRequest http1) http1.CaptureRequestPolicy(effective);
            else if (context.Request is MultiplexedRequest multiplexed) multiplexed.CaptureRequestPolicy(effective);
            else throw new NotSupportedException("Request policy admission requires an owned request backend.");
            effective.BodyPolicy?.Apply(context.Request);
        }
    }
}
