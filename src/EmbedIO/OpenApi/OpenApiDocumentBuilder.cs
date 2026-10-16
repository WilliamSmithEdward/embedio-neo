using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using EmbedIO.Authentication;
using EmbedIO.Routing;
using EmbedIO.Utilities;
using EmbedIO.WebApi;

namespace EmbedIO.OpenApi
{
    /// <summary>
    /// <para>Builds an OpenAPI 3.1 document from the generated bindings of Web API controllers,
    /// without reflection. Routes, parameters and results come from the bindings; request and
    /// response body schemas come from the JSON serializer options the module serializes with.</para>
    /// <para>Anything the builder cannot describe faithfully, such as a base route or a custom
    /// request-data attribute, is reported as a problem rather than guessed.</para>
    /// </summary>
    public sealed class OpenApiDocumentBuilder
    {
        // Controllers and declared operations, in the order they were added.
        private readonly List<Action<DocumentWriter>> _entries = new();
        private readonly List<(string Url, string? Description)> _servers = new();
        private readonly List<(string Name, OpenApiSecurityScheme Scheme)> _schemes = new();
        private readonly List<SecurityRequirement> _requirements = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenApiDocumentBuilder"/> class.
        /// </summary>
        /// <param name="title">The API's title.</param>
        /// <param name="version">The API's version, not the OpenAPI version.</param>
        /// <exception cref="ArgumentNullException"><paramref name="title"/> or <paramref name="version"/> is <see langword="null"/>.</exception>
        public OpenApiDocumentBuilder(string title, string version)
        {
            Title = Validate.NotNull(nameof(title), title);
            Version = Validate.NotNull(nameof(version), version);
        }

        /// <summary>Gets the API's title.</summary>
        public string Title { get; }

        /// <summary>Gets the API's version.</summary>
        public string Version { get; }

        /// <summary>Gets or sets the API's description; CommonMark is allowed.</summary>
        public string? Description { get; set; }

        /// <summary>Adds a server to the document.</summary>
        /// <param name="location">The server's URL. It may be relative to the document's location,
        /// and may contain OpenAPI server variables, so it is not a <see cref="Uri"/>.</param>
        /// <param name="description">An optional description.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="location"/> is <see langword="null"/>.</exception>
        public OpenApiDocumentBuilder AddServer(string location, string? description = null)
        {
            _servers.Add((Validate.NotNull(nameof(location), location), description));
            return this;
        }

        /// <summary>
        /// Adds the routes of a controller registered in a <see cref="WebApiModule"/> mounted at <paramref name="baseRoute"/>.
        /// </summary>
        /// <typeparam name="TController">The controller type.</typeparam>
        /// <param name="baseRoute">The module's base route, as passed to its constructor, such as <c>/api</c>.</param>
        /// <param name="binding">The controller's generated binding.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="baseRoute"/> or <paramref name="binding"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="baseRoute"/> does not start with a slash.</exception>
        public OpenApiDocumentBuilder AddController<TController>(string baseRoute, WebApiControllerBinding<TController> binding)
            where TController : WebApiController
        {
            baseRoute = Validate.NotNull(nameof(baseRoute), baseRoute);
            binding = Validate.NotNull(nameof(binding), binding);
            if (baseRoute.Length == 0 || baseRoute[0] != '/')
                throw new ArgumentException("A base route must start with a slash.", nameof(baseRoute));

            var basePath = baseRoute.TrimEnd('/');
            var routes = binding.Routes.Select(r => new RouteDescription(r.Verb, r.Route, r.IsBaseRoute, r.Operation)).ToArray();
            _entries.Add(writer =>
            {
                foreach (var route in routes)
                    writer.AddRoute(basePath, route);
            });
            return this;
        }

        /// <summary>
        /// Adds an operation the builder cannot read from a generated binding, such as a route
        /// handled by a <see cref="Routing.RoutingModule"/> or a custom module.
        /// </summary>
        /// <param name="verb">The HTTP method the route answers.</param>
        /// <param name="route">The route's full path, including any module base route, such as
        /// <c>/files/{name}</c>. Parameters use EmbedIO route syntax.</param>
        /// <param name="describe">A callback that declares the operation's parameters, body and responses.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="describe"/> is <see langword="null"/>.</exception>
        public OpenApiDocumentBuilder AddOperation(HttpVerbs verb, string route, Action<OpenApiOperationDeclaration> describe)
        {
            route = Validate.NotNull(nameof(route), route);
            describe = Validate.NotNull(nameof(describe), describe);
            var declaration = new OpenApiOperationDeclaration();
            describe(declaration);
            _entries.Add(writer => writer.AddDeclared(verb, route, declaration));
            return this;
        }

        /// <summary>Adds a security scheme to the document's components.</summary>
        /// <param name="name">The scheme's name, which requirements refer to. Letters, digits,
        /// <c>.</c>, <c>-</c> and <c>_</c> only.</param>
        /// <param name="scheme">The scheme.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="scheme"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid component name, or is already used.</exception>
        public OpenApiDocumentBuilder AddSecurityScheme(string name, OpenApiSecurityScheme scheme)
        {
            name = Validate.NotNull(nameof(name), name);
            scheme = Validate.NotNull(nameof(scheme), scheme);
            if (name.Length == 0 || name.Any(c => !(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '.' || c == '-' || c == '_')))
                throw new ArgumentException("A component name contains only letters, digits, '.', '-' and '_'.", nameof(name));
            if (_schemes.Any(s => s.Name == name))
                throw new ArgumentException($"A security scheme named '{name}' is already added.", nameof(name));

            _schemes.Add((name, scheme));
            return this;
        }

