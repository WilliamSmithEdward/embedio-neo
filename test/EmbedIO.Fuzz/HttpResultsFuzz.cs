using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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

        try
        {
            await RunCoreAsync(new StateMachineTrace("typed-results", 1729, []), true, stop.Token);
        }

        catch (StateMachineFailure failure) when (failure.Invariant == "typed-results.actor-body")
        {
            Console.WriteLine("Rejected injected typed-result actor mismatch: " + failure.Invariant);
            return true;
        }

        throw new StateMachineFailure("typed-results.negative-oracle", "The injected actor mismatch was accepted.");
    }

    internal static readonly string[] varyByHeaders = ["X-Actor", "X-Value"];

    private static async Task RunCoreAsync(StateMachineTrace trace, bool injectFault, CancellationToken token)
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
                        "/problem" => HttpResults.Problem(new ProblemDetails(409) { Detail = payload }),
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

            foreach (var step in trace.Steps)
            {
                var value = step.Value & 65535;
                var kind = (uint)step.Command % 8;

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

                var path = new[] { "/ok", "/missing", "/created", "/bytes", "/problem", "/redirect", "/nonce" }[kind];
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
                        "/redirect" => 302,
                        _ => 200
                    };
                    Require((int)response.StatusCode == expectedStatus, "typed-results.status");

                    if (path == "/missing" || path == "/redirect") Require(body.Length == 0, "typed-results.empty");

                    else if (path == "/problem") Require(body.Contains(expected, StringComparison.Ordinal), "typed-results.problem-body");

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
                Require(
                    results
                        .Distinct(StringComparer.Ordinal)
                        .Count()
                        == 3,
                    "typed-results.nonce-isolation"
                );

            trace.Cover("typed-results.three-clients-overlap");
        }
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
