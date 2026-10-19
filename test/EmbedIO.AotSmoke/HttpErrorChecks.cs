using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using EmbedIO;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    internal static class HttpErrorChecks
    {
        internal static WebApiModule CreateModule() =>
            new WebApiModule("/typed-errors", ResponseSerializer.None(false)).WithController(
                ErrorSmokeBindings.ErrorSmokeController
            );

        internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
        {
            foreach (var path in ErrorPaths)
            {
                using var response = await client.GetAsync(url + "typed-errors/" + path);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                if (
                    (int)response.StatusCode
                        != json.RootElement
                            .GetProperty("status")
                            .GetInt32()
                        || response.Content.Headers.ContentType?.MediaType
                        != "application/problem+json"
                    || string.IsNullOrEmpty(
                        json.RootElement
                            .GetProperty("requestId")
                            .GetString()
                    )
                )
                    throw new InvalidOperationException("Typed error status/media/correlation failed.");

                if (path is "validation" or "annotations" or "value")
                    if (
                        json.RootElement
                            .GetProperty("errors")
                            .GetProperty("book.Name")[0]
                            .GetString()
                        != "Required."
                    )
                        throw new InvalidOperationException("Typed validation field serialization failed.");
            }

            // This source-generated resolver contains no error/result types: their schema needs no reflection JSON
            // metadata.
            var doc = new OpenApiDocumentBuilder("Error smoke", "1")
                .AddController("/typed-errors", ErrorSmokeBindings.ErrorSmokeController)
                .Build(ResultSmokeJson.Default.Options);
            var responseSchema =
                doc.Root["paths"]?["/typed-errors/validation"]?["get"]?["responses"]?["400"]?["content"]?[
                    "application/problem+json"
                ]?["schema"];

            if (
                responseSchema?["properties"]?["errors"]?["additionalProperties"]?["items"]?["type"]?.GetValue<string>()
                != "string"
            )
                throw new InvalidOperationException("Typed validation OpenAPI schema missing.");

            checks["typed_errors"] =
                "fixed 400/404/409 problems, copied validation fields, DataAnnotations, Task/ValueTask, alternatives and OpenAPI without error JSON metadata";
        }

        private static readonly string[] ErrorPaths = [
            "bad", "missing", "conflict", "validation", "annotations", "task", "value", "choice"
        ];
    }

    [GeneratedControllerBinding(typeof(ErrorSmokeController))]
    internal static partial class ErrorSmokeBindings { }

    internal sealed class ErrorSmokeController : WebApiController
    {
        [Route(HttpVerbs.Get, "/bad")]
        public BadRequestProblemResult Bad() =>
            HttpResults.BadRequest(
                new ProblemDetails(HttpStatusCode.BadRequest) { Detail = HttpContext.RequestedPath }
            );

        [Route(HttpVerbs.Get, "/missing")]
        public NotFoundProblemResult Missing() =>
            HttpResults.NotFound(new ProblemDetails(HttpStatusCode.NotFound) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/conflict")]
        public ConflictProblemResult Conflict() =>
            HttpResults.Conflict(new ProblemDetails(HttpStatusCode.Conflict) { Detail = HttpContext.RequestedPath });

        [Route(HttpVerbs.Get, "/validation")]
        public ValidationProblemResult Validation() =>
            HttpResults.ValidationProblem(
                new Dictionary<string, string[]> { ["book.Name"] = ["Required."] },
                HttpContext.RequestedPath
            );

        [Route(HttpVerbs.Get, "/annotations")]
        public ValidationProblemResult Annotations()
        {
            var failure = new ValidationResult("Required.", MemberNames);
            return HttpResults.ValidationProblem([failure], HttpContext.RequestedPath);
        }

        [Route(HttpVerbs.Get, "/task")]
        public Task<BadRequestProblemResult> TaskError() => Task.FromResult(Bad());

        [Route(HttpVerbs.Get, "/value")]
        public ValueTask<ValidationProblemResult> ValueError() => new(Validation());

        [Route(HttpVerbs.Get, "/choice")]
        public HttpResult<BadRequestProblemResult, ValidationProblemResult, NotFoundProblemResult> Choice() =>
            HttpContext.RequestedPath.Length == 0 ? Validation() : Missing();

        private static readonly string[] MemberNames = ["book.Name"];
    }
}
