using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;

internal static class RequestValidationFuzz
{
    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        using var reflection = new TestWebServer();
        using var generated = new TestWebServer();
        reflection.WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null });
        generated.WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null });
        reflection.WithModule(new WebApiModule("/").WithController<ValidationFuzzController>());
        generated.WithModule(new WebApiModule("/").WithController(ValidationFuzzBindings.ValidationFuzzController));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runs = new[] { reflection.RunAsync(stop.Token), generated.RunAsync(stop.Token) };
        try
        {
            for (var mode = 0; mode < 8; mode++) await Check(mode, mode);
            foreach (var step in trace.Steps)
                await Task.WhenAll(Enumerable.Range(0, 3).Select(actor => Check((int)((uint)step.Command % 8), step.Value + actor)));
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task Check(int mode, int value)
        {
            var count = (int)((uint)value % 16);
            var names = new[] { "", " ", "a", "ab", "abcd", "abcde", "\U0001F600", "\U0001F600\U0001F600", "a\0b", "a\nb", "\u00e9\u20ac" };
            var name = names[(int)((uint)value % (uint)names.Length)];
            var valid = !string.IsNullOrWhiteSpace(name) && name.Length >= 2 && name.Length <= 4 && count >= 1 && count <= 10;
            var json = new JsonObject { ["Name"] = name, ["Count"] = count }.ToJsonString();
            var path = mode == 0 ? "/plain" : "/value";
            if (mode == 0) valid = true;
            if (mode == 1) { json = "null"; valid = false; }
            if (mode == 2) { json = "{bad"; valid = false; }
            if (mode == 3) { json = "{}"; valid = false; }
            if (mode == 4) { json = "{\"Name\":\"ok\",\"Count\":1,\"Child\":{}}"; valid = false; }
            if (mode == 5) { path = "/depth"; json = "{\"Name\":\"ok\",\"Count\":1,\"Child\":{\"Name\":\"ok\",\"Count\":1}}"; valid = false; }
            if (mode == 6) { path = "/errors"; json = "{}"; valid = false; }
            var expected = valid ? 200 : 400;
            trace.Cover("validation-mode-" + mode);
            trace.Cover("validation-status-" + expected);
            var replies = await Task.WhenAll(Send(reflection), Send(generated));
            if (replies[0] != replies[1] || replies[0] != expected)
                throw new InvalidOperationException($"Validation oracle mismatch: mode={mode}, value={value}, expected={expected}, replies={string.Join(',', replies)}");

            async Task<int> Send(TestWebServer server)
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await server.Client.PostAsync(path, body, token);
                var text = await response.Content.ReadAsStringAsync(token);
                if ((int)response.StatusCode == 400 && mode != 2)
                {
                    var errors = JsonNode.Parse(text)?["errors"]?.AsObject() ?? throw new InvalidOperationException("Validation errors missing.");
                    var total = errors.Sum(field => field.Value!.AsArray().Count);
                    if (total < 1 || total > (mode == 6 ? 1 : 32)) throw new InvalidOperationException("Validation error budget violated.");
                }
                return (int)response.StatusCode;
            }
        }
    }
}

[GeneratedControllerBinding(typeof(ValidationFuzzController))]
internal static partial class ValidationFuzzBindings { }

internal sealed class ValidationFuzzController : WebApiController
{
    [Route(HttpVerbs.Post, "/value")]
    public string Value([JsonData, Validate, Required] ValidationFuzzModel? input) => "ok";
    [Route(HttpVerbs.Post, "/plain")]
    public string Plain([JsonData] ValidationFuzzModel? input) => "ok";
    [Route(HttpVerbs.Post, "/depth")]
    public string Depth([JsonData, Validate(MaxDepth = 1)] ValidationFuzzModel? input) => "ok";
    [Route(HttpVerbs.Post, "/errors")]
    public string Errors([JsonData, Validate(MaxErrors = 1)] ValidationFuzzModel? input) => "ok";
}

internal sealed class ValidationFuzzModel
{
    [Required, StringLength(4, MinimumLength = 2)]
    public string? Name { get; set; }
    [Range(1, 10)]
    public int Count { get; set; }
    public ValidationFuzzModel? Child { get; set; }
}
