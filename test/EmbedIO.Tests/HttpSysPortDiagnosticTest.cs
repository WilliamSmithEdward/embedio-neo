using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Temporary hosted-CI diagnostic for the recurring Windows Microsoft-listener 503 in
    // RequestCodingChainTest (program #181). Every failing run's 503 landed on the same
    // test address, http://localhost:12292/, which the fixture reaches through the shared
    // allocator. This probe records what HTTP.sys routes on that port and its neighbours
    // on the runner, before any other fixture runs and again immediately before
    // RequestCodingChainTest. It records observations only; it never fails the run and it
    // does not touch the shared address counter.
    [NonParallelizable]
    public class AaaHttpSysPortProbeStartTest
    {
        [Test]
        public Task RecordHttpSysStateBeforeTheSuite() => HttpSysPortProbe.RunAsync("suite-start");
    }

    [NonParallelizable]
    public class RequestCodingChainProbeTest
    {
        [Test]
        public Task RecordHttpSysStateBeforeRequestCodingChainTest() => HttpSysPortProbe.RunAsync("before-request-coding-chain");
    }

    internal static class HttpSysPortProbe
    {
        private static readonly int[] DefaultPorts = { 12290, 12291, 12292, 12293, 12294 };

        // Local validation can move the probe away from the shared 11000+ test range
        // with EMBEDIO_HTTPSYS_PROBE_PORTS=20290,20291 (comma separated).
        private static int[] Ports { get; } = ReadPorts();

        public static async Task RunAsync(string phase)
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("HTTP.sys diagnostic applies to Windows only.");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                && Environment.GetEnvironmentVariable("EMBEDIO_HTTPSYS_PROBE") != "1")
                Assert.Ignore("Hosted CI diagnostic; local test port ranges are shared with concurrent runs.");

            var report = new StringBuilder();
            Line(report, $"ports={string.Join(",", Ports)}");
            Line(report, $"phase={phase} utc={DateTime.UtcNow:O} runtime={Environment.Version} os={Environment.OSVersion}");
            await AppendErrorLogEntries(report, "error-log-before").ConfigureAwait(false);
            await AppendCommand(report, "servicestate", "netsh", "http show servicestate view=requestq verbose=yes", FilterRequestQueueBlocks).ConfigureAwait(false);
            await AppendCommand(report, "urlacl", "netsh", "http show urlacl", FilterLinesMentioningPorts).ConfigureAwait(false);
            await AppendCommand(report, "netstat", "netstat", "-ano -p tcp", FilterLinesMentioningPorts).ConfigureAwait(false);
            foreach (var port in Ports)
                await ProbePort(report, port).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            await AppendErrorLogEntries(report, "error-log-after").ConfigureAwait(false);

            var text = report.ToString();
            TestContext.Out.WriteLine(text.Length > 60000 ? text[..60000] : text);
            try
            {
                var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
                if (!string.IsNullOrEmpty(root))
                {
                    var folder = Path.Combine(root, "TestResults", "httpsys-probe");
                    Directory.CreateDirectory(folder);
                    await File.WriteAllTextAsync(Path.Combine(folder, phase + ".txt"), text, new UTF8Encoding(false)).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                TestContext.Out.WriteLine($"report file not written: {error.Message}");
            }
        }

        private static async Task ProbePort(StringBuilder report, int port)
        {
            var prefix = string.Format(CultureInfo.InvariantCulture, "http://localhost:{0}/", port);
            var sw = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.Microsoft));
            var posts = 0;
            var gets = 0;
            server.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "GET")
                {
                    Interlocked.Increment(ref gets);
                    await context.SendStringAsync("healthy", "text/plain", Encoding.UTF8).ConfigureAwait(false);
                    return;
                }

                _ = await context.GetRequestBodyAsByteArrayAsync().ConfigureAwait(false);
                Interlocked.Increment(ref posts);
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8).ConfigureAwait(false);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("probe"));
                using var request = new HttpRequestMessage(HttpMethod.Post, prefix) { Content = content };
                using var response = await client.SendAsync(request, stop.Token).ConfigureAwait(false);
                Line(report, $"probe port={port} POST {await Describe(response).ConfigureAwait(false)} handlerPosts={posts} serverState={server.State}");
                using var health = await client.GetAsync(prefix, stop.Token).ConfigureAwait(false);
                Line(report, $"probe port={port} GET {await Describe(health).ConfigureAwait(false)} handlerGets={gets}");
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
            {
                Line(report, $"probe port={port} client error {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpListenerException or TimeoutException or ObjectDisposedException or InvalidOperationException)
                {
                    Line(report, $"probe port={port} server error {error.GetType().Name}: {error.Message}");
                }

                Line(report, $"probe port={port} elapsedMs={sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} finalState={server.State}");
            }
        }

        private static async Task<string> Describe(HttpResponseMessage response)
        {
            var headers = new StringBuilder();
            foreach (var header in response.Headers)
                headers.Append(header.Key).Append('=').Append(string.Join("|", header.Value)).Append("; ");
            foreach (var header in response.Content.Headers)
                headers.Append(header.Key).Append('=').Append(string.Join("|", header.Value)).Append("; ");
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (body.Length > 300) body = body[..300];
            body = body.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            return $"status={(int)response.StatusCode} reason=\"{response.ReasonPhrase}\" version={response.Version} headers=[{headers}] body=[{body}]";
        }

        private static async Task AppendErrorLogEntries(StringBuilder report, string label)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LogFiles", "HTTPERR");
            try
            {
                if (!Directory.Exists(folder))
                {
                    Line(report, $"{label}: folder missing {folder}");
                    return;
                }

                var matches = 0;
                foreach (var file in Directory.GetFiles(folder, "httperr*.log"))
                {
                    // HTTP.sys keeps the active log open for writing; share the read.
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (!MentionsPort(line)) continue;
                        matches++;
                        Line(report, $"{label} {Path.GetFileName(file)}: {line}");
                    }
                }

                Line(report, $"{label}: {matches} matching entries");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Line(report, $"{label}: unreadable ({error.GetType().Name}: {error.Message})");
            }
        }

        private static async Task AppendCommand(StringBuilder report, string label, string file, string arguments, Func<string, IEnumerable<string>> filter)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                Line(report, $"{label}: exit={process.ExitCode} outputChars={text.Length} stderr=[{(await errors.ConfigureAwait(false)).Trim()}]");
                foreach (var line in filter(text))
                    Line(report, $"{label}: {line}");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                Line(report, $"{label}: failed ({error.GetType().Name}: {error.Message})");
            }
        }

        // Keeps only the request-queue blocks that register one of the probed ports.
        private static IEnumerable<string> FilterRequestQueueBlocks(string text)
        {
            var block = new List<string>();
            var relevant = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.TrimStart().StartsWith("Request queue name:", StringComparison.Ordinal) && block.Count > 0)
                {
                    if (relevant) foreach (var kept in block) yield return kept;
                    block.Clear();
                    relevant = false;
                }

                block.Add(line);
                if (MentionsPort(line)) relevant = true;
            }

            if (relevant) foreach (var kept in block) yield return kept;
        }

        private static IEnumerable<string> FilterLinesMentioningPorts(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (MentionsPort(line)) yield return line;
            }
        }

        private static bool MentionsPort(string line)
        {
            foreach (var port in Ports)
            {
                var token = port.ToString(CultureInfo.InvariantCulture);
                if (line.Contains(":" + token + "/", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(":" + token + " ", StringComparison.Ordinal)
                    || line.Contains(" " + token + " ", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static int[] ReadPorts()
        {
            var configured = Environment.GetEnvironmentVariable("EMBEDIO_HTTPSYS_PROBE_PORTS");
            if (string.IsNullOrWhiteSpace(configured)) return DefaultPorts;
            var ports = new List<int>();
            foreach (var item in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
                    ports.Add(port);
            }

            return ports.Count > 0 ? ports.ToArray() : DefaultPorts;
        }

        private static void Line(StringBuilder report, string text) => report.Append(text).Append('\n');
    }
}
