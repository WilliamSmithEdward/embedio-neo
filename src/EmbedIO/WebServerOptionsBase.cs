using System;
using EmbedIO.Configuration;

namespace EmbedIO
{
    /// <summary>
    /// Base class for web server options.
    /// </summary>
    public abstract class WebServerOptionsBase : ConfiguredObject
    {
        // Finite defaults, modelled on Kestrel's KestrelServerLimits in ASP.NET Core 10.0.13:
        // MaxRequestBodySize, MaxRequestHeadersTotalSize and MaxRequestHeaderCount. Kestrel has
        // no per-read body deadline; it enforces a minimum data rate instead.
        internal const long DefaultMaximumRequestBodyBytes = 30_000_000;
        internal const int DefaultMaximumRequestHeaderBytes = 32 * 1024;
        internal const int DefaultMaximumRequestHeaderCount = 100;
        internal static readonly TimeSpan DefaultRequestBodyReadTimeout = TimeSpan.FromSeconds(60);

        private bool _supportCompressedRequests;
        private bool _decompressedLimitSet;
        private long? _maximumDecompressedRequestBodyBytes;

        /// <summary>
        /// <para>Gets or sets a value indicating whether compressed request bodies are supported.</para>
        /// <para>The default value is <see langword="false"/>, because of the security risk
        /// posed by <see href="https://en.wikipedia.org/wiki/Zip_bomb">decompression bombs</see>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set and this instance's
        /// configuration is locked.</exception>
        public bool SupportCompressedRequests
        {
            get => _supportCompressedRequests;
            set
            {
                EnsureConfigurationNotLocked();
                _supportCompressedRequests = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum decoded bytes read from a compressed request body.
        /// Until it is set, the limit is the request's body limit: <see cref="MaximumRequestBodyBytes"/>,
        /// or the limit of the route policy that applies to the request, and this property
        /// returns <see cref="MaximumRequestBodyBytes"/>. Setting a value, including null,
        /// replaces that default. Null removes the limit. Zero permits only an empty decoded
        /// body. Excess content raises HTTP 413.
        /// </summary>
        /// <remarks>This applies to request-stream helpers when compressed requests are enabled.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public long? MaximumDecompressedRequestBodyBytes
        {
            get => _decompressedLimitSet ? _maximumDecompressedRequestBodyBytes : _maximumRequestBodyBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumDecompressedRequestBodyBytes = value;
                _decompressedLimitSet = true;
            }
        }

        // The decoded limit of a request whose effective body limit is bodyLimit, null
        // when the body limit is lifted: the configured value, or else that body limit.
        internal long? DecompressedLimitFor(long? bodyLimit)
            => _decompressedLimitSet ? _maximumDecompressedRequestBodyBytes : bodyLimit;

        private long? _maximumRequestBodyBytes = DefaultMaximumRequestBodyBytes;
        private int? _maximumConcurrentRequests;
        private int _requestQueueLimit;
        private TimeSpan? _requestTimeout;
        private int? _maximumRequestHeaderBytes = DefaultMaximumRequestHeaderBytes;
        private int? _maximumRequestHeaderCount = DefaultMaximumRequestHeaderCount;
        private TimeSpan? _requestBodyReadTimeout = DefaultRequestBodyReadTimeout;
        private bool _enableTelemetry;
        private string? _telemetryServerName;

        /// <summary>
        /// Gets or sets a value indicating whether the server emits HTTP metrics and server spans
        /// through <c>System.Diagnostics</c>. The default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>When enabled, the server publishes the <c>EmbedIO.Server</c> meter and activity
        /// source, following the OpenTelemetry HTTP semantic conventions. Nothing is exported
        /// unless the application listens, for example through an OpenTelemetry SDK.</para>
        /// <para>Telemetry requires the .NET 10 asset; a server built on the .NET Standard 2.0
        /// asset throws <see cref="PlatformNotSupportedException"/> when it is enabled.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public bool EnableTelemetry
        {
            get => _enableTelemetry;
            set
            {
                EnsureConfigurationNotLocked();
                _enableTelemetry = value;
            }
        }

        /// <summary>
        /// Gets or sets a name that identifies this server in its metrics and spans, as the
        /// <c>embedio.server.name</c> attribute. The default is null, which omits the attribute.
        /// </summary>
        /// <remarks>Use a fixed, low-cardinality name such as <c>"public-api"</c>; it becomes a metric dimension.</remarks>
        /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public string? TelemetryServerName
        {
            get => _telemetryServerName;
            set
            {
                EnsureConfigurationNotLocked();
                if (value != null && value.Trim().Length == 0) throw new ArgumentException("The name cannot be empty.", nameof(value));
                _telemetryServerName = value;
            }
        }
        private readonly System.Collections.Generic.List<RequestLimitPolicy> _routeRequestLimits = new();
        private System.Collections.ObjectModel.ReadOnlyCollection<RequestLimitPolicy>? _routeRequestLimitsView;

        /// <summary>
        /// Gets the per-route request limit policies, in the order they were added.
        /// </summary>
        /// <remarks>
        /// The first policy whose base route matches a request replaces the server-wide body,
        /// read and request deadline values for it. Header limits, admission and connection
        /// limits stay server-wide.
        /// </remarks>
        public System.Collections.Generic.IReadOnlyList<RequestLimitPolicy> RouteRequestLimits
            => _routeRequestLimitsView ??= _routeRequestLimits.AsReadOnly();

        /// <summary>
        /// Adds a request limit policy for requests under a base route.
        /// </summary>
        /// <param name="baseRoute">The base route, matched like a module's base route.</param>
        /// <param name="configure">Sets the policy's limits.</param>
        /// <returns>The new policy.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="baseRoute"/> or
        /// <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="baseRoute"/> is not a valid base route
        /// or already has a policy.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public RequestLimitPolicy AddRouteRequestLimits(string baseRoute, Action<RequestLimitPolicy> configure)
        {
            EnsureConfigurationNotLocked();
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var policy = new RequestLimitPolicy(baseRoute);
            foreach (var existing in _routeRequestLimits)
                if (existing.BaseRoute == policy.BaseRoute)
                    throw new ArgumentException("The base route already has a request limit policy.", nameof(baseRoute));
            configure(policy);
            _routeRequestLimits.Add(policy);
            return policy;
        }

        // The first policy that matches the request path, or null.
        internal RequestLimitPolicy? FindRouteRequestLimits(string requestedPath)
        {
            foreach (var policy in _routeRequestLimits)
                if (policy.Matches(requestedPath)) return policy;
            return null;
        }

        // True when any policy sets a body limit, so 100 Continue must wait for the body read.
        internal bool HasBodyLimits
        {
            get
            {
                if (_maximumRequestBodyBytes.HasValue) return true;
                foreach (var policy in _routeRequestLimits)
                    if (policy.MaximumRequestBodyBytes.HasValue) return true;
                return false;
            }
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            base.OnBeforeLockConfiguration();
            foreach (var policy in _routeRequestLimits) policy.Lock();
        }

        /// <summary>
        /// Gets or sets how long one read of a request body may wait for data. The default is
        /// 60 seconds. Null removes the read deadline.
        /// </summary>
        /// <remarks>
        /// <para>The deadline applies to each read of <see cref="IHttpRequest.InputStream"/>
        /// separately, so a slow but steady upload is not cut off; a client that stops sending is.
        /// A read that misses it throws an <see cref="HttpException"/> with status 408, and every
        /// later read of the same body does too. On HTTP/1.x the connection is not reused.</para>
        /// <para>The deadline applies to every protocol and listener mode. <c>CONNECT</c> tunnels
        /// are not request bodies and are not timed. A route policy can replace or lift it.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestBodyReadTimeout
        {
            get => _requestBodyReadTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                if (value.HasValue && (value.Value.TotalMilliseconds < 1 || value.Value.TotalMilliseconds > int.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _requestBodyReadTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of request body bytes a handler may read, counted
        /// as transferred, before any content decoding.
        /// The default is 30,000,000 bytes, about 28.6 MiB. Null removes the limit.
        /// </summary>
        /// <remarks>
        /// <para>A request that declares a larger Content-Length is rejected with HTTP 413 before
        /// any module runs. A body without a declared length, or one longer than declared, fails
        /// with HTTP 413 when a read crosses the limit. On HTTP/1.x the connection is not reused
        /// after either rejection, because the unread remainder of the body cannot be trusted.</para>
        /// <para>Zero permits only requests without body content. When compressed requests are
        /// enabled, <see cref="MaximumDecompressedRequestBodyBytes"/> separately limits decoded
        /// bytes; until it is set, it follows this limit. A route policy can replace or lift the
        /// limit for requests under its base route.</para>
        /// <para>While a limit is set, a request carrying <c>Expect: 100-continue</c> receives its
        /// interim <c>100 Continue</c> response only when the handler first reads the body, so a
        /// refused body is never invited.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public long? MaximumRequestBodyBytes
        {
            get => _maximumRequestBodyBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestBodyBytes = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of requests the server handles at the same time.
        /// The default is null: dispatch is unlimited.
        /// </summary>
        /// <remarks>
        /// <para>A request arriving while every slot is busy waits in a bounded queue of
        /// <see cref="RequestQueueLimit"/> entries. When the queue is also full, the request is
        /// rejected with HTTP 503 before any module runs. The limit applies to all listener modes
        /// and protocol versions and counts each HTTP/2 or HTTP/3 stream as one request.</para>
        /// <para>No default fits every application, so set this for a server exposed to untrusted
        /// clients: the right number depends on what the handlers do.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumConcurrentRequests
        {
            get => _maximumConcurrentRequests;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumConcurrentRequests = value;
            }
        }

        /// <summary>
        /// Gets or sets how many requests may wait for a slot when
        /// <see cref="MaximumConcurrentRequests"/> is reached. The default is zero: a request
        /// that finds no free slot is rejected with HTTP 503 immediately.
        /// </summary>
        /// <remarks>Ignored when <see cref="MaximumConcurrentRequests"/> is null.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int RequestQueueLimit
        {
            get => _requestQueueLimit;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _requestQueueLimit = value;
            }
        }

        /// <summary>
        /// Gets or sets how long a request may be handled before the server cancels its context
        /// token. The default is null: requests have no deadline.
        /// </summary>
        /// <remarks>
        /// <para>The deadline starts when the server begins handling the request, including any time
        /// spent waiting for a <see cref="MaximumConcurrentRequests"/> slot. Cancellation is
        /// cooperative: handlers observe it through <see cref="IHttpContext.CancellationToken"/>.
        /// A handler that ignores the token runs to completion.</para>
        /// <para>When a handler stops because of the deadline before its response is committed,
        /// the server answers HTTP 503. After commitment the response is ended visibly instead.</para>
        /// <para>Long downloads, streamed responses and server-sent events make any single default
        /// wrong for some routes, so set this for a server exposed to untrusted clients and lift
        /// it per route where a request legitimately runs long.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestTimeout
        {
            get => _requestTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                if (value.HasValue && (value.Value <= TimeSpan.Zero || value.Value.TotalMilliseconds > int.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _requestTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum total size of a request's header fields. The default is
        /// 32 KiB (32,768 bytes). Null removes this limit and leaves the parsers' own ceiling.
        /// </summary>
        /// <remarks>
        /// <para>The size is the sum of each received header field's name and value lengths. The
        /// HTTP/1 request line and HTTP/2 or HTTP/3 pseudo-header fields are not counted. A larger
        /// request is rejected with HTTP 431 before any module runs.</para>
        /// <para>Each protocol parser also keeps its fixed 32 KiB ceiling, which applies first;
        /// this option can only lower the effective limit.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumRequestHeaderBytes
        {
            get => _maximumRequestHeaderBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestHeaderBytes = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of header fields a request may carry. The default is
        /// 100. Null removes the limit.
        /// </summary>
        /// <remarks>
        /// Every received field line counts, including repeated names and each HTTP/2 or HTTP/3
        /// cookie field. Pseudo-header fields are not counted. A request with more fields is
        /// rejected with HTTP 431 before any module runs.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumRequestHeaderCount
        {
            get => _maximumRequestHeaderCount;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestHeaderCount = value;
            }
        }

        /// <summary>
        /// Locks this instance, preventing further configuration.
        /// </summary>
        public void Lock() => LockConfiguration();
    }
}
