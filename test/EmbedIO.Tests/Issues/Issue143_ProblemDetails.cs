using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Opt-in RFC 9457 problem details for HTTP API errors.
    [TestFixture]
    public sealed class Issue143_ProblemDetails
    {
        private const string Secret = "Server=db;Password=hunter2";

        // Thrown repeatedly, so handlers must not change it.
        private static readonly ProblemDetails OutOfStock = new ProblemDetails(409)
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Out of stock",
            Detail = "Item 7 is out of stock.",
            Instance = "/orders/42",
        }.WithExtension("errors", new JsonArray((JsonNode?)"quantity"));

        public sealed class Item
        {
            public string Name { get; set; } = string.Empty;

            public int Count { get; set; }
        }

        public sealed class ItemsController : WebApiController
        {
            [Route(HttpVerbs.Get, "/items/{id}")]
            public Item Get(int id) => id == 1 ? new Item { Name = "one", Count = 1 } : throw HttpException.NotFound($"No item {id}.");

            [Route(HttpVerbs.Get, "/search")]
            public string Search([QueryField] int limit) => "found " + limit;

            [Route(HttpVerbs.Post, "/items")]
            public Item Create([JsonData] Item item) => item;
        }

        private static IWebModule Actions() => new ActionModule(async context =>
        {
            switch (context.RequestedPath)
            {
                case "/boom":
                    throw new InvalidOperationException("Cannot open " + Secret);

                case "/conflict":
                    throw new ProblemDetailsException(OutOfStock);

                case "/moved":
                    throw HttpException.Redirect("/elsewhere");

                case "/direct":
                    await context.SendProblemAsync(new ProblemDetails(422) { Detail = "Direct." });
                    return;

                case "/stream":
                    // Commit the status and part of the body, then fail.
                    context.Response.SendChunked = true;
                    var stream = context.Response.OutputStream;
                    var chunk = Encoding.ASCII.GetBytes(new string('x', 4096));
                    await stream.WriteAsync(chunk, 0, chunk.Length);
                    await stream.FlushAsync();
                    throw new InvalidOperationException("Failed mid-stream.");

                case "/slow":
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                    return;

                default:
                    throw HttpException.NotFound();
            }
        });

        // A running server whose exception handlers send problem details.
        private sealed class Running : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;

            internal Running(ProblemDetailsOptions? options)
            {
                Url = TestObjects.Resources.GetServerAddress();
                _server = new WebServer(o => o.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO))
                    .WithProblemDetails(options)
                    .WithModule(new WebApiModule("/api").WithController<ItemsController>())
                    .WithModule(Actions());
                _running = _server.RunAsync(_stop.Token);
            }

            internal string Url { get; }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                await _running.WaitAsync(TimeSpan.FromSeconds(10));
                _server.Dispose();
                _stop.Dispose();
            }
        }

        private static async Task<(HttpStatusCode Status, string? MediaType, string Body, HttpResponseMessage Response)> Send(HttpClient client, HttpMethod method, string url, string? accept = null, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            if (accept != null)
                request.Headers.TryAddWithoutValidation("Accept", accept);

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, response);
        }

        private static JsonObject Problem(string body) => JsonNode.Parse(body) as JsonObject ?? throw new AssertionException("Not a JSON object: " + body);

        private static void AssertProblem(JsonObject problem, int status, string title)
        {
            Assert.That(problem["status"]?.GetValue<int>(), Is.EqualTo(status));
            Assert.That(problem["title"]?.GetValue<string>(), Is.EqualTo(title));
            Assert.That(problem["type"], Is.Null, "about:blank is left implicit.");
            Assert.That(problem["requestId"]?.GetValue<string>(), Is.Not.Empty);
        }

        [Test]
        public async Task HttpExceptionsBecomeProblems()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var (status, mediaType, body, response) = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
                using (response)
                {
                    Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
                    Assert.That(mediaType, Is.EqualTo("application/problem+json"));
                    var problem = Problem(body);
                    AssertProblem(problem, 404, "Not Found");
                    Assert.That(problem["detail"]?.GetValue<string>(), Is.EqualTo("No item 9."));
                    Assert.That(problem.Select(p => p.Key), Is.EqualTo(new[] { "title", "status", "detail", "requestId" }));
                }

                // The default handler leaves the successful response alone.
                var ok = await Send(client, HttpMethod.Get, server.Url + "api/items/1");
                using (ok.Response)
                {
                    Assert.That(ok.Status, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(ok.MediaType, Is.EqualTo("application/json"));
                }
            }
        }

        [TestCase("api/items/abc", null, "Invalid route parameter value.")]
        [TestCase("api/search?limit=many", null, "Cannot convert field limit to Int32.")]
        [TestCase("api/items", "{not json", "Expected request body to be deserializable to EmbedIO.Tests.Issues.Issue143_ProblemDetails+Item.")]
        public async Task BindingFailuresAreBadRequestProblems(string path, string? json, string detail)
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var method = json == null ? HttpMethod.Get : HttpMethod.Post;
                var content = json == null ? null : new StringContent(json, Encoding.UTF8, "application/json");
                var (status, mediaType, body, response) = await Send(client, method, server.Url + path, content: content);
                using (response)
                {
                    Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(mediaType, Is.EqualTo("application/problem+json"));
                    var problem = Problem(body);
                    AssertProblem(problem, 400, "Bad Request");
                    Assert.That(problem["detail"]?.GetValue<string>(), Is.EqualTo(detail));
                }
            }
        }

        // An unhandled exception reveals nothing about itself by default.
        [Test]
        public async Task UnhandledExceptionsDiscloseNothingByDefault()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var (status, mediaType, body, response) = await Send(client, HttpMethod.Get, server.Url + "boom");
                using (response)
                {
                    Assert.That(status, Is.EqualTo(HttpStatusCode.InternalServerError));
                    Assert.That(mediaType, Is.EqualTo("application/problem+json"));
                    var problem = Problem(body);
                    AssertProblem(problem, 500, "Internal Server Error");
                    Assert.That(problem["detail"]?.GetValue<string>(), Is.EqualTo("An unexpected error occurred."));
                    Assert.That(body, Does.Not.Contain("hunter2").And.Not.Contain("InvalidOperationException").And.Not.Contain(" at "));
                }
            }
        }

        [Test]
        public async Task ExceptionDetailsCanBeIncludedForDevelopment()
        {
            await using (var server = new Running(new ProblemDetailsOptions { IncludeExceptionDetails = true, IncludeHttpExceptionMessages = false }))
            {
                using var client = new HttpClient();
                var boom = await Send(client, HttpMethod.Get, server.Url + "boom");
                using (boom.Response)
                {
                    var problem = Problem(boom.Body);
                    Assert.That(problem["detail"]?.GetValue<string>(), Is.EqualTo("Cannot open " + Secret));
                    Assert.That(problem["exception"]?["type"]?.GetValue<string>(), Is.EqualTo("System.InvalidOperationException"));
                    Assert.That(problem["exception"]?["stackTrace"]?.GetValue<string>(), Does.Contain("Issue143_ProblemDetails"));
                }

                // HTTP exception messages can be withheld independently.
                var missing = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
                using (missing.Response)
                    Assert.That(Problem(missing.Body)["detail"], Is.Null);
            }
        }

        // A thrown problem is sent as is, every time, with only the request ID added.
        [Test]
        public async Task ThrownProblemsAreSentAsIs()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var ids = new HashSet<string>();
                for (var i = 0; i < 2; i++)
                {
                    var (status, mediaType, body, response) = await Send(client, HttpMethod.Get, server.Url + "conflict");
                    using (response)
                    {
                        Assert.That(status, Is.EqualTo(HttpStatusCode.Conflict));
                        Assert.That(mediaType, Is.EqualTo("application/problem+json"));
                        var problem = Problem(body);
                        Assert.That(problem["type"]?.GetValue<string>(), Is.EqualTo("https://example.com/problems/out-of-stock"));
                        Assert.That(problem["title"]?.GetValue<string>(), Is.EqualTo("Out of stock"));
                        Assert.That(problem["detail"]?.GetValue<string>(), Is.EqualTo("Item 7 is out of stock."));
                        Assert.That(problem["instance"]?.GetValue<string>(), Is.EqualTo("/orders/42"));
                        Assert.That(problem["errors"]?.ToJsonString(), Is.EqualTo("[\"quantity\"]"));
                        Assert.That(problem.Select(p => p.Key), Is.EqualTo(new[] { "type", "title", "status", "detail", "instance", "errors", "requestId" }));
                        ids.Add(problem["requestId"]?.GetValue<string>() ?? string.Empty);
                    }
                }

                Assert.That(ids, Has.Count.EqualTo(2));
                Assert.That(OutOfStock.Extensions.Keys, Is.EqualTo(new[] { "errors" }), "The thrown instance is unchanged.");
            }
        }

        [Test]
        public async Task ProblemsCanBeCustomized()
        {
            var options = new ProblemDetailsOptions
            {
                Customize = (context, problem) =>
                {
                    problem.Type ??= "https://example.com/problems/http-" + problem.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    problem.WithExtension("path", context.Request.Url.AbsolutePath);
                },
            };
            await using (var server = new Running(options))
            {
                using var client = new HttpClient();
                var (_, _, body, response) = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
                using (response)
                {
                    var problem = Problem(body);
                    Assert.That(problem["type"]?.GetValue<string>(), Is.EqualTo("https://example.com/problems/http-404"));
                    Assert.That(problem["title"], Is.Null, "A typed problem has no default title.");
                    Assert.That(problem["path"]?.GetValue<string>(), Is.EqualTo("/api/items/9"));
                }
            }
        }

        [TestCase(null, "application/problem+json")]
        [TestCase("application/json", "application/problem+json")]
        [TestCase("application/problem+json", "application/problem+json")]
        [TestCase("*/*", "application/problem+json")]
        [TestCase("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", "text/html")]
        [TestCase("text/plain", "text/html")]
        public async Task ClientsThatPreferHtmlGetTheHtmlPage(string? accept, string mediaType)
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                foreach (var (path, status) in new[] { ("api/items/9", HttpStatusCode.NotFound), ("boom", HttpStatusCode.InternalServerError) })
                {
                    var result = await Send(client, HttpMethod.Get, server.Url + path, accept);
                    using (result.Response)
                    {
                        Assert.That(result.Status, Is.EqualTo(status));
                        Assert.That(result.MediaType, Is.EqualTo(mediaType), path);
                    }
                }
            }
        }

        [Test]
        public async Task HtmlFallbackCanBeTurnedOff()
        {
            await using (var server = new Running(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null, HtmlExceptionHandler = null }))
            {
                using var client = new HttpClient();
                foreach (var path in new[] { "api/items/9", "boom" })
                {
                    var result = await Send(client, HttpMethod.Get, server.Url + path, "text/html");
                    using (result.Response)
                        Assert.That(result.MediaType, Is.EqualTo("application/problem+json"), path);
                }
            }
        }

        [Test]
        public async Task RedirectsKeepTheirLocationAndGetNoBody()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var (status, _, body, response) = await Send(client, HttpMethod.Get, server.Url + "moved");
                using (response)
                {
                    Assert.That(status, Is.EqualTo(HttpStatusCode.Found));
                    Assert.That(response.Headers.Location?.AbsolutePath, Is.EqualTo("/elsewhere"));
                    Assert.That(body, Is.Empty);
                }
            }
        }

        [Test]
        public async Task ProblemsCanBeSentWithoutThrowing()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var (status, mediaType, body, response) = await Send(client, HttpMethod.Get, server.Url + "direct");
                using (response)
                {
                    Assert.That(status, Is.EqualTo((HttpStatusCode)422));
                    Assert.That(mediaType, Is.EqualTo("application/problem+json"));
                    AssertProblem(Problem(body), 422, "Unprocessable Entity");
                }
            }
        }

        // Once the status and part of the body are sent, no problem can replace them: the response is aborted.
        [Test]
        public async Task CommittedResponsesAreNotReplaced()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                using (var response = await client.GetAsync(server.Url + "stream", HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.Not.EqualTo("application/problem+json"));
                    var failure = await Assert.CatchAsync(async () => await response.Content.ReadAsStringAsync());
                    Assert.That(failure, Is.InstanceOf<HttpRequestException>().Or.InstanceOf<System.IO.IOException>());
                }

                // The server keeps answering.
                var after = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
                using (after.Response)
                    Assert.That(after.MediaType, Is.EqualTo("application/problem+json"));
            }
        }

        [Test]
        public async Task ConcurrentFailuresGetTheirOwnRequestIds()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
                {
                    var result = await Send(client, HttpMethod.Get, server.Url + (i % 2 == 0 ? "api/items/9" : "boom"));
                    using (result.Response)
                    {
                        Assert.That(result.MediaType, Is.EqualTo("application/problem+json"));
                        return Problem(result.Body)["requestId"]?.GetValue<string>();
                    }
                }));

                Assert.That(results.Distinct().Count(), Is.EqualTo(24));
            }
        }

        // A canceled request leaves the handlers working for the next one.
        [Test]
        public async Task CanceledRequestsDoNotDisturbLaterProblems()
        {
            await using (var server = new Running(null))
            {
                using var client = new HttpClient();
                using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                    await Assert.CatchAsync<OperationCanceledException>(async () => await client.GetAsync(server.Url + "slow", cancel.Token));

                var after = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
                using (after.Response)
                {
                    Assert.That(after.Status, Is.EqualTo(HttpStatusCode.NotFound));
                    Assert.That(after.MediaType, Is.EqualTo("application/problem+json"));
                }
            }
        }

        // A module can opt in without replacing the server's empty unhandled-error policy.
        [Test]
        public async Task DefaultsAreUnchangedAndModulesCanOptIn()
        {
            var api = new WebApiModule("/api").WithController<ItemsController>();
            api.HandleHttpException(ProblemDetailsHandler.ForHttpExceptions());
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(api, Actions());
            using var client = new HttpClient();

            var inModule = await Send(client, HttpMethod.Get, server.Url + "api/items/9");
            using (inModule.Response)
                Assert.That(inModule.MediaType, Is.EqualTo("application/problem+json"));

            var elsewhere = await Send(client, HttpMethod.Get, server.Url + "boom");
            using (elsewhere.Response)
            {
                Assert.That(elsewhere.Status, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(elsewhere.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(elsewhere.Body, Is.Empty);
            }
        }

        [Test]
        public void ProblemDetailsValidatesItsMembers()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ProblemDetails(399));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ProblemDetails(600));
            var problem = new ProblemDetails(400);
            foreach (var reserved in new[] { "type", "title", "status", "detail", "instance" })
                Assert.Throws<ArgumentException>(() => problem.WithExtension(reserved, 1));

            problem.WithExtension("count", 1);
            Assert.Throws<ArgumentException>(() => problem.WithExtension("count", 2));
            Assert.Throws<ArgumentException>(() => problem.WithExtension(string.Empty, 2));
            var owned = new JsonObject { ["inner"] = 1 };
            Assert.Throws<ArgumentException>(() => problem.WithExtension("owned", owned["inner"]));
            problem.WithExtension("none", null);

            Assert.That(problem.ToJson(), Is.EqualTo("{\"title\":\"Bad Request\",\"status\":400,\"count\":1,\"none\":null}"));
            Assert.That(new ProblemDetails(499).ToJson(), Is.EqualTo("{\"status\":499}"), "Without a reason phrase there is no default title.");
            Assert.That(ProblemDetailsHandler.MediaType, Is.EqualTo("application/problem+json"));
        }
    }
}
