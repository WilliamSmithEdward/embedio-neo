using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EmbedIO.WebApi;

namespace EmbedIO.Routing
{
    /// <summary>
    /// Provides utility methods to work with routes.
    /// </summary>
    /// <seealso cref="WebApiModule"/>
    /// <seealso cref="WebApiController"/>
    /// <seealso cref="RouteAttribute"/>
    public static class Route
    {
        // Characters in ValidParameterNameChars MUST be in ascending ordinal order!
        private static readonly char[] ValidParameterNameChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz".ToCharArray(
        );

        /// <summary>
        /// <para>Determines whether a string is a valid route parameter name.</para>
        /// <para>To be considered a valid route parameter name, the specified string:</para>
        /// <list type="bullet">
        /// <item><description>must not be <see langword="null"/>;</description></item>
        /// <item><description>must not be the empty string;</description></item>
        /// <item><description>must consist entirely of decimal digits, upper- or lower-case
        /// letters of the English alphabet, or underscore (<c>'_'</c>) characters;</description></item>
        /// <item><description>must not start with a decimal digit.</description></item>
        /// </list>
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><see langword="true"/> if <paramref name="value"/> is a valid route parameter;
        /// otherwise, <see langword="false"/>.</returns>
        public static bool IsValidParameterName(string value) =>
            !string.IsNullOrEmpty(value)
                && value[0] > '9'
                && !value.Any(c => c < '0' || c > 'z' || Array.BinarySearch(ValidParameterNameChars, c) < 0);

        /// <summary>
        /// <para>Determines whether a string is a valid route.</para>
        /// <para>To be considered a valid route, the specified string:</para>
        /// <list type="bullet">
        /// <item><description>must not be <see langword="null"/>;</description></item>
        /// <item><description>must not be the empty string;</description></item>
        /// <item><description>must start with a slash (<c>'/'</c>) character;</description></item>
        /// <item><description>if a base route, must end with a slash (<c>'/'</c>) character;</description></item>
        /// <item><description>if not a base route, must not end with a slash (<c>'/'</c>) character,
        /// unless it is the only character in the string;</description></item>
        /// <item><description>must not contain consecutive runs of two or more slash (<c>'/'</c>) characters;</description></item>
        /// <item><description>may contain one or more parameter specifications.</description></item>
        /// </list>
        /// <para>Each parameter specification must be enclosed in curly brackets (<c>'{'</c>
        /// and <c>'}'</c>. No whitespace is allowed inside a parameter specification.</para>
        /// <para>Two parameter specifications must be separated by literal text.</para>
        /// <para>A parameter specification consists of a valid parameter name, optionally
        /// followed by a <c>'?'</c> character to signify that it will also match an empty string.</para>
        /// <para>If <c>'?'</c> is not present, a parameter by default will NOT match an empty string.</para>
        /// <para>See <see cref="IsValidParameterName"/> for the definition of a valid parameter name.</para>
        /// <para>To include a literal open curly bracket in the route, it must be doubled (<c>"{{"</c>).</para>
        /// <para>A literal closed curly bracket (<c>'}'</c>) may be included in the route as-is.</para>
        /// <para>A segment of a base route cannot consist only of an optional parameter.</para>
        /// </summary>
        /// <param name="route">The route to check.</param>
        /// <param name="isBaseRoute"><see langword="true"/> if checking for a base route;
        /// otherwise, <see langword="false"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="route"/> is a valid route;
        /// otherwise, <see langword="false"/>.</returns>
        public static bool IsValid(string route, bool isBaseRoute) =>
            ValidateInternal(nameof(route), route, isBaseRoute) == null;

        // Check the validity of a route by parsing it without storing the results.
        // Returns: ArgumentNullException, ArgumentException, null if OK
        internal static Exception? ValidateInternal(
            string argumentName,
            string value,
            bool isBaseRoute
        ) => ParseInternal(value, isBaseRoute, null) switch
        {
            ArgumentNullException _ => new ArgumentNullException(argumentName),
            FormatException formatException => new ArgumentException(formatException.Message, argumentName),
            Exception exception => exception,
            _ => null
        };