        /// <summary>
        /// Requires a security scheme for every operation whose path <paramref name="module"/> handles,
        /// as decided by the module's own <see cref="IWebModule.MatchUrlPath"/>. Use the module that
        /// authenticates the requests.
        /// </summary>
        /// <param name="module">The module that authenticates the requests. It protects only the requests
        /// it sees, so it must come before the modules that serve them.</param>
        /// <param name="schemeName">The name of a scheme added with <see cref="AddSecurityScheme"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="module"/> or <paramref name="schemeName"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">No scheme named <paramref name="schemeName"/> has been added.</exception>
        public OpenApiDocumentBuilder RequireSecurity(IWebModule module, string schemeName)
        {
            module = Validate.NotNull(nameof(module), module);
            _requirements.Add(new SecurityRequirement(path => module.MatchUrlPath(path) != null, KnownScheme(schemeName), false));
            return this;
        }

        /// <summary>
        /// Requires a security scheme for every operation under <paramref name="baseRoute"/>, matched
        /// as an EmbedIO base route, for authentication that is not a module of its own.
        /// </summary>
        /// <param name="baseRoute">The base route, such as <c>/admin</c>.</param>
        /// <param name="schemeName">The name of a scheme added with <see cref="AddSecurityScheme"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="baseRoute"/> or <paramref name="schemeName"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException"><paramref name="baseRoute"/> is not a valid base route.</exception>
        /// <exception cref="ArgumentException">No scheme named <paramref name="schemeName"/> has been added.</exception>
        public OpenApiDocumentBuilder RequireSecurity(string baseRoute, string schemeName)
        {
            var matcher = RouteMatcher.Parse(Validate.NotNull(nameof(baseRoute), baseRoute), true);
            _requirements.Add(new SecurityRequirement(path => matcher.Match(path) != null, KnownScheme(schemeName), false));
            return this;
        }

        /// <summary>
        /// Describes a Basic authentication module: adds a Basic scheme named <paramref name="schemeName"/>,
        /// requires it for every operation whose path the module handles, and adds the 401 response
        /// the module sends for missing or wrong credentials.
        /// </summary>
        /// <param name="schemeName">The name to give the scheme.</param>
        /// <param name="module">The module. It protects only the requests it sees, so it must come
        /// before the modules that serve them.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="schemeName"/> or <paramref name="module"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="schemeName"/> is not a valid component name, or is already used.</exception>
        public OpenApiDocumentBuilder AddBasicAuthentication(string schemeName, BasicAuthenticationModuleBase module)
        {
            module = Validate.NotNull(nameof(module), module);
            AddSecurityScheme(schemeName, OpenApiSecurityScheme.Basic($"HTTP Basic authentication, realm \"{module.Realm}\"."));
            _requirements.Add(new SecurityRequirement(path => module.MatchUrlPath(path) != null, schemeName, true));
            return this;
        }

        private string KnownScheme(string schemeName)
        {
            schemeName = Validate.NotNull(nameof(schemeName), schemeName);
            if (!_schemes.Any(s => s.Name == schemeName))
                throw new ArgumentException($"No security scheme named '{schemeName}' has been added.", nameof(schemeName));

            return schemeName;
        }

        /// <summary>
        /// Builds the document, failing if any route cannot be described.
        /// </summary>
        /// <param name="options">The JSON serializer options the controllers' module serializes with,
        /// for example <c>AppJson.Default.Options</c> from a <c>JsonSerializerContext</c>.</param>
        /// <returns>The document.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="OpenApiDocumentException">One or more routes or types cannot be described.</exception>
        public OpenApiDocument Build(JsonSerializerOptions options) => Build(options, false);

        /// <summary>
        /// Builds the document.
        /// </summary>
        /// <param name="options">The JSON serializer options the controllers' module serializes with.</param>
        /// <param name="allowIncomplete"><see langword="true"/> to leave out what cannot be described and
        /// report it in <see cref="OpenApiDocument.Problems"/>; <see langword="false"/> to throw instead.</param>
        /// <returns>The document.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="OpenApiDocumentException"><paramref name="allowIncomplete"/> is <see langword="false"/>
        /// and one or more routes or types cannot be described.</exception>
        public OpenApiDocument Build(JsonSerializerOptions options, bool allowIncomplete)
        {
            options = Validate.NotNull(nameof(options), options);
            var writer = new DocumentWriter(options);
            var info = new JsonObject { ["title"] = Title, ["version"] = Version };
            if (Description != null)
                info["description"] = Description;

            var root = new JsonObject { ["openapi"] = "3.1.0", ["info"] = info };
            if (_servers.Count > 0)
            {
                var servers = new JsonArray();
                foreach (var (url, description) in _servers)
                {
                    var server = new JsonObject { ["url"] = url };
                    if (description != null)
                        server["description"] = description;
                    servers.Add((JsonNode)server);
                }

                root["servers"] = servers;
            }

            foreach (var entry in _entries)
                entry(writer);

            writer.ApplySecurity(_requirements);
            root["paths"] = writer.Paths;
            var components = new JsonObject();
            if (writer.Schemas.Count > 0)
                components["schemas"] = writer.Schemas;
            if (_schemes.Count > 0)
                components["securitySchemes"] = new JsonObject(_schemes.Select(s => new KeyValuePair<string, JsonNode?>(s.Name, s.Scheme.ToJson())));
            if (components.Count > 0)
                root["components"] = components;

            if (writer.Tags.Count > 0)
                root["tags"] = new JsonArray(writer.Tags.Select(t => (JsonNode)new JsonObject { ["name"] = t }).ToArray());

            if (writer.Problems.Count > 0 && !allowIncomplete)
                throw new OpenApiDocumentException(writer.Problems);

            return new OpenApiDocument(root, writer.Problems);
        }

