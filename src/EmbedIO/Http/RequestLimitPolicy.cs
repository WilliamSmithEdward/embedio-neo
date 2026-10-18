using System;
using System.Threading;
using EmbedIO.Configuration;
using EmbedIO.Routing;
using EmbedIO.Utilities;

namespace EmbedIO
{
    /// <summary>
    /// Request limits that replace the server-wide values for requests under one base route.
    /// Add a policy with
    /// <see cref="WebServerOptionsBaseExtensions.WithRouteRequestLimits{TOptions}(TOptions, string, Action{RequestLimitPolicy})"/>.
    /// </summary>
    /// <remarks>
    /// <para>A property left null inherits the server-wide value. To lift a server-wide limit for
    /// this route, set <see cref="MaximumRequestBodyBytes"/> to <see cref="long.MaxValue"/> or a
    /// deadline to <see cref="Timeout.InfiniteTimeSpan"/>.</para>
    /// <para>The base route is matched like a module's base route, against the request path
    /// before any module runs. When several policies match, the first one added applies, so add
    /// more specific routes first. A policy locks with the options that own it.</para>
    /// </remarks>
    public sealed class RequestLimitPolicy : ConfiguredObject
    {
        private readonly RouteMatcher _matcher;
        private long? _maximumRequestBodyBytes;
        private TimeSpan? _requestBodyReadTimeout;
        private TimeSpan? _requestTimeout;
        private MinimumDataRate? _minimumRequestBodyDataRate;

        internal RequestLimitPolicy(string baseRoute)
        {
            BaseRoute = Validate.Route(nameof(baseRoute), baseRoute, true);
            _matcher = RouteMatcher.Parse(BaseRoute, true);
        }

        /// <summary>
        /// Gets the base route this policy applies to.
        /// </summary>
        public string BaseRoute { get; }

        /// <summary>
        /// Gets or sets the maximum request body bytes for this route, replacing
        /// <see cref="WebServerOptionsBase.MaximumRequestBodyBytes"/>. Null inherits the server value;
        /// <see cref="long.MaxValue"/> lifts the limit.
        /// </summary>
        /// <remarks>Unless <see cref="WebServerOptionsBase.MaximumDecompressedRequestBodyBytes"/> is
        /// set, the route's limit also bounds what the compressed-request helpers decode.</remarks>
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
        /// Gets or sets how long one body read may wait for data on this route, replacing
        /// <see cref="WebServerOptionsBase.RequestBodyReadTimeout"/>. Null inherits the server value;
        /// <see cref="Timeout.InfiniteTimeSpan"/> lifts the deadline.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestBodyReadTimeout
        {
            get => _requestBodyReadTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                _requestBodyReadTimeout = ValidateDeadline(value);
            }
        }

        /// <summary>
        /// Gets or sets how long a request on this route may be handled, replacing
        /// <see cref="WebServerOptionsBase.RequestTimeout"/>. Null inherits the server value;
        /// <see cref="Timeout.InfiniteTimeSpan"/> lifts the deadline.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestTimeout
        {
            get => _requestTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                _requestTimeout = ValidateDeadline(value);
            }
        }

        /// <summary>
        /// Gets or sets the minimum rate at which a request body must arrive on this route,
        /// replacing <see cref="WebServerOptionsBase.MinimumRequestBodyDataRate"/>. Null inherits
        /// the server value; <see cref="MinimumDataRate.None"/> lifts the rate.
        /// </summary>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public MinimumDataRate? MinimumRequestBodyDataRate
        {
            get => _minimumRequestBodyDataRate;
            set
            {
                EnsureConfigurationNotLocked();
                _minimumRequestBodyDataRate = value;
            }
        }

        internal bool Matches(string requestedPath) => _matcher.Match(requestedPath) != null;

        internal void Lock() => LockConfiguration();

        // Applies this policy over a server-wide value. The lifted markers become null.
        internal static long? Resolve(long? route, long? server) =>
            route is long value ? (value == long.MaxValue ? null : value) : server;

        internal static TimeSpan? Resolve(TimeSpan? route, TimeSpan? server) =>
            route is TimeSpan value ? (value == Timeout.InfiniteTimeSpan ? null : value) : server;

        internal static MinimumDataRate? Resolve(MinimumDataRate? route, MinimumDataRate? server) =>
            route != null ? (route.IsNone ? null : route) : server;

        private static TimeSpan? ValidateDeadline(TimeSpan? value)
        {
            if (
                value.HasValue
                && value.Value != Timeout.InfiniteTimeSpan
                && (value.Value <= TimeSpan.Zero || value.Value.TotalMilliseconds > int.MaxValue)
            )
                throw new ArgumentOutOfRangeException(nameof(value));

            return value;
        }
    }
}
