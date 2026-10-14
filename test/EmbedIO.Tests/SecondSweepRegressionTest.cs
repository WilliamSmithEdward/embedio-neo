using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.Tests.Issues;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Regressions for defects found by the second review sweep of the engine.
    [TestFixture]
    public class SecondSweepRegressionTest
    {
        // One request over a fresh connection; returns everything the server sent before closing.
        private static async Task<string> RawHttp1Async(Uri server, string request, CancellationToken token)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port, token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, token);
            return Encoding.UTF8.GetString(received.ToArray());
        }

        // RFC 9110, Section 5.3: repeated field lines combine in order. HTTP/1 kept only the last line,
        // where HTTP/2 and HTTP/3 already combined them; cookie lines join with "; ".
        [Test]
        public async Task RepeatedHttp1FieldLinesCombine()
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, context =>
                context.SendStringAsync(
                    context.Request.Headers["X-Trace"] + "|" + context.Request.Headers["Cookie"] + "|"
                    + string.Join(",", context.Request.Cookies.Select(cookie => cookie.Name + "=" + cookie.Value)),
                    "text/plain",
                    WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                var address = new Uri(url);
                var response = await RawHttp1Async(address,
                    "GET / HTTP/1.1\r\nHost: " + address.Authority + "\r\nX-Trace: one\r\nCookie: a=1\r\nX-Trace: two\r\nCookie: b=2\r\nConnection: close\r\n\r\n",
                    stop.Token);
                Assert.That(response, Does.StartWith("HTTP/1.1 200"));
                Assert.That(response, Does.Contain("one, two|a=1; b=2|a=1,b=2"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        // RFC 9110, Section 12.4.2: the weight's name is case-insensitive, so Q=0 refuses a coding as q=0 does.
        [TestCase("gzip;Q=0, br", 0)]
        [TestCase("gzip; Q=0.5", 500)]
        public void QualityWeightNamesAreCaseInsensitive(string header, int expected)
        {
            var list = new EmbedIO.Utilities.QValueList(true, header);
            Assert.That(list.TryGetWeight("gzip", out var weight), Is.True);
            Assert.That(weight, Is.EqualTo(expected));
        }

        // OpenAPI ------------------------------------------------------------------------------------------------

        private static OpenApiDocument Describe<TController>(string baseRoute, WebApiControllerBinding<TController> binding, JsonSerializerOptions? options = null)
            where TController : WebApiController
            => new OpenApiDocumentBuilder("Second sweep", "1.0").AddController(baseRoute, binding)
                .Build(options ?? EmbedIO.Serialization.Json.CreateOptions(), true);

        // EmbedIO's default options write strings with their own converter, which the schema exporter
        // alone describes as accepting anything; it writes what the built-in converter writes.
        [Test]
        public void DefaultOptionsDescribeStringsAsTheBuiltInConverterDoes()
        {
            var properties = Describe("/api", SecondSweepBindings.SecondSweepOpenApiController).Root["components"]?["schemas"]?["SecondSweepLabels"]?["properties"];
            var builtIn = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(SecondSweepLabels))["properties"];
            Assert.That(properties?["Label"]?.ToJsonString(), Is.EqualTo(builtIn?["Label"]?.ToJsonString()));
            Assert.That(properties?["Note"]?.ToJsonString(), Is.EqualTo(builtIn?["Note"]?.ToJsonString()));
            Assert.That(properties?["Label"]?["type"]?.ToJsonString(), Does.Contain("\"string\""));
        }

        // [FromServices] reads nothing from the request, so it leaves the operation describable.
        [Test]
        public void ServiceParametersLeaveTheOperationDocumented()
        {
            var document = Describe("/api", DependencyInjectionTest.Bindings.InjectedController);
            Assert.That(document.Problems, Has.None.Contains("FromServices"));
            var operation = document.Root["paths"]?["/api/argument"]?["get"];
            Assert.That(operation, Is.Not.Null);
            Assert.That(operation?["parameters"], Is.Null);
        }

        // OpenAPI path templating: every template expression, the module's base route included, is a declared parameter.
        [Test]
        public void BaseRouteParametersAreDeclared()
        {
            var document = Describe("/tenants/{tenant}", Issue139Bindings.Issue139ValuesController);
            Assert.That(document.Problems, Is.Empty);
            var parameters = document.Root["paths"]?["/tenants/{tenant}/values"]?["get"]?["parameters"]?.AsArray();
            Assert.That(parameters?.Select(p => p?["name"]?.GetValue<string>() + " in " + p?["in"]?.GetValue<string>()), Is.EqualTo(new[] { "tenant in path" }));
        }

        // The exporter refuses some options (ReferenceHandler.Preserve); that is a problem, not an escaping exception.
        [Test]
        public void SchemaExporterFailuresAreReported()
        {
            var options = EmbedIO.Serialization.Json.CreateOptions();
            options.ReferenceHandler = ReferenceHandler.Preserve;
            var document = Describe("/api", Issue139Bindings.Issue139ValuesController, options);
            Assert.That(document.Problems, Has.Some.Contains("schema exporter cannot describe"));
            Assert.Throws<OpenApiDocumentException>(() => new OpenApiDocumentBuilder("Second sweep", "1.0")
                .AddController("/api", Issue139Bindings.Issue139ValuesController).Build(options));
        }

        // A returned value is serialized as its runtime type, without the discriminator a polymorphic schema requires.
        [Test]
        public void PolymorphicResultsAreReportedNotMisdescribed()
        {
            var options = EmbedIO.Serialization.Json.CreateOptions();
            var document = Describe("/api", SecondSweepBindings.SecondSweepAnimalController, options);
            Assert.That(document.Problems, Has.Some.Contains("is polymorphic"));
            Assert.That(document.Root["paths"]?["/api/animal"], Is.Null);
            var written = JsonNode.Parse(EmbedIO.Serialization.Json.Serialize(new SecondSweepDog { Name = "Rex" }, options));
            Assert.That(written?.AsObject().ContainsKey("$type"), Is.False);
        }

        // Two parameters can bind one form field; JSON Schema requires the required list's items to be unique.
        [Test]
        public void AFormFieldBoundTwiceIsRequiredOnce()
        {
            var document = Describe("/api", SecondSweepBindings.SecondSweepOpenApiController);
            var schema = document.Root["paths"]?["/api/form"]?["post"]?["requestBody"]?["content"]?["application/x-www-form-urlencoded"]?["schema"];
            Assert.That(schema?["required"]?.ToJsonString(), Is.EqualTo("[\"a\"]"));
        }

        // OpenAPI Components Object keys must match ^[a-zA-Z0-9.\-_]+$; C# type names need not.
        [Test]
        public void SchemaNamesAreValidComponentKeys()
        {
            var schemas = Describe("/api", SecondSweepBindings.SecondSweepOpenApiController).Root["components"]?["schemas"]?.AsObject();
            var keys = schemas?.Select(schema => schema.Key).ToArray() ?? Array.Empty<string>();
            Assert.That(keys, Has.Some.StartsWith("SecondSweepCaf"));
            Assert.That(keys.Where(key => !Regex.IsMatch(key, "^[a-zA-Z0-9.\\-_]+$")), Is.Empty);
        }
    }

    [GeneratedControllerBinding(typeof(SecondSweepOpenApiController))]
    [GeneratedControllerBinding(typeof(SecondSweepAnimalController))]
    internal static partial class SecondSweepBindings
    {
    }

    public sealed class SecondSweepLabels
    {
        public string Label { get; set; } = string.Empty;

        public string? Note { get; set; }
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(SecondSweepDog), "dog")]
    public abstract class SecondSweepAnimal
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SecondSweepDog : SecondSweepAnimal
    {
        public int Barks { get; set; }
    }

    // A legal C# name that is not a legal OpenAPI component key.
    public sealed class SecondSweepCafé
    {
        public int Value { get; set; }
    }

    internal sealed class SecondSweepOpenApiController : WebApiController
    {
        [Route(HttpVerbs.Get, "/labels")]
        public SecondSweepLabels Labels() => new();

        [Route(HttpVerbs.Post, "/form")]
        public string Form([FormField("a", true)] string first, [FormField("a", true)] string second) => first + second;

        [Route(HttpVerbs.Get, "/cafe")]
        public SecondSweepCafé Cafe() => new();
    }

    internal sealed class SecondSweepAnimalController : WebApiController
    {
        [Route(HttpVerbs.Get, "/animal")]
        public SecondSweepAnimal Animal() => new SecondSweepDog { Name = "Rex" };
    }
}
