using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Testing;

internal static class HttpResultsFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) =>
        RunCoreAsync(trace, false, token);

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--typed-results-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --typed-results-selftest");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await RejectAsync(true, false, "typed-results.actor-body");
        await RejectAsync(false, true, "typed-results.validation-fields");
        return true;

        async Task RejectAsync(bool actorFault, bool validationFault, string invariant)
        {
            try
            {
                await RunCoreAsync(
                    new StateMachineTrace("typed-results", 1729, []),
                    actorFault,
                    stop.Token,
                    validationFault
                );
            }

            catch (StateMachineFailure failure) when (failure.Invariant == invariant)
            {
                Console.WriteLine("Rejected injected typed-result mismatch: " + failure.Invariant);
                return;
            }

            throw new StateMachineFailure("typed-results.negative-oracle", "The injected mismatch was accepted.");
        }
    }

    internal static readonly string[] varyByHeaders = ["X-Actor", "X-Value"];

    private static async Task RunCoreAsync(
        StateMachineTrace trace,
        bool injectFault,
        CancellationToken token,
        bool injectValidationFault = false
    )
    {
        var routes = new NamedRoutes(new Dictionary<string, string> { ["item"] = "/items/{id}" });
        var nonce = new SecurityHeadersPolicy("script-src 'nonce-{nonce}'");
        var cache = new OutputCache();
        var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1), varyByHeaders);
        var seenNonces = new HashSet<string>(StringComparer.Ordinal);
        var entered = 0;
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TestWebServer();
        server.WithModule(new ActionModule(async context =>
            {
                var gate = release;

                if (Interlocked.Increment(ref entered) == 3) gate.TrySetResult();

                await gate.Task.WaitAsync(token);
                var actor = context.Request.Headers["X-Actor"] ?? throw new InvalidOperationException("Missing actor.");
                var value = context.Request.Headers["X-Value"] ?? throw new InvalidOperationException("Missing value.");
                var payload = actor + ":" + value;
                var kind = context.Request.Url.AbsolutePath;

                if (kind == "/nonce") nonce.Apply(context);

                await cache.ExecuteAsync(context, policy, async captured =>
                {
                    Interlocked.Increment(ref calls);
                    HttpResult result = kind switch
                    {
                        "/missing" => (HttpResult<OkResult<string>, NotFoundResult<string>>)HttpResults.NotFound<string>(
                        ),
                        "/created" => HttpResults.CreatedAt(
                            routes,
                            "item",
                            new Dictionary<string, object?> { ["id"] = payload },
                            payload
                        ),
                        "/bytes" => (HttpResult<FileHttpResult, NoContentResult>)HttpResults.Bytes(
                            Encoding.UTF8.GetBytes(payload),
                            "text/plain"
                        ),
                        "/problem" => HttpResults.Conflict(new ProblemDetails(409) { Detail = payload }),
                        "/bad-problem" => HttpResults.BadRequest(new ProblemDetails(400) { Detail = payload }),
                        "/missing-problem" => HttpResults.NotFound(new ProblemDetails(404) { Detail = payload }),
                        "/validation" => Validation(payload, false, injectValidationFault),
                        "/annotation-validation" => Validation(payload, true, false),
                        "/redirect" => HttpResults.Redirect("/next/" + actor + "?v=" + value),
                        "/nonce" => HttpResults.Text(payload + ":" + captured.GetSecurityHeadersNonce()),
                        _ => (HttpResult<OkResult<string>, NotFoundResult<string>, ConflictResult<string>>)HttpResults.Ok(
                            injectFault && actor == "1" ? "wrong-actor" : payload
                        )
                    };
                    await result.ExecuteAsync(captured, ResponseSerializer.None(false));
                });
            }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = server.RunAsync(stop.Token);
        var clients = Enumerable
            .Range(0, 3)
            .Select(_ => TestHttpClient.Create(server))
            .ToArray();

        try
        {
            await Batch("/ok", 42);
            await Batch("/validation", 43);

            foreach (var step in trace.Steps)
            {
                var value = step.Value & 65535;
                var kind = (uint)step.Command % 13;

                if (kind == 7)
                {
                    var rejected = false;

                    try
                    {
                        _ = routes.GetPath("item", new Dictionary<string, object?> { ["id"] = "bad/" + value });
                    }

                    catch (ArgumentException)
                    {
                        rejected = true;
                    }

                    Require(rejected, "typed-results.route-boundaries");
                    trace.Cover("typed-results.route-boundaries");
                    continue;
                }

                if (kind == 12)
                {
                    var longMessage = new string('x', 4097);
                    var invalid = (value % 4) switch
                    {
                        0 => new Dictionary<string, string[]> { ["value"] = [] },
                        1 => new Dictionary<string, string[]> { ["value"] = [longMessage] },
                        2 => new Dictionary<string, string[]> { ["value"] = ["\uD800"] },
                        _ => []
                    };
                    var rejected = false;

                    try
                    {
                        _ = HttpResults.ValidationProblem(invalid);
                    }

                    catch (ArgumentException)
                    {
                        rejected = true;
                    }

                    Require(rejected, "typed-results.validation-limits");
                    trace.Cover("typed-results.validation-limits");
                    continue;
                }

                var path = ResultPaths[kind];
                await Batch(path, value);

                if (kind == 0)
                {
                    var before = calls;
                    await Batch(path, value);
                    Require(calls == before, "typed-results.cached-finite");
                    trace.Cover("typed-results.cached-finite");
                }

                if (kind == 3)
                {
                    var before = calls;
                    await Batch(path, value);
                    Require(calls == before + 3, "typed-results.file-cache-bypass");
                    trace.Cover("typed-results.file-cache-bypass");
                }

                if (kind == 6) cache.Clear();

                trace.Cover("typed-results." + path[1..]);
            }
        }

        finally
        {
            foreach (var client in clients) client.Dispose();

            stop.Cancel();
            await running;
        }

        async Task Batch(string path, int value)
        {
            entered = 0;
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var text = value.ToString(CultureInfo.InvariantCulture);
            var results = await Task.WhenAll(clients.Select(async (client, actor) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    request.Headers.Add("X-Actor", actor.ToString(CultureInfo.InvariantCulture));
                    request.Headers.Add("X-Value", text);
                    using var response = await client.SendAsync(request, token);
                    var body = await response.Content.ReadAsStringAsync(token);
                    var expected = actor.ToString(CultureInfo.InvariantCulture) + ":" + text;
                    var expectedStatus = path switch
                    {
                        "/missing" => 404,
                        "/created" => 201,
                        "/problem" => 409,
                        "/bad-problem" or "/validation" or "/annotation-validation" => 400,
                        "/missing-problem" => 404,
                        "/redirect" => 302,
                        _ => 200
                    };
                    Require((int)response.StatusCode == expectedStatus, "typed-results.status");

                    if (path == "/missing" || path == "/redirect") Require(body.Length == 0, "typed-results.empty");

                    else if (path is "/problem" or "/bad-problem" or "/missing-problem" or "/validation" or "/annotation-validation")
                    {
                        using var problem = JsonDocument.Parse(body);
                        var root = problem.RootElement;
                        Require(
                            response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                                && root
                                    .GetProperty("status")
                                    .GetInt32()
                                    == expectedStatus
                                && root
                                    .GetProperty("detail")
                                    .GetString()
                                    == expected
                                && !string.IsNullOrEmpty(
                                    root
                                        .GetProperty("requestId")
                                        .GetString()
                                ),
                            "typed-results.problem-body"
                        );

                        if (path is "/validation" or "/annotation-validation")
                        {
                            Require(
                                root
                                    .GetProperty("errors")
                                    .TryGetProperty("value", out var messages)
                                    && messages.GetArrayLength() == 1
                                    && messages[0].GetString() == expected + "\"<tag>\r\n界",
                                "typed-results.validation-fields"
                            );
                        }
                    }

                    else if (path == "/nonce")
                    {
                        Require(body.StartsWith(expected + ":", StringComparison.Ordinal), "typed-results.actor-body");
                        var n = body[(expected.Length + 1)..];
                        Require(
                            Convert.FromBase64String(n).Length == 32
                                && response.Headers
                                    .GetValues("Content-Security-Policy")
                                    .Single()
                                    == "script-src 'nonce-" + n + "'"
                                && response.Headers.CacheControl?.NoStore
                                == true,
                            "typed-results.nonce"
                        );
                    }

                    else Require(body == expected, "typed-results.actor-body");

                    if (path == "/created") Require(response.Headers.Location?.OriginalString == "/items/" + actor.ToString(CultureInfo.InvariantCulture) + "%3A" + text, "typed-results.location");

                    if (path == "/redirect") Require(response.Headers.Location?.OriginalString == "/next/" + actor.ToString(CultureInfo.InvariantCulture) + "?v=" + text, "typed-results.redirect");

                    return body;
                }));

            if (path == "/nonce")
                foreach (var body in results)
                    Require(seenNonces.Add(body[(body.LastIndexOf(':') + 1)..]), "typed-results.nonce-isolation");

            trace.Cover("typed-results.three-clients-overlap");
        }
    }

    private static readonly string[] ResultPaths = [
        "/ok", "/missing", "/created", "/bytes", "/problem", "/redirect", "/nonce", "/unused", "/validation",
        "/annotation-validation", "/bad-problem", "/missing-problem"
    ];

    private static ValidationProblemResult Validation(string payload, bool annotations, bool fault)
    {
        var message = payload + "\"<tag>\r\n界";
        var fieldName = fault ? "wrong-field" : "value";

        if (annotations)
        {
            var members = new[] { fieldName };
            var source = new ValidationResult(message, members);
            var result = HttpResults.ValidationProblem([source], payload);
            members[0] = "mutated";
            source.ErrorMessage = "mutated";
            return result;
        }

        var messages = new[] { message };
        var errors = new Dictionary<string, string[]> { [fieldName] = messages };
        var snapshot = HttpResults.ValidationProblem(errors, payload);
        messages[0] = "mutated";
        errors.Clear();
        return snapshot;
    }

    private static void Require(bool condition, string invariant)
    {
        if (!condition)
            throw new StateMachineFailure(
                invariant,
                "Typed response violated its independent actor/status/location model."
            );
    }
}
