using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class EngineLoad
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--engine-load", StringComparer.Ordinal)) return false;
        RunAsync(args).GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync(string[] args)
    {
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name);
        }
        var uri = new Uri(Option("--url", "http://127.0.0.1:8080/plaintext"));
        var seconds = int.Parse(Option("--seconds", "15"), CultureInfo.InvariantCulture);
        var concurrency = int.Parse(Option("--concurrency", "16"), CultureInfo.InvariantCulture);
        var pipeline = int.Parse(Option("--pipeline", "1"), CultureInfo.InvariantCulture);
        if (uri.Scheme != "http" || uri.AbsolutePath is not ("/plaintext" or "/json")
            || seconds < 1 || concurrency < 1 || pipeline is not (1 or 16))
            throw new ArgumentException("HTTP benchmark endpoints, positive duration/concurrency, pipeline 1 or 16 required.");
        var payload = Encoding.UTF8.GetBytes(uri.AbsolutePath == "/json" ? "{\"message\":\"Hello, World!\"}" : "Hello, World!");
        var ordinary = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: keep-alive\r\n\r\n";
        var batch = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(ordinary, pipeline)));
        var finalBatch = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(ordinary, pipeline - 1))
            + ordinary.Replace("keep-alive", "close", StringComparison.Ordinal));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 45));
        var token = timeout.Token;
        await Exercise(5, false);
        BenchmarkControl.Write("READY");
        if (await Console.In.ReadLineAsync(token) != "start") throw new InvalidOperationException("Missing measurement handshake.");
        var result = await Exercise(seconds, true);
        BenchmarkControl.Write("DONE");
        if (await Console.In.ReadLineAsync(token) != "report") throw new InvalidOperationException("Missing report handshake.");
        var latencies = result.Samples.Order().ToArray();
        double Percentile(double p) => latencies.Length == 0 ? 0 : latencies[(int)Math.Min(latencies.Length - 1, Math.Ceiling(latencies.Length * p) - 1)];
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            uri = uri.ToString(),
            concurrency,
            pipeline,
            requests = result.Requests,
            errors = 0,
            elapsedSeconds = result.Seconds,
            requestsPerSecond = result.Requests / result.Seconds,
            p50Milliseconds = Percentile(.5),
            p95Milliseconds = Percentile(.95),
            p99Milliseconds = Percentile(.99),
            latencySamples = latencies.Length,
            note = "Separate client process; every response body validated. Latency samples every 67 responses, from batch send to each selected response. 80 requests per connection; final request closes explicitly. Loopback and shared host CPU are not an official ranking."
        }));

        async Task<(long Requests, double Seconds, List<double> Samples)> Exercise(int duration, bool measure)
        {
            var clock = Stopwatch.StartNew();
            var workers = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                long count = 0;
                var samples = new List<double>();
                while (clock.Elapsed.TotalSeconds < duration)
                {
                    using var client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(uri.Host, uri.Port, token);
                    using var stream = client.GetStream();
                    var reader = new ResponseReader(stream, payload);
                    for (var sent = 0; sent < 80; sent += pipeline)
                    {
                        var timestamp = Stopwatch.GetTimestamp();
                        await stream.WriteAsync(sent + pipeline == 80 ? finalBatch : batch, token);
                        for (var index = 0; index < pipeline; index++)
                        {
                            await reader.Read(token);
                            count++;
                            if (measure && count % 67 == 0) samples.Add(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
                        }
                    }
                    await reader.VerifyEof(token);
                }
                return (count, samples);
            }));
            clock.Stop();
            return (workers.Sum(row => row.count), clock.Elapsed.TotalSeconds, workers.SelectMany(row => row.samples).ToList());
        }
    }

    private sealed class ResponseReader(NetworkStream source, byte[] expected)
    {
        private readonly byte[] _buffer = new byte[32768];
        private int _start;
        private int _end;

        internal async Task Read(CancellationToken token)
        {
            int boundary;
            while ((boundary = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0)
                await Fill(token);
            var header = Encoding.ASCII.GetString(_buffer, _start, boundary);
            if (!header.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)) throw new InvalidDataException(header);
            var length = -1;
            foreach (var field in header.Split("\r\n", StringSplitOptions.None))
            {
                if (field.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(field.AsSpan(15).Trim(), CultureInfo.InvariantCulture);
                if (field.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                    || field.StartsWith("Content-Encoding:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected response encoding.");
            }
            if (length != expected.Length) throw new InvalidDataException("Unexpected response length.");
            _start += boundary + 4;
            while (_end - _start < length) await Fill(token);
            if (!_buffer.AsSpan(_start, length).SequenceEqual(expected)) throw new InvalidDataException("Response body mismatch.");
            _start += length;
        }

        internal async Task VerifyEof(CancellationToken token)
        {
            if (_start != _end || await source.ReadAsync(_buffer.AsMemory(0, 1), token) != 0)
                throw new InvalidDataException("Unexpected data after final response.");
        }

        private async Task Fill(CancellationToken token)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) throw new InvalidDataException("Oversized response header.");
            var read = await source.ReadAsync(_buffer.AsMemory(_end), token);
            if (read == 0) throw new EndOfStreamException("Incomplete benchmark response.");
            _end += read;
        }
    }
}
