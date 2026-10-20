using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Internal;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.WebApi;

namespace EmbedIO.Endpoints
{
    /// <summary>Exact typed endpoint routing with registration-time binding and immutable compiled policies.</summary>
    public sealed class EndpointModule : WebApiModuleBase
    {
        private readonly List<EndpointBuilder> _endpoints = [];
        private NamedRoutes? _links;
        internal readonly List<(HttpVerbs Verb, string Route, WebApiOperation Operation)> Descriptions = [];

        /// <summary>Creates a root endpoint module with the default response serializer.</summary>
        public EndpointModule() : this(ResponseSerializer.Default) { }

        /// <summary>Creates a root endpoint module with an explicit serializer.</summary>
        public EndpointModule(ResponseSerializerCallback serializer) : base("/", serializer) =>
            Root = new EndpointRouteGroup(this, null, "/");

        /// <summary>Gets the root group. Add this module after authentication and other request middleware.</summary>
        public EndpointRouteGroup Root { get; }

        /// <summary>Gets the number of endpoint registrations; a multi-method endpoint counts once.</summary>
        public int EndpointCount => _endpoints.Count;

        /// <summary>Validates, prepares and freezes every endpoint and policy without starting a listener.</summary>
        public void Compile() => LockConfiguration();

        /// <summary>Gets immutable named links, compiling and freezing endpoint configuration on first access.</summary>
        public NamedRoutes Links
        {
            get
            {
                Compile();
                return _links ?? throw new InvalidOperationException("Endpoint compilation did not finish.");
            }
        }

        internal void CheckMutable() => EnsureConfigurationNotLocked();

        internal EndpointBuilder Add(EndpointRouteGroup group, string route, HttpVerbs verb, EndpointHandler handler) =>
            Add(group, route, [verb], handler);

        internal EndpointBuilder Add(
            EndpointRouteGroup group,
            string route,
            HttpVerbs[] methods,
            EndpointHandler handler
        )
        {
            EnsureConfigurationNotLocked();

            methods = global::EmbedIO.Utilities.Validate.NotNull(nameof(methods), methods);

            var copy = methods
                .Distinct()
                .ToArray();

            if (
                copy.Length == 0
                || copy.Any(
                    m => m is not (HttpVerbs.Any
                        or HttpVerbs.Delete
                        or HttpVerbs.Get
                        or HttpVerbs.Head
                        or HttpVerbs.Options
                        or HttpVerbs.Patch
                        or HttpVerbs.Post
                        or HttpVerbs.Put
                        or HttpVerbs.Query)
                )
                || copy.Length > 1 && copy.Contains(HttpVerbs.Any)
            )
                throw new ArgumentException("Select defined methods, or Any on its own.", nameof(methods));

            var endpoint = new EndpointBuilder(this, group, route, copy, handler);
            _endpoints.Add(endpoint);
            return endpoint;
        }

        /// <inheritdoc />
        protected override Task OnPathNotFoundAsync(IHttpContext context) =>
            throw new RequestHandlerPassThroughException();

        private readonly List<(HttpVerbs Verb, RouteMatcher Matcher)> _compiledMethods = [];

        /// <inheritdoc />
        protected override Task OnMethodNotAllowedAsync(IHttpContext context)
        {
            context.Response.Headers["Allow"] = string.Join(
                ", ",
                _compiledMethods
                    .Where(d => d.Matcher.Match(context.RequestedPath) != null)
                    .Select(d => d.Verb
                        .ToString()
                        .ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
            );
            throw HttpException.MethodNotAllowed();
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            var pending =
                new List<(
                    EndpointBuilder Endpoint,
                    HttpVerbs Verb,
                    RouteMatcher Matcher,
                    RouteHandlerCallback Handler,
                    WebApiOperation Operation
                )>();
            var declarations = new List<(HttpVerbs Verb, string Shape)>();
            var names = new List<KeyValuePair<string, string>>();

            foreach (var endpoint in _endpoints)
            {
                var matcher = RouteMatcher.Parse(endpoint.Route, false);

                if (CaseInsensitiveRoutes) matcher = matcher.WithCaseInsensitiveLiterals();

                var shape = string.Empty;
                EmbedIO.Routing.Route.ParseInternal(
                    endpoint.Route,
                    false,
                    (_, _, pattern) => shape = CaptureShape(pattern)
                );

                if (endpoint.Name != null) names.Add(new KeyValuePair<string, string>(endpoint.Name, endpoint.Route));

                var plan = EndpointPlan.Create(endpoint);

                foreach (var verb in endpoint.Methods)
                {
                    if (
                        declarations.Any(
                            d => (CaseInsensitiveRoutes
                                ? System.Text.RegularExpressions.Regex.IsMatch(
                                    shape,
                                    "\\A" + System.Text.RegularExpressions.Regex.Escape(d.Shape) + "\\z",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                                )
                                : d.Shape == shape)
                                && (d.Verb == verb || d.Verb == HttpVerbs.Any || verb == HttpVerbs.Any)
                        )
                    )
                        throw new InvalidOperationException(
                            "An overlapping endpoint route/method is already registered: " + endpoint.Route
                        );

                    declarations.Add((verb, shape));
                    var operation = Describe(endpoint, plan, verb);

                    foreach (var parameter in operation.Parameters.Where(
                        x => x.Source == WebApiParameterSource.RouteOrDefault
                    ))
                        if (!matcher.ParameterNames.Contains(parameter.FieldName) && parameter.IsRequired)
                            throw new InvalidOperationException(
                                "Required handler parameter has no route placeholder: " + parameter.Name
                            );

                    pending.Add(
                        (endpoint, verb, matcher, PrepareEndpoint(endpoint.Handler, matcher, plan, verb), operation)
                    );
                }
            }

            var links = new NamedRoutes(names);

            // Literal endpoints take precedence even when a parameter route was declared first.
            foreach (var (endpoint, verb, matcher, handler, operation) in pending.OrderBy(
                item => item.Matcher.ParameterNames.Count
            ))
            {
                AddHandler(verb, matcher, handler);
                _compiledMethods.Add((verb, matcher));

                if (!endpoint.Excluded) Descriptions.Add((verb, endpoint.Route, operation));
            }

            _links = links;
            base.OnBeforeLockConfiguration();
        }

        // Preserve escaped literal text while making capture names irrelevant to duplicate detection.
        private static string CaptureShape(string pattern)
        {
            var result = new System.Text.StringBuilder(pattern.Length);

            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\\' && i + 1 < pattern.Length)
                {
                    result.Append(pattern[i]);
                    result.Append(pattern[++i]);
                    continue;
                }

                if (
                    i + 3 < pattern.Length
                    && pattern[i] == '('
                    && pattern[i + 1] == '?'
                    && pattern[i + 2] == '<'
                    && pattern[i + 3] != '='
                    && pattern[i + 3] != '!'
                )
                {
                    var end = i + 3;

                    while (end < pattern.Length && pattern[end] != '>') end++;

                    if (end < pattern.Length)
                    {
                        result.Append("(?:");
                        i = end;
                        continue;
                    }
                }

                result.Append(pattern[i]);
            }

            return result.ToString();
        }

