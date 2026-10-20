using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using EmbedIO.Utilities;

namespace EmbedIO.Routing
{
    /// <summary>
    /// Matches URL paths against a route.
    /// </summary>
    public sealed class RouteMatcher : IEquatable<RouteMatcher>
    {
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<(bool, string), RouteMatcher> Cache = [];
        private static readonly string[] EscapedBraceSeparator = ["{{"];
        private readonly Regex? _regex;
        private readonly Lazy<Regex>? _fallbackRegex;
        private readonly bool _caseInsensitive;
        private readonly string? _literalPath;

        private RouteMatcher(
            bool isBaseRoute,
            string route,
            string pattern,
            IReadOnlyList<string> parameterNames,
            bool caseInsensitive = false
        )
        {
            IsBaseRoute = isBaseRoute;
            Route = route;
            ParameterNames = parameterNames;
            _caseInsensitive = caseInsensitive;

            // Case-sensitive literal routes need no regex compilation, including on their first match.
            // Retain the fallback for callers that mutate the legacy concrete parameter-name list.
            if (parameterNames.Count == 0 && !caseInsensitive)
            {
                _fallbackRegex = CreateRegexFallback(pattern);

                if (!isBaseRoute)
                    _literalPath = route.IndexOf("{{", StringComparison.Ordinal) < 0
                        ? route
                        : string.Join("{", route.Split(EscapedBraceSeparator, StringSplitOptions.None));
            }

            else
                _regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        private static Lazy<Regex> CreateRegexFallback(string pattern) =>
            new(() => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));

        // Module-local copies never modify the public parser's case-sensitive cache.
        internal RouteMatcher WithCaseInsensitiveLiterals()
        {
            RouteMatcher? result = null;
            var error = Routing.Route.ParseInternal(
                Route,
                IsBaseRoute,
                (_, names, pattern) =>
                    result = new RouteMatcher(IsBaseRoute, Route, pattern, (List<string>)[.. names], true),
                true
            );

            if (error != null)
                throw error;

            return result ?? throw new InvalidOperationException("The route parser did not produce a matcher.");
        }

