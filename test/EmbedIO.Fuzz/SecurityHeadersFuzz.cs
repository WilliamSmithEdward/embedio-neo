using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Security;
using EmbedIO.Testing;

internal static class SecurityHeadersFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) =>
        RunCoreAsync(trace, false, token);

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--security-headers-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --security-headers-selftest");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await RunCoreAsync(new StateMachineTrace("security-headers", 1729, []), true, stop.Token);
        }

        catch (StateMachineFailure failure) when (failure.Invariant == "security-headers.nonce-binding")
        {
            Console.WriteLine("Rejected injected security-header/body nonce mismatch: " + failure.Invariant);
            return true;
        }

        throw new StateMachineFailure("security-headers.negative-oracle", "The injected mismatch was not detected.");
    }

    private static async Task RunCoreAsync(StateMachineTrace trace, bool injectFault, CancellationToken token)
    {
        var nonce = new SecurityHeadersPolicy(
            "default-src 'self'; script-src 'nonce-{nonce}'",
            "style-src 'nonce-{nonce}'"
        );
        var report = new SecurityHeadersPolicy(contentSecurityPolicyReportOnly: "script-src 'nonce-{nonce}'");
        var plain = new SecurityHeadersPolicy("default-src https://static.example");
        var cache = new OutputCache();
        var caching = new OutputCachePolicy(TimeSpan.FromMinutes(1));
        using var server = new TestWebServer();
        var entered = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.WithModule(new SecurityHeadersModule("/", plain));
        server.WithModule(new ActionModule(async context =>
            {
                var barrier = release;

                if (Interlocked.Increment(ref entered) == 3) barrier.TrySetResult(true);

                await barrier.Task.WaitAsync(token);
                var path = context.Request.Url.AbsolutePath;

                if (path != "/static") (path == "/report" ? report : nonce).Apply(context);

                await cache.ExecuteAsync(context, caching, async captured =>
                {
                    if (path == "/static")
                    {
                        var bytes = WebServer.Utf8NoBomEncoding.GetBytes("static");
                        captured.Response.ContentType = "text/plain";
                        captured.Response.ContentLength64 = bytes.Length;
                        await captured.Response.OutputStream.WriteAsync(bytes, captured.CancellationToken);
                        return;
                    }

                    var value = captured.GetSecurityHeadersNonce();
                    var actor = captured.Request.Headers["X-Actor"] ?? "missing";
                    captured.Response.Headers["Content-Security-Policy"] = "default-src *";
                    captured.Response.Headers["Cache-Control"] = "public, max-age=600";
                    captured.Response.Headers["ETag"] = "\"must-not-survive\"";
                    await captured.SendStringAsync(actor + "|" + (injectFault ? "fault" : value), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = server.RunAsync(stop.Token);
        var clients = Enumerable
            .Range(0, 3)
            .Select(_ => TestHttpClient.Create(server))
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            CheckExpansion(77);
            CheckExpansion(78);
            await Batch("/nonce");
            await Batch("/static");
            Require(cache.Count == 1, "security-headers.static-cache-stored");
            trace.Cover("security-headers.three-clients-overlap");

            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();

                switch ((uint)step.Command % 6)
                {
                    case 0:
                        await Batch("/nonce");
                        trace.Cover("security-headers.fresh-nonces");
                        break;

                    case 1:
                        await Batch("/static");
                        trace.Cover("security-headers.static-cache");
                        break;

                    case 2:
                        cache.Clear();
                        trace.Cover("security-headers.invalidate");
                        break;

                    case 3:
                        await Batch("/report");
                        trace.Cover("security-headers.report-only");
                        break;

                    case 4:
                        var rejected = false;

                        try
                        {
                            _ = new SecurityHeadersPolicy("default-src 'self';" + (char)(step.Value & 31) + "injected");
                        }

                        catch (ArgumentException)
                        {
                            rejected = true;
                        }

                        Require(rejected, "security-headers.controls-rejected");
                        trace.Cover("security-headers.controls-rejected");
                        break;

                    default:
                        CheckExpansion(1 + (step.Value & 127));
                        break;
                }
            }
        }

        finally
        {
            foreach (var client in clients) client.Dispose();

            stop.Cancel();
            await running;
        }

        void CheckExpansion(int repeats)
        {
            var accepted = true;

            try
            {
                _ = new SecurityHeadersPolicy(
                    "script-src " + string.Concat(Enumerable.Repeat("'nonce-{nonce}' ", repeats))
                );
            }

            catch (ArgumentException)
            {
                accepted = false;
            }

            // Independent wire-size model: prefix 11, quoted 44-character nonce source plus space 53.
            Require(accepted == (11 + 53 * repeats <= 4096), "security-headers.expanded-bound");
            trace.Cover("security-headers.expanded-bound");
        }

        async Task Batch(string path)
        {
            entered = 0;
            release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var values = await Task.WhenAll(
                Enumerable
                    .Range(0, 3)
                    .Select(actor => Send(actor, path))
            );

            if (path != "/static") foreach (var value in values) Require(seen.Add(value), "security-headers.no-replay");
        }

        async Task<string> Send(int actor, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Actor", actor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await clients[actor].SendAsync(request, token);
            Require(response.StatusCode == HttpStatusCode.OK, "security-headers.status");
            var body = await response.Content.ReadAsStringAsync(token);
            Require(Header(response, "X-Content-Type-Options") == "nosniff", "security-headers.static-fields");
            Require(Header(response, "Strict-Transport-Security") == "", "security-headers.transport-origin");

            if (path == "/static")
            {
                Require(
                    body == "static"
                        && Header(response, "Content-Security-Policy") == "default-src https://static.example",
                    "security-headers.cached-policy"
                );
                return "";
            }

            var parts = body.Split('|');
            Require(
                parts.Length == 2 && parts[0] == actor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "security-headers.actor-isolation"
            );
            var name = path == "/report" ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy";
            Require(
                Header(response, name).Contains("'nonce-" + parts[1] + "'", StringComparison.Ordinal),
                "security-headers.nonce-binding"
            );
            Require(Convert.FromBase64String(parts[1]).Length == 32, "security-headers.entropy-shape");
            Require(
                Header(response, "Cache-Control") == "no-store" && Header(response, "ETag") == "",
                "security-headers.no-storage"
            );

            if (path == "/report")
                Require(Header(response, "Content-Security-Policy") == "", "security-headers.override-removal");

            return parts[1];
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : "";

    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new StateMachineFailure(invariant, "Security-header model invariant failed.");
    }
}