        // Validate and parse a route, constructing a Regex pattern.
        // setResult will be called at the end with the isBaseRoute flag, parameter names and the constructed pattern.
        // Returns: ArgumentNullException, FormatException, null if OK
        internal static Exception? ParseInternal(
            string route,
            bool isBaseRoute,
            Action<bool, IEnumerable<string>, string>? setResult,
            bool ignoreCase = false
        )
        {
            if (route == null)
                return new ArgumentNullException(nameof(route));

            if (route.Length == 0)
                return new FormatException("Route is empty.");

            if (route[0] != '/')
                return new FormatException("Route does not start with a slash.");

            /*
             * Regex options set at start of pattern:
             * IgnoreCase              : only for explicitly opted-in controller routes
             * Multiline               : no
             * Singleline              : yes
             * ExplicitCapture         : yes
             * IgnorePatternWhitespace : no
             * See https://docs.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options
             * See https://docs.microsoft.com/en-us/dotnet/standard/base-types/grouping-constructs-in-regular-expressions#group_options
             */
            var initialRegexOptions = ignoreCase ? "(?sin-mx)" : "(?sn-imx)";

            // If setResult is null we only validate; no pattern text is built.
            var pattern = new RoutePattern(isBaseRoute, setResult != null);

            if (route.Length == 1)
            {
                // If the route consists of a single slash, only a single slash will match.
                pattern.AppendRoot();
            }

            else
            {
                // Divide the route in segments, separated by slashes. The route is not
                // necessarily normalized, so there could be runs of consecutive slashes.
                foreach (var segment in GetSegments(route))
                {
                    var error = pattern.AppendSegment(segment);

                    if (error != null)
                        return error;
                }

                pattern.Close();
            }

            // Pass the results to the callback if needed.
            setResult?.Invoke(isBaseRoute, pattern.ParameterNames, initialRegexOptions + pattern.Text);

            // Everything's fine, thus no exception.
            return null;
        }

        // The Regex pattern and parameter names of a route under construction.
        // When only validating, no pattern text is built and Text stays null.
        private sealed class RoutePattern
        {
            private readonly StringBuilder? _text;
            private readonly bool _isBaseRoute;
            private int _segmentCount;
            private int _optionalSegmentCount;

            internal RoutePattern(bool isBaseRoute, bool buildText)
            {
                _isBaseRoute = isBaseRoute;
                _text = buildText ? new StringBuilder("^") : null;
            }

            internal List<string> ParameterNames { get; } = [];
            internal string? Text => _text?.ToString();

            internal void AppendRoot() => _text?.Append(_isBaseRoute ? "/" : "/$");

            // Parse the segment, looking alternately for a '{', that opens a parameter specification,
            // then for a '}', that closes it.
            // Characters outside parameter specifications are Regex-escaped and added to the pattern.
            // A parameter specification consists of a parameter name, optionally followed by '?'
            // to indicate that an empty parameter will match.
            // The default is to NOT match empty parameters, consistently with ASP.NET and EmbedIO version 2.
            // More syntax rules:
            // - There cannot be two parameters without literal text in between.
            // - If a segment consists ONLY of an OPTIONAL parameter, then the slash preceding it is optional too.
            internal FormatException? AppendSegment(string segment)
            {
                _segmentCount++;
                var position = 0;
                var afterParameter = false;

                while (true)
                {
                    var openPosition = segment.IndexOf('{', position);

                    if (openPosition < 0)
                    {
                        // No more parameter specs: escape the remainder of the segment.
                        AppendLiteral(segment, position, segment.Length, position == 0);
                        return null;
                    }

                    if (openPosition + 1 < segment.Length && segment[openPosition + 1] == '{')
                    {
                        // A doubled bracket is a single literal bracket. Keep the literal text
                        // before the pair and resume parsing after its second bracket.
                        AppendLiteral(segment, position, openPosition, position == 0);
                        _text?.Append(@"\{");
                        position = openPosition + 2;
                        afterParameter = false;
                        continue;
                    }

                    // A parameter immediately following another parameter,
                    // with no literal text in between, is a syntax error.
                    if (afterParameter && openPosition == position)
                        return new FormatException("Route syntax error: parameters must be separated by literal text.");

                    // The segment's slash goes with its literal text only if there is some.
                    // Otherwise the parameter spec decides, because an optional segment owns its slash.
                    AppendLiteral(segment, position, openPosition, position == 0 && openPosition > 0);
                    var error = AppendParameter(segment, openPosition + 1, out position);

                    if (error != null)
                        return error;

                    afterParameter = true;
                }
            }

