using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EmbedIO;

internal static class EngineParser
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--engine-parser", StringComparer.Ordinal)) return false;
        var type = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var connection = RuntimeHelpers.GetUninitializedObject(type);
        (type.GetField("_connectionSync", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).SetValue(connection, new object());
        (type.GetField("<Stream>k__BackingField", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).SetValue(connection, Stream.Null);
        var initialize = (type.GetMethod("InitWithPendingInput", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).CreateDelegate<Action<ArraySegment<byte>>>(connection);
        var process = (type.GetMethod("ProcessInput", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).CreateDelegate<Func<MemoryStream, bool>>(connection);
        var streamField = (type.GetField("_ms", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var positionField = (type.GetField("_position", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var contextField = (type.GetField("_context", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var errorField = (type.GetField("_errorMessage", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var rows = new List<object>();
        foreach (var batch in new[] { 1, 16, 64 })
        {
            var request = "GET /baseline11?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\nX-Padding: " + new string('p', 128) + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(request, batch)));
            void Parse()
            {
                var pending = new ArraySegment<byte>(bytes);
                for (var index = 0; index < batch; index++)
                {
                    initialize(pending);
                    using var stream = (MemoryStream)(streamField.GetValue(connection) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
                    if (!process(stream) || errorField.GetValue(connection) != null) throw new InvalidOperationException("Parser failed.");
                    var context = (IHttpContext)(contextField.GetValue(connection) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
                    if (context.Request.RawTarget != "/baseline11?a=13&b=42" || context.Request.Headers["X-Padding"]?.Length != 128)
                        throw new InvalidOperationException("Parsed request changed.");
                    var position = (int)(positionField.GetValue(connection) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
                    pending = new ArraySegment<byte>(stream.GetBuffer(), position, (int)stream.Length - position);
                }
                if (pending.Count != 0) throw new InvalidOperationException("Pipeline was not fully consumed.");
            }
            for (var i = 0; i < 2000; i++) Parse();
            var samples = new List<object>();
            for (var round = 0; round < 5; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 2000; i++) Parse();
                watch.Stop();
                var allocations = GC.GetAllocatedBytesForCurrentThread() - before;
                samples.Add(new
                {
                    round,
                    nanosecondsPerRequest = watch.Elapsed.TotalNanoseconds / (2000 * batch),
                    bytesPerRequest = allocations / (double)(2000 * batch)
                });
            }
            rows.Add(new { batch, samples });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            note = "Parser/context construction and buffered pipeline handoff, including reflection observations. No sockets or URI finalization; not end-to-end throughput.",
            rows
        },
            new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}
