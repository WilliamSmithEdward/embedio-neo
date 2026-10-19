using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;

internal static class RequestBindingFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) =>
        RunCoreAsync(trace, false, false, token);

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--request-binding-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --request-binding-selftest");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Reject(true, false, "request-binding.actor-body");
        await Reject(false, true, "request-binding.error-fields");
        return true;

        async Task Reject(bool bodyFault, bool fieldsFault, string invariant)
        {
            try
            {
                await RunCoreAsync(
                    new StateMachineTrace("request-binding", 1729, []),
                    bodyFault,
                    fieldsFault,
                    stop.Token
                );
            }

            catch (StateMachineFailure error) when (error.Invariant == invariant)
            {
                Console.WriteLine("Rejected injected request-binding corruption: " + invariant);
                return;
            }

            throw new StateMachineFailure("request-binding.negative-oracle", "The injected corruption was accepted.");
        }
    }

    private static async Task RunCoreAsync(
        StateMachineTrace trace,
        bool bodyFault,
        bool fieldsFault,
        CancellationToken token
    )
    {
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reflection = new TestWebServer();
        using var generated = new TestWebServer();
        Configure(reflection, false);
        Configure(generated, true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = new[]
        {
            reflection.RunAsync(stop.Token),
            generated.RunAsync(stop.Token)
        };

        try
        {
            for (var mode = 0; mode < 12; mode++) await Batch(mode, mode);

            foreach (var step in trace.Steps) await Batch((int)((uint)step.Command % 12), step.Value);
        }

        finally
        {
            stop.Cancel();
            await Task
                .WhenAll(running)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        void Configure(TestWebServer server, bool useGenerated)
        {
            server.WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null });
            server.WithModule(new PreRequestModule(async _ =>
                {
                    var gate = release;

                    if (Interlocked.Increment(ref entered) == 6) gate.TrySetResult();

                    await gate.Task.WaitAsync(token);
                }));
            var module = new WebApiModule("/", ResponseSerializer.None(false));

            if (useGenerated) module.WithController(BindingFuzzBindings.BindingFuzzController);

            else module.WithController<BindingFuzzController>();

            server.WithModule(module);
        }

        async Task Batch(int mode, int value)
        {
            entered = 0;
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.WhenAll(
                Enumerable
                    .Range(0, 3)
                    .SelectMany(actor => new[]
                    {
                        Send(reflection, actor),
                        Send(generated, actor)
                    })
            );

            async Task Send(TestWebServer server, int actor)
            {
                var n = (int)((uint)(value + actor) % 10) + 1;
                var name = "actor"
                    + actor.ToString(CultureInfo.InvariantCulture)
                    + "-"
                    + n.ToString(CultureInfo.InvariantCulture)
                    + "\"<\n😀";
                var query = "actor="
                    + actor.ToString(CultureInfo.InvariantCulture)
                    + "&value="
                    + n.ToString(CultureInfo.InvariantCulture)
                    + "&name="
                    + Uri.EscapeDataString(name);
                var path = "/query?" + query;
                string? form = null;
                var expected = 200;
                var errorField = "input.Value";
                var expectedBody = actor.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + n.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + name;

                if (mode == 1)
                {
                    path += "&value=2";
                    expected = 400;
                }

                if (mode == 2)
                {
                    path = "/route/invalid";
                    expected = 400;
                    errorField = "id";
                }

                if (mode == 3)
                {
                    path = "/form";
                    form = query;
                }

                if (mode == 4)
                {
                    path = "/form";
                    form = "name=%FF";
                    expected = 400;
                    errorField = string.Empty;
                }

                if (mode == 5)
                {
                    path = "/required";
                    expected = 400;
                    errorField = "input.Name";
                }

                if (mode == 6)
                {
                    path = "/header";
                    expectedBody = actor.ToString(CultureInfo.InvariantCulture)
                        + "|"
                        + n.ToString(CultureInfo.InvariantCulture);
                }

                if (mode == 7)
                {
                    path = "/header";
                    expected = 400;
                    errorField = "value";
                }

                if (mode == 8)
                {
                    path = "/cookie";
                    expectedBody = actor.ToString(CultureInfo.InvariantCulture)
                        + "|last-"
                        + n.ToString(CultureInfo.InvariantCulture);
                }

                if (mode == 9)
                {
                    path = "/strict?" + query + (actor % 2 == 0 ? "&na%C2%ADme=1" : "&unknown=1");
                    expected = 400;
                    errorField = "input";
                }

                if (mode == 10)
                {
                    path = "/form";
                    form = "name=" + new string('x', 4097);
                    expected = 400;
                    errorField = string.Empty;
                }

                if (mode == 11)
                {
                    path = "/route/book-" + n.ToString(CultureInfo.InvariantCulture);
                    expectedBody = actor.ToString(CultureInfo.InvariantCulture)
                        + "|"
                        + n.ToString(CultureInfo.InvariantCulture);
                }

                trace.Cover("binding-source-" + mode.ToString(CultureInfo.InvariantCulture));
                trace.Cover("binding-status-" + expected.ToString(CultureInfo.InvariantCulture));
                using var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, path);
                request.Headers.Add("X-Actor", actor.ToString(CultureInfo.InvariantCulture));

                if (mode is 6 or 7)
                    request.Headers.TryAddWithoutValidation(
                        "X-Value",
                        mode == 6 ? new[] { n.ToString(CultureInfo.InvariantCulture) } : ["first", "last"]
                    );

                if (mode == 8)
                    request.Headers.TryAddWithoutValidation(
                        "Cookie",
                        "Theme=wrong; theme=first; theme=last-" + n.ToString(CultureInfo.InvariantCulture)
                    );

                if (form != null)
                    request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");

                using var response = await server.Client.SendAsync(request, token);
                var text = await response.Content.ReadAsStringAsync(token);

                if ((int)response.StatusCode != expected)
                    throw new StateMachineFailure(
                        "request-binding.status",
                        "Unexpected binding status for mode " + mode.ToString(CultureInfo.InvariantCulture)
                    );

                if (expected == 200)
                {
                    if (bodyFault && actor == 0) text += "wrong-actor";

                    if (text != expectedBody)
                        throw new StateMachineFailure(
                            "request-binding.actor-body",
                            "The bound payload belongs to another actor or value."
                        );
                }

                else
                {
                    var json = JsonNode.Parse(text);
                    var errors = json?["errors"]?.AsObject()
                        ?? throw new StateMachineFailure(
                            "request-binding.error-fields",
                            "Missing bounded field errors."
                        );

                    if (fieldsFault && mode == 1 && actor == 0) errors.Clear();

                    if (
                        json?["status"]?.GetValue<int>() != 400
                        || string.IsNullOrEmpty(json?["requestId"]?.GetValue<string>())
                        || !errors.ContainsKey(errorField)
                    )
                        throw new StateMachineFailure(
                            "request-binding.error-fields",
                            "The structured error lost its field or request identity."
                        );
                }
            }
        }
    }
}

