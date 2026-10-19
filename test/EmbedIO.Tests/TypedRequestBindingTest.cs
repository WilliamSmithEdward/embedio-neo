using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using Range = System.ComponentModel.DataAnnotations.RangeAttribute;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed partial class TypedRequestBindingTest
    {
        private static readonly string[] CookiePaths = ["cookie-first", "cookie-last", "cookie"];

        [TestCase(false)]
        [TestCase(true)]
        public async Task QueryAndFormModelsPreserveDefaultsAndBindInvariantValues(bool generated)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            try
            {
                await WithController(generated, async client =>
                {
                    using var assertions = Assert.EnterMultipleScope();
                    Assert.That(await client.GetStringAsync("/api/query"), Is.EqualTo("1|25|default|7|book-9|owned"));
                    Assert.That(await client.GetStringAsync("/api/query?page=2&page-size=12&tags=a&tags=b&count=&id=book-4&serverOwned=evil"), Is.EqualTo("2|12|a,b||book-4|owned"));
                    using var content = new FormUrlEncodedContent(
                        [
                            new KeyValuePair<string, string>("page", "3"),
                            new KeyValuePair<string, string>("page-size", "8"),
                            new KeyValuePair<string, string>("id", "book-5")
                        ]
                    );
                    using var form = await client.PostAsync("/api/form", content);
                    Assert.That(await form.Content.ReadAsStringAsync(), Is.EqualTo("3|8|default|7|book-5|owned"));
                    Assert.That(await client.GetStringAsync("/api/legacy?value=1&value=2"), Is.EqualTo("2"));
                });
            }

            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestCase(false, "query?page=2&page=2", "input.Page")]
        [TestCase(true, "query?page=2&page=2", "input.Page")]
        [TestCase(false, "query?page=bad", "input.Page")]
        [TestCase(true, "query?page=bad", "input.Page")]
        [TestCase(false, "query?page=999999999999999999999999999999", "input.Page")]
        [TestCase(true, "query?page=999999999999999999999999999999", "input.Page")]
        [TestCase(false, "query?page=0", "input.Page")]
        [TestCase(true, "query?page=0", "input.Page")]
        [TestCase(false, "query?id=bad", "input.Id")]
        [TestCase(true, "query?id=bad", "input.Id")]
        [TestCase(false, "strict?extra=1", "input")]
        [TestCase(true, "strict?extra=1", "input")]
        [TestCase(false, "required", "input.Value")]
        [TestCase(true, "required", "input.Value")]
        public async Task BindingAndValidationFailuresHaveSafeFieldErrorsAndHealthyRecovery(
            bool generated,
            string path,
            string errorField
        )
        {
            var controller = new TypedBindingController();
            await WithController(
                generated,
                async client =>
                {
                    using var assertions = Assert.EnterMultipleScope();
                    using var response = await client.GetAsync("/api/" + path);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(
                        response.Content.Headers.ContentType?.MediaType,
                        Is.EqualTo("application/problem+json")
                    );
                    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                    Assert.That(json?["errors"]?[errorField], Is.Not.Null);
                    Assert.That(json?["requestId"]?.GetValue<string>(), Is.Not.Empty);
                    Assert.That(controller.Calls, Is.Zero);
                    Assert.That(controller.Disposed, Is.EqualTo(1));
                    Assert.That(await client.GetStringAsync("/api/query"), Does.StartWith("1|25|"));
                },
                controller
            );
            Assert.That(controller.Disposed, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HeaderCookieDefaultsArraysCaseAndDuplicatesHaveExplicitSemantics(bool generated) =>
            await WithController(generated, async client =>
            {
                using var assertions = Assert.EnterMultipleScope();
                Assert.That(await client.GetStringAsync("/api/header"), Is.EqualTo("fallback"));
                using var headers = new HttpRequestMessage(HttpMethod.Get, "/api/header-array");
                headers.Headers.TryAddWithoutValidation("x-values", ["a,b", "c"]);
                using var array = await client.SendAsync(headers);
                Assert.That(await array.Content.ReadAsStringAsync(), Is.EqualTo("a,b|c"));
                using var duplicates = new HttpRequestMessage(HttpMethod.Get, "/api/header");
                duplicates.Headers.TryAddWithoutValidation("X-Value", ["first", "last"]);
                using var rejected = await client.SendAsync(duplicates);
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

                foreach (var path in CookiePaths)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/" + path);
                    request.Headers.TryAddWithoutValidation("Cookie", "Theme=upper; theme=first; theme=last");
                    using var response = await client.SendAsync(request);
                    Assert.That((int)response.StatusCode, Is.EqualTo(path == "cookie" ? 400 : 200));

                    if (response.IsSuccessStatusCode) Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(path == "cookie-first" ? "first" : "last"));
                }

                using var exactCase = new HttpRequestMessage(HttpMethod.Get, "/api/cookie");
                exactCase.Headers.TryAddWithoutValidation("Cookie", "Theme=upper");
                using var missing = await client.SendAsync(exactCase);
                Assert.That(await missing.Content.ReadAsStringAsync(), Is.EqualTo("absent"));
            });

        [TestCase(false)]
        [TestCase(true)]
        public async Task BindingDoesNotEnableDataAnnotationsImplicitly(bool generated) =>
            await WithController(
                generated,
                async client => Assert.That(await client.GetStringAsync("/api/raw?page=101"), Does.StartWith("101|"))
            );

        [Test]
        public void BoundedPlansRejectMalformedInputAndAcceptExactLimits()
        {
            var member = RequestObjectMember<BindingLimitModel>.Create(
                "a",
                "Values",
                (model, values) => model.Values = values,
                raw => raw,
                false,
                true,
                RequestDuplicateValues.Reject
            );
            var plan = new RequestObjectPlan<BindingLimitModel>(() => new BindingLimitModel(), [member], false);
            var exact = new NameValueCollection();

            for (var i = 0; i < 16; i++) exact.Add("a", new string('x', 4095));

            Assert.That(plan.Bind(exact, "input").Values, Has.Length.EqualTo(16));
            exact.Add("a", "x");
            Assert.Throws<ProblemDetailsException>(() => plan.Bind(exact, "input"));
            var many = new NameValueCollection();

            for (var i = 0; i < 1024; i++) many.Add("a", "x");

            Assert.That(plan.Bind(many, "input").Values, Has.Length.EqualTo(1024));
            many.Add("a", "x");
            Assert.Throws<ProblemDetailsException>(() => plan.Bind(many, "input"));
            Assert.Throws<ProblemDetailsException>(
                () => plan.Bind(new NameValueCollection { ["a"] = new string('x', 4097) }, "input")
            );
            Assert.Throws<ProblemDetailsException>(
                () => plan.Bind(new NameValueCollection { ["a"] = "\ud800" }, "input")
            );
            Assert.Throws<ProblemDetailsException>(
                () => plan.Bind(new NameValueCollection { ["a"] = "\udc00" }, "input")
            );
            Assert.That(
                plan.Bind(new NameValueCollection { ["a"] = "\U0001f600" }, "input").Values,
                Is.EqualTo(["\U0001f600"])
            );
            var fields = new NameValueCollection();

            for (var i = 0; i < 128; i++) fields.Add("field" + i.ToString(CultureInfo.InvariantCulture), "x");

            Assert.That(plan.Bind(fields, "input").Values, Is.Empty);
            fields.Add("extra", "x");
            Assert.Throws<ProblemDetailsException>(() => plan.Bind(fields, "input"));
        }

        [Test]
        public void GeneratedOpenApiFlattensFieldsAndRequiresNoDtoJsonMetadata()
        {
            var document = new OpenApiDocumentBuilder("Binding", "1")
                .AddController("/api", TypedBindings.TypedBindingController)
                .Build(BindingSmokeJson.Default.Options);
            var query = document.Root["paths"]?["/api/query"]?["get"]
                ?? throw new InvalidOperationException("Missing query operation.");
            var fields = query["parameters"]
                ?.AsArray()
                .OfType<JsonObject>()
                .ToArray()
                ?? throw new InvalidOperationException("Missing parameters.");
            using var assertions = Assert.EnterMultipleScope();
            var page = fields.Single(p => p["name"]?.GetValue<string>() == "Page");
            Assert.That(page["schema"]?["allOf"]?[1]?["minimum"]?.GetValue<double>(), Is.EqualTo(1));
            Assert.That(fields.Any(p => p["name"]?.GetValue<string>() == "page-size"), Is.True);
            Assert.That(fields.All(p => p["name"]?.GetValue<string>() != "ServerOwned"), Is.True);
            Assert.That(query["responses"]?["400"], Is.Not.Null);
            Assert.That(
                document.Root["paths"]?["/api/form"]?["post"]?["requestBody"]?["content"]?[
                    "application/x-www-form-urlencoded"
                ],
                Is.Not.Null
            );
            Assert.That(
                document.Root["paths"]?["/api/header"]?["get"]?["parameters"]?[0]?["in"]?.GetValue<string>(),
                Is.EqualTo("header")
            );
            var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "openapi");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "typed-binding.json"), document.ToJson());
        }

        private static Task WithController(
            bool generated,
            Func<HttpClient, Task> use,
            TypedBindingController? controller = null
        )
        {
            controller ??= new TypedBindingController();
            var module = new WebApiModule("/api", ResponseSerializer.None(false));

            if (generated) module.WithController(TypedBindings.TypedBindingController, () => controller);

            else module.WithController(() => controller);

            return TestWebServer.UseAsync(
                server => server
                    .WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null })
                    .WithModule(module),
                use
            );
        }
    }

    public sealed class BindingLimitModel
    {
        public string[] Values { get; set; } = [];
    }

    public sealed class BindingSearch
    {
        [Range(1, 100)]
        public int Page { get; set; } = 1;
        [RequestField("page-size")]
        [Range(1, 100)]
        public int PageSize { get; set; } = 25;
        public string[] Tags { get; set; } = ["default"];
        public int? Count { get; set; } = 7;
        public BindingBookId Id { get; set; } = new(9);
        public string ServerOwned { get; } = "owned";

        public override string ToString() =>
            string.Join(
                "|",
                Page.ToString(CultureInfo.InvariantCulture),
                PageSize.ToString(CultureInfo.InvariantCulture),
                string.Join(",", Tags),
                Count?.ToString(CultureInfo.InvariantCulture),
                "book-" + Id.Value.ToString(CultureInfo.InvariantCulture),
                ServerOwned
            );
    }

    public readonly record struct BindingBookId(int Value)
    {
        public static bool TryParse(string? text, IFormatProvider? provider, out BindingBookId result)
        {
            result = default;

            if (
                text == null
                || !text.StartsWith("book-", StringComparison.Ordinal)
                || !int.TryParse(text.AsSpan(5), NumberStyles.None, provider, out var value)
            ) return false;

            result = new BindingBookId(value);
            return true;
        }
    }

    public sealed class BindingRequired
    {
        [RequestField("value", Required = true)]
        public string Value { get; set; } = "initial";
    }

    [GeneratedControllerBinding(typeof(TypedBindingController))]
    internal static partial class TypedBindings { }

    public sealed partial class TypedBindingController : WebApiController, IDisposable
    {
        public int Calls { get; private set; }
        public int Disposed { get; private set; }

        [Route(HttpVerbs.Get, "/query")]
        public string Query([QueryObject, Validate] BindingSearch input)
        {
            Calls++;
            return (input ?? throw new ArgumentNullException(nameof(input))).ToString();
        }

        [Route(HttpVerbs.Post, "/form")]
        public string Form([FormObject, Validate] BindingSearch input)
        {
            Calls++;
            return (input ?? throw new ArgumentNullException(nameof(input))).ToString();
        }

        [Route(HttpVerbs.Get, "/raw")]
        public string Raw([QueryObject] BindingSearch input)
        {
            Calls++;
            return (input ?? throw new ArgumentNullException(nameof(input))).ToString();
        }

        [Route(HttpVerbs.Get, "/strict")]
        public string Strict([QueryObject(RejectUnknownFields = true)] BindingSearch input)
        {
            Calls++;
            return (input ?? throw new ArgumentNullException(nameof(input))).ToString();
        }

        [Route(HttpVerbs.Get, "/required")]
        public string Required([QueryObject] BindingRequired input)
        {
            Calls++;
            return (input ?? throw new ArgumentNullException(nameof(input))).Value;
        }

        [Route(HttpVerbs.Get, "/header")]
        public string Header([HeaderField("X-Value")] string input = "fallback")
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return input;
        }

        [OpenApiIgnore]
        [Route(HttpVerbs.Get, "/header-array")]
        public string HeaderArray([HeaderField("X-Values")] string[] input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return string.Join("|", input ?? []);
        }

        [Route(HttpVerbs.Get, "/cookie")]
        public string Cookie([CookieField("theme")] string input = "absent")
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return input;
        }

        [Route(HttpVerbs.Get, "/cookie-first")]
        public string CookieFirst([CookieField("theme", DuplicateValues = RequestDuplicateValues.First)] string input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return input;
        }

        [Route(HttpVerbs.Get, "/cookie-last")]
        public string CookieLast([CookieField("theme", DuplicateValues = RequestDuplicateValues.Last)] string input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return input;
        }

        [Route(HttpVerbs.Get, "/legacy")]
        public string Legacy([QueryField] int value)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose() => Disposed++;
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(string))]
    internal partial class BindingSmokeJson : System.Text.Json.Serialization.JsonSerializerContext { }
}
