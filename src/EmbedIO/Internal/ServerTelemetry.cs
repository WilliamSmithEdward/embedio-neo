using System;
#if NET10_0_OR_GREATER
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Threading;
#endif

namespace EmbedIO.Internal
{
    // Server-side HTTP metrics and spans, following the OpenTelemetry HTTP semantic
    // conventions where they define an instrument. The meter and activity source are
    // shared; each server tags its measurements with its port and optional name. Built on
    // the .NET 10 BCL only.
    internal sealed class ServerTelemetry
    {
        internal const string SourceName = "EmbedIO.Server";

#if NET10_0_OR_GREATER
        private static readonly string? Version = typeof(ServerTelemetry).Assembly.GetName().Version?.ToString();
        private static readonly Meter Meter = new(SourceName, Version);
        private static readonly ActivitySource Source = new(SourceName, Version);
        // Bucket boundaries recommended by the HTTP semantic conventions.
        private static readonly double[] DurationBuckets = { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 };
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
            "http.server.request.duration", "s", "Duration of HTTP server requests.", null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        private static readonly UpDownCounter<long> ActiveRequests = Meter.CreateUpDownCounter<long>(
            "http.server.active_requests", "{request}", "Number of active HTTP server requests.");
        private static readonly Histogram<long> RequestBodySize = Meter.CreateHistogram<long>(
            "http.server.request.body.size", "By", "Size of HTTP server request bodies read by the application.");
        private static readonly Histogram<long> ResponseBodySize = Meter.CreateHistogram<long>(
            "http.server.response.body.size", "By", "Size of HTTP server response bodies sent.");
        private static readonly UpDownCounter<long> ActiveConnections = Meter.CreateUpDownCounter<long>(
            "embedio.server.active_connections", "{connection}", "Number of open TCP and QUIC connections.");
        private static readonly Histogram<double> ConnectionDuration = Meter.CreateHistogram<double>(
            "embedio.server.connection.duration", "s", "Duration of TCP and QUIC connections.");
        private static readonly UpDownCounter<long> ActiveWebSockets = Meter.CreateUpDownCounter<long>(
            "embedio.server.active_websockets", "{connection}", "Number of open WebSocket connections.");
        private static readonly Histogram<double> WebSocketDuration = Meter.CreateHistogram<double>(
            "embedio.server.websocket.duration", "s", "Duration of WebSocket connections.");

        private readonly string? _serverName;

        private ServerTelemetry(string? serverName) => _serverName = serverName;

        internal static ServerTelemetry Create(string? serverName) => new(serverName);

