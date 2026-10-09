using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var quicResult = await QuicRebindProbe.Run(args);
if (quicResult.HasValue) return quicResult.Value;

var iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1024;
var parallelism = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 16;
var output = args.Length > 2 ? args[2] : "TestResults/runtime-close-probe/result.json";
if (iterations is < 1 or > 4096 || parallelism is < 1 or > 32)
    throw new ArgumentOutOfRangeException(nameof(args), "Use 1-4096 connections and 1-32 concurrent independent clients.");
if (Environment.Version.ToString() != "10.0.12")
    throw new InvalidOperationException("This reproduction requires the exact .NET 10.0.12 runtime.");

var reports = new List<object>();
var failures = new ConcurrentBag<object>();
foreach (var nearDeadline in new[] { false, true })
    reports.Add(await RunScenario(nearDeadline, iterations, parallelism, failures));
var result = new
{
    passed = failures.IsEmpty,
    runtime = Environment.Version.ToString(),
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    processorCount = Environment.ProcessorCount,
    iterations,
    parallelism,
    scenarios = reports,
    failures = failures.ToArray(),
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? throw new InvalidOperationException("Missing output directory."));
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine($"Runtime {result.runtime}, {result.architecture}: {iterations * 2} connections, {failures.Count} failures. Report: {output}");
return failures.IsEmpty ? 0 : 1;

static async Task<object> RunScenario(bool nearDeadline, int iterations, int parallelism, ConcurrentBag<object> failures)
{
    var scenario = nearDeadline ? "eof-near-one-second" : "immediate-eof";
    var elapsed = Stopwatch.StartNew();
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var traces = new ConcurrentBag<object>();
    var peers = new ConcurrentBag<Task>();
    var completed = 0;
    var accepting = Task.Run(async () =>
    {
        try
        {
            for (var index = 0; index < iterations; index++)
            {
                var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                peers.Add(Serve(tcp, nearDeadline, stop.Token, scenario, traces, failures));
            }
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            failures.Add(new { scenario, side = "listener", stage = "accept", error = error.ToString() });
        }
    });
    using var gate = new SemaphoreSlim(parallelism);
    await Task.WhenAll(Enumerable.Range(0, iterations).Select(async index =>
    {
        ClientWebSocket? client = null;
        var entered = false;
        var stage = "queue";
        var clock = Stopwatch.StartNew();
        double? acknowledgeMilliseconds = null;
        try
        {
            await gate.WaitAsync(stop.Token);
            entered = true;
            client = new ClientWebSocket();
            client.Options.Proxy = null;
            client.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            stage = "connect";
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/probe/{index}"), stop.Token);
            stage = "receive-close";
            var close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), stop.Token);
            if (close.MessageType != WebSocketMessageType.Close || close.CloseStatus != WebSocketCloseStatus.PolicyViolation || close.CloseStatusDescription != "probe")
                throw new InvalidDataException("Expected the peer's exact close payload.");
            // Every public call is awaited before the next. Dispose/Abort never
            // overlaps an application send, receive or close operation.
            stage = "acknowledge-close";
            var acknowledge = Stopwatch.StartNew();
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", stop.Token);
            acknowledgeMilliseconds = acknowledge.Elapsed.TotalMilliseconds;
            if (client.State != WebSocketState.Closed)
                throw new InvalidDataException($"Expected Closed after acknowledgement; received {client.State}.");
            Interlocked.Increment(ref completed);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            failures.Add(new { scenario, index, side = "client", stage, error = error.ToString() });
        }
        finally
        {
            try { client?.Dispose(); }
            catch (Exception error) when (IsRecoverable(error))
            {
                failures.Add(new { scenario, index, side = "client", stage = "dispose", error = error.ToString() });
            }
            traces.Add(new { index, side = "client", stage, acknowledgeMilliseconds, milliseconds = clock.Elapsed.TotalMilliseconds });
            if (entered) gate.Release();
        }
    }));
    await accepting;
    await Task.WhenAll(peers);
    Console.WriteLine($"{scenario}: completed {completed}/{iterations} in {elapsed.Elapsed.TotalSeconds:F3}s");
    return new { scenario, completed, seconds = elapsed.Elapsed.TotalSeconds, traces = traces.ToArray() };
}

static async Task Serve(TcpClient tcp, bool nearDeadline, CancellationToken token, string scenario, ConcurrentBag<object> traces, ConcurrentBag<object> failures)
{
    var stage = "handshake";
    int? index = null;
    double? waitedMilliseconds = null;
    var delayMilliseconds = 0;
    try
    {
        using (tcp)
        {
            var stream = tcp.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                await stream.ReadExactlyAsync(one, token);
                header.Add(one[0]);
                if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var path = lines[0].Split(' ')[1];
            index = int.Parse(path[(path.LastIndexOf("/", StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture);
            var keyLine = lines.Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var key = keyLine[(keyLine.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            // SHA-1 is the RFC 6455 upgrade checksum, not authentication.
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), token);
            stage = "send-close";
            await stream.WriteAsync(new byte[] { 0x88, 7, 3, 240, (byte)'p', (byte)'r', (byte)'o', (byte)'b', (byte)'e' }, token);
            stage = "receive-acknowledgement";
            var prefix = new byte[2];
            await stream.ReadExactlyAsync(prefix, token);
            if (prefix[0] != 0x88 || prefix[1] != 0x85) throw new InvalidDataException("Expected a masked five-byte close acknowledgement.");
            var maskAndPayload = new byte[9];
            await stream.ReadExactlyAsync(maskAndPayload, token);
            var expected = new byte[] { 3, 232, (byte)'a', (byte)'c', (byte)'k' };
            for (var i = 0; i < expected.Length; i++)
                if ((maskAndPayload[i + 4] ^ maskAndPayload[i % 4]) != expected[i]) throw new InvalidDataException("Unexpected close acknowledgement payload.");
            stage = "wait-before-eof";
            delayMilliseconds = nearDeadline ? 980 + index.Value % 41 : 0;
            var wait = Stopwatch.StartNew();
            await Task.Delay(delayMilliseconds, token);
            waitedMilliseconds = wait.Elapsed.TotalMilliseconds;
            // The peer uses only TCP; no EmbedIO or server WebSocket code runs.
            stage = "tcp-eof";
        }
    }
    catch (Exception error) when (IsRecoverable(error))
    {
        failures.Add(new { scenario, index, side = "peer", stage, error = error.ToString() });
    }
    finally
    {
        traces.Add(new { index, side = "peer", stage, delayMilliseconds, waitedMilliseconds });
    }
}

static bool IsRecoverable(Exception error)
    => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