        // Operations whose sample path Covers returns true for require Scheme.
        private sealed class SecurityRequirement
        {
            internal SecurityRequirement(Func<string, bool> covers, string scheme, bool unauthorizedResponse)
            {
                Covers = covers;
                Scheme = scheme;
                UnauthorizedResponse = unauthorizedResponse;
            }

            internal Func<string, bool> Covers { get; }

            internal string Scheme { get; }

            // Whether the requirement's module answers missing or wrong credentials with 401.
            internal bool UnauthorizedResponse { get; }
        }

        private sealed class RouteDescription
        {
            internal RouteDescription(HttpVerbs verb, string route, bool isBaseRoute, WebApiOperation? operation)
            {
                Verb = verb;
                Route = route;
                IsBaseRoute = isBaseRoute;
                Operation = operation;
            }

            internal HttpVerbs Verb { get; }

            internal string Route { get; }

            internal bool IsBaseRoute { get; }

            internal WebApiOperation? Operation { get; }
        }

        // Accumulates paths, schemas and problems for one Build call.
        private sealed class DocumentWriter
        {
            private const string JsonMediaType = "application/json";
            private const string FormMediaType = "application/x-www-form-urlencoded";
            // EmbedIO.DependencyInjection references this assembly, so its attribute is known by name.
            private const string FromServicesAttributeName = "EmbedIO.DependencyInjection.FromServicesAttribute";

            private readonly JsonSerializerOptions _options;
            private readonly Dictionary<Type, string> _schemaNames = new();
            private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _pathTemplates = new(StringComparer.Ordinal);

            internal DocumentWriter(JsonSerializerOptions options)
            {
                _options = options;
            }

            internal JsonObject Paths { get; } = new();

            internal JsonObject Schemas { get; } = new();

            internal List<string> Tags { get; } = new();

            internal List<string> Problems { get; } = new();

            internal void AddRoute(string basePath, RouteDescription route)
            {
                var operation = route.Operation;
                var where = $"{route.Verb.ToString().ToUpperInvariant()} {basePath}{route.Route}";
                if (operation == null)
                {
                    Problems.Add(where + ": the binding has no operation metadata; rebuild with the current EmbedIO-Neo source generator.");
                    return;
                }

                if (operation.Ignored)
                    return;

                where = $"{where} ({operation.ControllerName}.{operation.MethodName})";
                if (route.IsBaseRoute)
                {
                    Problems.Add(where + ": a base route matches any sub-path, which OpenAPI paths cannot express. Add [OpenApiIgnore] to leave it out.");
                    return;
                }

                var method = MethodName(route.Verb, where, " Add [OpenApiIgnore] to leave it out.");
                if (method == null)
                    return;

                // A module's base route can hold parameters too; each must be declared (OpenAPI path templating).
                var (path, routeParameters) = PathTemplate(basePath + route.Route);
                var repeated = routeParameters.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (repeated != null)
                {
                    Problems.Add($"{where}: route parameter '{repeated.Key}' appears more than once in the path.");
                    return;
                }

                if (IsTaken(path, method, where))
                    return;

                // Only an operation described without problems enters the document.
                var problemCount = Problems.Count;
                var described = Describe(operation, routeParameters, where);
                if (Problems.Count == problemCount)
                    Insert(path, method, described);
            }

            internal void AddDeclared(HttpVerbs verb, string route, OpenApiOperationDeclaration declaration)
            {
                var where = $"{verb.ToString().ToUpperInvariant()} {route} (declared)";
                var method = MethodName(verb, where, string.Empty);
                if (method == null)
                    return;

                if (!RouteMatcher.TryParse(route, false, out var matcher) || matcher == null)
                {
                    Problems.Add(where + ": the route is not a valid EmbedIO route.");
                    return;
                }

                var (path, routeParameters) = PathTemplate(matcher.Route);
                if (IsTaken(path, method, where))
                    return;

                var problemCount = Problems.Count;
                var described = DescribeDeclared(declaration, routeParameters, where);
                if (Problems.Count == problemCount)
                    Insert(path, method, described);
            }

            // Every operation whose path a requirement covers needs that requirement's scheme.
            // Requirements that cover the same operation all apply, as their modules all run.
            internal void ApplySecurity(IReadOnlyList<SecurityRequirement> requirements)
            {
                if (requirements.Count == 0)
                    return;

                foreach (var pathItem in Paths)
                {
                    var sample = SamplePath(pathItem.Key);
                    var applying = requirements.Where(r => r.Covers(sample)).ToArray();
                    if (applying.Length == 0)
                        continue;

                    foreach (var operation in ((JsonObject?)pathItem.Value ?? new JsonObject()).Select(o => o.Value).OfType<JsonObject>())
                    {
                        var schemes = new JsonObject();
                        foreach (var requirement in applying)
                        {
                            if (!schemes.ContainsKey(requirement.Scheme))
                                schemes[requirement.Scheme] = new JsonArray();
                        }

                        operation["security"] = new JsonArray((JsonNode)schemes);
                        if (applying.Any(r => r.UnauthorizedResponse) && operation["responses"] is JsonObject responses && !responses.ContainsKey("401"))
                        {
                            responses["401"] = new JsonObject
                            {
                                ["description"] = "The credentials are missing or wrong.",
                                ["headers"] = new JsonObject { [HttpHeaderNames.WWWAuthenticate] = new JsonObject { ["schema"] = StringSchema() } },
                            };
                        }
                    }
                }
            }

