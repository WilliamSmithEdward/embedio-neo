using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Idempotency;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed partial class HttpResultsTest
    {
        [Test]
        public void TypedBranchesAndTestingInspectionHaveNoWireDependency()
        {
            HttpResult<ResultBook> found = HttpResults.Ok(new ResultBook(7));
            HttpResult<ResultBook> missing = HttpResults.NotFound<ResultBook>();
            HttpResult<CreatedResult<ResultBook>, NotFoundResult<ResultBook>> selected = HttpResults.Created(
                "/books/7",
                new ResultBook(7)
            );
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(found.GetPayload<ResultBook>()?.Id, Is.EqualTo(7));
            Assert.That(missing.StatusCode, Is.EqualTo(404));
            Assert.That(missing.HasValue, Is.False);
            Assert.That(() => missing.GetPayload<ResultBook>(), Throws.InvalidOperationException);
            Assert.That(selected.Headers["Location"], Is.EqualTo("/books/7"));
            Assert.That(selected.SelectedResult, Is.TypeOf<CreatedResult<ResultBook>>());
            Assert.That(selected.GetPayload<ResultBook>()?.Id, Is.EqualTo(7));
        }

        [TestCase(199)]
        [TestCase(600)]
        [TestCase(-1)]
        public void StatusBoundsRejectInvalidResponses(int status) =>
            Assert.That(() => HttpResults.Status(status), Throws.TypeOf<ArgumentOutOfRangeException>());

        [TestCase(204)]
        [TestCase(205)]
        [TestCase(304)]
        public void BodyResultsRejectBodylessStatuses(int status) =>
            Assert.That(
                () => HttpResults.Text("body", "text/plain", status),
                Throws.TypeOf<ArgumentOutOfRangeException>()
            );

        [TestCase("//evil.example/path")]
        [TestCase("javascript:alert(1)")]
        [TestCase("/a\r\nX: 1")]
        [TestCase("/a\\b")]
        [TestCase("https://user:secret@example.com/")]
        [TestCase("relative")]
        public void LocationsRejectInjectionAndUnintendedSchemes(string value) =>
            Assert.That(() => HttpResults.Redirect(value), Throws.ArgumentException);

        [TestCase("text/plain\r\nX: 1")]
        [TestCase("*/*")]
        [TestCase("image/*")]
        [TestCase("bad media type")]
        public void MediaTypesRejectInvalidHeaders(string value) =>
            Assert.That(() => HttpResults.Text("x", value), Throws.ArgumentException);

        [TestCase("../file")]
        [TestCase("dir\\file")]
        [TestCase("bad\rname")]
        [TestCase("")]
        public void DownloadsRejectPathAndHeaderInjection(string name) =>
            Assert.That(() => HttpResults.Bytes([], downloadName: name), Throws.ArgumentException);

        private static readonly string[] SuccessfulResultPaths = ["ok", "task", "value", "choice", "json", "plain"];

        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllersExecuteResultsAndPreservePlainObjects(bool generated)
        {
            var controller = new ResultController();
            await WithController(controller, generated, async client =>
            {
                foreach (var path in SuccessfulResultPaths)
                {
                    using var response = await client.GetAsync("/api/" + path);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
                        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("7"), path);
                    }
                }

                using var missing = await client.GetAsync("/api/missing");

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                    Assert.That(await missing.Content.ReadAsByteArrayAsync(), Is.Empty);
                }

                using var none = await client.GetAsync("/api/none");

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(none.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                    Assert.That(await none.Content.ReadAsByteArrayAsync(), Is.Empty);
                }

                using var created = await client.GetAsync("/api/created");

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                    Assert.That(created.Headers.Location?.OriginalString, Is.EqualTo("/books/7"));
                }

                using var problem = await client.GetAsync("/api/problem");

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(problem.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                    Assert.That(problem.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                    Assert.That(await problem.Content.ReadAsStringAsync(), Does.Contain("requestId"));
                }
            });
            Assert.That(controller.Disposed, Is.EqualTo(10));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllerLivesUntilTheResultTaskAndSerializationFinish(bool generated)
        {
            var controller = new ResultController();
            await WithController(controller, generated, async client =>
            {
                var pending = client.GetAsync("/api/pending");
                await controller.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.Disposed, Is.Zero);
                controller.Continue.TrySetResult();
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("7"));
                }
            });
            Assert.That(controller.Disposed, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OwnedAndBorrowedStreamsCloseExactlyAsDeclared(bool leaveOpen)
        {
            var stream = new CountedResultStream(Encoding.UTF8.GetBytes("abcdef"));
            using var result = HttpResults.Stream(stream, leaveOpen: leaveOpen);
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => result.ExecuteAsync(c))),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("abcdef"));
                }
            );
            result.Dispose();
            Assert.That(stream.Closes, Is.EqualTo(leaveOpen ? 0 : 1));
            stream.Dispose();
        }

        [Test]
        public async Task StreamResultCannotBeExecutedTwiceAndAbandonedSourcesCanBeDisposed()
        {
            var source = new CountedResultStream([1, 2]);
            using var result = HttpResults.Stream(source);
            result.Dispose();
            result.Dispose();
            Assert.That(source.Closes, Is.EqualTo(1));
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => result.ExecuteAsync(c))),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                }
            );
            Assert.That(source.Closes, Is.EqualTo(1));
        }

        [TestCase("GET", null, 200, "abcdef")]
        [TestCase("GET", "bytes=1-3", 206, "bcd")]
        [TestCase("HEAD", null, 200, "")]
        [TestCase("HEAD", "bytes=1-3", 200, "")]
        [TestCase("GET", "bytes=999-", 416, "")]
        public async Task FileResultsReuseRangeAndHeadBehavior(string method, string? range, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes("abcdef");
            var result = HttpResults.Bytes(bytes, "text/plain", "résumé.txt", "\"v1\"");
            bytes[0] = 0;
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => result.ExecuteAsync(c))),
                async client =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/");

                    if (range != null) request.Headers.TryAddWithoutValidation("Range", range);

                    using var response = await client.SendAsync(request);
                    Assert.That((int)response.StatusCode, Is.EqualTo(status));

                    if (status == 416) Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("416"));

                    else
                    {
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(body));
                            Assert.That(
                                response.Content.Headers.ContentDisposition?.FileNameStar,
                                Is.EqualTo("résumé.txt")
                            );
                        }
                    }
                }
            );
        }

        [TestCase("If-None-Match", "\"v1\"", 304)]
        [TestCase("If-Match", "\"other\"", 412)]
        public async Task FilePreconditionsDoNotSendABody(string header, string value, int expected)
        {
            var result = HttpResults.Bytes(Encoding.UTF8.GetBytes("abcdef"), entityTag: "\"v1\"");
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => result.ExecuteAsync(c))),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                    request.Headers.TryAddWithoutValidation(header, value);
                    using var response = await client.SendAsync(request);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That((int)response.StatusCode, Is.EqualTo(expected));
                        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                    }
                }
            );
        }

        [Test]
        public void NamedRoutesSnapshotEscapeAndValidateValues()
        {
            var source = new Dictionary<string, string>
            {
                ["book"] = "/api/books/{id}",
                ["optional"] = "/api/{name?}"
            };
            var routes = new NamedRoutes(source);
            source["book"] = "/changed";
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(
                routes.GetPath("book", new Dictionary<string, object?> { ["id"] = "a +é?#" }),
                Is.EqualTo("/api/books/a%20%2B%C3%A9%3F%23")
            );
            Assert.That(routes.GetPath("optional"), Is.EqualTo("/api"));
            Assert.That(() => routes.GetPath("book"), Throws.ArgumentException);
            Assert.That(() => routes.GetPath("missing"), Throws.ArgumentException);
            Assert.That(
                () => routes.GetPath("optional", new Dictionary<string, object?> { ["extra"] = 1 }),
                Throws.ArgumentException
            );
            Assert.That(
                HttpResults.CreatedAt(
                    routes,
                    "book",
                    new Dictionary<string, object?> { ["id"] = 7 },
                    new ResultBook(7)
                ).Headers["Location"],
                Is.EqualTo("/api/books/7")
            );
        }

        [TestCase("../x")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("%2f")]
        [TestCase("x\0")]
        public void NamedRoutesRejectAmbiguousValues(string value)
        {
            var routes = new NamedRoutes(new Dictionary<string, string> { ["r"] = "/{id}" });
            Assert.That(
                () => routes.GetPath("r", new Dictionary<string, object?> { ["id"] = value }),
                Throws.ArgumentException
            );
        }

        [TestCase("/../x")]
        [TestCase("//evil/x")]
        [TestCase("/x?query")]
        [TestCase("/x#fragment")]
        [TestCase("/x%2f")]
        public void NamedRoutesRejectUnsafeLiteralPaths(string template) =>
            Assert.That(
                () => new NamedRoutes(new Dictionary<string, string> { ["r"] = template }),
                Throws.ArgumentException
            );

        private static readonly string[] ExpectedCreateResponseCodes = ["201", "400", "409"];
        private static readonly string[] ExpectedResponseCodes = ["200", "404"];

        [Test]
        public void GeneratedAlternativesDescribeActualStatusesAndNamedControllerRoutes()
        {
            var document = new OpenApiDocumentBuilder("Books", "1")
                .AddController("/api", ResultBindings.ResultController)
                .Build(ResultJson.Default.Options);
            var responses = document.Root["paths"]?["/api/choice"]?["get"]?["responses"]?.AsObject()
                ?? throw new InvalidOperationException("Missing responses.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(responses.Select(p => p.Key), Is.EquivalentTo(ExpectedResponseCodes));
                Assert.That(document.Root["paths"]?["/api/none"]?["get"]?["responses"]?["204"], Is.Not.Null);
                Assert.That(
                    document.Root["paths"]?["/api/create-choice"]?["post"]?["responses"]
                        ?.AsObject()
                        .Select(p => p.Key),
                    Is.EquivalentTo(ExpectedCreateResponseCodes)
                );
                Assert.That(
                    document.Root["paths"]?["/api/problem-choice"]?["get"]?["responses"]?["default"],
                    Is.Not.Null
                );
            }

            var routes = NamedRoutes.FromController("/api", ResultBindings.ResultController);
            Assert.That(routes.GetPath("ResultController.Ok"), Is.EqualTo("/api/ok"));
        }

        [Test]
        public async Task FileResultsBypassEveryNestedCacheCapture()
        {
            var outer = new OutputCache();
            var inner = new OutputCache();
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1));
            var calls = 0;
            await TestWebServer.UseAsync(
                server => server.WithModule(
                    new ActionModule(
                        context => outer.ExecuteAsync(
                            context,
                            policy,
                            capture => inner.ExecuteAsync(capture, policy, nested => HttpResults
                                .Bytes(
                                    Encoding.UTF8.GetBytes(
                                        Interlocked
                                            .Increment(ref calls)
                                            .ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    )
                                )
                                .ExecuteAsync(nested))
                        )
                    )
                ),
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/file"), Is.EqualTo("1"));
                    Assert.That(await client.GetStringAsync("/file"), Is.EqualTo("2"));
                }
            );
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(outer.Count, Is.Zero);
            Assert.That(inner.Count, Is.Zero);
        }

        [Test]
        public async Task SecurityNoncesAreBoundToTypedTextAndBypassOutputCaching()
        {
            var cache = new OutputCache();
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1));
            var calls = 0;
            await TestWebServer.UseAsync(
                s => s
                    .WithModule(new SecurityHeadersModule("/", new SecurityHeadersPolicy("script-src 'nonce-{nonce}'")))
                    .WithModule(new ActionModule(c => cache.ExecuteAsync(c, policy, async capture =>
                            {
                                calls++;
                                await HttpResults
                                    .Text(capture.GetSecurityHeadersNonce())
                                    .ExecuteAsync(capture);
                            }))),
                async client =>
                {
                    string? previous = null;

                    for (var i = 0; i < 3; i++)
                    {
                        using var response = await client.GetAsync("/");
                        var body = await response.Content.ReadAsStringAsync();

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(
                                response.Headers
                                    .GetValues("Content-Security-Policy")
                                    .Single(),
                                Is.EqualTo("script-src 'nonce-" + body + "'")
                            );
                            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
                            Assert.That(body, Is.Not.EqualTo(previous));
                        }

                        previous = body;
                    }
                }
            );
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public async Task FiniteResultsCacheAndReplayButStreamsCannotBeRecorded()
        {
            var cache = new OutputCache();
            var caching = new OutputCachePolicy(TimeSpan.FromMinutes(1));
            var calls = 0;
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => cache.ExecuteAsync(c, caching, x =>
                        {
                            calls++;
                            return HttpResults
                                .Text("cached")
                                .ExecuteAsync(x);
                        }))),
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("cached"));
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("cached"));
                }
            );
            Assert.That(calls, Is.EqualTo(1));
            var coordinator = new IdempotencyCoordinator();
            var policy = new IdempotencyPolicy("result", _ => "trusted-user", TimeSpan.FromMinutes(1));
            calls = 0;
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => coordinator.ExecuteAsync(c, policy, x =>
                        {
                            calls++;
                            return HttpResults
                                .Created("/books/7", "created")
                                .ExecuteAsync(x, ResponseSerializer.None(false));
                        }))),
                async client =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
                        request.Headers.Add("Idempotency-Key", "same");
                        using var response = await client.SendAsync(request);
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                    }
                }
            );
            Assert.That(calls, Is.EqualTo(1));
        }

        private static readonly string[] NullResultPaths = ["null", "null-task", "null-value"];

        [TestCase(false)]
        [TestCase(true)]
        public async Task NullResultsFailWithoutLeakingControllerOwnership(bool generated)
        {
            var controller = new ResultController();
            await WithController(controller, generated, async client =>
            {
                foreach (var path in NullResultPaths)
                {
                    using var response = await client.GetAsync("/api/" + path);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                }
            });
            Assert.That(controller.Disposed, Is.EqualTo(3));
        }

        private static Task WithController(ResultController controller, bool generated, Func<HttpClient, Task> use)
        {
            var api = new WebApiModule("/api", ResponseSerializer.Json(ResultJson.Default.Options));

            if (generated) api.WithController(ResultBindings.ResultController, () => controller);

            else api.WithController(() => controller);

            return TestWebServer.UseAsync(s => s.WithModule(api), use);
        }
    }

    public sealed record ResultBook(int Id);

    [JsonSerializable(typeof(ResultBook))]
    internal partial class ResultJson : JsonSerializerContext { }

    [GeneratedControllerBinding(typeof(ResultController))]
    internal static partial class ResultBindings { }

    public sealed class ResultController : WebApiController, IDisposable
    {
        public int Disposed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ResultBook Book() => new(HttpContext.RequestedPath.Length > 0 ? 7 : -1);

        [Route(HttpVerbs.Get, "/ok")]
        public HttpResult<ResultBook> Ok() => HttpResults.Ok(Book());

        [Route(HttpVerbs.Get, "/task")]
        public Task<HttpResult<ResultBook>> TaskResult() =>
            Task.FromResult<HttpResult<ResultBook>>(HttpResults.Ok(Book()));

        [Route(HttpVerbs.Get, "/value")]
        public ValueTask<HttpResult<ResultBook>> Value() => new(HttpResults.Ok(Book()));

        [Route(HttpVerbs.Get, "/choice")]
        public HttpResult<OkResult<ResultBook>, NotFoundResult<ResultBook>> Choice() =>
            HttpContext.RequestedPath.Length > 0 ? HttpResults.Ok(Book()) : HttpResults.NotFound<ResultBook>();

        [Route(HttpVerbs.Post, "/create-choice")]
        public HttpResult<CreatedResult<ResultBook>, BadRequestResult<ResultBook>, ConflictResult<ResultBook>> CreateChoice() =>
            HttpContext.RequestedPath.Length > 0
                ? HttpResults.Created("/books/7", Book())
                : HttpResults.BadRequest<ResultBook>();

        [Route(HttpVerbs.Get, "/problem-choice")]
        public HttpResult<OkResult<ResultBook>, ProblemHttpResult> ProblemChoice() =>
            HttpContext.RequestedPath.Length > 0
                ? HttpResults.Ok(Book())
                : HttpResults.Problem(new ProblemDetails(409));

        [Route(HttpVerbs.Get, "/missing")]
        public NotFoundResult<ResultBook> Missing()
        {
            _ = HttpContext;
            return HttpResults.NotFound<ResultBook>();
        }

        [Route(HttpVerbs.Get, "/none")]
        public NoContentResult None()
        {
            _ = HttpContext;
            return HttpResults.NoContent();
        }

        [Route(HttpVerbs.Get, "/created")]
        public CreatedResult<ResultBook> Created() => HttpResults.Created("/books/7", Book());

        [Route(HttpVerbs.Get, "/problem")]
        public ProblemHttpResult Problem() =>
            HttpResults.Problem(new ProblemDetails(409) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/json")]
        public JsonHttpResult<ResultBook> Json() => HttpResults.Json(Book(), ResultJson.Default.ResultBook);

        [Route(HttpVerbs.Get, "/plain")]
        public ResultBook Plain() => Book();

        [Route(HttpVerbs.Get, "/pending")]
        public async Task<HttpResult<ResultBook>> Pending()
        {
            Entered.TrySetResult();
            await Continue.Task;

            if (Disposed != 0) throw new InvalidOperationException("Controller disposed before task completion.");

            return HttpResults.Ok(Book());
        }

        [Route(HttpVerbs.Get, "/null")]
        public HttpResult<ResultBook>? Null()
        {
            _ = HttpContext;
            return null;
        }

        [Route(HttpVerbs.Get, "/null-task")]
        public Task<HttpResult<ResultBook>?> NullTask()
        {
            _ = HttpContext;
            return Task.FromResult<HttpResult<ResultBook>?>(null);
        }

        [Route(HttpVerbs.Get, "/null-value")]
        public ValueTask<HttpResult<ResultBook>?> NullValue()
        {
            _ = HttpContext;
            return new((HttpResult<ResultBook>?)null);
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    internal sealed class CountedResultStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Closes;

        protected override void Dispose(bool disposing)
        {
            if (disposing) Closes++;

            base.Dispose(disposing);
        }
    }
}