[GeneratedControllerBinding(typeof(BindingFuzzController))]
internal static partial class BindingFuzzBindings { }

internal sealed class BindingFuzzController : WebApiController
{
    [Route(HttpVerbs.Get, "/query")]
    public string Query([QueryObject, Validate] BindingFuzzInput input)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return Echo(input);
    }

    [Route(HttpVerbs.Post, "/form")]
    public string Form([FormObject, Validate] BindingFuzzInput input)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return Echo(input);
    }

    [Route(HttpVerbs.Get, "/strict")]
    public string Strict([QueryObject(RejectUnknownFields = true)] BindingFuzzInput input)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return Echo(input);
    }

    [Route(HttpVerbs.Get, "/required")]
    public string Required([QueryObject] BindingFuzzRequired input)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return input.Name;
    }

    [Route(HttpVerbs.Get, "/route/{id}")]
    public string RouteId([RouteField] BindingFuzzId id, [HeaderField("X-Actor")] int actor)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return actor.ToString(CultureInfo.InvariantCulture) + "|" + id.Value.ToString(CultureInfo.InvariantCulture);
    }

    [Route(HttpVerbs.Get, "/header")]
    public string Header([HeaderField("X-Value")] string value, [HeaderField("X-Actor")] int actor)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return actor.ToString(CultureInfo.InvariantCulture) + "|" + value;
    }

    [Route(HttpVerbs.Get, "/cookie")]
    public string Cookie(
        [CookieField("theme", DuplicateValues = RequestDuplicateValues.Last)] string theme,
        [HeaderField("X-Actor")] int actor
    )
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return actor.ToString(CultureInfo.InvariantCulture) + "|" + theme;
    }

    private static string Echo(BindingFuzzInput input) =>
        input.Actor.ToString(CultureInfo.InvariantCulture)
            + "|"
            + input.Value.ToString(CultureInfo.InvariantCulture)
            + "|"
            + input.Name;
}

internal sealed class BindingFuzzInput
{
    public int Actor { get; set; }
    [System.ComponentModel.DataAnnotations.Range(1, 10)]
    public int Value { get; set; }
    public string Name { get; set; } = "default";
}

internal sealed class BindingFuzzRequired
{
    [RequestField(Required = true)]
    public string Name { get; set; } = "default";
}

internal readonly record struct BindingFuzzId(int Value)
{
    public static bool TryParse(string value, IFormatProvider provider, out BindingFuzzId result)
    {
        result = default;

        if (
            !value.StartsWith("book-", StringComparison.Ordinal)
            || !int.TryParse(value.AsSpan(5), NumberStyles.None, provider, out var parsed)
        ) return false;

        result = new BindingFuzzId(parsed);
        return true;
    }
}