        private static WebApiOperation Describe(EndpointBuilder endpoint, EndpointPlan plan, HttpVerbs verb)
        {
            var source = endpoint.Handler.Operation;
            var a = source.Operation;
            var operation = a == null
                && endpoint.Name == null
                && endpoint.Methods.Count == 1
                && endpoint.Summary == null
                && endpoint.Description == null
                && plan.Tags == null
                ? null
                : new OpenApiOperationAttribute
                {
                    OperationId = endpoint.Methods.Count > 1
                        ? (endpoint.Name ?? a?.OperationId ?? source.ControllerName + "_" + source.MethodName)
                            + "_"
                            + verb
                                .ToString()
                                .ToUpperInvariant()
                        : endpoint.Name ?? a?.OperationId,
                    Summary = endpoint.Summary ?? a?.Summary,
                    Description = endpoint.Description ?? a?.Description,
                    Tags = (endpoint.Tags ?? a?.Tags ?? plan.Tags) is { } tags ? (string[])tags.Clone() : null,
                    Deprecated = a?.Deprecated == true
                };
            return new WebApiOperation(
                source.ControllerName,
                source.MethodName,
                source.ResultType,
                [.. source.Parameters],
                operation,
                [
                    .. source.Responses
                        .Where(r => !endpoint.Responses.Any(x => x.StatusCode == r.StatusCode))
                        .Concat(endpoint.Responses)
                        .Select(r => new OpenApiResponseAttribute(r.StatusCode)
                        {
                            Type = r.Type,
                            Description = r.Description,
                            ContentType = r.ContentType
                        })
                ],
                source.Ignored,
                plan.Authorization
            );
        }
    }

    internal sealed class EndpointPlan
    {
        internal AuthorizationMetadata? Authorization;
        internal BrowserProtectionMode? Browser;
        internal string[]? Tags;
        internal string? Cache, Idempotency, Headers;
        internal readonly List<Func<IHttpContext, Task>> Before = [];
        internal readonly List<Func<IHttpContext, Task>> After = [];

        internal static EndpointPlan Create(EndpointBuilder endpoint)
        {
            var plan = new EndpointPlan();
            var requirements = new List<AuthorizationRequirement>();
            bool? anonymous = null;
            var groups = new List<EndpointRouteGroup>();

            for (var g = endpoint.Parent; g != null; g = g.Parent) groups.Insert(0, g);

            foreach (var group in groups) Apply(group);

            var handler = endpoint.Handler;

            if (handler.Authorization != null)
            {
                requirements.AddRange(handler.Authorization.Requirements);

                if (handler.Authorization.AllowAnonymous) anonymous = true;
            }

            if (handler.Browser.HasValue) plan.Browser = handler.Browser;

            if (handler.Cache != null) plan.Cache = handler.Cache;

            if (handler.Idempotency != null) plan.Idempotency = handler.Idempotency;

            if (handler.SecurityHeaders != null) plan.Headers = handler.SecurityHeaders;

            Apply(endpoint);
            plan.Authorization = requirements.Count == 0 && anonymous != true
                ? null
                : new AuthorizationMetadata(anonymous == true, [.. requirements]);
            plan.After.Reverse();
            return plan;

            void Apply<T>(EndpointOptions<T> options) where T : EndpointOptions<T>
            {
                if (options.Tags != null) plan.Tags = (string[])options.Tags.Clone();

                requirements.AddRange(options.Requirements);

                if (options.Anonymous.HasValue) anonymous = options.Anonymous;

                if (options.Browser.HasValue) plan.Browser = options.Browser;

                if (options.HasCache) plan.Cache = options.Cache;

                if (options.HasIdempotency) plan.Idempotency = options.Idempotency;

                if (options.HasHeaders) plan.Headers = options.Headers;

                plan.Before.AddRange(options.Before);
                plan.After.AddRange(options.After);
            }
        }
    }
}
