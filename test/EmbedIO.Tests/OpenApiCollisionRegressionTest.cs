using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Serialization;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class OpenApiCollisionRegressionTest
    {
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void IdenticalQueryBindingsShareOneParameterWithCombinedRequirement(bool firstRequired, bool secondRequired)
        {
            var document = Builder(Query("first", typeof(int), firstRequired), Query("second", typeof(int), secondRequired)).Build(Json.CreateOptions());
            var parameters = document.Root["paths"]?["/items"]?["get"]?["parameters"]?.AsArray() ?? throw new AssertionException("Missing parameters.");
            Assert.That(parameters.Count, Is.EqualTo(1));
            Assert.That(parameters[0]?["name"]?.GetValue<string>(), Is.EqualTo("id"));
            Assert.That(parameters[0]?["required"]?.GetValue<bool>(), Is.EqualTo(firstRequired || secondRequired));
            Assert.That(document.Problems, Is.Empty);
        }

        [Test]
        public void ConflictingQueryBindingSchemasUseTheExistingProblemContract()
        {
            var builder = Builder(Query("first", typeof(int), false), Query("second", typeof(string), false));
            Assert.Throws<OpenApiDocumentException>(() => builder.Build(Json.CreateOptions()));
            var incomplete = builder.Build(Json.CreateOptions(), true);
            Assert.That(incomplete.Problems, Has.Some.Contains("query parameter 'id'"));
            Assert.That(incomplete.Root["paths"]?.AsObject().Count, Is.Zero);
        }

        [Test]
        public void QueryNamesRemainCaseSensitive()
        {
            var upper = new WebApiOperationParameter("upper", "ID", WebApiParameterSource.Query, typeof(int), false, null, null);
            var document = Builder(Query("lower", typeof(int), false), upper).Build(Json.CreateOptions());
            Assert.That(document.Root["paths"]?["/items"]?["get"]?["parameters"]?.AsArray().Count, Is.EqualTo(2));
        }

        [Test]
        public void TemplateAliasesWithDifferentVerbsShareTheFirstPathAndRetainSchemas()
        {
            var document = new OpenApiDocumentBuilder("Paths", "1")
                .AddOperation(HttpVerbs.Get, "/items/{id}/parts/{part}", o => o.Parameter("id", OpenApiParameterLocation.Path, typeof(int)).Response(200))
                .AddOperation(HttpVerbs.Delete, "/items/{key}/parts/{piece}", o => o.Parameter("key", OpenApiParameterLocation.Path, typeof(Guid), description: "Key").Response(204))
                .Build(Json.CreateOptions());
            var paths = document.Root["paths"]?.AsObject() ?? throw new AssertionException("Missing paths.");
            Assert.That(paths.Count, Is.EqualTo(1));
            var parameters = paths["/items/{id}/parts/{part}"]?["delete"]?["parameters"]?.AsArray() ?? throw new AssertionException("Missing delete parameters.");
            Assert.That(parameters.Select(p => p?["name"]?.GetValue<string>()), Is.EqualTo(new[] { "id", "part" }));
            Assert.That(parameters[0]?["schema"]?["format"]?.GetValue<string>(), Is.EqualTo("uuid"));
            Assert.That(parameters[0]?["description"]?.GetValue<string>(), Is.EqualTo("Key"));
        }

        [Test]
        public void TemplateAliasesWithTheSameVerbReportCollision()
        {
            var builder = new OpenApiDocumentBuilder("Paths", "1")
                .AddOperation(HttpVerbs.Get, "/items/{id}", o => o.Response(200))
                .AddOperation(HttpVerbs.Get, "/items/{key}", o => o.Response(200));
            Assert.Throws<OpenApiDocumentException>(() => builder.Build(Json.CreateOptions()));
            var incomplete = builder.Build(Json.CreateOptions(), true);
            Assert.That(incomplete.Problems, Has.Some.Contains("already uses this method and path"));
            Assert.That(incomplete.Root["paths"]?.AsObject().Count, Is.EqualTo(1));
        }

        [Test]
        public void DistinctLiteralPathsRemainSeparate()
        {
            var document = new OpenApiDocumentBuilder("Paths", "1")
                .AddOperation(HttpVerbs.Get, "/items/{id}", o => o.Response(200))
                .AddOperation(HttpVerbs.Get, "/parts/{id}", o => o.Response(200))
                .Build(Json.CreateOptions());
            Assert.That(document.Root["paths"]?.AsObject().Count, Is.EqualTo(2));
        }

        [Test]
        public void RepeatedTemplateParameterNamesRemainInvalidRoutes()
        {
            var builder = new OpenApiDocumentBuilder("Paths", "1")
                .AddOperation(HttpVerbs.Get, "/items/{id}/parts/{id}", o => o.Response(200));
            Assert.Throws<OpenApiDocumentException>(() => builder.Build(Json.CreateOptions()));
            var incomplete = builder.Build(Json.CreateOptions(), true);
            Assert.That(incomplete.Problems, Has.Some.Contains("not a valid EmbedIO route"));
            Assert.That(incomplete.Root["paths"]?.AsObject().Count, Is.Zero);
        }
        private static WebApiOperationParameter Query(string parameter, Type type, bool required)
            => new(parameter, "id", WebApiParameterSource.Query, type, required, null, null);

        private static OpenApiDocumentBuilder Builder(params WebApiOperationParameter[] parameters)
        {
            var operation = new WebApiOperation("Probe", "Get", null, parameters, null, Array.Empty<OpenApiResponseAttribute>(), false);
            var binding = new WebApiControllerBinding<Probe>(new WebApiRouteBinding<Probe>(HttpVerbs.Get, "/items", false, "Get",
                () => (_, _, _, _) => Task.FromResult(Task.CompletedTask), operation));
            return new OpenApiDocumentBuilder("Queries", "1").AddController("/", binding);
        }

        private sealed class Probe : WebApiController { }
    }
}
