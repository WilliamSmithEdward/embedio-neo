using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Stateful HTTP/1.1 campaign. Each iteration builds a script of pipelined operations
// on one connection: valid requests with random framing, at most one invalid request,
// optional abort, and optional Connection: close. An independent model predicts every
// response. Delivery is randomly fragmented. Seeds replay exactly.
internal static class Http1Fuzz
{
    private enum Kind { Valid, Invalid, Abort }

    private sealed record Op(Kind Kind, string Label, byte[] Bytes, string Method, string Path, byte[] Body, bool Close, int? RangeStart, int? RangeEnd);

    private sealed record Stats(long ManagedBytes, int Handles, int Threads, long ActiveHandlers, long CompletedHandlers);

    private static string _authority = "a";

    private static byte[] Wire(string text) => Encoding.Latin1.GetBytes(RawHttp1.Bind(text, _authority));

    private static readonly string[] Invalid =
    {
        "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n",
        "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\n",
        "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 1, 2\r\n\r\nab",
        "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: -1\r\n\r\n",
        "GET /plain HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n",
        "GET /plain HTTP/1.1\r\n\r\n",
        "GET /plain HTTP/1.1\r\nHost: a\r\nBad Name: x\r\n\r\n",
        "GET /plain HTTP/1.1\r\nHost : a\r\n\r\n",
        "GET /plain HTTP/1.1\r\nHost: a\r\nX: a\rb\r\n\r\n",
        "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: identity\r\n\r\n",
        "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nabc\r\n0\r\n\r\n",
        "GET / HTTP/9.9\r\nHost: a\r\n\r\n",
        "GET  /plain HTTP/1.1\r\nHost: a\r\n\r\n",
        "GET /plain HTTP/1.1 \r\nHost: a\r\n\r\n",
    };

