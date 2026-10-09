using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using EmbedIO;

// Modes:
//   serve [--http P] [--https P] [--h3 P] [--combined]   host the application surface and wait for "stop" on stdin
//   h1 --port P [--host H] [--filter TEXT] [--out FILE]  run HTTP/1.1 requirement checks against a running server
//   h1-fuzz --port P [--host H] --seed S --iterations N [--out DIR]  stateful HTTP/1.1 campaign
//   self [--out DIR] [--seed S] [--iterations N]          start an in-process server on loopback, then run h1 and h1-fuzz
var mode = args.Length > 0 ? args[0] : "self";
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
int IntOption(string name, int fallback) => Option(name) is { } text ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Off;

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

string Identity() => JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    arch = RuntimeInformation.ProcessArchitecture.ToString(),
    processors = Environment.ProcessorCount,
    assembly = typeof(WebServer).Assembly.Location,
    assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(WebServer).Assembly.Location))),
    informational = typeof(WebServer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
});

switch (mode)
{
    case "serve":
        {
            using var host = ConformanceServer.Create();
            var endpoints = new List<(string Name, string Url, HttpListenerMode Mode)>();
            if (Option("--http") is { } http) endpoints.Add(("http", $"http://*:{http}/", HttpListenerMode.EmbedIO));
            if (Option("--https") is { } https) endpoints.Add(("https", $"https://*:{https}/", HttpListenerMode.EmbedIO));
            if (Option("--h3") is { } h3)
                endpoints.Add(("h3", $"https://*:{h3}/", args.Contains("--combined") ? HttpListenerMode.EmbedIOCombined : HttpListenerMode.EmbedIOHttp3));
            using var stop = new CancellationTokenSource();
            var running = new List<Task>();
            foreach (var endpoint in endpoints)
                running.Add(host.Add(endpoint.Url, endpoint.Mode, endpoint.Url.StartsWith("https", StringComparison.Ordinal)).RunAsync(stop.Token));
            if (host.Certificate != null)
                File.WriteAllText(Option("--cert-out") ?? "conformance-cert.pem", host.Certificate.ExportCertificatePem());
            Console.WriteLine("READY " + ConformanceServer.Describe(endpoints));
            Console.WriteLine("IDENTITY " + Identity());
            Console.Out.Flush();
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line == "stats") Console.WriteLine("STATS " + ConformanceServer.Snapshot());
                else if (line == "stop") break;
                Console.Out.Flush();
            }
            stop.Cancel();
            try { await Task.WhenAll(running); }
            catch (OperationCanceledException) { }
            Console.WriteLine("STATS " + ConformanceServer.Snapshot());
            return 0;
        }
    case "h1":
        {
            var target = new Http1Conformance.Target(Option("--host") ?? "127.0.0.1", IntOption("--port", 0), TimeSpan.FromSeconds(IntOption("--timeout", 5)));
            var outcomes = Http1Conformance.Run(target, Option("--filter"));
            foreach (var outcome in outcomes) Console.WriteLine(Http1Conformance.Ascii(Http1Conformance.Line(outcome)));
            Console.WriteLine(Http1Conformance.Summary(outcomes));
            if (Option("--out") is { } file) File.WriteAllText(file, Http1Conformance.ToJson(new { identity = JsonDocument.Parse(Identity()).RootElement, outcomes }));
            return outcomes.Any(o => o.Result is "violation" or "error") ? 1 : 0;
        }
    case "h1-fuzz":
        {
            var target = new Http1Conformance.Target(Option("--host") ?? "127.0.0.1", IntOption("--port", 0), TimeSpan.FromSeconds(IntOption("--timeout", 5)));
            var report = Http1Fuzz.Run(target, IntOption("--seed", 1), IntOption("--iterations", 2000), Option("--out"));
            Console.WriteLine(report);
            return report.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1;
        }
    case "self":
        {
            var outDir = Option("--out") ?? Path.Combine("TestResults", "http-conformance", "self");
            Directory.CreateDirectory(outDir);
            using var host = ConformanceServer.Create();
            var port = FreePort();
            using var stop = new CancellationTokenSource();
            var running = host.Add($"http://127.0.0.1:{port}/", HttpListenerMode.EmbedIO, false).RunAsync(stop.Token);
            var target = new Http1Conformance.Target("127.0.0.1", port, TimeSpan.FromSeconds(5));
            var outcomes = Http1Conformance.Run(target, Option("--filter"));
            foreach (var outcome in outcomes) Console.WriteLine(Http1Conformance.Ascii(Http1Conformance.Line(outcome)));
            Console.WriteLine(Http1Conformance.Summary(outcomes));
            File.WriteAllText(Path.Combine(outDir, "h1-conformance.json"), Http1Conformance.ToJson(new { identity = JsonDocument.Parse(Identity()).RootElement, outcomes }));
            var iterations = IntOption("--iterations", 0);
            var fuzzPassed = true;
            if (iterations > 0)
            {
                var report = Http1Fuzz.Run(target, IntOption("--seed", 1), iterations, outDir);
                Console.WriteLine(report);
                fuzzPassed = report.StartsWith("PASS", StringComparison.Ordinal);
            }
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
            return outcomes.Any(o => o.Result is "violation" or "error") || !fuzzPassed ? 1 : 0;
        }
    default:
        Console.Error.WriteLine("Unknown mode " + mode);
        return 2;
}
