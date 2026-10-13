using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // OpenAPI documents built from generated controller bindings.
    [TestFixture]
    public sealed class Issue139_OpenApi
    {
        internal static OpenApiDocument PeopleDocument()
            => new OpenApiDocumentBuilder("People", "1.0") { Description = "Test API." }
                .AddServer("/")
                .AddController("/api", Issue139Bindings.Issue139PeopleController)
                .Build(Issue139Json.Default.Options);

        private static JsonNode Operation(OpenApiDocument document, string path, string method)
            => document.Root["paths"]?[path]?[method] ?? throw new AssertionException($"No {method} {path} in the document.");

        private static string[] Strings(JsonNode? array)
            => (array as JsonArray ?? new JsonArray()).Select(n => n?.GetValue<string>() ?? string.Empty).ToArray();

        // Written where CI's independent OpenAPI validator reads it; see .github/workflows/ci.yml.
        [Test]
        public void DocumentIsWrittenForIndependentValidation()
        {
            var document = PeopleDocument();
            var folder = Environment.GetEnvironmentVariable("EMBEDIO_OPENAPI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "openapi");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "people.json"), document.ToJson(indented: true));
            var values = new OpenApiDocumentBuilder("Values", "1.0").AddController("/api", Issue139Bindings.Issue139ValuesController).Build(EmbedIO.Serialization.Json.CreateOptions());
            File.WriteAllText(Path.Combine(folder, "values.json"), values.ToJson(indented: true));
            Assert.That(document.Problems, Is.Empty);
            var parsed = JsonNode.Parse(document.ToJson());
            Assert.That(parsed?["openapi"]?.GetValue<string>(), Is.EqualTo("3.1.0"));
            Assert.That(parsed?["info"]?["title"]?.GetValue<string>(), Is.EqualTo("People"));
            Assert.That(parsed?["info"]?["description"]?.GetValue<string>(), Is.EqualTo("Test API."));
            Assert.That(parsed?["servers"]?[0]?["url"]?.GetValue<string>(), Is.EqualTo("/"));
        }

        [Test]
        public void PathsAndMethodsFollowTheRoutes()
        {
            var paths = PeopleDocument().Root["paths"]?.AsObject() ?? throw new AssertionException("No paths.");
            var operations = paths.SelectMany(p => p.Value?.AsObject().Select(m => m.Key + " " + p.Key) ?? Enumerable.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal);
            Assert.That(operations, Is.EqualTo(new[]
            {
                "delete /api/people/{index}",
                "get /api/people",
                "get /api/people/{index}",
                "get /api/tree",
                "post /api/people",
                "put /api/people/{index}/name",
            }));
        }

        [Test]
        public void QueryParametersDescribeNamesTypesArraysAndEnums()
        {
            var list = Operation(PeopleDocument(), "/api/people", "get");
            Assert.That(list["summary"]?.GetValue<string>(), Is.EqualTo("List people"));
            Assert.That(Strings(list["tags"]), Is.EqualTo(new[] { "People", "Directory" }));
            Assert.That(list["operationId"]?.GetValue<string>(), Is.EqualTo("Issue139People_List"));
            var parameters = list["parameters"]?.AsArray() ?? throw new AssertionException("No parameters.");
            Assert.That(parameters.Select(p => p?["name"]?.GetValue<string>()), Is.EqualTo(new[] { "limit", "status", "query" }));
            Assert.That(parameters.Select(p => p?["in"]?.GetValue<string>()), Is.All.EqualTo("query"));
            Assert.That(parameters[0]?["schema"]?.ToJsonString(), Is.EqualTo("{\"type\":\"integer\",\"format\":\"int32\"}"));
            Assert.That(parameters[0]?["required"]?.GetValue<bool>(), Is.False);
            Assert.That(parameters[1]?["schema"]?.ToJsonString(), Is.EqualTo("{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"Active\",\"Retired\"]}}"));
            Assert.That(parameters[2]?["style"]?.GetValue<string>(), Is.EqualTo("form"));
            Assert.That(parameters[2]?["explode"]?.GetValue<bool>(), Is.True);
            Assert.That(parameters[2]?["schema"]?["additionalProperties"]?["type"]?.GetValue<string>(), Is.EqualTo("string"));
        }

        [Test]
        public void PathParametersAreRequiredAndOptionalOnesMayBeEmpty()
        {
            var document = PeopleDocument();
            var get = Operation(document, "/api/people/{index}", "get")["parameters"]?[0];
            Assert.That(get?["in"]?.GetValue<string>(), Is.EqualTo("path"));
            Assert.That(get?["required"]?.GetValue<bool>(), Is.True);
            Assert.That(get?["schema"]?["type"]?.GetValue<string>(), Is.EqualTo("integer"));
            Assert.That(get?["description"], Is.Null);
            var remove = Operation(document, "/api/people/{index}", "delete");
            Assert.That(remove["parameters"]?[0]?["required"]?.GetValue<bool>(), Is.True);
            Assert.That(remove["parameters"]?[0]?["description"]?.GetValue<string>(), Is.EqualTo("May be empty."));
            Assert.That(remove["operationId"]?.GetValue<string>(), Is.EqualTo("RemovePerson"));
            Assert.That(remove["deprecated"]?.GetValue<bool>(), Is.True);
            Assert.That(remove["description"]?.GetValue<string>(), Is.EqualTo("Removed by name."));
        }

        [Test]
        public void RequestBodiesDescribeJsonAndForms()
        {
            var document = PeopleDocument();
            var create = Operation(document, "/api/people", "post")["requestBody"];
            Assert.That(create?["required"]?.GetValue<bool>(), Is.True);
            Assert.That(create?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>(), Is.EqualTo("#/components/schemas/Issue139Person"));
            var rename = Operation(document, "/api/people/{index}/name", "put")["requestBody"];
            Assert.That(rename?["required"]?.GetValue<bool>(), Is.True);
            var form = rename?["content"]?["application/x-www-form-urlencoded"]?["schema"];
            Assert.That(form?["properties"]?["name"]?["type"]?.GetValue<string>(), Is.EqualTo("string"));
            Assert.That(form?["properties"]?["aliases"]?["type"]?.GetValue<string>(), Is.EqualTo("array"));
            Assert.That(Strings(form?["required"]), Is.EqualTo(new[] { "name" }));
        }

        [Test]
        public void ResponsesComeFromReturnTypesAndDeclarations()
        {
            var document = PeopleDocument();
            var get = Operation(document, "/api/people/{index}", "get")["responses"]?.AsObject() ?? throw new AssertionException("No responses.");
            Assert.That(get.Select(r => r.Key), Is.EqualTo(new[] { "200", "404", "400" }));
            Assert.That(get["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>(), Is.EqualTo("#/components/schemas/Issue139Person"));
            Assert.That(get["404"]?["description"]?.GetValue<string>(), Is.EqualTo("No person has that index."));
            Assert.That(get["404"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>(), Is.EqualTo("#/components/schemas/Issue139Error"));

            // A declared 2xx response replaces the one described from the return type.
            var create = Operation(document, "/api/people", "post")["responses"]?.AsObject() ?? throw new AssertionException("No responses.");
            Assert.That(create.Select(r => r.Key), Is.EqualTo(new[] { "201", "400" }));

            // No result, no content; nothing to bind, no 400.
            var remove = Operation(document, "/api/people/{index}", "delete")["responses"]?.AsObject() ?? throw new AssertionException("No responses.");
            Assert.That(remove.Select(r => r.Key), Is.EqualTo(new[] { "200" }));
            Assert.That(remove["200"]?["content"], Is.Null);
        }

        [Test]
        public void SchemasAreComponentsWithReferencesRebased()
        {
            var schemas = PeopleDocument().Root["components"]?["schemas"]?.AsObject() ?? throw new AssertionException("No schemas.");
            Assert.That(schemas.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "Issue139Error", "Issue139Person", "Issue139Tree", "ListOfIssue139Person" }));
            Assert.That(schemas["Issue139Tree"]?["properties"]?["Children"]?["items"]?["$ref"]?.GetValue<string>(), Is.EqualTo("#/components/schemas/Issue139Tree"));
            Assert.That(schemas["Issue139Person"]?["properties"]?["Name"]?["type"]?.GetValue<string>(), Is.EqualTo("string"));
        }

        // EmbedIO's default options convert booleans, enums and dates with their own converters,
        // which the schema exporter alone would describe as accepting anything.
        [Test]
        public void SchemasFollowTheSerializerOptions()
        {
            var context = PeopleDocument().Root["components"]?["schemas"]?["Issue139Person"]?["properties"]?["Status"];
            Assert.That(context?.ToJsonString(), Is.EqualTo("{\"type\":\"integer\"}"));

            var options = EmbedIO.Serialization.Json.CreateOptions();
            var defaults = new OpenApiDocumentBuilder("Values", "1.0").AddController("/api", Issue139Bindings.Issue139ValuesController).Build(options);
            var properties = defaults.Root["components"]?["schemas"]?["Issue139Values"]?["properties"];
            Assert.That(properties?["Flag"]?.ToJsonString(), Is.EqualTo("{\"type\":\"boolean\"}"));
            Assert.That(properties?["MaybeFlag"]?.ToJsonString(), Is.EqualTo("{\"type\":[\"boolean\",\"null\"]}"));
            Assert.That(properties?["Status"]?.ToJsonString(), Is.EqualTo("{\"type\":\"integer\"}"));
            Assert.That(properties?["When"]?.ToJsonString(), Is.EqualTo("{\"type\":\"string\",\"format\":\"date-time\"}"));
            Assert.That(properties?["Offset"]?.ToJsonString(), Is.EqualTo("{\"type\":[\"string\",\"null\"],\"format\":\"date-time\"}"));
            // The default options also read numbers written as strings, and the exporter says so.
            Assert.That(properties?["Count"]?["type"]?.ToJsonString(), Is.EqualTo("[\"string\",\"integer\"]"));

            // What the converters write is what the schema says.
            var written = JsonNode.Parse(EmbedIO.Serialization.Json.Serialize(new Issue139Values { Flag = true, Status = Issue139Status.Retired, When = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc) }, options));
            Assert.That(written?["Flag"]?.GetValueKind(), Is.EqualTo(JsonValueKind.True));
            JsonNode? maybe = JsonValue.Create(false);
            Assert.That(written?.AsObject().TryGetPropertyValue("MaybeFlag", out maybe), Is.True);
            Assert.That(maybe, Is.Null);
            Assert.That(written?["Status"]?.GetValue<int>(), Is.EqualTo(1));
            Assert.That(written?["When"]?.GetValue<string>(), Is.EqualTo("2026-10-12T00:00:00Z"));
        }

        [Test]
        public void IgnoredMethodsAndControllersAreLeftOut()
        {
            var document = new OpenApiDocumentBuilder("People", "1.0")
                .AddController("/api", Issue139Bindings.Issue139PeopleController)
                .AddController("/hidden", Issue139Bindings.Issue139HiddenController)
                .Build(Issue139Json.Default.Options);
            var json = document.ToJson();
            Assert.That(json, Does.Not.Contain("/internal").And.Not.Contain("/hidden"));
            Assert.That(document.Problems, Is.Empty);
        }

        [Test]
        public void WhatCannotBeDescribedIsReportedNotGuessed()
        {
            var builder = new OpenApiDocumentBuilder("People", "1.0")
                .AddController("/api", Issue139Bindings.Issue139PeopleController)
                .AddController("/api", Issue139Bindings.Issue139ProblemController);
            var error = Assert.Throws<OpenApiDocumentException>(() => builder.Build(Issue139Json.Default.Options));
            Assert.That(error?.Problems, Has.Count.EqualTo(4));
            Assert.That(error?.Problems, Has.Some.Contains("/api/files/").And.Some.Contains("base route"));
            Assert.That(error?.Problems, Has.Some.Contains("HttpVerbs.Any"));
            Assert.That(error?.Problems, Has.Some.Contains("Issue138HeaderAttribute"));
            Assert.That(error?.Problems, Has.Some.Contains("Issue139ProblemController.Duplicate").And.Some.Contains("already uses this method and path"));
            Assert.That(error?.Message, Does.Contain("cannot describe every route"));

            // Allowed incomplete, the rest is described and the problems are listed.
            var partial = builder.Build(Issue139Json.Default.Options, allowIncomplete: true);
            Assert.That(partial.Problems, Is.EqualTo(error?.Problems));
            Assert.That(partial.Root["paths"]?["/api/fine"]?["get"], Is.Not.Null);
            Assert.That(partial.Root["paths"]?["/api/files/"], Is.Null);
            Assert.That(partial.Root["paths"]?["/api/custom"], Is.Null);
            Assert.That(partial.Root["paths"]?["/api/people"]?["get"]?["operationId"]?.GetValue<string>(), Is.EqualTo("Issue139People_List"));
        }

        [Test]
        public void TypesWithoutJsonMetadataAreReported()
        {
            var builder = new OpenApiDocumentBuilder("People", "1.0").AddController("/api", Issue139Bindings.Issue139PeopleController);
            var error = Assert.Throws<OpenApiDocumentException>(() => builder.Build(Issue139EmptyJson.Default.Options));
            Assert.That(error?.Problems, Has.Some.Contains("no metadata for EmbedIO.Tests.Issues.Issue139Person"));
        }

        [Test]
        public void BuilderValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new OpenApiDocumentBuilder(Missing<string>(), "1"));
            var builder = new OpenApiDocumentBuilder("t", "1");
            Assert.Throws<ArgumentException>(() => builder.AddController("api", Issue139Bindings.Issue139PeopleController));
            Assert.Throws<ArgumentNullException>(() => builder.AddController("/api", Missing<WebApiControllerBinding<Issue139PeopleController>>()));
            Assert.Throws<ArgumentNullException>(() => builder.Build(Missing<JsonSerializerOptions>()));
            Assert.That(builder.Build(Issue139Json.Default.Options).Root["paths"]?.AsObject(), Is.Empty);
        }

        // The served document, and a request to every documented operation, answered with a documented status.
        [Test]
        public async Task ServedDocumentDescribesTheRunningApi()
        {
            var document = PeopleDocument();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(
                new OpenApiDocumentModule("/openapi.json", document),
                new WebApiModule("/api", ResponseSerializer.Json(Issue139Json.Default.Options)).WithController(Issue139Bindings.Issue139PeopleController));
            using var client = new HttpClient();

            using (var served = await client.GetAsync(server.Url + "openapi.json"))
            {
                Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(served.Content.Headers.ContentType?.ToString(), Is.EqualTo("application/json; charset=utf-8"));
                Assert.That(await served.Content.ReadAsStringAsync(), Is.EqualTo(document.ToJson()));
            }

            using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + "openapi.json")))
            {
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
            }

            using (var posted = await client.PostAsync(server.Url + "openapi.json", new StringContent(string.Empty)))
                Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            async Task Expect(HttpMethod method, string path, string documentedPath, HttpContent? content, int status, string? expectedBody = null)
            {
                using var request = new HttpRequestMessage(method, server.Url + path.TrimStart('/')) { Content = content };
                using var response = await client.SendAsync(request);
                Assert.That((int)response.StatusCode, Is.EqualTo(status), method + " " + path);
                if (expectedBody != null) Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(expectedBody));
                var key = method == HttpMethod.Get ? "get" : method == HttpMethod.Post ? "post" : method == HttpMethod.Put ? "put" : "delete";
                var responses = Operation(document, documentedPath, key)["responses"]?.AsObject();
                Assert.That(responses?.ContainsKey(status.ToString(System.Globalization.CultureInfo.InvariantCulture)), Is.True, $"{method} {path} answered {status}, which the document does not list.");
            }

            Issue139PeopleController.People.Clear();
            Issue139PeopleController.People.Add(new Issue139Person { Name = "Ada", Age = 36 });
            await Expect(HttpMethod.Get, "/api/people?limit=1&status=Active", "/api/people", null, 200);
            await Expect(HttpMethod.Get, "/api/people?status=Retired", "/api/people", null, 200, "[]");
            await Expect(HttpMethod.Get, "/api/people?limit=1&status=Retired", "/api/people", null, 200, "[]");
            await Expect(HttpMethod.Get, "/api/people?limit=x", "/api/people", null, 400);
            await Expect(HttpMethod.Get, "/api/people/0", "/api/people/{index}", null, 200);
            await Expect(HttpMethod.Get, "/api/people/9", "/api/people/{index}", null, 404);
            await Expect(HttpMethod.Get, "/api/people/x", "/api/people/{index}", null, 400);
            await Expect(HttpMethod.Post, "/api/people", "/api/people", new StringContent("{\"Name\":\"Bo\",\"Age\":2,\"Status\":1}", Encoding.UTF8, "application/json"), 201);
            await Expect(HttpMethod.Post, "/api/people", "/api/people", new StringContent("{bad", Encoding.UTF8, "application/json"), 400);
            await Expect(HttpMethod.Put, "/api/people/0/name", "/api/people/{index}/name", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("name", "Ada L") }), 200);
            await Expect(HttpMethod.Put, "/api/people/0/name", "/api/people/{index}/name", new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()), 400);
            await Expect(HttpMethod.Get, "/api/tree", "/api/tree", null, 200);
            await Expect(HttpMethod.Delete, "/api/people/Bo", "/api/people/{index}", null, 200);
            Assert.That(Issue139PeopleController.People.Select(p => p.Name), Is.EqualTo(new[] { "Ada L" }));
        }

        private static T Missing<T>()
            where T : class
            => (new T[1])[0];
    }

    // A context that declares none of the controller's types.
    [JsonSerializable(typeof(int))]
    internal sealed partial class Issue139EmptyJson : JsonSerializerContext
    {
    }
}