    internal static string Run(Http1Conformance.Target target, int seed, int iterations, string? outDir)
    {
        _authority = target.Host + ":" + target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var random = new Random(seed);
        var file = ConformanceServer.FileBytes();
        var baseline = ReadStats(target);
        var checkpoints = new List<Stats> { baseline };
        var requests = 0;
        var invalid = 0;
        var aborts = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var script = Build(random);
            var chunks = new List<int>();
            var bytes = script.SelectMany(o => o.Bytes).ToArray();
            var fragmented = random.Next(3) == 0;
            if (fragmented)
                for (var sent = 0; sent < bytes.Length;) { var size = random.Next(1, 600); chunks.Add(size); sent += size; }
            var delay = fragmented && random.Next(8) == 0 ? 1 : 0;
            try
            {
                Execute(target, script, bytes, chunks, delay, file);
            }
            catch (Exception error) when (Http1Conformance.IsDriverFailure(error))
            {
                var reproducer = new
                {
                    seed,
                    iteration,
                    error = error.ToString(),
                    script = script.Select(o => new { o.Kind, o.Label, wire = Convert.ToBase64String(o.Bytes) }),
                    chunks,
                    delay,
                };
                if (outDir != null) File.WriteAllText(Path.Combine(outDir, $"h1-fuzz-failure-{seed}-{iteration}.json"), JsonSerializer.Serialize(reproducer, new JsonSerializerOptions { WriteIndented = true }));
                return $"FAIL seed={seed} iteration={iteration}: {error.GetType().Name}: {error.Message}";
            }
            requests += script.Count(o => o.Kind == Kind.Valid);
            invalid += script.Count(o => o.Kind == Kind.Invalid);
            aborts += script.Count(o => o.Kind == Kind.Abort);
            if ((iteration + 1) % Math.Max(1, iterations / 5) == 0)
            {
                var health = Http1Conformance.Healthy(target);
                if (health != null) return $"FAIL seed={seed} iteration={iteration}: unhealthy: {health}";
                checkpoints.Add(ReadStats(target));
            }
        }
        // Handlers must drain once all clients are gone.
        var settled = ReadStats(target);
        for (var wait = 0; wait < 50 && settled.ActiveHandlers > 0; wait++) { Thread.Sleep(100); settled = ReadStats(target); }
        checkpoints.Add(settled);
        var summary = new
        {
            seed,
            iterations,
            requests,
            invalid,
            aborts,
            checkpoints,
            handleGrowth = settled.Handles - baseline.Handles,
            managedGrowth = settled.ManagedBytes - baseline.ManagedBytes,
        };
        var json = JsonSerializer.Serialize(summary);
        if (outDir != null) File.WriteAllText(Path.Combine(outDir, $"h1-fuzz-{seed}.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        if (settled.ActiveHandlers > 0) return "FAIL handlers still active after campaign: " + json;
        // Bounds are generous; they exist to detect unbounded growth, not to measure footprint.
        if (summary.handleGrowth > 64 || summary.managedGrowth > 32L << 20) return "FAIL resource growth: " + json;
        return "PASS " + json;
    }

    private static Stats ReadStats(Http1Conformance.Target target)
    {
        using var client = target.Connect();
        client.Send("GET /__stats HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n");
        var response = client.Read(false);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        return new Stats(root.GetProperty("managedBytes").GetInt64(), root.GetProperty("handles").GetInt32(), root.GetProperty("threads").GetInt32(),
            root.GetProperty("activeHandlers").GetInt64(), root.GetProperty("completedHandlers").GetInt64());
    }

    private static List<Op> Build(Random random)
    {
        var script = new List<Op>();
        var count = random.Next(1, 7);
        for (var i = 0; i < count; i++)
        {
            var roll = random.Next(100);
            if (roll < 8)
            {
                var text = Invalid[random.Next(Invalid.Length)];
                script.Add(new Op(Kind.Invalid, text.Split('\r')[0], Wire(text), "", "", Array.Empty<byte>(), false, null, null));
                return script;
            }
            if (roll < 12)
            {
                // Partial request then reset; the server must release its resources.
                var body = RandomBody(random, 2000);
                var head = Wire($"POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: {body.Length + 100}\r\n\r\n");
                var partial = head.Concat(body).ToArray();
                partial = partial.AsSpan(0, random.Next(1, partial.Length + 1)).ToArray();
                script.Add(new Op(Kind.Abort, "abort", partial, "", "", Array.Empty<byte>(), false, null, null));
                return script;
            }
            var close = i == count - 1 && random.Next(4) == 0;
            script.Add(Valid(random, close));
            if (close) return script;
        }
        return script;
    }

    private static byte[] RandomBody(Random random, int max)
    {
        var body = new byte[random.Next(4) == 0 ? 0 : random.Next(1, max)];
        random.NextBytes(body);
        return body;
    }

    private static Op Valid(Random random, bool close)
    {
        var builder = new StringBuilder();
        var connection = close ? "Connection: close\r\n" : random.Next(6) == 0 ? "Connection: keep-alive\r\n" : string.Empty;
        switch (random.Next(6))
        {
            case 0:
                builder.Append("GET /plain HTTP/1.1\r\nHost: a\r\n").Append(connection).Append("\r\n");
                return new Op(Kind.Valid, "GET /plain", Wire(builder.ToString()), "GET", "/plain", Array.Empty<byte>(), close, null, null);
            case 1:
                builder.Append("HEAD /plain HTTP/1.1\r\nHost: a\r\n").Append(connection).Append("\r\n");
                return new Op(Kind.Valid, "HEAD /plain", Wire(builder.ToString()), "HEAD", "/plain", Array.Empty<byte>(), close, null, null);
            case 2:
                {
                    var start = random.Next(ConformanceServer.FileLength);
                    var end = Math.Min(ConformanceServer.FileLength - 1, start + random.Next(0, 5000));
                    builder.Append("GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=").Append(start).Append('-').Append(end).Append("\r\n").Append(connection).Append("\r\n");
                    return new Op(Kind.Valid, $"GET range {start}-{end}", Wire(builder.ToString()), "GET", "/files/data.bin", Array.Empty<byte>(), close, start, end);
                }
            default:
                {
                    var method = new[] { "POST", "PUT", "QUERY", "PATCH" }[random.Next(4)];
                    var body = RandomBody(random, random.Next(10) == 0 ? 200_000 : 3000);
                    builder.Append(method).Append(" /").Append(method == "QUERY" ? "query" : "echo").Append(" HTTP/1.1\r\nHost: a\r\nContent-Type: application/octet-stream\r\n").Append(connection);
                    var head = new List<byte>();
                    if (random.Next(2) == 0)
                    {
                        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
                        head.AddRange(Wire(builder.ToString()));
                        head.AddRange(body);
                    }
                    else
                    {
                        builder.Append("Transfer-Encoding: chunked\r\n\r\n");
                        head.AddRange(Wire(builder.ToString()));
                        for (var offset = 0; offset < body.Length;)
                        {
                            var size = Math.Min(body.Length - offset, random.Next(1, 9000));
                            var extension = random.Next(5) == 0 ? ";e=1" : string.Empty;
                            head.AddRange(Encoding.Latin1.GetBytes((random.Next(2) == 0 ? size.ToString("x") : size.ToString("X")) + extension + "\r\n"));
                            head.AddRange(body.AsSpan(offset, size).ToArray());
                            head.AddRange("\r\n"u8.ToArray());
                            offset += size;
                        }
                        head.AddRange(Encoding.Latin1.GetBytes("0\r\n" + (random.Next(4) == 0 ? "X-T: v\r\n" : string.Empty) + "\r\n"));
                    }
                    return new Op(Kind.Valid, $"{method} body={body.Length}", head.ToArray(), method, method == "QUERY" ? "/query" : "/echo", body, close, null, null);
                }
        }
    }

    private static void Execute(Http1Conformance.Target target, List<Op> script, byte[] bytes, List<int> chunks, int delay, byte[] file)
    {
        using var client = target.Connect();
        // Write on another thread: large pipelined bodies can fill both socket buffers.
        var writer = Task.Run(() =>
        {
            try
            {
                if (chunks.Count > 0) client.SendFragmented(bytes, chunks, delay); else client.Send(bytes);
            }
            catch (IOException) when (script.Any(o => o.Kind != Kind.Valid)) { }
            catch (ObjectDisposedException) when (script[^1].Kind == Kind.Abort) { }
        });
        foreach (var op in script)
        {
            if (op.Kind == Kind.Abort) { writer.GetAwaiter().GetResult(); client.Abort(); return; }
            if (op.Kind == Kind.Invalid)
            {
                try
                {
                    var rejected = client.Read(false);
                    if (rejected.Status is < 400 or >= 500 and not (501 or 505))
                        throw new InvalidDataException($"Invalid request '{op.Label}' answered {rejected.Status}.");
                }
                catch (Exception error) when (error is IOException) { }
                if (!client.WaitClosed(out var extra)) throw new InvalidDataException($"Connection stayed usable after '{op.Label}' ({extra} extra bytes).");
                writer.Wait(TimeSpan.FromSeconds(5));
                return;
            }
            var response = client.Read(op.Method == "HEAD");
            Check(op, response, file);
            if (op.Close)
            {
                if (!client.WaitClosed(out var extra)) throw new InvalidDataException($"Connection: close not honored ({extra} extra bytes).");
            }
        }
        if (!writer.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Request writer did not finish.");
    }

    private static void Check(Op op, Http1Response response, byte[] file)
    {
        if (op.RangeStart is { } start && op.RangeEnd is { } end)
        {
            if (response.Status != 206 || !response.Body.AsSpan().SequenceEqual(file.AsSpan(start, end - start + 1)))
                throw new InvalidDataException($"{op.Label}: {response.Status}, {response.Body.Length} bytes, Content-Range {response.Header("Content-Range")}.");
            return;
        }
        if (response.Status != 200) throw new InvalidDataException($"{op.Label}: status {response.Status}.");
        if (op.Path == "/plain")
        {
            var expected = op.Method == "HEAD" ? string.Empty : "hello";
            if (response.BodyText != expected) throw new InvalidDataException($"{op.Label}: body '{response.BodyText}'.");
            return;
        }
        var sha = Convert.ToHexString(SHA256.HashData(op.Body));
        if (response.Header("X-Method") != op.Method || response.Header("X-Body-Sha256") != sha || !response.Body.AsSpan().SequenceEqual(op.Body))
            throw new InvalidDataException($"{op.Label}: method {response.Header("X-Method")}, length {response.Header("X-Body-Length")}, received {response.Body.Length}.");
    }

    internal static bool IsReset(Exception error) => error is IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset } };
}