            // A concrete path the template stands for, to match against a module's base route.
            private static string SamplePath(string template)
            {
                var sample = new StringBuilder(template.Length);
                var inParameter = false;
                foreach (var c in template)
                {
                    if (c == '{')
                    {
                        inParameter = true;
                        sample.Append('x');
                    }
                    else if (c == '}')
                    {
                        inParameter = false;
                    }
                    else if (!inParameter)
                    {
                        sample.Append(c);
                    }
                }

                return sample.ToString();
            }

            private string? MethodName(HttpVerbs verb, string where, string remedy)
            {
                if (verb == HttpVerbs.Any)
                {
                    Problems.Add(where + ": HttpVerbs.Any answers every method, which OpenAPI operations cannot express." + remedy);
                    return null;
                }

                var method = verb switch
                {
                    HttpVerbs.Delete => "delete",
                    HttpVerbs.Get => "get",
                    HttpVerbs.Head => "head",
                    HttpVerbs.Options => "options",
                    HttpVerbs.Patch => "patch",
                    HttpVerbs.Post => "post",
                    HttpVerbs.Put => "put",
                    _ => null,
                };
                if (method == null)
                    Problems.Add(where + ": OpenAPI 3.1 has no operation for the " + verb.ToString().ToUpperInvariant() + " method." + remedy);

                return method;
            }

            private bool IsTaken(string path, string method, string where)
            {
                if (_pathTemplates.TryGetValue(TemplateKey(path), out var canonical)) path = canonical;
                if (Paths[path] is not JsonObject pathItem || !pathItem.ContainsKey(method))
                    return false;

                Problems.Add(where + ": another operation already uses this method and path, so the document cannot say which one answers.");
                return true;
            }

            private void Insert(string path, string method, JsonObject described)
            {
                var template = TemplateKey(path);
                if (_pathTemplates.TryGetValue(template, out var canonical) && canonical != path)
                {
                    var (_, originalNames) = PathTemplate(path);
                    var (_, canonicalNames) = PathTemplate(canonical);
                    if (described["parameters"] is JsonArray parameters)
                        foreach (var parameter in parameters.OfType<JsonObject>())
                            if (parameter["in"]?.GetValue<string>() == "path")
                            {
                                var index = originalNames.FindIndex(p => p.Name == parameter["name"]?.GetValue<string>());
                                if (index >= 0) parameter["name"] = canonicalNames[index].Name;
                            }
                    path = canonical;
                }
                else _pathTemplates[template] = path;
                if (Paths[path] is not JsonObject pathItem)
                {
                    pathItem = new JsonObject();
                    Paths[path] = pathItem;
                }

                pathItem[method] = described;
                foreach (var tag in (described["tags"] as JsonArray ?? new JsonArray()).Select(t => t?.GetValue<string>()))
                {
                    if (tag != null && !Tags.Contains(tag))
                        Tags.Add(tag);
                }
            }

