using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;

internal static class WebSocketRead
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--websocket-read", StringComparer.Ordinal)) return false;
        var assembly = typeof(WebServer).Assembly;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var socketType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true) ?? throw new InvalidOperationException("Missing socket type.");
        var readerType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrameStream", true) ?? throw new InvalidOperationException("Missing frame reader.");
        var frameType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true) ?? throw new InvalidOperationException("Missing frame type.");
        var payloadType = assembly.GetType("EmbedIO.WebSockets.Internal.PayloadData", true) ?? throw new InvalidOperationException("Missing payload type.");
        var continuation = socketType.GetProperty("InContinuation", flags) ?? throw new InvalidOperationException("Missing continuation state.");
        var read = readerType.GetMethod("ReadFrameAsync", flags) ?? throw new InvalidOperationException("Missing read method.");
        var payload = frameType.GetProperty("PayloadData", flags) ?? throw new InvalidOperationException("Missing payload property.");
        var bytesMethod = payloadType.GetMethod("ToArray", flags) ?? throw new InvalidOperationException("Missing payload bytes.");
        var rows = new List<object>();
        foreach (var size in new[] { 16, 1024, 65536, 65538 })
            foreach (var fragments in new[] { 1, 16 })
                foreach (var text in new[] { false, true })
                {
                    var expected = Encoding.UTF8.GetBytes(new string('é', size / 2));
                    using var wire = Wire(expected, fragments, text);
                    var socket = RuntimeHelpers.GetUninitializedObject(socketType);
                    GC.SuppressFinalize(socket); // Only the pure parser's continuation property is used.
                    var reader = Activator.CreateInstance(readerType, wire, false) ?? throw new InvalidOperationException("Missing reader constructor.");
                    var arguments = new[] { socket };
                    void Parse()
                    {
                        wire.Position = 0;
                        var offset = 0;
                        for (var index = 0; index < fragments; ++index)
                        {
                            continuation.SetValue(socket, index != 0);
                            var task = (Task)(read.Invoke(reader, arguments) ?? throw new InvalidOperationException("Missing read task."));
                            task.GetAwaiter().GetResult();
                            var frame = task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new InvalidOperationException("Missing frame.");
                            var data = payload.GetValue(frame) ?? throw new InvalidOperationException("Missing payload.");
                            var bytes = (byte[])(bytesMethod.Invoke(data, null) ?? throw new InvalidOperationException("Missing payload bytes."));
                            if (!bytes.AsSpan().SequenceEqual(expected.AsSpan(offset, bytes.Length))) throw new InvalidDataException("Frame content changed.");
                            offset += bytes.Length;
                        }
                        if (offset != expected.Length || wire.Position != wire.Length) throw new InvalidDataException("Incomplete message consumption.");
                    }
                    const int iterations = 5000;
                    for (var index = 0; index < 1000; ++index) Parse();
                    var samples = new List<object>();
                    for (var round = 0; round < 5; ++round)
                    {
                        var gen0 = GC.CollectionCount(0);
                        var gen1 = GC.CollectionCount(1);
                        var gen2 = GC.CollectionCount(2);
                        var startBytes = GC.GetAllocatedBytesForCurrentThread();
                        var start = Stopwatch.GetTimestamp();
                        for (var index = 0; index < iterations; ++index) Parse();
                        var elapsed = Stopwatch.GetElapsedTime(start);
                        var allocated = GC.GetAllocatedBytesForCurrentThread() - startBytes;
                        samples.Add(new { round, nanosecondsPerMessage = elapsed.TotalNanoseconds / iterations, bytesPerMessage = allocated / (double)iterations, gen0 = GC.CollectionCount(0) - gen0, gen1 = GC.CollectionCount(1) - gen1, gen2 = GC.CollectionCount(2) - gen2 });
                    }
                    rows.Add(new { size, fragments, text, iterations, samples });
                }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            note = "Masked frame parsing/unmasking and text validation from memory, including identical reflection/content checks. Reused reader per workload; not socket throughput, callback cost or end-to-end latency. Use identical runner binaries and replace only the core assembly for comparison.",
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
    private static MemoryStream Wire(byte[] payload, int fragments, bool text)
    {
        var stream = new MemoryStream();
        for (var index = 0; index < fragments; ++index)
        {
            var offset = payload.Length * index / fragments;
            var length = payload.Length * (index + 1) / fragments - offset;
            stream.WriteByte((byte)((index == 0 ? text ? 1 : 2 : 0) | (index == fragments - 1 ? 128 : 0)));
            if (length < 126) stream.WriteByte((byte)(128 | length));
            else if (length <= ushort.MaxValue)
            { stream.WriteByte(254); stream.WriteByte((byte)(length >> 8)); stream.WriteByte((byte)length); }
            else
            { stream.WriteByte(255); for (var shift = 56; shift >= 0; shift -= 8) stream.WriteByte((byte)((long)length >> shift)); }
            var key = new byte[] { 3, 7, 13, 29 }; stream.Write(key);
            for (var i = 0; i < length; ++i) stream.WriteByte((byte)(payload[offset + i] ^ key[i % 4]));
        }
        stream.Position = 0;
        return stream;
    }
}