        internal bool HasCaseEquivalentTemplate(RouteMatcher other) =>
            IsBaseRoute == other.IsBaseRoute
                && Regex.IsMatch(
                    Route,
                    "\\A" + Regex.Escape(other.Route) + "\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );

        /// <summary>
        /// Gets a value indicating whether the <see cref="Route"/> property
        /// is a base route.
        /// </summary>
        public bool IsBaseRoute { get; }

        /// <summary>
        /// Gets the route this instance matches URL paths against.
        /// </summary>
        public string Route { get; }

        /// <summary>
        /// Gets the names of the route's parameters.
        /// </summary>
        public IReadOnlyList<string> ParameterNames { get; }

        /// <summary>
        /// Constructs an instance of <see cref="RouteMatcher"/> by parsing the specified route.
        /// <para>If the same route was previously parsed and the <see cref="ClearCache"/> method has not been called since,
        /// this method obtains an instance from a static cache.</para>
        /// </summary>
        /// <param name="route">The route to parse.</param>
        /// <param name="isBaseRoute"><see langword="true"/> if the route to parse
        /// is a base route; otherwise, <see langword="false"/>.</param>
        /// <returns>A newly-constructed instance of <see cref="RouteMatcher"/>
        /// that will match URL paths against <paramref name="route"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="route"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException"><paramref name="route"/> is not a valid route.</exception>
        /// <seealso cref="TryParse"/>
        /// <seealso cref="ClearCache"/>
        public static RouteMatcher Parse(string? route, bool isBaseRoute)
        {
            var exception = TryParseInternal(route, isBaseRoute, out var result);

            if (exception != null)
                throw exception;

            return result ?? throw new InvalidOperationException("The route parser did not produce a matcher.");
        }

        /// <summary>
        /// <para>Attempts to obtain an instance of <see cref="RouteMatcher" /> by parsing the specified route.</para>
        /// <para>If the same route was previously parsed and the <see cref="ClearCache"/> method has not been called since,
        /// this method obtains an instance from a static cache.</para>
        /// </summary>
        /// <param name="route">The route to parse.</param>
        /// <param name="isBaseRoute"><see langword="true"/> if the route to parse
        /// is a base route; otherwise, <see langword="false"/>.</param>
        /// <param name="result">When this method returns <see langword="true"/>, a newly-constructed instance of <see cref="RouteMatcher" />
        /// that will match URL paths against <paramref name="route"/>; otherwise, <see langword="null"/>.
        /// This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if parsing was successful; otherwise, <see langword="false"/>.</returns>
        /// <seealso cref="Parse"/>
        /// <seealso cref="ClearCache"/>
        public static bool TryParse(string? route, bool isBaseRoute, out RouteMatcher? result) =>
            TryParseInternal(route, isBaseRoute, out result) == null;

        /// <summary>
        /// Clears <see cref="RouteMatcher"/>'s internal instance cache.
        /// </summary>
        /// <seealso cref="Parse"/>
        /// <seealso cref="TryParse"/>
        public static void ClearCache()
        {
            lock (SyncRoot)
            {
                Cache.Clear();
            }
        }

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        /// <returns>A hash code for this instance, suitable for use in hashing algorithms
        /// and data structures like a hash table.</returns>
        public override int GetHashCode() =>
            _caseInsensitive ? (Route, IsBaseRoute, true).GetHashCode() : (Route, IsBaseRoute).GetHashCode();

        /// <summary>
        /// Determines whether the specified <see cref="object"/> is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="object"/> to compare with this instance.</param>
        /// <returns><see langword="true"/> if <paramref name="obj"/> is equal to this instance;
        /// otherwise, <see langword="false"/>.</returns>
        public override bool Equals(object? obj) => obj is RouteMatcher other && Equals(other);

        /// <summary>
        /// Indicates whether this instance is equal to another instance of <see cref="RouteMatcher"/>.
        /// </summary>
        /// <param name="other">A <see cref="RouteMatcher"/> to compare with this instance.</param>
        /// <returns><see langword="true"/> if this instance is equal to <paramref name="other"/>;
        /// otherwise, <see langword="false"/>.</returns>
        public bool Equals(RouteMatcher? other) =>
            other != null
                && other.Route == Route
                && other.IsBaseRoute == IsBaseRoute
                && other._caseInsensitive == _caseInsensitive;

        /// <summary>
        /// Matches the specified URL path against <see cref="Route"/>
        /// and extracts values for the route's parameters.
        /// </summary>
        /// <param name="path">The URL path to match.</param>
        /// <returns>If the match is successful, a <see cref="RouteMatch"/> object;
        /// otherwise, <see langword="null"/>.</returns>
        public RouteMatch? Match(string? path)
        {
            if (path == null)
                return null;

            // Optimize for parameterless base routes
            if (IsBaseRoute && !_caseInsensitive)
            {
                if (Route.Length == 1)
                    return RouteMatch.UnsafeFromRoot(path);

                if (ParameterNames.Count == 0)
                    return RouteMatch.UnsafeFromBasePath(Route, path);
            }

            if (_literalPath != null && ParameterNames.Count == 0)
            {
                // Preserve Regex '$': one final LF may follow a complete literal match.
                var length = _literalPath.Length;
                return (path.Length == length || (path.Length == length + 1 && path[length] == '\n'))
                    && string.CompareOrdinal(path, 0, _literalPath, 0, length) == 0
                    ? new RouteMatch(path, ParameterNames, [], null)
                    : null;
            }

            var match = (_regex ?? _fallbackRegex?.Value
                ?? throw new InvalidOperationException("The route matcher has no regular expression.")).Match(path);

            if (!match.Success)
                return null;

            var values = new string[match.Groups.Count - 1];

            for (var i = 0; i < values.Length; i++)
                values[i] = DecodeSegment(match.Groups[i + 1].Value);

            return new RouteMatch(
                path,
                ParameterNames,
                values,
                IsBaseRoute ? "/" + path.Substring(match.Groups[0].Length) : null
            );
        }

        // A path segment is percent-decoded only: '+' is a literal there, not form encoding of a space.
        // Every other result, malformed escapes included, stays what WebUtility.UrlDecode produces.
        private static string DecodeSegment(string value) =>
            WebUtility.UrlDecode(
                value.IndexOf("+", StringComparison.Ordinal) < 0 ? value : string.Join("%2B", value.Split('+'))
            );

        private static Exception? TryParseInternal(string? route, bool isBaseRoute, out RouteMatcher? result)
        {
            if (route == null)
            {
                result = null;
                return new ArgumentNullException(nameof(route));
            }

            lock (SyncRoot)
            {
                if (Cache.TryGetValue((isBaseRoute, route), out result))
                    return null;

                string? pattern = null;
                var parameterNames = new List<string>();
                var exception = Routing.Route.ParseInternal(route, isBaseRoute, (_, n, p) =>
                {
                    parameterNames.AddRange(n);
                    pattern = p;
                });

                if (exception != null)
                {
                    result = null;
                    return exception;
                }

                route = UrlPath.UnsafeNormalize(route, isBaseRoute);

                if (Cache.TryGetValue((isBaseRoute, route), out result))
                    return null;

                result = new RouteMatcher(
                    isBaseRoute,
                    route,
                    pattern ?? throw new InvalidOperationException("The route parser did not produce a pattern."),
                    parameterNames
                );
                Cache.Add((isBaseRoute, route), result);
                return null;
            }
        }
    }
}
