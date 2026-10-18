using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EmbedIO.WebApi;

namespace EmbedIO.Routing
{
    /// <summary>An immutable, ordinal registry of application-relative routes for Location generation.</summary>
    public sealed class NamedRoutes
    {
        private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);

        /// <summary>Copies named complete application route templates. Duplicate names and unsafe literal paths fail at configuration.</summary>
        public NamedRoutes(IEnumerable<KeyValuePair<string, string>> routes)
        {
            routes = global::EmbedIO.Utilities.Validate.NotNull(nameof(routes), routes);

            foreach (var pair in routes)
            {
                if (_routes.Count >= 1024 || string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128)
                    throw new ArgumentException(
                        "Use at most 1024 nonempty route names of at most 128 characters.",
                        nameof(routes)
                    );

                var template = pair.Value;

                if (template == null || template.Length > 4096 || template.StartsWith("//", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid route template.", nameof(routes));

                RouteMatcher.Parse(template, false);

                foreach (var c in template)
                    if (c < 33 || c > 126 || c == '\\' || c == '#' || c == '%')
                        throw new ArgumentException(
                            "Route literals must be safe ASCII path characters.",
                            nameof(routes)
                        );

                // Question marks are legal only in optional parameter specifications.
                var literal = false;

                foreach (var segment in template.Split('/'))
                {
                    if (segment == "." || segment == "..")
                        throw new ArgumentException("Dot segments are not route literals.", nameof(routes));

                    literal = false;

                    for (var i = 0; i < segment.Length; i++)
                    {
                        if (segment[i] == '{' && i + 1 < segment.Length && segment[i + 1] == '{')
                        {
                            i++;
                            continue;
                        }

                        if (segment[i] == '{') literal = true;

                        else if (segment[i] == '}') literal = false;

                        else if (segment[i] == '?' && !literal)
                            throw new ArgumentException("A route cannot contain a query string.", nameof(routes));
                    }
                }

                _routes.Add(pair.Key, template);
            }
        }

        /// <summary>Builds names from generated controller routes using OperationId or ControllerName.MethodName.</summary>
        /// <remarks>Multiple aliases or verbs with the same inferred name are rejected; use an explicit registry for ambiguous routes. Registration of the actual controller remains separate.</remarks>
        public static NamedRoutes FromController<TController>(
            string baseRoute,
            WebApiControllerBinding<TController> binding
        ) where TController : WebApiController
        {
            baseRoute = global::EmbedIO.Utilities.Validate.NotNull(nameof(baseRoute), baseRoute);

            binding = global::EmbedIO.Utilities.Validate.NotNull(nameof(binding), binding);

            if (baseRoute.Length == 0 || baseRoute[0] != '/')
                throw new ArgumentException("A root-relative module path is required.", nameof(baseRoute));

            return new NamedRoutes(binding.Routes.Select(route =>
                {
                    if (route.IsBaseRoute) throw new ArgumentException("Base routes need an explicit complete named template.", nameof(binding));

                    var operation = route.Operation ?? throw new ArgumentException("Generated operation metadata is required.", nameof(binding));
                    return new KeyValuePair<string, string>(operation.Operation?.OperationId ?? operation.ControllerName + "." + operation.MethodName, baseRoute.TrimEnd('/') + route.Route);
                }));
        }

        /// <summary>Generates an escaped root-relative path, rejecting missing/extra values and ambiguous path segments.</summary>
        /// <remarks>Values use invariant culture. Slashes, backslashes, percent signs, dot segments and controls are rejected. No incoming host or forwarding header is consulted.</remarks>

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1865:Use char overload",
            Justification = "The shared implementation targets netstandard2.0, which has no string.EndsWith(char)."
        )]
        public string GetPath(string name, IReadOnlyDictionary<string, object?>? values = null)
        {
            name = global::EmbedIO.Utilities.Validate.NotNull(nameof(name), name);

            if (!_routes.TryGetValue(name, out var template))
                throw new ArgumentException("Unknown route name.", nameof(name));

            var used = new HashSet<string>(StringComparer.Ordinal);
            var path = new StringBuilder();

            foreach (var segment in template
                .Split('/')
                .Skip(1))
            {
                var output = new StringBuilder();
                var skip = false;

                for (var i = 0; i < segment.Length; i++)
                {
                    if (segment[i] != '{')
                    {
                        output.Append(segment[i]);
                        continue;
                    }

                    if (i + 1 < segment.Length && segment[i + 1] == '{')
                    {
                        output.Append("%7B");
                        i++;
                        continue;
                    }

                    var end = segment.IndexOf('}', i + 1);
                    var key = segment.Substring(i + 1, end - i - 1);
                    var optional = key.EndsWith("?", StringComparison.Ordinal);

                    if (optional) key = key.Remove(key.Length - 1);

                    used.Add(key);
                    object? value = null;
                    values?.TryGetValue(key, out value);
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

                    if (text.Length == 0)
                    {
                        if (!optional) throw new ArgumentException("Missing route value: " + key, nameof(values));

                        skip = i == 0 && end == segment.Length - 1;
                    }

                    else
                    {
                        if (text.Length > 2048 || text == "." || text == "..")
                            throw new ArgumentException("Invalid route value: " + key, nameof(values));

                        foreach (var c in text)
                            if (char.IsControl(c) || c == '/' || c == '\\' || c == '%')
                                throw new ArgumentException("Ambiguous route value: " + key, nameof(values));

                        output.Append(Uri.EscapeDataString(text));
                    }

                    i = end;
                }

                if (!skip)
                    path
                        .Append('/')
                        .Append(output);

                if (path.Length > 8192) throw new ArgumentException("Generated path is too long.", nameof(values));
            }

            if (values != null && values.Keys.Any(key => !used.Contains(key)))
                throw new ArgumentException("Unexpected route values.", nameof(values));

            return path.Length == 0 ? "/" : path.ToString();
        }
    }
}