            // Parameter spelling does not distinguish OpenAPI paths of the same shape.
            private static string TemplateKey(string path)
            {
                var key = new StringBuilder(path.Length);
                var parameter = false;
                foreach (var character in path)
                {
                    if (character == '{') { parameter = true; key.Append("{}"); }
                    else if (character == '}') parameter = false;
                    else if (!parameter) key.Append(character);
                }
                return key.ToString();
            }
            private JsonObject DescribeDeclared(OpenApiOperationDeclaration declaration, List<(string Name, bool Optional)> routeParameters, string where)
            {
                var result = new JsonObject();
                if (declaration.Tags is { Count: > 0 } tags)
                    result["tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());
                if (declaration.Summary != null)
                    result["summary"] = declaration.Summary;
                if (declaration.Description != null)
                    result["description"] = declaration.Description;
                if (declaration.OperationId != null)
                    result["operationId"] = UniqueOperationId(declaration.OperationId);
                if (declaration.Deprecated)
                    result["deprecated"] = true;

                var parameters = new JsonArray();
                foreach (var (name, optional) in routeParameters)
                {
                    var declared = declaration.Parameters.FirstOrDefault(p => p.Location == OpenApiParameterLocation.Path && p.Name == name);
                    var parameter = new JsonObject
                    {
                        ["name"] = name,
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = declared == null ? StringSchema() : ValueSchema(declared.Type, EnumNames(declared.Type)),
                    };
                    var description = declared?.Description ?? (optional ? "May be empty." : null);
                    if (description != null)
                        parameter["description"] = description;

                    parameters.Add((JsonNode)parameter);
                }

                var seen = new HashSet<(OpenApiParameterLocation, string)>();
                foreach (var declared in declaration.Parameters)
                {
                    // Header names are case-insensitive; other names are not.
                    var key = declared.Location == OpenApiParameterLocation.Header ? declared.Name.ToUpperInvariant() : declared.Name;
                    if (!seen.Add((declared.Location, key)))
                    {
                        Problems.Add($"{where}: parameter '{declared.Name}' is declared more than once.");
                        continue;
                    }

                    if (declared.Location == OpenApiParameterLocation.Path)
                    {
                        if (!routeParameters.Any(p => p.Name == declared.Name))
                            Problems.Add($"{where}: path parameter '{declared.Name}' is not in the route.");
                        continue;
                    }

                    if (declared.Location == OpenApiParameterLocation.Header && IsReservedHeader(declared.Name))
                    {
                        Problems.Add($"{where}: OpenAPI ignores a '{declared.Name}' header parameter; describe it with the request body, the response or a security scheme.");
                        continue;
                    }

                    var parameter = new JsonObject
                    {
                        ["name"] = declared.Name,
                        ["in"] = declared.Location switch
                        {
                            OpenApiParameterLocation.Query => "query",
                            OpenApiParameterLocation.Header => "header",
                            _ => "cookie",
                        },
                        ["required"] = declared.Required,
                        ["schema"] = ValueSchema(declared.Type, EnumNames(declared.Type)),
                    };
                    if (declared.Description != null)
                        parameter["description"] = declared.Description;

                    parameters.Add((JsonNode)parameter);
                }

                if (parameters.Count > 0)
                    result["parameters"] = parameters;

                if (declaration.Body is { } body)
                {
                    var media = Media(body.Type, body.ContentType, where);
                    if (media != null)
                    {
                        result["requestBody"] = new JsonObject
                        {
                            ["required"] = body.Required,
                            ["content"] = new JsonObject { [body.ContentType] = media },
                        };
                    }
                }

                if (declaration.Responses.Count == 0)
                {
                    Problems.Add(where + ": the declaration lists no responses. Declare every status the handler sends.");
                    return result;
                }

                var responses = new JsonObject();
                foreach (var declared in declaration.Responses)
                    AddResponse(responses, declared.StatusCode, declared.Description, declared.Type, declared.ContentType, where);

                result["responses"] = responses;
                return result;
            }

            private static bool IsReservedHeader(string name)
                => string.Equals(name, HttpHeaderNames.Accept, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.ContentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.Authorization, StringComparison.OrdinalIgnoreCase);

            private static IReadOnlyList<string> EnumNames(Type type)
            {
                var element = ElementType(type);
                return element.IsEnum ? Enum.GetNames(element) : Array.Empty<string>();
            }

            // A media type object for a body of the given type. JSON media types take a schema
            // from the serializer options; others describe text, binary content or nothing.
            private JsonObject? Media(Type? type, string contentType, string where)
            {
                var media = new JsonObject();
                if (type == null)
                    return media;

                if (IsJson(contentType))
                {
                    var schema = BodySchema(type, where);
                    if (schema == null)
                        return null;

                    media["schema"] = schema;
                    return media;
                }

                if (type == typeof(string))
                {
                    media["schema"] = StringSchema();
                    return media;
                }

                // OpenAPI 3.1 describes binary content by its media type alone.
                if (type == typeof(byte[]) || typeof(System.IO.Stream).IsAssignableFrom(type))
                    return media;

                Problems.Add($"{where}: a {type.FullName} body cannot be described as {contentType}; use a JSON media type, a string, a byte array or a stream.");
                return null;
            }

            private static bool IsJson(string contentType)
            {
                var semicolon = EmbedIO.Internal.StringOperations.IndexOfOrdinal(contentType, ';');
                var mediaType = (semicolon < 0 ? contentType : contentType.Substring(0, semicolon)).Trim();
                return string.Equals(mediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase)
                    || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
            }

            private JsonObject Describe(WebApiOperation operation, List<(string Name, bool Optional)> routeParameters, string where)
            {
                var controller = TrimSuffix(operation.ControllerName, "Controller");
                var metadata = operation.Operation;
                var result = new JsonObject();
                var tags = metadata?.Tags is { Length: > 0 } explicitTags ? explicitTags : new[] { controller };
                result["tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());

                if (metadata?.Summary != null)
                    result["summary"] = metadata.Summary;
                if (metadata?.Description != null)
                    result["description"] = metadata.Description;

                result["operationId"] = UniqueOperationId(metadata?.OperationId ?? controller + "_" + operation.MethodName);
                if (metadata?.Deprecated == true)
                    result["deprecated"] = true;

                var parameters = new JsonArray();
                var canFailBinding = false;
                var queryParameters = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var (name, optional) in routeParameters)
                {
                    var bound = operation.Parameters.FirstOrDefault(p => p.Source == WebApiParameterSource.RouteOrDefault && p.FieldName == name);
                    var parameter = new JsonObject
                    {
                        ["name"] = name,
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = bound == null ? StringSchema() : ValueSchema(bound.Type, bound.EnumNames),
                    };
                    if (optional)
                        parameter["description"] = "May be empty.";

                    canFailBinding |= bound != null && bound.Type != typeof(string);
                    parameters.Add((JsonNode)parameter);
                }

                JsonObject? formProperties = null;
                List<string>? formRequired = null;
                var formIsOpen = false;
                WebApiOperationParameter? jsonBody = null;
                var multipart = false;
                foreach (var parameter in operation.Parameters)
                {
                    switch (parameter.Source)
                    {
                        case WebApiParameterSource.Query:
                            AddQueryParameter(parameters, queryParameters, new JsonObject
                            {
                                ["name"] = parameter.FieldName,
                                ["in"] = "query",
                                ["required"] = parameter.IsRequired,
                                ["schema"] = ValueSchema(parameter.Type, parameter.EnumNames),
                            }, where);
                            canFailBinding |= parameter.IsRequired || ElementType(parameter.Type) != typeof(string);
                            break;

                        case WebApiParameterSource.QueryCollection:
                            AddQueryParameter(parameters, queryParameters, new JsonObject
                            {
                                ["name"] = parameter.Name,
                                ["in"] = "query",
                                ["style"] = "form",
                                ["explode"] = true,
                                ["schema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = StringSchema() },
                            }, where);
                            break;

                        case WebApiParameterSource.Form:
                            formProperties ??= new JsonObject();
                            formRequired ??= new List<string>();
                            var formSchema = ValueSchema(parameter.Type, parameter.EnumNames);
                            // Two parameters can bind one field, as for query fields; JSON Schema's required list must be unique.
                            if (formProperties.TryGetPropertyValue(parameter.FieldName, out var bound) && !JsonNode.DeepEquals(bound, formSchema))
                            {
                                Problems.Add($"{where}: form field '{parameter.FieldName}' has conflicting binding schemas.");
                                break;
                            }

                            formProperties[parameter.FieldName] = formSchema;
                            if (parameter.IsRequired && !formRequired.Contains(parameter.FieldName))
                                formRequired.Add(parameter.FieldName);
                            canFailBinding |= parameter.IsRequired || ElementType(parameter.Type) != typeof(string);
                            break;

                        case WebApiParameterSource.FormCollection:
                            formProperties ??= new JsonObject();
                            formIsOpen = true;
                            break;

                        case WebApiParameterSource.JsonBody:
                            if (jsonBody != null)
                                Problems.Add(where + ": more than one parameter reads the request body as JSON.");
                            jsonBody = parameter;
                            canFailBinding = true;
                            break;

                        case WebApiParameterSource.Multipart:
                            if (multipart)
                                Problems.Add(where + ": more than one parameter reads the multipart request body.");
                            multipart = true;
                            canFailBinding = true;
                            break;

                        // A service the dependency injection package supplies reads nothing from the request.
                        case WebApiParameterSource.Custom when parameter.CustomAttribute == FromServicesAttributeName:
                            break;

                        case WebApiParameterSource.Custom:
                            Problems.Add($"{where}: parameter '{parameter.Name}' is bound by {parameter.CustomAttribute}, which the document cannot describe. Add [OpenApiIgnore] to leave the operation out.");
                            break;
                    }
                }

                if (parameters.Count > 0)
                    result["parameters"] = parameters;

                if (multipart && (jsonBody != null || formProperties != null))
                {
                    Problems.Add(where + ": a streaming multipart reader cannot share the request body with another body binding.");
                }
                else if (multipart)
                {
                    result["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["description"] = "Streaming multipart form with application-defined field names, repeated fields and binary file parts. Upload limits are enforced while reading.",
                        ["content"] = new JsonObject
                        {
                            ["multipart/form-data"] = new JsonObject
                            {
                                ["schema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                            },
                        },
                    };
                }
                else if (jsonBody != null && formProperties != null)
                {
                    Problems.Add(where + ": the method reads the request body both as JSON and as a form.");
                }
                else if (jsonBody != null)
                {
                    var schema = BodySchema(jsonBody.Type, where);
                    if (schema != null)
                    {
                        result["requestBody"] = new JsonObject
                        {
                            ["required"] = jsonBody.IsRequired,
                            ["content"] = new JsonObject { [JsonMediaType] = new JsonObject { ["schema"] = schema } },
                        };
                    }
                }
                else if (formProperties != null)
                {
                    var schema = new JsonObject { ["type"] = "object", ["properties"] = formProperties };
                    if (formRequired is { Count: > 0 })
                        schema["required"] = new JsonArray(formRequired.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
                    if (formIsOpen)
                        schema["additionalProperties"] = StringSchema();

                    result["requestBody"] = new JsonObject
                    {
                        ["required"] = formRequired is { Count: > 0 },
                        ["content"] = new JsonObject { [FormMediaType] = new JsonObject { ["schema"] = schema } },
                    };
                }

                var responses = Responses(operation, canFailBinding, where);
                if (multipart)
                {
                    if (!responses.ContainsKey("413"))
                        responses["413"] = new JsonObject { ["description"] = "A configured multipart upload limit was exceeded." };
                    if (!responses.ContainsKey("415"))
                        responses["415"] = new JsonObject { ["description"] = "The request content type is not multipart/form-data." };
                }
                result["responses"] = responses;
                return result;
            }

            private void AddQueryParameter(JsonArray parameters, Dictionary<string, JsonObject> queries, JsonObject parameter, string where)
            {
                var name = parameter["name"]?.GetValue<string>() ?? throw new InvalidOperationException("A query parameter must have a name.");
                if (!queries.TryGetValue(name, out var existing))
                {
                    queries.Add(name, parameter);
                    parameters.Add((JsonNode)parameter);
                    return;
                }
                if (!JsonNode.DeepEquals(existing["schema"], parameter["schema"])
                    || !JsonNode.DeepEquals(existing["style"], parameter["style"])
                    || !JsonNode.DeepEquals(existing["explode"], parameter["explode"]))
                {
                    Problems.Add($"{where}: query parameter '{name}' has conflicting binding schemas.");
                    return;
                }
                if (parameter["required"]?.GetValue<bool>() == true) existing["required"] = true;
            }
            private JsonObject Responses(WebApiOperation operation, bool canFailBinding, string where)
            {
                // The success response described from the return type comes first, unless a 2xx response is declared.
                var responses = new JsonObject();
                if (!operation.Responses.Any(r => r.StatusCode >= 200 && r.StatusCode < 300))
                {
                    var success = new JsonObject { ["description"] = "OK" };
                    if (operation.ResultType != null && IsPolymorphic(operation.ResultType))
                    {
                        Problems.Add($"{where}: {operation.ResultType.FullName} is polymorphic, but a returned value is serialized as its runtime type, without the type discriminator its schema requires. Add [OpenApiIgnore] to leave the operation out.");
                    }
                    else if (operation.ResultType != null)
                    {
                        var schema = BodySchema(operation.ResultType, where);
                        if (schema != null)
                            success["content"] = new JsonObject { [JsonMediaType] = new JsonObject { ["schema"] = schema } };
                    }

                    responses["200"] = success;
                }

                foreach (var declared in operation.Responses)
                    AddResponse(responses, declared.StatusCode, declared.Description, declared.Type, declared.ContentType, where);

                if (canFailBinding && !responses.ContainsKey("400"))
                    responses["400"] = new JsonObject { ["description"] = "The request data could not be bound to the method's parameters." };

                return responses;
            }

            private void AddResponse(JsonObject responses, int statusCode, string? description, Type? type, string? contentType, string where)
            {
                var key = statusCode.ToString(CultureInfo.InvariantCulture);
                if (statusCode < 100 || statusCode > 599)
                {
                    Problems.Add($"{where}: response status {key} is not between 100 and 599.");
                    return;
                }

                if (responses.ContainsKey(key))
                {
                    Problems.Add($"{where}: response status {key} is declared more than once.");
                    return;
                }

                var phrase = EmbedIO.Net.Internal.HttpListenerResponseHelper.GetStatusDescription(statusCode);
                var response = new JsonObject { ["description"] = description ?? (phrase.Length > 0 ? phrase : "Status " + key) };
                if (type != null || contentType != null)
                {
                    var mediaType = contentType ?? JsonMediaType;
                    var media = Media(type, mediaType, where);
                    if (media != null)
                        response["content"] = new JsonObject { [mediaType] = media };
                }

                responses[key] = response;
            }

            // A JSON body schema: a reference to a component exported from the serializer's metadata.
            private JsonNode? BodySchema(Type type, string where)
            {
                if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonDocument))
                    return new JsonObject();

                if (_schemaNames.TryGetValue(type, out var existing))
                    return Reference(existing);

                // GetTypeInfo uses the options' resolver, or the reflection-based default where
                // the application allows reflection; otherwise it throws.
                JsonTypeInfo typeInfo;
                try
                {
                    typeInfo = _options.GetTypeInfo(type);
                }
                catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
                {
                    Problems.Add($"{where}: the JSON serializer options have no metadata for {type.FullName}. Add it to the JsonSerializerContext.");
                    return null;
                }

                // Some options cannot be exported (ReferenceHandler.Preserve, for one); report them like missing metadata.
                JsonNode schema;
                try
                {
                    schema = JsonSchemaExporter.GetJsonSchemaAsNode(typeInfo, ExporterOptions);
                }
                catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
                {
                    Problems.Add($"{where}: the schema exporter cannot describe {type.FullName} with these JSON serializer options: {error.Message}");
                    return null;
                }

                var name = UniqueSchemaName(type);
                _schemaNames.Add(type, name);
                Schemas[name] = RebaseReferences(schema, "#/components/schemas/" + name);
                return Reference(name);
            }

            // A returned value is serialized through its runtime type's contract, which leaves out the
            // discriminator a polymorphic base type's schema requires.
            private bool IsPolymorphic(Type type)
            {
                try
                {
                    return _options.GetTypeInfo(type).PolymorphismOptions != null;
                }
                catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
                {
                    return false; // BodySchema reports the missing metadata.
                }
            }

            private static readonly JsonSchemaExporterOptions ExporterOptions = new() { TransformSchemaNode = DescribeCompatibleValue };

            // The exporter cannot see inside custom converters and describes their values as
            // anything. EmbedIO's default options convert strings, booleans, enums and dates this
            // way; the schema states what those converters write.
            private static JsonNode DescribeCompatibleValue(JsonSchemaExporterContext context, JsonNode schema)
            {
                var typeInfo = context.TypeInfo;
                var type = Nullable.GetUnderlyingType(typeInfo.Type);
                var converter = type == null ? typeInfo.Converter : typeInfo.Options.GetTypeInfo(type).Converter;
                var declaringType = converter.GetType().DeclaringType;
                if (declaringType == typeof(EmbedIO.Serialization.Json) && typeInfo.Type == typeof(string))
                {
                    // It writes what the built-in string converter writes, so the exporter's own nullability rule applies.
                    var nullable = context.PropertyInfo is { } property
                        ? property.IsGetNullable || property.IsSetNullable
                        : !ExporterOptions.TreatNullObliviousAsNonNullable;
                    return nullable ? new JsonObject { ["type"] = new JsonArray((JsonNode?)"string", (JsonNode?)"null") } : StringSchema();
                }

                if (declaringType != typeof(EmbedIO.Serialization.CompatibleJsonConverterFactory))
                    return schema;

                var valueType = type ?? typeInfo.Type;
                var written = valueType == typeof(bool) ? new JsonObject { ["type"] = "boolean" }
                    : valueType.IsEnum ? new JsonObject { ["type"] = "integer" }
                    : new JsonObject { ["type"] = "string", ["format"] = "date-time" };
                if (type != null)
                    written["type"] = new JsonArray((JsonNode?)written["type"]?.GetValue<string>(), (JsonNode?)"null");

                return written;
            }

            private string UniqueSchemaName(Type type)
            {
                var baseName = ComponentKey(SchemaName(type));
                var name = baseName;
                for (var i = 2; Schemas.ContainsKey(name); i++)
                    name = baseName + i.ToString(CultureInfo.InvariantCulture);

                return name;
            }

            // Component keys must match ^[a-zA-Z0-9.\-_]+$ (OpenAPI Components Object); C# type names need not.
            private static string ComponentKey(string name)
            {
                var key = new StringBuilder(name.Length);
                foreach (var character in name)
                    key.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' ? character : '_');

                return key.ToString();
            }

            private static string SchemaName(Type type)
            {
                if (type.IsArray)
                    return SchemaName(type.GetElementType() ?? typeof(object)) + "Array";

                if (!type.IsGenericType)
                    return type.Name;

                var name = type.Name;
                var tick = EmbedIO.Internal.StringOperations.IndexOfOrdinal(name, '`');
                var builder = new StringBuilder(tick < 0 ? name : name.Substring(0, tick));
                foreach (var argument in type.GetGenericArguments())
                    builder.Append("Of").Append(SchemaName(argument));

                return builder.ToString();
            }

            // JsonSchemaExporter writes references relative to the exported schema's root.
            private static JsonNode? RebaseReferences(JsonNode? node, string root)
            {
                switch (node)
                {
                    case JsonObject value:
                        foreach (var property in value.ToArray())
                        {
                            if (property.Key == "$ref" && property.Value is JsonValue reference && reference.TryGetValue<string>(out var text) && text.StartsWith("#", StringComparison.Ordinal))
                                value["$ref"] = root + text.Substring(1);
                            else
                                RebaseReferences(property.Value, root);
                        }

                        break;

                    case JsonArray array:
                        foreach (var item in array)
                            RebaseReferences(item, root);
                        break;
                }

                return node;
            }

            private string UniqueOperationId(string candidate)
            {
                var id = candidate;
                for (var i = 2; !_operationIds.Add(id); i++)
                    id = candidate + "_" + i.ToString(CultureInfo.InvariantCulture);

                return id;
            }

            private static JsonObject Reference(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

            private static JsonObject StringSchema() => new() { ["type"] = "string" };

            private static Type ElementType(Type type)
            {
                if (type.IsArray)
                    type = type.GetElementType() ?? type;

                return Nullable.GetUnderlyingType(type) ?? type;
            }

            // The schema of a value converted from a route, query or form string.
            private static JsonObject ValueSchema(Type type, IReadOnlyList<string> enumNames)
            {
                if (type.IsArray)
                    return new JsonObject { ["type"] = "array", ["items"] = ValueSchema(type.GetElementType() ?? typeof(string), enumNames) };

                type = Nullable.GetUnderlyingType(type) ?? type;
                if (type.IsEnum)
                    return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(enumNames.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()) };

                static JsonObject Of(string kind, string? format = null)
                {
                    var schema = new JsonObject { ["type"] = kind };
                    if (format != null)
                        schema["format"] = format;
                    return schema;
                }

                if (type == typeof(bool)) return Of("boolean");
                if (type == typeof(int) || type == typeof(short) || type == typeof(sbyte) || type == typeof(byte) || type == typeof(ushort)) return Of("integer", "int32");
                if (type == typeof(long) || type == typeof(uint)) return Of("integer", "int64");
                if (type == typeof(ulong)) return Of("integer");
                if (type == typeof(float)) return Of("number", "float");
                if (type == typeof(double)) return Of("number", "double");
                if (type == typeof(decimal)) return Of("number");
                if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return Of("string", "date-time");
                if (type == typeof(Guid)) return Of("string", "uuid");
                if (type == typeof(Uri)) return Of("string", "uri-reference");
                if (type == typeof(char))
                {
                    var single = Of("string");
                    single["minLength"] = 1;
                    single["maxLength"] = 1;
                    return single;
                }

                switch (type.FullName)
                {
                    case "System.DateOnly": return Of("string", "date");
                    case "System.TimeOnly": return Of("string", "time");
                    case "System.Int128":
                    case "System.UInt128": return Of("integer");
                    case "System.Half": return Of("number");
                    default: return Of("string");
                }
            }

            private static (string Path, List<(string Name, bool Optional)> Parameters) PathTemplate(string route)
            {
                var path = new StringBuilder();
                var parameters = new List<(string, bool)>();
                var position = 0;
                while (position < route.Length)
                {
                    var open = route.IndexOf('{', position);
                    var close = open < 0 ? -1 : route.IndexOf('}', open);
                    if (open < 0 || close < 0)
                    {
                        path.Append(route, position, route.Length - position);
                        break;
                    }

                    path.Append(route, position, open - position);
                    var name = route.Substring(open + 1, close - open - 1);
                    var optional = name.EndsWith("?", StringComparison.Ordinal);
                    if (optional)
                        name = name.Substring(0, name.Length - 1);

                    path.Append('{').Append(name).Append('}');
                    parameters.Add((name, optional));
                    position = close + 1;
                }

                return (path.Length == 0 ? "/" : path.ToString(), parameters);
            }

            private static string TrimSuffix(string value, string suffix)
                => value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.Ordinal) ? value.Substring(0, value.Length - suffix.Length) : value;
        }
    }
}
