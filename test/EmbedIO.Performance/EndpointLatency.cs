using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

// A fresh server process per sample; readiness is reported without sending a request.
// The client is a separate process. Timings include its HTTP stack and loopback I/O.
internal static class EndpointLatency
{
    internal static bool Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--endpoint-latency-host")
        {
            HostAsync(args[1], args[2]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length < 1 || args[0] != "--endpoint-latency") return false;
        var rounds = args.Length == 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 7;
        if (args.Length > 2 || rounds is < 1 or > 100) throw new ArgumentException("Use --endpoint-latency [rounds: 1..100].");
        MeasureAsync(rounds).GetAwaiter().GetResult();
        return true;
    }

    private static async Task MeasureAsync(int rounds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        using var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var assembly = typeof(WebServer).Assembly;
        var available = new[] { "literal", "literal-100", "parameter", "controller", "json", "cookies", "large-header" };
        var selected = Environment.GetEnvironmentVariable("EMBEDIO_LATENCY_SCENARIOS")?.Split(',') ?? available;
        if (selected.Length == 0 || selected.Any(scenario => !available.Contains(scenario)) || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("EMBEDIO_LATENCY_SCENARIOS must contain distinct supported scenario names.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            environment = new
            {
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                runnerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EndpointLatency).Assembly.Location))),
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                rounds,
                scenarios = selected,
                traceLevel = Environment.GetEnvironmentVariable("EMBEDIO_LATENCY_TRACE_LEVEL") ?? "Off",
                method = "Fresh server per sample; no HTTP readiness probe; HTTP/1.1 loopback; complete bodies validated; first error aborts without retry. Cold means fresh process, not cold filesystem/OS caches. Ready notification includes runtime launch and console reporting. Request timings include the separate client HTTP stack. No latency pass/fail thresholds."
            }
        }));
        // Exercise the client before measured samples, using a disposable server process.
        await SampleAsync(client, "literal", true, -1, token).ConfigureAwait(false);
        for (var round = 0; round < rounds; round++)
        {
            var scenarios = (string[])selected.Clone();
            if (round % 2 != 0) Array.Reverse(scenarios);
            foreach (var scenario in scenarios)
                foreach (var endpointOnly in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    Console.WriteLine(JsonSerializer.Serialize(await SampleAsync(client, scenario, endpointOnly, round, token).ConfigureAwait(false)));
        }
    }

    private static async Task<object> SampleAsync(HttpClient client, string scenario, bool endpointOnly, int round, CancellationToken token)
    {
        // Reserving then releasing an ephemeral port has a bind race. A bind failure aborts;
        // it is never silently retried or removed from the sample population.
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(EndpointLatency).Assembly.Location);
        info.ArgumentList.Add("--endpoint-latency-host");
        info.ArgumentList.Add(scenario);
        info.ArgumentList.Add(url);
        var start = Stopwatch.GetTimestamp();
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Unable to start latency host.");
        var errors = child.StandardError.ReadToEndAsync(token);
        try
        {
            var line = await child.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            var readyMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (line is null) throw new InvalidOperationException("Host exited before readiness: " + await errors.ConfigureAwait(false));
            var ready = line.Split(' ');
            if (ready.Length != 3 || ready[0] != "READY") throw new InvalidDataException("Invalid readiness record.");
            var configureMilliseconds = double.Parse(ready[1], CultureInfo.InvariantCulture);
            var listenMilliseconds = double.Parse(ready[2], CultureInfo.InvariantCulture);
            if (endpointOnly)
                for (var i = 0; i < 20; i++) await RequestAsync(client, url + "__control", "ok", token).ConfigureAwait(false);
            var target = url + (scenario == "parameter" ? "target/42" : "target");
            var expected = scenario == "json" ? "{\"message\":\"ok\"}" : "ok";
            var first = await RequestAsync(client, target, expected, token, scenario).ConfigureAwait(false);
            var firstCompleteFromLaunch = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var warm = new List<double>();
            for (var i = 0; i < 100; i++) warm.Add((await RequestAsync(client, target, expected, token, scenario).ConfigureAwait(false)).CompleteMilliseconds);
            warm.Sort();
            await child.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
            await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (child.ExitCode != 0) throw new InvalidOperationException("Host failed: " + await errors.ConfigureAwait(false));
            return new
            {
                scenario,
                round,
                phase = endpointOnly ? "first-endpoint-after-control" : "first-server-request",
                readyNotificationMilliseconds = readyMilliseconds,
                serverConfigureMilliseconds = configureMilliseconds,
                serverListenMilliseconds = listenMilliseconds,
                first.HeadersMilliseconds,
                first.CompleteMilliseconds,
                processToFirstCompleteMilliseconds = endpointOnly ? (double?)null : firstCompleteFromLaunch,
                warmP50Milliseconds = warm[49],
                warmP95Milliseconds = warm[94],
                warmP99Milliseconds = warm[98],
                warmMaxMilliseconds = warm[99],
                warmCount = warm.Count
            };
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private static async Task<(double HeadersMilliseconds, double CompleteMilliseconds)> RequestAsync(HttpClient client, string url, string expected, CancellationToken token, string? scenario = null)
    {
        var start = Stopwatch.GetTimestamp();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var headers = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var complete = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (response.StatusCode != HttpStatusCode.OK || response.Version != HttpVersion.Version11 || body != expected)
            throw new InvalidDataException("Unexpected latency response: " + response.StatusCode + " " + body);
        if (scenario == "cookies")
        {
            var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
            if (cookies.Length != 2 || !cookies.Contains("raw=one; Path=/") || !cookies.Contains("structured=two; Path=/"))
                throw new InvalidDataException("Unexpected latency cookies.");
        }
        if (scenario == "large-header" && response.Headers.GetValues("X-Large").Single() != new string('x', 17000))
            throw new InvalidDataException("Unexpected latency large header.");
        return (headers, complete);
    }

    private static async Task HostAsync(string scenario, string url)
    {
        EmbedIO.Diagnostics.Log.Source.Switch.Level = Enum.Parse<SourceLevels>(Environment.GetEnvironmentVariable("EMBEDIO_LATENCY_TRACE_LEVEL") ?? "Off");
        var start = Stopwatch.GetTimestamp();
        using var server = new WebServer(url).PreferNoCompressionFor("text/*").PreferNoCompressionFor("application/json");
        if (scenario == "controller")
        {
            server.WithWebApi("/", ResponseSerializer.None(false), module => module.WithController<LatencyController>());
        }
        else
        {
            var routes = new RoutingModule("/");
            routes.Add(HttpVerbs.Get, RouteMatcher.Parse("/__control", false), (context, _) => ReplyAsync(context, "ok"));
            if (scenario == "literal-100")
                for (var i = 0; i < 99; i++) routes.Add(HttpVerbs.Get, RouteMatcher.Parse("/unused-" + i.ToString(CultureInfo.InvariantCulture), false), (context, _) => ReplyAsync(context, "ok"));
            routes.Add(HttpVerbs.Get, RouteMatcher.Parse(scenario == "parameter" ? "/target/{id}" : "/target", false),
                (context, route) =>
                {
                    if (scenario == "parameter" && route["id"] != "42") throw new InvalidDataException("Unexpected route parameter.");
                    if (scenario == "cookies")
                    {
                        context.Response.Headers.Add("Set-Cookie", "raw=one; Path=/");
                        context.Response.SetCookie(new Cookie("structured", "two", "/"));
                    }
                    if (scenario == "large-header") context.Response.Headers["X-Large"] = new string('x', 17000);
                    return scenario == "json" ? context.SendDataAsync(new Payload { Message = "ok" }) : ReplyAsync(context, "ok");
                });
            server.WithModule(routes);
        }
        var configured = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = server.RunAsync(stop.Token);
        if (server.State != WebServerState.Listening) throw new InvalidOperationException("Host did not report readiness.");
        var listening = Stopwatch.GetElapsedTime(start).TotalMilliseconds - configured;
        // Do not initialize JSON metadata while reporting readiness: JSON is itself a cold workload.
        BenchmarkControl.Write(FormattableString.Invariant($"READY {configured:R} {listening:R}"));
        try
        {
            if (await Console.In.ReadLineAsync(stop.Token).ConfigureAwait(false) != "stop") throw new InvalidDataException("Expected stop command.");
        }
        finally { stop.Cancel(); await running.ConfigureAwait(false); }
    }

    public sealed class Payload
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class LatencyController : WebApiController
    {
        [Route(HttpVerbs.Get, "/__control")]
        public Task Control() => ReplyAsync(HttpContext, "ok");
        [Route(HttpVerbs.Get, "/target")]
        public Task Target() => ReplyAsync(HttpContext, "ok");
    }

    private static async Task ReplyAsync(IHttpContext context, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, context.CancellationToken).ConfigureAwait(false);
    }
}
