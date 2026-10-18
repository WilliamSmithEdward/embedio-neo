using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Idempotency;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpResultsTest
    {
        private static readonly string[] BadValidationMessage = ["bad"];

        private static ErrorHttpResult Error(int status, ProblemDetails problem) => status switch
        {
            400 => HttpResults.BadRequest(problem),
            404 => HttpResults.NotFound(problem),
            409 => HttpResults.Conflict(problem),
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        [TestCase(400)]
        [TestCase(404)]
        [TestCase(409)]
        public void FixedProblemsCopyTheirInputAndInspectionSnapshots(int status)
        {
            var extension = new JsonObject { ["code"] = "original" };
            var problem = new ProblemDetails(status) { Detail = "client-safe" }.WithExtension("extra", extension);
            var result = Error(status, problem);
            problem.Detail = "changed";
            extension["code"] = "changed";
            var inspected = result.GetProblem();
            inspected.Detail = "changed again";
            var copiedExtension = inspected.Extensions["extra"]
                ?? throw new InvalidOperationException("Missing extension.");
            copiedExtension["code"] = "changed again";
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(result.StatusCode, Is.EqualTo(status));
            Assert.That(result.ContentType, Is.EqualTo("application/problem+json"));
            Assert.That(result.GetProblem().Detail, Is.EqualTo("client-safe"));
            Assert.That(
                result
                    .GetProblem()
                    .Extensions["extra"]?["code"]?.GetValue<string>(),
                Is.EqualTo("original")
            );
        }

        [TestCase(400, 404)]
        [TestCase(400, 409)]
        [TestCase(404, 400)]
        [TestCase(404, 409)]
        [TestCase(409, 400)]
        [TestCase(409, 404)]
        public void FixedProblemsRejectMismatchedStatuses(int result, int problem) =>
            Assert.That(() => Error(result, new ProblemDetails(problem)), Throws.ArgumentException);

        [TestCase(400)]
        [TestCase(404)]
        [TestCase(409)]
        public void FixedProblemsRejectNull(int status)
        {
            var name = status switch
            {
                400 => "BadRequest",
                404 => "NotFound",
                _ => "Conflict"
            };
            var problemType = typeof(ProblemDetails);
            var factory = typeof(HttpResults).GetMethod(name, [problemType])
                ?? throw new InvalidOperationException("Missing factory.");
            Assert.That(
                () => factory.Invoke(null, [null]),
                Throws
                    .TypeOf<TargetInvocationException>()
                    .With.InnerException.TypeOf<ArgumentNullException>()
            );
        }

        [Test]
        public void ValidationMessagesAreImmutableSnapshotsIncludingTypedAlternatives()
        {
            var messages = new[] { "Name is required.", "Name is too short." };
            var errors = new Dictionary<string, string[]>
            {
                ["book.Name"] = messages,
                [""] = ["The book is invalid."]
            };
            HttpResult<OkResult<ResultBook>, ValidationProblemResult> result = HttpResults.ValidationProblem(errors);
            messages[0] = "mutated";
            errors.Clear();
            var fields = result.GetValidationErrors();
            var copy = result.GetProblem();
            var copiedMessages = copy.Extensions["errors"]?["book.Name"]?.AsArray()
                ?? throw new InvalidOperationException("Missing messages.");
            copiedMessages[0] = "mutated";
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(fields["book.Name"][0], Is.EqualTo("Name is required."));
            Assert.That(fields[""][0], Is.EqualTo("The book is invalid."));
            Assert.That(((IDictionary<string, IReadOnlyList<string>>)fields).IsReadOnly, Is.True);
            Assert.That(
                () => ((IList<string>)fields["book.Name"])[0] = "mutated",
                Throws.TypeOf<NotSupportedException>()
            );
            Assert.That(
                result
                    .GetProblem()
                    .Extensions["errors"]?["book.Name"]?[0]?.GetValue<string>(),
                Is.EqualTo("Name is required.")
            );
        }

        [Test]
        public void DataAnnotationsMapEveryMemberAndRetainModelLevelErrors()
        {
            var members = new[] { "book.Name", "book.Title" };
            var input = new List<ValidationResult?>
            {
                ValidationResult.Success,
                new("Required.", members),
                new("Invalid book."),
                new(null, ["book.Other"])
            };
            var result = HttpResults.ValidationProblem(input);
            members[0] = "changed";
            input.Clear();
            var errors = result.GetValidationErrors();
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(errors["book.Name"][0], Is.EqualTo("Required."));
            Assert.That(errors["book.Title"][0], Is.EqualTo("Required."));
            Assert.That(errors[""][0], Is.EqualTo("Invalid book."));
            Assert.That(errors["book.Other"][0], Is.EqualTo("The value is invalid."));
        }

        [TestCase("empty-map")]
        [TestCase("empty-array")]
        [TestCase("blank-message")]
        [TestCase("long-path")]
        [TestCase("long-message")]
        [TestCase("many-fields")]
        [TestCase("many-messages")]
        [TestCase("total-utf8")]
        [TestCase("surrogate-path")]
        [TestCase("surrogate-message")]
        public void ValidationInputLimitsRejectUnusableOrExcessiveErrors(string kind)
        {
            var longMessage = new string('x', 4097);
            IReadOnlyDictionary<string, string[]> errors = kind switch
            {
                "empty-map" => [],
                "empty-array" => new Dictionary<string, string[]> { ["x"] = [] },
                "blank-message" => new Dictionary<string, string[]> { ["x"] = [" "] },
                "long-path" => new Dictionary<string, string[]> { [new string('x', 1025)] = ["bad"] },
                "long-message" => new Dictionary<string, string[]> { ["x"] = [longMessage] },
                "many-fields" => Enumerable
                    .Range(0, 1025)
                    .ToDictionary(
                        index => index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => BadValidationMessage
                    ),
                "many-messages" => new Dictionary<string, string[]> { ["x"] = RepeatMessages("bad", 1025) },
                "total-utf8" => new Dictionary<string, string[]> { ["x"] = RepeatMessages(new string('界', 4096), 6) },
                "surrogate-path" => new Dictionary<string, string[]> { ["\uD800"] = ["bad"] },
                "surrogate-message" => new Dictionary<string, string[]> { ["x"] = ["\uDC00"] },
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            Assert.That(() => HttpResults.ValidationProblem(errors), Throws.InstanceOf<ArgumentException>());
        }

        [TestCase("errors")]
        [TestCase("validationResults")]
        public void NullValidationInputsReportTheirPublicParameter(string parameter)
        {
            var inputType = parameter == "errors"
                ? typeof(IReadOnlyDictionary<string, string[]>)
                : typeof(IEnumerable<ValidationResult>);
            var factory = typeof(HttpResults).GetMethod(
                "ValidationProblem",

                [
                    inputType,
                    typeof(string)
                ]
            )
                ?? throw new InvalidOperationException("Missing factory.");
            var failure = Assert.Throws<TargetInvocationException>(() => factory.Invoke(null, [null, null]));
            Assert.That((failure?.InnerException as ArgumentNullException)?.ParamName, Is.EqualTo(parameter));
        }

        [Test]
        public void ValidationRejectsNullMessageArrays() =>
            AssertInvalidValidationMap(new Dictionary<string, string[]?> { ["x"] = null });

        [Test]
        public void ValidationRejectsNullMessages() =>
            AssertInvalidValidationMap(new Dictionary<string, string?[]> { ["x"] = [null] });

        private static void AssertInvalidValidationMap(object errors)
        {
            var factory = typeof(HttpResults).GetMethod(
                "ValidationProblem",

                [
                    typeof(IReadOnlyDictionary<string, string[]>),
                    typeof(string)
                ]
            )
                ?? throw new InvalidOperationException("Missing factory.");
            Assert.That(
                () => factory.Invoke(null, [errors, null]),
                Throws
                    .TypeOf<TargetInvocationException>()
                    .With.InnerException.TypeOf<ArgumentException>()
            );
        }

        [TestCase("fields")]
        [TestCase("messages")]
        [TestCase("path")]
        [TestCase("message")]
        [TestCase("utf8-total")]
        public void ValidationLimitsAcceptTheirExactBoundary(string boundary)
        {
            var errors = boundary switch
            {
                "fields" => Enumerable
                    .Range(0, 1024)
                    .ToDictionary(
                        index => index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => BadValidationMessage
                    ),
                "messages" => new Dictionary<string, string[]> { ["x"] = RepeatMessages("bad", 1024) },
                "path" => new Dictionary<string, string[]> { [new string('x', 1024)] = ["bad"] },
                "message" => new Dictionary<string, string[]> { ["x"] = RepeatMessages(new string('x', 4096), 1) },
                "utf8-total" => new Dictionary<string, string[]>
                {
                    ["x"] =
                    [
                        .. Enumerable.Repeat(new string('x', 4096), 15),
                        new string('x', 4095)
                    ]
                },
                _ => throw new ArgumentOutOfRangeException(nameof(boundary))
            };
            Assert.That(() => HttpResults.ValidationProblem(errors), Throws.Nothing);
        }

        [Test]
        public void AnnotationTraversalIsBoundedEvenForSuccessesAndInfiniteMemberLists()
        {
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(
                () => HttpResults.ValidationProblem(Enumerable.Repeat(ValidationResult.Success, 1025)),
                Throws.ArgumentException
            );
            var infiniteError = new ValidationResult("bad", InfiniteMembers());
            Assert.That(() => HttpResults.ValidationProblem([infiniteError]), Throws.ArgumentException);
            Assert.That(() => HttpResults.ValidationProblem([]), Throws.ArgumentException);
        }

        private static string[] RepeatMessages(string message, int count)
        {
            var messages = new string[count];
            Array.Fill(messages, message);
            return messages;
        }

        private static IEnumerable<string> InfiniteMembers()
        {
            while (true) yield return "field";
        }

        [Test]
        public void InspectionRejectsOtherResultKindsAndSupportsExistingDynamicProblems()
        {
            var dynamic = HttpResults.Problem(new ProblemDetails(422));
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(dynamic.GetProblem().Status, Is.EqualTo(422));
            Assert.That(
                () => HttpResults
                    .Ok("value")
                    .GetProblem(),
                Throws.InvalidOperationException
            );
            Assert.That(() => dynamic.GetValidationErrors(), Throws.InvalidOperationException);
        }

        private static readonly string[] ErrorPaths = [
            "bad-problem", "missing-problem", "conflict-problem", "validation-problem", "annotation-problem",
            "task-problem", "value-problem"
        ];

        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllersSendProblemsIndependentlyOfTheModuleSerializer(bool generated)
        {
            var controller = new ResultController();
            var api = new WebApiModule(
                "/api",
                (_, _) => throw new InvalidOperationException("Error bodies must not use payload serialization.")
            );

            if (generated) api.WithController(ResultBindings.ResultController, () => controller);

            else api.WithController(() => controller);

            await TestWebServer.UseAsync(server => server.WithModule(api), async client =>
            {
                foreach (var path in ErrorPaths)
                {
                    using var response = await client.GetAsync("/api/" + path);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    using var assertions = Assert.EnterMultipleScope();
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                    Assert.That(
                        json.RootElement
                            .GetProperty("status")
                            .GetInt32(),
                        Is.EqualTo((int)response.StatusCode)
                    );
                    Assert.That(
                        json.RootElement
                            .GetProperty("requestId")
                            .GetString(),
                        Is.Not.Empty
                    );

                    if (path.Contains("validation", StringComparison.Ordinal) || path.Contains("annotation", StringComparison.Ordinal))
                        Assert.That(
                            json.RootElement
                                .GetProperty("errors")
                                .GetProperty("book.Name")[0]
                                .GetString(),
                            Is.EqualTo("Required.")
                        );
                }
            });
            Assert.That(controller.Disposed, Is.EqualTo(ErrorPaths.Length));
        }

        [Test]
        public void ProblemResultsUsedAsPayloadsDoNotBecomeProblemResponseMetadata()
        {
            var operation = ResultBindings.ResultController.Routes.Single(
                route => route.Route == "/wrapped-problem"
            ).Operation
                ?? throw new InvalidOperationException("Missing operation metadata.");
            var response = operation.Responses.Single();
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(response.Type, Is.EqualTo(typeof(BadRequestProblemResult)));
            Assert.That(response.ContentType, Is.Null);
        }

        [Test]
        public void ErrorOpenApiUsesProblemSchemasWithoutJsonMetadataAndHonorsOverrides()
        {
            var document = new OpenApiDocumentBuilder("Errors", "1")
                .AddController("/api", ResultBindings.ResultController)
                .Build(ResultJson.Default.Options);
            var directory = Path.Combine(AppContext.BaseDirectory, "openapi");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "typed-errors.json"), document.ToJson());
            var paths = document.Root["paths"] ?? throw new InvalidOperationException("Missing paths.");
            var validation =
                paths["/api/validation-problem"]?["get"]?["responses"]?["400"]?["content"]?[
                    "application/problem+json"
                ]?["schema"];
            var general =
                paths["/api/shared-problem"]?["get"]?["responses"]?["400"]?["content"]?["application/problem+json"]?[
                    "schema"
                ];
            var reverse =
                paths["/api/shared-problem-reverse"]?["get"]?["responses"]?["400"]?["content"]?[
                    "application/problem+json"
                ]?["schema"];
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(validation?["properties"]?["status"]?["const"]?.GetValue<int>(), Is.EqualTo(400));
            Assert.That(
                validation?["properties"]?["errors"]?["additionalProperties"]?["items"]?["type"]?.GetValue<string>(),
                Is.EqualTo("string")
            );
            Assert.That(
                validation?["required"]
                    ?.AsArray()
                    .Select(item => item?.GetValue<string>()),
                Does.Contain("errors")
            );
            Assert.That(general?["properties"]?["errors"], Is.Null);
            Assert.That(JsonNode.DeepEquals(general, reverse), Is.True);
            Assert.That(paths["/api/validation-problem"]?["get"]?["responses"]?["default"], Is.Null);
            Assert.That(
                paths["/api/override-problem"]?["get"]?["responses"]?["400"]?["content"]?["text/plain"]?["schema"]?[
                    "type"
                ]?.GetValue<string>(),
                Is.EqualTo("string")
            );
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task ValidationProblemsCompleteRealHttpResponsesAndHead(int protocol)
        {
            var shared = HttpResults.ValidationProblem(
                new Dictionary<string, string[]> { ["book.Name"] = ["Required."] }
            );
            await WireResult(
                context => shared.ExecuteAsync(context),
                async client =>
                {
                    using var first = await client.GetAsync("/");
                    using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
                    using var second = await client.GetAsync("/");
                    using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
                    using var request = new HttpRequestMessage(HttpMethod.Head, "/")
                    {
                        Version = client.DefaultRequestVersion,
                        VersionPolicy = client.DefaultVersionPolicy
                    };
                    using var head = await client.SendAsync(request);
                    using var assertions = Assert.EnterMultipleScope();
                    Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(
                        firstJson.RootElement
                            .GetProperty("errors")
                            .GetProperty("book.Name")[0]
                            .GetString(),
                        Is.EqualTo("Required.")
                    );
                    Assert.That(
                        firstJson.RootElement
                            .GetProperty("requestId")
                            .GetString(),
                        Is.Not.EqualTo(
                            secondJson.RootElement
                                .GetProperty("requestId")
                                .GetString()
                        )
                    );
                    Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                },
                protocol
            );
        }

        [TestCase("bad")]
        [TestCase("missing")]
        [TestCase("conflict")]
        [TestCase("validation")]
        public async Task ErrorResultsAreNotPublishedIntoOutputCaching(string kind)
        {
            var cache = new OutputCache();
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1));
            var calls = 0;
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(context => cache.ExecuteAsync(context, policy, captured =>
                        {
                            calls++;
                            HttpResult result = kind switch
                            {
                                "bad" => HttpResults.BadRequest(new ProblemDetails(400)),
                                "missing" => HttpResults.NotFound(new ProblemDetails(404)),
                                "conflict" => HttpResults.Conflict(new ProblemDetails(409)),
                                _ => HttpResults.ValidationProblem(
                                    new Dictionary<string, string[]> { ["value"] = ["Invalid."] }
                                )
                            };
                            return result.ExecuteAsync(captured);
                        }))),
                async client =>
                {
                    using var first = await client.GetAsync("/");
                    using var second = await client.GetAsync("/");
                    Assert.That(second.StatusCode, Is.EqualTo(first.StatusCode));
                }
            );
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(cache.Count, Is.Zero);
        }

        [Test]
        public async Task ValidationProblemsRetainFiniteIdempotencyReplayBehavior()
        {
            var coordinator = new IdempotencyCoordinator();
            var policy = new IdempotencyPolicy("validation-result", _ => "owner", TimeSpan.FromMinutes(1));
            var calls = 0;
            await TestWebServer.UseAsync(
                server => server.WithModule(
                    new ActionModule(context => coordinator.ExecuteAsync(context, policy, staged =>
                        {
                            calls++;
                            return HttpResults
                                .ValidationProblem(new Dictionary<string, string[]> { ["book.Name"] = ["Required."] })
                                .ExecuteAsync(staged);
                        }))
                ),
                async client =>
                {
                    string? body = null;

                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
                        request.Headers.Add("Idempotency-Key", "same");
                        using var response = await client.SendAsync(request);
                        var current = await response.Content.ReadAsStringAsync();
                        using var assertions = Assert.EnterMultipleScope();
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

                        if (body != null) Assert.That(current, Is.EqualTo(body));

                        body = current;
                    }
                }
            );
            Assert.That(calls, Is.EqualTo(1));
        }
    }

    public sealed partial class ResultController
    {
        [Route(HttpVerbs.Get, "/bad-problem")]
        public BadRequestProblemResult BadProblem() =>
            HttpResults.BadRequest(new ProblemDetails(400) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/missing-problem")]
        public NotFoundProblemResult MissingProblem() =>
            HttpResults.NotFound(new ProblemDetails(404) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/conflict-problem")]
        public ConflictProblemResult ConflictProblem() =>
            HttpResults.Conflict(new ProblemDetails(409) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/validation-problem")]
        public ValidationProblemResult ValidationProblem() =>
            HttpResults.ValidationProblem(
                new Dictionary<string, string[]> { ["book.Name"] = ["Required."] },
                HttpContext.RequestedPath
            );

        [Route(HttpVerbs.Get, "/annotation-problem")]
        public ValidationProblemResult AnnotationProblem()
        {
            var failure = new ValidationResult("Required.", ErrorMemberNames);
            return HttpResults.ValidationProblem([failure], HttpContext.RequestedPath);
        }

        private static readonly string[] ErrorMemberNames = ["book.Name"];

        [Route(HttpVerbs.Get, "/task-problem")]
        public Task<BadRequestProblemResult> TaskProblem() => Task.FromResult(BadProblem());

        [Route(HttpVerbs.Get, "/value-problem")]
        public ValueTask<ValidationProblemResult> ValueProblem() => new(ValidationProblem());

        [Route(HttpVerbs.Get, "/shared-problem")]
        public HttpResult<ValidationProblemResult, BadRequestProblemResult> SharedProblem() =>
            HttpContext.RequestedPath.Length == 0 ? ValidationProblem() : BadProblem();

        [Route(HttpVerbs.Get, "/shared-problem-reverse")]
        public HttpResult<BadRequestProblemResult, ValidationProblemResult> SharedProblemReverse() =>
            HttpContext.RequestedPath.Length == 0 ? BadProblem() : ValidationProblem();

        [Route(HttpVerbs.Get, "/wrapped-problem")]
        [OpenApiIgnore]
        public OkResult<BadRequestProblemResult> WrappedProblem() => HttpResults.Ok(BadProblem());

        [Route(HttpVerbs.Get, "/override-problem")]
        [OpenApiResponse(400, Type = typeof(string), ContentType = "text/plain")]
        public BadRequestProblemResult OverrideProblem() => BadProblem();
    }
}
