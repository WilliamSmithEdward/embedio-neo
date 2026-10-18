using System.Text.Json.Serialization;
using EmbedIO;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    internal static class HttpResultChecks
    {
        internal static WebApiModule CreateModule() =>
            new WebApiModule("/typed-results", ResponseSerializer.Json(ResultSmokeJson.Default.Options)).WithController(
                ResultSmokeBindings.ResultSmokeController
            );

        internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
        {
            foreach (var path in new[] { "book/7", "task", "value" })
            {
                using var response = await client.GetAsync(url + "typed-results/" + path);

                if (
                    response.StatusCode != System.Net.HttpStatusCode.OK
                    || await response.Content.ReadAsStringAsync() != "{\"Id\":7}"
                ) throw new InvalidOperationException("Generated typed payload execution failed.");
            }

            using var missing = await client.GetAsync(url + "typed-results/book/0");

            if (
                missing.StatusCode != System.Net.HttpStatusCode.NotFound
                || (await missing.Content.ReadAsByteArrayAsync()).Length != 0
            ) throw new InvalidOperationException("Typed empty alternative failed.");

            using var created = await client.GetAsync(url + "typed-results/create");

            if (
                created.StatusCode != System.Net.HttpStatusCode.Created || created.Headers.Location?.OriginalString
                != "/typed-results/book/7"
            ) throw new InvalidOperationException("Typed named-route Location failed.");

            var doc = new OpenApiDocumentBuilder("Result smoke", "1")
                .AddController("/typed-results", ResultSmokeBindings.ResultSmokeController)
                .Build(ResultSmokeJson.Default.Options);

            if (doc.Root["paths"]?["/typed-results/book/{id}"]?["get"]?["responses"]?["404"] == null)
                throw new InvalidOperationException("Typed alternative OpenAPI metadata missing.");

            checks["typed_results"] =
                "generated sync/Task/ValueTask payloads, typed alternatives, named CreatedAt and OpenAPI";
        }
    }

    internal sealed record ResultSmokeBook(int Id);

    [JsonSerializable(typeof(ResultSmokeBook))]
    internal partial class ResultSmokeJson : JsonSerializerContext { }

    [GeneratedControllerBinding(typeof(ResultSmokeController))]
    internal static partial class ResultSmokeBindings { }

    internal sealed class ResultSmokeController : WebApiController
    {
        [OpenApiOperation(OperationId = "book")]
        [Route(HttpVerbs.Get, "/book/{id}")]
        public HttpResult<OkResult<ResultSmokeBook>, NotFoundResult<ResultSmokeBook>> Get(int id) =>
            id == 0 || HttpContext.RequestedPath.Length == 0
                ? HttpResults.NotFound<ResultSmokeBook>()
                : HttpResults.Ok(new ResultSmokeBook(id));

        [Route(HttpVerbs.Get, "/task")]
        public Task<HttpResult<ResultSmokeBook>> TaskResult() =>
            Task.FromResult<HttpResult<ResultSmokeBook>>(
                HttpResults.Ok(new ResultSmokeBook(HttpContext.RequestedPath.Length > 0 ? 7 : 0))
            );

        [Route(HttpVerbs.Get, "/value")]
        public ValueTask<HttpResult<ResultSmokeBook>> Value() =>
            new(HttpResults.Ok(new ResultSmokeBook(HttpContext.RequestedPath.Length > 0 ? 7 : 0)));

        [Route(HttpVerbs.Get, "/create")]
        public CreatedResult<ResultSmokeBook> Create()
        {
            _ = HttpContext;
            var routes = NamedRoutes.FromController("/typed-results", ResultSmokeBindings.ResultSmokeController);
            return HttpResults.CreatedAt(
                routes,
                "book",
                new Dictionary<string, object?> { ["id"] = 7 },
                new ResultSmokeBook(7)
            );
        }
    }
}