            // Appends segment[start..end) Regex-escaped, preceded by the segment's slash when leading.
            private void AppendLiteral(string segment, int start, int end, bool leadingSlash)
            {
                if (leadingSlash)
                    _text?.Append('/');

                _text?.Append(Regex.Escape(segment.Substring(start, end - start)));
            }

            // Parses the "{name}" or "{name?}" specification whose name starts at start, just after
            // the opening bracket, and appends its capturing group. On success, next is the position
            // after the closing bracket.
            private FormatException? AppendParameter(string segment, int start, out int next)
            {
                next = start;

                // Look for end of spec, bail out if not found.
                var closePosition = segment.IndexOf('}', start);

                if (closePosition < 0)
                    return new FormatException("Route syntax error: unclosed parameter specification.");

                // Parameter spec cannot be empty.
                if (closePosition == start)
                    return new FormatException("Route syntax error: empty parameter specification.");

                // {name} means empty parameter does not match; {name?} means empty parameter matches.
                // If '?' is found, the parameter name ends before it.
                var allowEmpty = segment[closePosition - 1] == '?';
                var nameEndPosition = allowEmpty ? closePosition - 1 : closePosition;

                // Bail out if only '?' is found inside the spec.
                if (nameEndPosition == start)
                    return new FormatException("Route syntax error: missing parameter name.");

                var parameterName = segment.Substring(start, nameEndPosition - start);

                if (!IsValidParameterName(parameterName))
                    return new FormatException(
                        "Route syntax error: parameter name contains one or more invalid characters."
                    );

                if (ParameterNames.Contains(parameterName))
                    return new FormatException("Route syntax error: duplicate parameter name.");

                ParameterNames.Add(parameterName);

                // Parameters are made of non-slash characters ("[^/]") and match non-greedily
                // ("*?" if optional, "+?" if non optional). The spec starts the segment when
                // start is 1, just after the opening bracket.
                if (allowEmpty && start == 1 && closePosition == segment.Length - 1)
                {
                    if (_isBaseRoute)
                        return new FormatException("No segment of a base route can be optional.");

                    // If the segment consists only of an optional parameter, then the slash
                    // preceding the segment is optional as well. The parameter must then match
                    // only when not empty, because it's (slash + parameter) that is optional.
                    _text
                        ?.Append("(/(?<")
                        .Append(parameterName)
                        .Append(">[^/]+?))?");
                    _optionalSegmentCount++;
                }

                else
                {
                    // If at the start of a segment, don't forget the slash!
                    if (start == 1)
                        _text?.Append('/');

                    _text
                        ?.Append("(?<")
                        .Append(parameterName)
                        .Append(">[^/]")
                        .Append(allowEmpty ? '*' : '+')
                        .Append("?)");
                }

                next = closePosition + 1;
                return null;
            }

            internal void Close()
            {
                _text?.Append(_isBaseRoute ? "(/|$)" : "$");

                // If all segments are optional segments, "/" must match too.
                if (_optionalSegmentCount == _segmentCount)
                    _text
                        ?.Insert(0, "(/$)|(")
                        .Append(')');
            }
        }

        // Enumerate the segments of a route, ignoring consecutive slashes.
        private static IEnumerable<string> GetSegments(string route)
        {
            var length = route.Length;
            var position = 0;

            for (; ; )
            {
                while (route[position] == '/')
                {
                    position++;

                    if (position >= length)
                        break;
                }

                if (position >= length)
                    break;

                var slashPosition = route.IndexOf('/', position);

                if (slashPosition < 0)
                {
                    yield return route.Substring(position);
                    break;
                }

                yield return route.Substring(position, slashPosition - position);
                position = slashPosition;
            }
        }
    }
}
