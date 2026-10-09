using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.WebSockets;

// End-to-end managed WebSocket echo benchmark for issue #190. The host and the
// load client run as separate processes; the client is an independent RFC 6455
// implementation so the measured server never shares code with its peer. Only
// public EmbedIO APIs are used, so one runner build can be paired with either a
// baseline or candidate EmbedIO.dll.
internal static class WebSocketEcho
{
    internal static bool Run(string[] args)
    {
        if (args.Contains("--websocket-host", StringComparer.Ordinal)) { HostAsync(args).GetAwaiter().GetResult(); return true; }
        if (args.Contains("--websocket-load", StringComparer.Ordinal)) { LoadAsync(args).GetAwaiter().GetResult(); return true; }
        return false;
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name + " requires a value.");
    }

    private sealed class Echo : WebSocketModule
    {
        internal Echo() : base("/ws", false) { }
        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            => context.WebSocket.SendAsync(buffer, result.MessageType == 0, context.CancellationToken);
    }

    private static async Task HostAsync(string[] args)
    {
        var url = Option(args, "--url", "http://127.0.0.1:8080/");
        EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
        using var stop = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(new Echo());
        var running = server.RunAsync(stop.Token);
        BenchmarkControl.Write("HOST " + url);
        if (await Console.In.ReadLineAsync(stop.Token) != "start") throw new InvalidOperationException("Expected start.");
        // Connections are open and warm. Collect so the window starts clean.
        var liveBefore = LiveBytes();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var bytes = GC.GetTotalAllocatedBytes(true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var clock = Stopwatch.StartNew();
        BenchmarkControl.Write("MEASURING");
        if (await Console.In.ReadLineAsync(stop.Token) != "stop") throw new InvalidOperationException("Expected stop.");
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - bytes;
        var cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
        var gcs = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        // Retained memory with every connection still open and idle.
        var liveOpen = LiveBytes();
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            cpuSeconds,
            allocatedBytes = allocated,
            collections = gcs,
            liveBeforeBytes = liveBefore,
            liveOpenIdleBytes = liveOpen,
            workingSetBytes = process.WorkingSet64,
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WebServer).Assembly.Location))),
        }));
        if (await Console.In.ReadLineAsync(stop.Token) != "closed") throw new InvalidOperationException("Expected closed.");
        // Retained after every client completed its close handshake.
        await Task.Delay(250).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { liveAfterCloseBytes = LiveBytes() }));
        stop.Cancel();
        await running.ConfigureAwait(false);
    }

    // Bytes surviving a forced, blocking, compacting full collection. GC.GetTotalMemory
    // returned negative values in this host on .NET 10, so it is not used.
    private static long LiveBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
    }

    private sealed class Connection : IDisposable
    {
        private static readonly byte[] Key = { 0x5a, 0x1c, 0xe3, 0x07 };
        private readonly TcpClient _tcp = new() { NoDelay = true };
        private readonly byte[] _input = new byte[1 << 16];
        private int _start, _end;
        private NetworkStream _stream = null!;

        internal static async Task<Connection> OpenAsync(Uri uri, CancellationToken token)
        {
            var connection = new Connection();
            await connection._tcp.ConnectAsync(IPAddress.Loopback, uri.Port, token).ConfigureAwait(false);
            connection._stream = connection._tcp.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET /ws HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
            await connection._stream.WriteAsync(request, token).ConfigureAwait(false);
            var head = new StringBuilder();
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await connection.FillAsync(1, token).ConfigureAwait(false);
                head.Append((char)connection._input[connection._start++]);
                if (head.Length > 8192) throw new InvalidDataException("Oversized handshake.");
            }
            if (!head.ToString().StartsWith("HTTP/1.1 101", StringComparison.Ordinal)) throw new InvalidDataException("Upgrade refused: " + head);
            return connection;
        }

        // Build the masked frames of one message. The payload is copied per call
        // because the caller stamps a sequence number into it.
        internal static int Encode(byte[] payload, int fragments, bool text, byte[] output)
        {
            var offset = 0;
            for (var index = 0; index < fragments; ++index)
            {
                var from = payload.Length * index / fragments;
                var length = payload.Length * (index + 1) / fragments - from;
                output[offset++] = (byte)((index == 0 ? text ? 1 : 2 : 0) | (index == fragments - 1 ? 0x80 : 0));
                if (length < 126) output[offset++] = (byte)(0x80 | length);
                else if (length <= ushort.MaxValue) { output[offset++] = 0xfe; BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset), (ushort)length); offset += 2; }
                else { output[offset++] = 0xff; BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(offset), (ulong)length); offset += 8; }
                Key.CopyTo(output, offset); offset += 4;
                for (var i = 0; i < length; ++i) output[offset + i] = (byte)(payload[from + i] ^ Key[i & 3]);
                offset += length;
            }
            return offset;
        }

        internal ValueTask SendAsync(byte[] wire, int count, CancellationToken token) => _stream.WriteAsync(wire.AsMemory(0, count), token);

        private async ValueTask FillAsync(int needed, CancellationToken token)
        {
            if (_end - _start >= needed) return;
            if (_start > 0) { Buffer.BlockCopy(_input, _start, _input, 0, _end - _start); _end -= _start; _start = 0; }
            while (_end < needed)
            {
                var read = await _stream.ReadAsync(_input.AsMemory(_end), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Server closed the connection.");
                _end += read;
            }
        }

        // Read one complete data message into output and return its length and opcode.
        internal async Task<(int Length, int Opcode)> ReceiveAsync(byte[] output, CancellationToken token)
        {
            var total = 0; var opcode = -1;
            while (true)
            {
                await FillAsync(2, token).ConfigureAwait(false);
                var flags = _input[_start]; long length = _input[_start + 1] & 0x7f;
                if ((_input[_start + 1] & 0x80) != 0) throw new InvalidDataException("Masked server frame.");
                var header = 2;
                if (length == 126) { await FillAsync(4, token).ConfigureAwait(false); length = BinaryPrimitives.ReadUInt16BigEndian(_input.AsSpan(_start + 2)); header = 4; }
                else if (length == 127) { await FillAsync(10, token).ConfigureAwait(false); length = (long)BinaryPrimitives.ReadUInt64BigEndian(_input.AsSpan(_start + 2)); header = 10; }
                _start += header;
                var op = flags & 0x0f;
                if (op >= 8) throw new InvalidDataException($"Unexpected control frame {op}.");
                if (opcode < 0) opcode = op;
                if (total + length > output.Length) throw new InvalidDataException("Echo longer than the request.");
                var remaining = (int)length;
                while (remaining > 0)
                {
                    if (_end == _start) { _start = _end = 0; await FillAsync(1, token).ConfigureAwait(false); }
                    var take = Math.Min(remaining, _end - _start);
                    Buffer.BlockCopy(_input, _start, output, total, take);
                    _start += take; total += take; remaining -= take;
                }
                if ((flags & 0x80) != 0) return (total, opcode);
            }
        }

        internal async Task CloseAsync(CancellationToken token)
        {
            var close = new byte[8]; close[0] = 0x88; close[1] = 0x82; Key.CopyTo(close, 2);
            close[6] = (byte)(0x03 ^ Key[0]); close[7] = (byte)(0xe8 ^ Key[1]);
            await _stream.WriteAsync(close, token).ConfigureAwait(false);
            await FillAsync(2, token).ConfigureAwait(false);
            if ((_input[_start] & 0x0f) != 8) throw new InvalidDataException("Expected a close acknowledgement.");
            var frame = 2 + (_input[_start + 1] & 0x7f);
            await FillAsync(frame, token).ConfigureAwait(false);
            _start += frame;
        }

        public void Dispose() { _stream?.Dispose(); _tcp.Dispose(); }
    }

    private static async Task LoadAsync(string[] args)
    {
        var uri = new Uri(new Uri(Option(args, "--url", "http://127.0.0.1:8080/")), "ws");
        var size = int.Parse(Option(args, "--size", "32"), System.Globalization.CultureInfo.InvariantCulture);
        var fragments = int.Parse(Option(args, "--fragments", "1"), System.Globalization.CultureInfo.InvariantCulture);
        var connections = int.Parse(Option(args, "--connections", "1"), System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(Option(args, "--seconds", "10"), System.Globalization.CultureInfo.InvariantCulture);
        var text = args.Contains("--text", StringComparer.Ordinal);
        if (size < 8 || fragments < 1 || fragments > size || connections < 1) throw new ArgumentException("Invalid workload.");
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));
        var sockets = new Connection[connections];
        for (var i = 0; i < connections; ++i) sockets[i] = await Connection.OpenAsync(uri, abort.Token).ConfigureAwait(false);
        var measuring = 0;
        var stopAt = long.MaxValue;
        var latencies = new List<long>[connections];
        var counts = new long[connections];
        async Task Drive(int index, long deadline)
        {
            var socket = sockets[index];
            var payload = new byte[size];
            for (var i = 0; i < size; ++i) payload[i] = (byte)('a' + (i + index) % 26);
            var wire = new byte[size + fragments * 14];
            var echo = new byte[size];
            var recorded = latencies[index] ??= new List<long>(1 << 16);
            for (long sequence = 0; Stopwatch.GetTimestamp() < Math.Min(deadline, Volatile.Read(ref stopAt)); ++sequence)
            {
                // Stamp eight ASCII digits so a stale or reordered echo cannot pass.
                var stamp = sequence;
                for (var d = 7; d >= 0; --d) { payload[d] = (byte)('0' + stamp % 10); stamp /= 10; }
                var count = Connection.Encode(payload, fragments, text, wire);
                var started = Stopwatch.GetTimestamp();
                await socket.SendAsync(wire, count, abort.Token).ConfigureAwait(false);
                var (length, opcode) = await socket.ReceiveAsync(echo, abort.Token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetTimestamp() - started;
                if (length != size || opcode != (text ? 1 : 2) || !echo.AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException($"Echo mismatch on connection {index} message {sequence}.");
                if (Volatile.Read(ref measuring) == 1) { recorded.Add(elapsed); counts[index]++; }
            }
        }
        // Warm up JIT, pools and TCP windows on every connection before measuring.
        var warm = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        await Task.WhenAll(Enumerable.Range(0, connections).Select(i => Task.Run(() => Drive(i, warm)))).ConfigureAwait(false);
        foreach (var list in latencies) list.Clear();
        BenchmarkControl.Write("READY");
        if (Console.ReadLine() != "start") throw new InvalidOperationException("Expected start.");
        Volatile.Write(ref measuring, 1);
        var begin = Stopwatch.GetTimestamp();
        var end = begin + (long)(Stopwatch.Frequency * seconds);
        await Task.WhenAll(Enumerable.Range(0, connections).Select(i => Task.Run(() => Drive(i, end)))).ConfigureAwait(false);
        var elapsedSeconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        BenchmarkControl.Write("DONE");
        if (Console.ReadLine() != "report") throw new InvalidOperationException("Expected report.");
        var all = latencies.SelectMany(l => l).Select(t => t * 1_000_000.0 / Stopwatch.Frequency).ToArray();
        Array.Sort(all);
        double Percentile(double p) => all.Length == 0 ? 0 : all[(int)Math.Min(all.Length - 1, Math.Ceiling(all.Length * p) - 1)];
        var messages = counts.Sum();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            size,
            fragments,
            connections,
            text,
            seconds = elapsedSeconds,
            messages,
            messagesPerSecond = messages / elapsedSeconds,
            payloadMegabytesPerSecond = messages * (double)size * 2 / elapsedSeconds / (1024 * 1024),
            p50Microseconds = Percentile(.5),
            p90Microseconds = Percentile(.9),
            p99Microseconds = Percentile(.99),
            p999Microseconds = Percentile(.999),
            maxMicroseconds = all.Length == 0 ? 0 : all[^1],
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            note = "Closed-loop echo; latency spans client send through complete verified echo. Payload throughput counts both directions.",
        }));
        foreach (var socket in sockets) { await socket.CloseAsync(abort.Token).ConfigureAwait(false); socket.Dispose(); }
        BenchmarkControl.Write("CLOSED");
    }
}
