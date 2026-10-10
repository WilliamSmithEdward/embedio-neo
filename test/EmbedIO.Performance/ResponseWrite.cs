using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;

// Socket-free component measurement of the managed HTTP/1 response stream: the
// first write that commits headers, and subsequent fixed-length or chunked writes.
// A counting transport records every write the stream submits, so the rows show
// transport submissions, bytes, time and managed allocation per operation without
// kernel or TLS costs. Use the same runner against two core assemblies.
internal static class ResponseWrite
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ConnectionType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true)
        ?? throw new InvalidOperationException("Expected a non-null test value.");

    internal static bool Run(string[] args)
    {
        if (!args.Contains("--response-write", StringComparer.Ordinal)) return false;
        RunAsync().GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync()
    {
        var rows = new List<object>();
        var transport = new CountingTransport();
        var body = new byte[1 << 20];
        for (var index = 0; index < body.Length; index++) body[index] = (byte)('a' + index % 26);

        // Control: context construction without any response work.
        await Measure("context-only", 5000, () => { Context(transport); return Task.CompletedTask; });

        foreach (var size in new[] { 13, 16384, 1 << 20 })
        {
            await Measure("first-fixed-" + size, size > 65536 ? 300 : 5000, () =>
            {
                var context = Context(transport);
                context.Response.ContentLength64 = size;
                return context.Response.OutputStream.WriteAsync(body, 0, size);
            });
            await Measure("first-chunked-" + size, size > 65536 ? 300 : 5000, () =>
            {
                var context = Context(transport);
                context.Response.SendChunked = true;
                return context.Response.OutputStream.WriteAsync(body, 0, size);
            });
        }

        // A complete small chunked response: first write plus synchronous disposal.
        await Measure("first-chunked-13-dispose", 5000, () =>
        {
            var context = Context(transport);
            context.Response.SendChunked = true;
            var stream = context.Response.OutputStream;
            return WriteThenDispose(stream, body, 13);
        });

        foreach (var size in new[] { 1024, 16384, 65536, 81920, 1 << 20 })
        {
            var context = Context(transport);
            context.Response.SendChunked = true;
            var stream = context.Response.OutputStream;
            await stream.WriteAsync(Array.Empty<byte>(), 0, 0);
            await Measure("chunk-" + size, size > 65536 ? 300 : 5000, () => stream.WriteAsync(body, 0, size));
            await Measure("chunk-" + size + "-memory", size > 65536 ? 300 : 5000, () => stream.WriteAsync(body.AsMemory(0, size)).AsTask());
            await Measure("chunk-" + size + "-sync", size > 65536 ? 300 : 5000, () => { stream.Write(body, 0, size); return Task.CompletedTask; });
        }

        var core = typeof(WebServer).Assembly.Location;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core))),
            note = "Socket-free counting transport; first-* rows include context construction (see context-only); chunk-* rows reuse one committed chunked response. Timing is informational.",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Measure(string name, int iterations, Func<Task> operation)
        {
            for (var index = 0; index < Math.Min(iterations, 2000); index++) await operation();
            var samples = new List<(double Nanoseconds, double Bytes, double Writes, double WrittenBytes)>();
            for (var round = 0; round < 5; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                transport.Reset();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var index = 0; index < iterations; index++) await operation();
                watch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                samples.Add((watch.Elapsed.TotalNanoseconds / iterations, allocated / (double)iterations,
                    transport.Writes / (double)iterations, transport.Bytes / (double)iterations));
            }
            var ns = samples.Select(sample => sample.Nanoseconds).Order().ElementAt(2);
            var bytes = samples.Select(sample => sample.Bytes).Order().ElementAt(2);
            var writes = samples.Select(sample => sample.Writes).Order().ElementAt(2);
            var written = samples.Select(sample => sample.WrittenBytes).Order().ElementAt(2);
            rows.Add(new { name, iterations, medianNanoseconds = ns, allocatedBytes = bytes, transportWrites = writes, transportBytes = written, syncWrites = transport.SyncWrites / (double)iterations });
            Console.Error.WriteLine($"{name}: {ns:F0} ns/op, {bytes:F0} B/op, {writes:F2} writes/op, {written:F0} bytes/op");
        }
    }

    private static async Task WriteThenDispose(Stream stream, byte[] body, int count)
    {
        await stream.WriteAsync(body, 0, count);
        stream.Dispose();
    }

    private static IHttpContext Context(Stream transport)
    {
        var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
        (ConnectionType.GetField("_connectionSync", PrivateInstance) ?? throw new InvalidOperationException("Expected a non-null test value.")).SetValue(connection, new object());
        (ConnectionType.GetField("<Stream>k__BackingField", PrivateInstance) ?? throw new InvalidOperationException("Expected a non-null test value.")).SetValue(connection, transport);
        (ConnectionType.GetMethod("Init", PrivateInstance) ?? throw new InvalidOperationException("Expected a non-null test value.")).Invoke(connection, null);
        return (IHttpContext)((ConnectionType.GetField("_context", PrivateInstance) ?? throw new InvalidOperationException("Expected a non-null test value.")).GetValue(connection)
            ?? throw new InvalidOperationException("Expected a non-null test value."));
    }

    // Records submissions without copying or retaining any payload.
    private sealed class CountingTransport : Stream
    {
        public long Writes;
        public long SyncWrites;
        public long Bytes;

        public void Reset() { Writes = 0; SyncWrites = 0; Bytes = 0; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            SyncWrites++;
            Bytes += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            SyncWrites++;
            Bytes += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Writes++;
            Bytes += count;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            Bytes += buffer.Length;
            return default;
        }
    }
}
