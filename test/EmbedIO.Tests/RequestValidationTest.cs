using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
    public sealed class RequestValidationTest
    {
        [TestCase(false, "{}", 400, "input.Name")]
        [TestCase(true, "{}", 400, "input.Name")]
        [TestCase(false, "{\"Name\":\"ok\",\"Count\":5}", 200, null)]
        [TestCase(true, "{\"Name\":\"ok\",\"Count\":5}", 200, null)]
        [TestCase(false, "{\"Name\":\" \",\"Count\":5}", 400, "input.Name")]
        [TestCase(true, "{\"Name\":\" \",\"Count\":5}", 400, "input.Name")]
        [TestCase(false, "{\"Name\":\"long name\",\"Count\":5}", 400, "input.Name")]
        [TestCase(true, "{\"Name\":\"long name\",\"Count\":5}", 400, "input.Name")]
        [TestCase(false, "{\"Name\":\"ok\",\"Count\":11}", 400, "input.Count")]
        [TestCase(true, "{\"Name\":\"ok\",\"Count\":11}", 400, "input.Count")]
        [TestCase(false, "{\"Name\":\"ok\",\"Count\":5,\"Children\":[{}]}", 400, "input.Children[0].Name")]
        [TestCase(true, "{\"Name\":\"ok\",\"Count\":5,\"Children\":[{}]}", 400, "input.Children[0].Name")]
        [TestCase(false, "null", 400, "input")]
        [TestCase(true, "null", 400, "input")]
        public async Task BodyValidationPrecedesActionAndReleasesController(bool generated, string json, int status, string? field)
        {
            var controller = new RequestValidationController();
            await TestWebServer.UseAsync(server => server.WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null })
                .WithModule(Module(generated, controller)), async client =>
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("/api/body", content);
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(controller.Calls, Is.EqualTo(status == 200 ? 1 : 0));
                Assert.That(controller.Disposed, Is.EqualTo(1));
                if (field != null)
                {
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                    Assert.That(body?["errors"]?[field], Is.Not.Null);
                }
                using var healthy = await client.GetAsync("/api/healthy");
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [TestCase(false, "/plain", "{}", 200)]
        [TestCase(true, "/plain", "{}", 200)]
        [TestCase(false, "/query?n=11", null, 400)]
        [TestCase(true, "/query?n=11", null, 400)]
        [TestCase(false, "/query?n=1", null, 200)]
        [TestCase(true, "/query?n=1", null, 200)]
        [TestCase(false, "/form", "name=toolong", 400)]
        [TestCase(true, "/form", "name=toolong", 400)]
        [TestCase(false, "/form", "name=ok", 200)]
        [TestCase(true, "/form", "name=ok", 200)]
        public async Task OptInAndScalarBindings(bool generated, string path, string? text, int status)
        {
            await TestWebServer.UseAsync(server => server.WithModule(Module(generated, new RequestValidationController())), async client =>
            {
                using var request = new HttpRequestMessage(text == null ? HttpMethod.Get : HttpMethod.Post, "/api" + path);
                if (text != null) request.Content = new StringContent(text, Encoding.UTF8, path == "/form" ? "application/x-www-form-urlencoded" : "application/json");
                using var response = await client.SendAsync(request);
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
            });
        }

        [Test]
        public void CyclesSharedChildrenAndCancellationAreBounded()
        {
            var validation = Metadata("Body");
            var node = new ValidationInput { Name = "ok", Count = 1 };
            node.Children = new[] { node };
            Assert.That(validation.Validate(node, "input"), Is.SameAs(node));
            var invalid = new ValidationInput();
            node.Children = new[] { invalid, invalid };
            var error = Assert.Throws<ProblemDetailsException>(() => validation.Validate(node, "input"));
            var problem = (ProblemDetails)error!.DataObject!;
            Assert.That(problem.Extensions["errors"]?["input.Children[0].Name"], Is.Not.Null);
            Assert.That(problem.Extensions["errors"]?["input.Children[1].Name"], Is.Not.Null);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            Assert.Throws<OperationCanceledException>(() => validation.Validate(node, "input", stop.Token));
        }

        [TestCase("Depth")]
        [TestCase("Nodes")]
        [TestCase("Errors")]
        public void TraversalAndErrorLimitsRejectInsteadOfCallingAction(string action)
        {
            var node = new ValidationInput { Name = "ok", Count = 1, Children = new[] { new ValidationInput(), new ValidationInput() } };
            var error = Assert.Throws<ProblemDetailsException>(() => Metadata(action).Validate(node, "input"));
            var problem = (ProblemDetails)error!.DataObject!;
            var count = problem.Extensions["errors"]!.AsObject().Sum(field => field.Value!.AsArray().Count);
            Assert.That(count, Is.InRange(1, action == "Errors" ? 1 : 32));
        }

        [Test]
        public void CustomRulesAndObjectRulesKeepMemberPaths()
        {
            var input = new ValidationInput { Name = "ok", Count = 1, Code = "bad", Confirm = false };
            var error = Assert.Throws<ProblemDetailsException>(() => Metadata("Body").Validate(input, "input"));
            var errors = ((ProblemDetails)error!.DataObject!).Extensions["errors"];
            Assert.That(errors?["input.Code"], Is.Not.Null);
            Assert.That(errors?["input.Confirm"], Is.Not.Null);
        }

        [Test]
        public void OpenApiConstraintsAreLocalToOptedInRequests()
        {
            var document = new OpenApiDocumentBuilder("Validation", "1").AddController("/api", ValidationBindings.RequestValidationController)
                .Build(new JsonSerializerOptions(Serialization.Json.CreateOptions()) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var paths = document.Root["paths"]!;
            var validated = paths["/api/body"]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["allOf"]?[1];
            Assert.That(validated?["properties"]?["name"]?["maxLength"]?.GetValue<int>(), Is.EqualTo(4));
            Assert.That(validated?["properties"]?["count"]?["minimum"]?.GetValue<double>(), Is.EqualTo(1));
            Assert.That(paths["/api/plain"]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["allOf"], Is.Null);
            Assert.That(paths["/api/query"]?["get"]?["parameters"]?[0]?["schema"]?["allOf"]?[1]?["maximum"]?.GetValue<double>(), Is.EqualTo(10));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InheritedPropertyRulesAreApplied(bool generated)
        {
            await TestWebServer.UseAsync(server => server.WithModule(Module(generated, new RequestValidationController())), async client =>
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("/api/inherited", content);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            });
        }

        [Test]
        public void InfiniteSuccessfulObjectResultsConsumeTheNodeBudget()
        {
            var plan = new RequestValidationPlan(typeof(InfiniteValidationInput), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>());
            var validation = new RequestValidationParameter(plan, Array.Empty<ValidationAttribute>(), new ValidateAttribute { MaxNodes = 4 });
            Assert.Throws<ProblemDetailsException>(() => validation.Validate(new InfiniteValidationInput(), "input"));
        }

        [TestCase(0, 1, 1)]
        [TestCase(65, 1, 1)]
        [TestCase(1, 0, 1)]
        [TestCase(1, 100001, 1)]
        [TestCase(1, 1, 0)]
        [TestCase(1, 1, 1025)]
        public void InvalidSettingsFailBeforeRequests(int depth, int nodes, int errors)
        {
            var plan = new RequestValidationPlan(typeof(string), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>());
            Assert.Throws<InvalidOperationException>(() => new RequestValidationParameter(plan, Array.Empty<ValidationAttribute>(),
                new ValidateAttribute { MaxDepth = depth, MaxNodes = nodes, MaxErrors = errors }));
        }

        [Test]
        public void Utf16LengthConstraintsDoNotRejectValidSupplementaryCharactersInSchema()
        {
            var model = new ValidationInput { Name = "\U0001F600", Count = 1 };
            Assert.That(Metadata("Body").Validate(model, "input"), Is.SameAs(model));
            var document = new OpenApiDocumentBuilder("Unicode", "1").AddController("/api", ValidationBindings.RequestValidationController).Build(Serialization.Json.CreateOptions());
            var name = document.Root["paths"]?["/api/body"]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["allOf"]?[1]?["properties"]?["Name"];
            Assert.That(name?["minLength"]?.GetValue<long>(), Is.EqualTo(1));
            Assert.That(name?["x-embedio-minLengthUtf16"]?.GetValue<int>(), Is.EqualTo(2));
        }

        [Test]
        public void CollectionObjectRulesAreNotSkipped()
        {
            var scalar = new RequestValidationPlan(typeof(string), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>());
            var plan = new RequestValidationPlan(typeof(ValidatedCollection), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>(), () => scalar);
            var validation = new RequestValidationParameter(plan, Array.Empty<ValidationAttribute>(), new ValidateAttribute());
            Assert.Throws<ProblemDetailsException>(() => validation.Validate(new ValidatedCollection(), "input"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestAwareReleaseRunsOnceAfterValidationFailure(bool generated)
        {
            var controller = new RequestValidationController();
            var released = 0;
            var module = new WebApiModule("/api");
            if (generated) module.RegisterControllerWithContext(ValidationBindings.RequestValidationController, _ => controller,
                (_, _) => { released++; return Task.CompletedTask; });
            else module.RegisterControllerWithContext(typeof(RequestValidationController), _ => controller,
                (_, _) => { released++; return Task.CompletedTask; });
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("/api/body", content);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(controller.Calls, Is.Zero);
                Assert.That(released, Is.EqualTo(1));
            });
        }

        private static RequestValidationParameter Metadata(string method)
            => ValidationBindings.RequestValidationController.Routes.First(route => route.Operation?.MethodName == method).Operation!.Parameters[0].Validation!;

        private static WebApiModule Module(bool generated, RequestValidationController controller)
            => generated ? new WebApiModule("/api").WithController(ValidationBindings.RequestValidationController, () => controller)
                : new WebApiModule("/api").WithController(() => controller);
    }

    [GeneratedControllerBinding(typeof(RequestValidationController))]
    internal static partial class ValidationBindings { }

    public sealed class RequestValidationController : WebApiController, IDisposable
    {
        public int Calls { get; private set; }
        public int Disposed { get; private set; }
        [Route(HttpVerbs.Post, "/body")]
        public string Body([JsonData, Validate, Required] ValidationInput? input) { Calls++; return "ok"; }
        [Route(HttpVerbs.Post, "/plain")]
        public string Plain([JsonData] ValidationInput? input) => "ok";
        [Route(HttpVerbs.Get, "/query")]
        public int Query([QueryField, Validate, Range(1, 10)] int n) => n;
        [Route(HttpVerbs.Post, "/form")]
        public string Form([FormField, Validate, StringLength(4)] string name) => name;
        [Route(HttpVerbs.Post, "/inherited")]
        public string Inherited([JsonData, Validate] ValidationDerived input) => "ok";
        [Route(HttpVerbs.Get, "/healthy")]
        public string Healthy() => "ok";
        [Route(HttpVerbs.Post, "/depth")]
        public string Depth([JsonData, Validate(MaxDepth = 2)] ValidationInput input) => "ok";
        [Route(HttpVerbs.Post, "/nodes")]
        public string Nodes([JsonData, Validate(MaxNodes = 2)] ValidationInput input) => "ok";
        [Route(HttpVerbs.Post, "/errors")]
        public string Errors([JsonData, Validate(MaxErrors = 1)] ValidationInput input) => "ok";
        public void Dispose() => Disposed++;
    }

    public sealed class ValidationInput : IValidatableObject
    {
        [Required, StringLength(4, MinimumLength = 2)]
        public string? Name { get; set; }
        [Range(1, 10)]
        public int Count { get; set; }
        public ValidationInput[]? Children { get; set; }
        [AllowedCode]
        public string? Code { get; set; }
        public bool? Confirm { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Confirm == false) yield return new ValidationResult("Confirmation required.", new[] { nameof(Confirm) });
        }
    }

    public class ValidationBase
    {
        [Required]
        public virtual string? Name { get; set; }
    }

    public sealed class ValidationDerived : ValidationBase
    {
        public override string? Name { get; set; }
    }

    public sealed class InfiniteValidationInput : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            while (true) yield return ValidationResult.Success!;
        }
    }

    public sealed class ValidatedCollection : List<string>, IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Count == 0) yield return new ValidationResult("At least one value is required.");
        }
    }

    public sealed class AllowedCodeAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => value == null || Equals(value, "good");
    }
}