        // Starts recording one request. Never throws: a failing observer cannot fail a request.
        internal RequestTelemetry Start(IHttpContextImpl context)
        {
            var request = context.Request;
            var scope = new RequestTelemetry
            {
                Owner = this,
                Started = Stopwatch.GetTimestamp(),
                Method = NormalizeMethod(request.HttpMethod),
                Scheme = request.Url.Scheme,
                Port = context.LocalEndPoint.Port,
                ServerName = _serverName,
            };
            if (context is ITelemetryContext owner) owner.Telemetry = scope;
            try
            {
                // CONNECT carries tunnel data, not a request body.
                if (request.HasEntityBody && request.HttpMethod != "CONNECT" && request is IRequestPolicyTarget target)
                {
                    target.CountBody(scope);
                    scope.HasRequestBody = true;
                }
                var tags = CommonTags(scope);
                ActiveRequests.Add(1, tags);
                scope.Counted = true;
                if (Source.HasListeners())
                {
                    var parent = ExtractParent(request);
                    var activity = Source.StartActivity(scope.Method, ActivityKind.Server, parent);
                    if (activity != null)
                    {
                        if (activity.IsAllDataRequested)
                        {
                            activity.SetTag("http.request.method", scope.Method);
                            if (scope.Method == "_OTHER") activity.SetTag("http.request.method_original", request.HttpMethod);
                            activity.SetTag("url.scheme", scope.Scheme);
                            activity.SetTag("url.path", request.Url.AbsolutePath);
                            activity.SetTag("server.port", scope.Port);
                            activity.SetTag("network.protocol.version", ProtocolVersion(request.ProtocolVersion));
                            activity.SetTag("client.address", request.RemoteEndPoint.Address.ToString());
                            if (!string.IsNullOrEmpty(request.UserAgent)) activity.SetTag("user_agent.original", request.UserAgent);
                            if (scope.ServerName != null) activity.SetTag("embedio.server.name", scope.ServerName);
                        }
                        scope.Activity = activity;
                    }
                }
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { }
            return scope;
        }

        // Records the completed request: its response is closed, streamed bodies included.
        internal void Stop(IHttpContextImpl context, RequestTelemetry scope)
        {
            if (context is ITelemetryContext owner) owner.Telemetry = null;
            try
            {
                var status = context.Response.StatusCode;
                // A request no module handled has no route.
                var route = context.IsHandled ? scope.Route : null;
                var error = scope.ErrorType ?? (status >= 500 ? status.ToString(CultureInfo.InvariantCulture) : null);
                if (scope.Counted) ActiveRequests.Add(-1, CommonTags(scope));

                var tags = CommonTags(scope);
                tags.Add("network.protocol.name", "http");
                tags.Add("network.protocol.version", ProtocolVersion(context.Request.ProtocolVersion));
                tags.Add("http.response.status_code", status);
                if (route != null) tags.Add("http.route", route);
                if (error != null) tags.Add("error.type", error);
                Duration.Record(Stopwatch.GetElapsedTime(scope.Started).TotalSeconds, tags);
                if (scope.HasRequestBody) RequestBodySize.Record(scope.RequestBodyBytes, tags);
                ResponseBodySize.Record(SentBodyBytes(context), tags);

                if (scope.Activity is Activity activity)
                {
                    if (route != null) activity.DisplayName = scope.Method + " " + route;
                    if (activity.IsAllDataRequested)
                    {
                        activity.SetTag("http.response.status_code", status);
                        if (route != null) activity.SetTag("http.route", route);
                        if (error != null) activity.SetTag("error.type", error);
                    }
                    if (error != null) activity.SetStatus(ActivityStatusCode.Error);
                    activity.Stop();
                }
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { }
        }

        // Starts recording one TCP or QUIC connection. Returns null if the observer failed.
        internal LifetimeTelemetry? ConnectionStarted(string transport, int port)
        {
            var tags = new TagList { { "network.transport", transport }, { "server.port", port } };
            if (_serverName != null) tags.Add("embedio.server.name", _serverName);
            try
            {
                ActiveConnections.Add(1, tags);
                return new LifetimeTelemetry(tags, false);
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return null; }
        }

        // Starts recording one accepted WebSocket. Returns null if the observer failed.
        internal LifetimeTelemetry? WebSocketStarted(RequestTelemetry request)
        {
            var tags = CommonTags(request);
            if (request.Route != null) tags.Add("http.route", request.Route);
            try
            {
                ActiveWebSockets.Add(1, tags);
                return new LifetimeTelemetry(tags, true);
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return null; }
        }

        private static long SentBodyBytes(IHttpContextImpl context) => context switch
        {
            Net.Internal.HttpListenerContext http1 => http1.HttpListenerResponse.SentBodyBytes,
            Net.Internal.MultiplexedContext multiplexed => multiplexed.SentBodyBytes,
            _ => 0,
        };

        private static TagList CommonTags(RequestTelemetry scope)
        {
            var tags = new TagList
            {
                { "http.request.method", scope.Method },
                { "url.scheme", scope.Scheme },
                { "server.port", scope.Port },
            };
            if (scope.ServerName != null) tags.Add("embedio.server.name", scope.ServerName);
            return tags;
        }

        // Unknown methods are reported as _OTHER so a client cannot create new dimensions.
        private static string NormalizeMethod(string method) => method switch
        {
            "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "CONNECT" or "OPTIONS" or "TRACE" or "PATCH" or "QUERY" => method,
            _ => "_OTHER",
        };

        private static string ProtocolVersion(Version version)
            => version.Major switch { 2 => "2", 3 => "3", _ => version.Minor == 0 ? "1.0" : "1.1" };

        // W3C trace context (traceparent and tracestate) through the configured propagator.
        private static ActivityContext ExtractParent(IHttpRequest request)
        {
            DistributedContextPropagator.Current.ExtractTraceIdAndState(request, static (object? carrier, string name, out string? value, out IEnumerable<string>? values) =>
            {
                values = null;
                value = carrier is IHttpRequest source ? source.Headers[name] : null;
            }, out var traceParent, out var traceState);
            return traceParent != null && ActivityContext.TryParse(traceParent, traceState, true, out var parent) ? parent : default;
        }

        // A connection or WebSocket being counted. Stop records it exactly once.
        internal sealed class LifetimeTelemetry
        {
            private readonly TagList _tags;
            private readonly bool _webSocket;
            private readonly long _started = Stopwatch.GetTimestamp();
            private int _stopped;

            internal LifetimeTelemetry(TagList tags, bool webSocket)
            {
                _tags = tags;
                _webSocket = webSocket;
            }

            internal void Stop(string? protocolVersion = null)
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                try
                {
                    (_webSocket ? ActiveWebSockets : ActiveConnections).Add(-1, _tags);
                    var tags = _tags;
                    if (protocolVersion != null) tags.Add("network.protocol.version", protocolVersion);
                    (_webSocket ? WebSocketDuration : ConnectionDuration).Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { }
            }
        }
#else
        private ServerTelemetry() { }

        internal static ServerTelemetry Create(string? serverName)
            => throw new PlatformNotSupportedException("Telemetry requires the .NET 10 asset of EmbedIO.");

        internal RequestTelemetry Start(IHttpContextImpl context) => throw new PlatformNotSupportedException();

        internal void Stop(IHttpContextImpl context, RequestTelemetry scope) => throw new PlatformNotSupportedException();

        internal LifetimeTelemetry? ConnectionStarted(string transport, int port) => throw new PlatformNotSupportedException();

        internal LifetimeTelemetry? WebSocketStarted(RequestTelemetry request) => throw new PlatformNotSupportedException();

        internal sealed class LifetimeTelemetry
        {
            private LifetimeTelemetry() { }

            internal void Stop(string? protocolVersion = null) { }
        }
#endif

        // Exception filter target: records the exception type and never handles it. HTTP
        // exceptions are intentional responses; their status code decides error.type.
        internal static bool Observe(RequestTelemetry? scope, Exception exception)
        {
            if (scope != null && scope.ErrorType == null && exception is not IHttpException)
                scope.ErrorType = exception.GetType().FullName;
            return false;
        }

        // Exceptions that escape after handling come from completion and cleanup. Cancellation
        // there is server shutdown or a finished request's flush, not a failed request; a
        // handler's own cancellation was already observed inside dispatch.
        internal static bool ObserveCleanup(RequestTelemetry? scope, Exception exception)
            => exception is not OperationCanceledException && Observe(scope, exception);
    }

    internal sealed partial class RequestTelemetry
    {
        private long _requestBodyBytes;

        internal ServerTelemetry? Owner { get; set; }
        internal long Started { get; set; }
        internal string Method { get; set; } = "_OTHER";
        internal string Scheme { get; set; } = "http";
        internal int Port { get; set; }
        internal string? ServerName { get; set; }
        internal string? ErrorType { get; set; }
        internal bool Counted { get; set; }
        internal bool HasRequestBody { get; set; }
        internal long RequestBodyBytes => System.Threading.Volatile.Read(ref _requestBodyBytes);
        internal void AddRequestBodyBytes(int count) => System.Threading.Interlocked.Add(ref _requestBodyBytes, count);
#if NET10_0_OR_GREATER
        internal System.Diagnostics.Activity? Activity { get; set; }
#endif
    }
}
