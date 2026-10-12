using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Security metadata and declared operations in OpenAPI documents.
    [TestFixture]
    public sealed class Issue139_OpenApiSecurity
    {
        private static BasicAuthenticationModule ApiAuthentication()
            => new BasicAuthenticationModule("/api", "People").WithAccount("ada", "secret");

        private static OpenApiDocumentBuilder Builder()
            => new OpenApiDocumentBuilder("People", "1.0").AddController("/api", Issue139Bindings.Issue139PeopleController);

        private static JsonNode Operation(OpenApiDocument document, string path, string method)
            => document.Root["paths"]?[path]?[method] ?? throw new AssertionException($"No {method} {path} in the document.");

        private static string? Security(JsonNode operation) => operation["security"]?.ToJsonString();

        // Written where CI's independent OpenAPI validator reads it; see .github/workflows/ci.yml.
        private static void WriteForValidation(string name, OpenApiDocument document)
        {
            var folder = Environment.GetEnvironmentVariable("EMBEDIO_OPENAPI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "openapi");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name), document.ToJson(indented: true));
        }

        private static OpenApiDocument SecuredDocument(BasicAuthenticationModule module)
            => Builder()
                .AddBasicAuthentication("basic", module)
                .AddOperation(HttpVerbs.Get, "/public/status", o => o.Response(200, "The service is up.", typeof(string), "text/plain"))
                .Build(Issue139Json.Default.Options);

        [Test]
        public void SchemesAreWrittenAsComponents()
        {
            var document = new OpenApiDocumentBuilder("Schemes", "1.0")
                .AddSecurityScheme("basic", OpenApiSecurityScheme.Basic())
                .AddSecurityScheme("jwt", OpenApiSecurityScheme.Bearer("JWT", "A signed token."))
                .AddSecurityScheme("header-key", OpenApiSecurityScheme.ApiKey("X-Key", OpenApiParameterLocation.Header))
                .AddSecurityScheme("query.key", OpenApiSecurityScheme.ApiKey("key", OpenApiParameterLocation.Query))
                .AddSecurityScheme("cookie_key", OpenApiSecurityScheme.ApiKey("key", OpenApiParameterLocation.Cookie))
                .Build(Issue139Json.Default.Options);
            Assert.That(document.Root["components"]?["securitySchemes"]?.ToJsonString(), Is.EqualTo(
                "{\"basic\":{\"type\":\"http\",\"scheme\":\"basic\"},"
                + "\"jwt\":{\"type\":\"http\",\"scheme\":\"bearer\",\"bearerFormat\":\"JWT\",\"description\":\"A signed token.\"},"
                + "\"header-key\":{\"type\":\"apiKey\",\"name\":\"X-Key\",\"in\":\"header\"},"
                + "\"query.key\":{\"type\":\"apiKey\",\"name\":\"key\",\"in\":\"query\"},"
                + "\"cookie_key\":{\"type\":\"apiKey\",\"name\":\"key\",\"in\":\"cookie\"}}"));
            Assert.That(document.Root["components"]?["schemas"], Is.Null);
        }

        [Test]
        public void SchemeAndRequirementArgumentsAreValidated()
        {
            Assert.Throws<ArgumentException>(() => OpenApiSecurityScheme.ApiKey("key", OpenApiParameterLocation.Path));
            Assert.Throws<ArgumentException>(() => OpenApiSecurityScheme.ApiKey(string.Empty, OpenApiParameterLocation.Header));
            var builder = new OpenApiDocumentBuilder("t", "1").AddSecurityScheme("basic", OpenApiSecurityScheme.Basic());
            Assert.Throws<ArgumentException>(() => builder.AddSecurityScheme("basic", OpenApiSecurityScheme.Basic()));
            Assert.Throws<ArgumentException>(() => builder.AddSecurityScheme("has space", OpenApiSecurityScheme.Basic()));
            Assert.Throws<ArgumentException>(() => builder.AddSecurityScheme(string.Empty, OpenApiSecurityScheme.Basic()));
            Assert.Throws<ArgumentException>(() => builder.RequireSecurity("/api", "unknown"));
            Assert.Throws<ArgumentException>(() => builder.RequireSecurity(ApiAuthentication(), "unknown"));
            Assert.Throws<ArgumentException>(() => builder.AddBasicAuthentication("basic", ApiAuthentication()));
            Assert.Throws<FormatException>(() => builder.RequireSecurity("api", "basic"));
            Assert.Throws<InvalidOperationException>(() => builder.AddOperation(HttpVerbs.Post, "/x", o => o.RequestBody(null).RequestBody(null)));
        }

        // The module's own matcher decides: everything under /api, nothing outside it.
        [Test]
        public void BasicAuthenticationCoversWhatTheModuleHandles()
        {
            var document = Builder()
                .AddBasicAuthentication("basic", ApiAuthentication())
                .AddOperation(HttpVerbs.Get, "/public/status", o => o.Response(200))
                .AddOperation(HttpVerbs.Get, "/apiary", o => o.Response(200))
                .Build(Issue139Json.Default.Options);

            Assert.That(document.Root["components"]?["securitySchemes"]?["basic"]?.ToJsonString(),
                Is.EqualTo("{\"type\":\"http\",\"scheme\":\"basic\",\"description\":\"HTTP Basic authentication, realm \\u0022People\\u0022.\"}"));
            foreach (var (path, method) in new[] { ("/api/people", "get"), ("/api/people", "post"), ("/api/people/{index}", "get"), ("/api/people/{key}", "delete"), ("/api/tree", "get") })
            {
                var operation = Operation(document, path, method);
                Assert.That(Security(operation), Is.EqualTo("[{\"basic\":[]}]"), method + " " + path);
                var unauthorized = operation["responses"]?["401"];
                Assert.That(unauthorized?["description"]?.GetValue<string>(), Is.EqualTo("The credentials are missing or wrong."), method + " " + path);
                Assert.That(unauthorized?["headers"]?["WWW-Authenticate"]?["schema"]?["type"]?.GetValue<string>(), Is.EqualTo("string"));
                Assert.That(unauthorized?["content"], Is.Null);
            }

            foreach (var path in new[] { "/public/status", "/apiary" })
            {
                var operation = Operation(document, path, "get");
                Assert.That(operation["security"], Is.Null, path);
                Assert.That(operation["responses"]?["401"], Is.Null, path);
            }
        }

        // Nested authentication: both modules run, so both schemes are required together.
        [Test]
        public void OverlappingRequirementsAreAllRequired()
        {
            var document = Builder()
                .AddBasicAuthentication("basic", ApiAuthentication())
                .AddSecurityScheme("key", OpenApiSecurityScheme.ApiKey("X-Key", OpenApiParameterLocation.Header))
                .RequireSecurity("/api/people", "key")
                .RequireSecurity("/api/people/", "key")
                .AddOperation(HttpVerbs.Get, "/api/declared", o => o.Response(200).Response(401, "Sign in first."))
                .Build(Issue139Json.Default.Options);

            Assert.That(Security(Operation(document, "/api/people", "get")), Is.EqualTo("[{\"basic\":[],\"key\":[]}]"));
            Assert.That(Security(Operation(document, "/api/people/{index}", "get")), Is.EqualTo("[{\"basic\":[],\"key\":[]}]"));
            Assert.That(Security(Operation(document, "/api/tree", "get")), Is.EqualTo("[{\"basic\":[]}]"));

            // A declared 401 is kept rather than replaced.
            var declared = Operation(document, "/api/declared", "get");
            Assert.That(Security(declared), Is.EqualTo("[{\"basic\":[]}]"));
            Assert.That(declared["responses"]?["401"]?["description"]?.GetValue<string>(), Is.EqualTo("Sign in first."));

            // A requirement that is not Basic authentication adds no 401 of its own.
            var keyOnly = new OpenApiDocumentBuilder("t", "1")
                .AddSecurityScheme("key", OpenApiSecurityScheme.ApiKey("X-Key", OpenApiParameterLocation.Header))
                .RequireSecurity(ApiAuthentication(), "key")
                .AddOperation(HttpVerbs.Get, "/api/x", o => o.Response(200))
                .Build(Issue139Json.Default.Options);
            Assert.That(Security(Operation(keyOnly, "/api/x", "get")), Is.EqualTo("[{\"key\":[]}]"));
            Assert.That(Operation(keyOnly, "/api/x", "get")["responses"]?["401"], Is.Null);
        }

        [Test]
        public void DeclaredOperationsDescribeParametersBodiesAndResponses()
        {
            var document = new OpenApiDocumentBuilder("Files", "1.0")
                .AddOperation(HttpVerbs.Get, "/files/{name}/{part?}", o =>
                {
                    o.Summary = "Read a file";
                    o.Description = "Reads part of a file.";
                    o.OperationId = "ReadFile";
                    o.Tags = new[] { "Files" };
                    o.Parameter("part", OpenApiParameterLocation.Path, typeof(int), description: "The part's index.")
                        .Parameter("status", OpenApiParameterLocation.Query, typeof(Issue139Status[]))
                        .Parameter("X-Trace", OpenApiParameterLocation.Header, typeof(Guid), required: true)
                        .Parameter("session", OpenApiParameterLocation.Cookie, typeof(string))
                        .Response(200, type: typeof(string), contentType: "text/plain; charset=utf-8")
                        .Response(206, type: typeof(byte[]), contentType: "application/octet-stream")
                        .Response(404);
                })
                .AddOperation(HttpVerbs.Post, "/files", o =>
                {
                    o.Deprecated = true;
                    o.RequestBody(typeof(Issue139Person))
                        .Response(201, "Stored.", typeof(Issue139Person))
                        .Response(415, contentType: "text/html");
                })
                .AddOperation(HttpVerbs.Put, "/files/{name}", o => o.RequestBody(typeof(Stream), "application/octet-stream", required: false).Response(204))
                .Build(Issue139Json.Default.Options);

            WriteForValidation("declared.json", document);
            var read = Operation(document, "/files/{name}/{part}", "get");
            Assert.That(read["summary"]?.GetValue<string>(), Is.EqualTo("Read a file"));
            Assert.That(read["description"]?.GetValue<string>(), Is.EqualTo("Reads part of a file."));
            Assert.That(read["operationId"]?.GetValue<string>(), Is.EqualTo("ReadFile"));
            Assert.That(read["tags"]?.ToJsonString(), Is.EqualTo("[\"Files\"]"));
            Assert.That(read["parameters"]?.ToJsonString(), Is.EqualTo(
                "[{\"name\":\"name\",\"in\":\"path\",\"required\":true,\"schema\":{\"type\":\"string\"}},"
                + "{\"name\":\"part\",\"in\":\"path\",\"required\":true,\"schema\":{\"type\":\"integer\",\"format\":\"int32\"},\"description\":\"The part\\u0027s index.\"},"
                + "{\"name\":\"status\",\"in\":\"query\",\"required\":false,\"schema\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"Active\",\"Retired\"]}}},"
                + "{\"name\":\"X-Trace\",\"in\":\"header\",\"required\":true,\"schema\":{\"type\":\"string\",\"format\":\"uuid\"}},"
                + "{\"name\":\"session\",\"in\":\"cookie\",\"required\":false,\"schema\":{\"type\":\"string\"}}]"));
            Assert.That(read["responses"]?.ToJsonString(), Is.EqualTo(
                "{\"200\":{\"description\":\"OK\",\"content\":{\"text/plain; charset=utf-8\":{\"schema\":{\"type\":\"string\"}}}},"
                + "\"206\":{\"description\":\"Partial Content\",\"content\":{\"application/octet-stream\":{}}},"
                + "\"404\":{\"description\":\"Not Found\"}}"));
            Assert.That(read["requestBody"], Is.Null);
            Assert.That(read["deprecated"], Is.Null);

            var store = Operation(document, "/files", "post");
            Assert.That(store["deprecated"]?.GetValue<bool>(), Is.True);
            Assert.That(store["operationId"], Is.Null);
            Assert.That(store["tags"], Is.Null);
            Assert.That(store["requestBody"]?.ToJsonString(), Is.EqualTo("{\"required\":true,\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"#/components/schemas/Issue139Person\"}}}}"));
            Assert.That(store["responses"]?["201"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>(), Is.EqualTo("#/components/schemas/Issue139Person"));
            Assert.That(store["responses"]?["415"]?.ToJsonString(), Is.EqualTo("{\"description\":\"Unsupported Media Type\",\"content\":{\"text/html\":{}}}"));

            var upload = Operation(document, "/files/{name}", "put");
            Assert.That(upload["requestBody"]?.ToJsonString(), Is.EqualTo("{\"required\":false,\"content\":{\"application/octet-stream\":{}}}"));
            Assert.That(document.Root["components"]?["schemas"]?["Issue139Person"], Is.Not.Null);
            Assert.That(document.Root["tags"]?.ToJsonString(), Is.EqualTo("[{\"name\":\"Files\"}]"));
        }

        [Test]
        public void DeclarationsThatCannotBeDescribedAreReported()
        {
            var builder = Builder()
                .AddOperation(HttpVerbs.Get, "/silent", _ => { })
                .AddOperation(HttpVerbs.Get, "/orphan", o => o.Parameter("id", OpenApiParameterLocation.Path, typeof(int)).Response(200))
                .AddOperation(HttpVerbs.Get, "/twice", o => o.Parameter("X-A", OpenApiParameterLocation.Header, typeof(string)).Parameter("x-a", OpenApiParameterLocation.Header, typeof(string)).Response(200))
                .AddOperation(HttpVerbs.Get, "/auth-header", o => o.Parameter("authorization", OpenApiParameterLocation.Header, typeof(string)).Response(200))
                .AddOperation(HttpVerbs.Any, "/any", o => o.Response(200))
                .AddOperation(HttpVerbs.Query, "/query", o => o.Response(200))
                .AddOperation(HttpVerbs.Get, "no-slash", o => o.Response(200))
                .AddOperation(HttpVerbs.Post, "/text-person", o => o.RequestBody(typeof(Issue139Person), "text/plain").Response(200))
                .AddOperation(HttpVerbs.Get, "/api/people", o => o.Response(200))
                .AddOperation(HttpVerbs.Get, "/values", o => o.Response(200, type: typeof(Issue139Values)))
                .AddOperation(HttpVerbs.Get, "/fine", o => o.Response(200));
            var error = Assert.Throws<OpenApiDocumentException>(() => builder.Build(Issue139Json.Default.Options));
            var problems = error?.Problems ?? Array.Empty<string>();
            Assert.That(problems, Has.Count.EqualTo(10));
            Assert.That(problems, Has.Some.StartsWith("GET /silent (declared): the declaration lists no responses."));
            Assert.That(problems, Has.Some.EqualTo("GET /orphan (declared): path parameter 'id' is not in the route."));
            Assert.That(problems, Has.Some.EqualTo("GET /twice (declared): parameter 'x-a' is declared more than once."));
            Assert.That(problems, Has.Some.StartsWith("GET /auth-header (declared): OpenAPI ignores a 'authorization' header parameter"));
            Assert.That(problems, Has.Some.StartsWith("ANY /any (declared): HttpVerbs.Any answers every method"));
            Assert.That(problems, Has.Some.StartsWith("QUERY /query (declared): OpenAPI 3.1 has no operation for the QUERY method."));
            Assert.That(problems, Has.Some.EqualTo("GET no-slash (declared): the route is not a valid EmbedIO route."));
            Assert.That(problems, Has.Some.StartsWith("POST /text-person (declared): a EmbedIO.Tests.Issues.Issue139Person body cannot be described as text/plain"));
            Assert.That(problems, Has.Some.StartsWith("GET /api/people (declared): another operation already uses this method and path"));
            Assert.That(problems, Has.Some.StartsWith("GET /values (declared): the JSON serializer options have no metadata for EmbedIO.Tests.Issues.Issue139Values"));

            var partial = builder.Build(Issue139Json.Default.Options, allowIncomplete: true);
            var paths = partial.Root["paths"]?.AsObject().Select(p => p.Key).ToArray();
            Assert.That(paths, Does.Contain("/fine").And.Contain("/api/people"));
            Assert.That(paths, Has.None.EqualTo("/silent").And.None.EqualTo("/orphan").And.None.EqualTo("/twice").And.None.EqualTo("/auth-header")
                .And.None.EqualTo("/any").And.None.EqualTo("/query").And.None.EqualTo("/text-person").And.None.EqualTo("/values"));

            // The controller's operation stays; the declaration did not replace it.
            Assert.That(Operation(partial, "/api/people", "get")["operationId"]?.GetValue<string>(), Is.EqualTo("Issue139People_List"));
        }

        // The served document's security, 401 and declared route answer as documented.
        [Test]
        public async Task SecuredServerAnswersAsDocumented()
        {
            var module = ApiAuthentication();
            var document = SecuredDocument(module);
            WriteForValidation("security.json", document);

            var routing = new RoutingModule("/");
            routing.OnGet("/public/status", false, (ctx, _) => ctx.SendStringAsync("up", "text/plain", WebServer.DefaultEncoding));
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(
                new OpenApiDocumentModule("/openapi.json", document),
                module,
                new WebApiModule("/api", ResponseSerializer.Json(Issue139Json.Default.Options)).WithController(Issue139Bindings.Issue139PeopleController),
                routing);
            using var client = new HttpClient();

            async Task<HttpResponseMessage> Send(string path, bool authenticated)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Url + path.TrimStart('/'));
                if (authenticated)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:secret")));

                return await client.SendAsync(request);
            }

            void AssertDocumented(string path, int status)
                => Assert.That(Operation(document, path, "get")["responses"]?.AsObject().ContainsKey(status.ToString(System.Globalization.CultureInfo.InvariantCulture)), Is.True, $"GET {path} answered {status}, which the document does not list.");

            Issue139PeopleController.People.Clear();
            Issue139PeopleController.People.Add(new Issue139Person { Name = "Ada", Age = 36 });

            using (var anonymous = await Send("/api/people", false))
            {
                Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(anonymous.Headers.WwwAuthenticate.ToString(), Does.StartWith("Basic realm=\"People\""));
                AssertDocumented("/api/people", 401);
            }

            using (var wrong = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, server.Url + "api/people/0") { Headers = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:wrong"))) } }))
            {
                Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                AssertDocumented("/api/people/{index}", 401);
            }

            using (var signedIn = await Send("/api/people", true))
            {
                Assert.That(signedIn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                AssertDocumented("/api/people", 200);
            }

            using (var missing = await Send("/api/people/9", true))
            {
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                AssertDocumented("/api/people/{index}", 404);
            }

            // Outside /api: no credentials needed, as the document says.
            using (var status = await Send("/public/status", false))
            {
                Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(status.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
                Assert.That(await status.Content.ReadAsByteArrayAsync(), Is.EqualTo(Encoding.ASCII.GetBytes("up")));
                AssertDocumented("/public/status", 200);
                Assert.That(Operation(document, "/public/status", "get")["security"], Is.Null);
            }

            using (var served = await Send("/openapi.json", false))
                Assert.That(await served.Content.ReadAsStringAsync(), Is.EqualTo(document.ToJson()));
        }
    }
}
