using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EmbedIO;

internal static class ListenerAllocations
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ConnectionType = ((typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true)) ?? throw new System.InvalidOperationException("Expected a non-null test value."));

    internal static bool Run(string[] args)
    {
        var verify = args.Contains("--verify-listener-allocations", StringComparer.Ordinal);
        if (!verify && !args.Contains("--listener-allocations", StringComparer.Ordinal)) return false;
        var rows = new List<object>();
        foreach (var consumed in new[] { true, false })
        {
            using var source = new MemoryStream(Enumerable.Repeat((byte)'b', 4096).ToArray());
            var context = Context(source);
            context.Request.Headers["Content-Length"] = "4096";
            var input = context.Request.InputStream;
            var remaining = input.GetType().GetField("_remainingBody", PrivateInstance);
            object length = 4096L;
            input.CopyTo(Stream.Null);
            var flush = ((context.Request.GetType().GetMethod("FlushInput", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).CreateDelegate<Func<bool>>(context.Request);
            Measure(consumed ? "drain-consumed-4k" : "drain-unread-4k", () =>
            {
                if (!consumed) { source.Position = 0; ((remaining) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).SetValue(input, length); }
                if (!flush() || source.Position != 4096) throw new InvalidOperationException("Body drain boundary changed.");
            }, consumed ? 0 : 160);
        }
        foreach (var size in new[] { 0, 1024, 16384 })
        {
            var context = Context(Stream.Null);
            context.Response.StatusCode = 201;
            var text = "caf\u00e9-" + new string('a', size);
            context.Response.Headers["X-Text"] = text;
            context.Response.Headers["X-Last"] = "tail";
            var expected = Encoding.UTF8.GetBytes($"HTTP/1.1 201 Created\r\nX-Text: {text}\r\nX-Last: tail\r\n\r\n");
            var write = ((context.Response.GetType().GetMethod("WriteHeaders", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).CreateDelegate<Func<MemoryStream>>(context.Response);
            Measure("headers-" + size, () =>
            {
                using var wire = write();
                if (!wire.GetBuffer().AsSpan(0, (int)wire.Length).SequenceEqual(expected))
                    throw new InvalidOperationException("Header bytes changed.");
            }, size == 0 ? 1000 : size == 1024 ? 8500 : 105000);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            note = "Actual body drain and header serializer; fixtures/setup excluded, timing informational. Unread-body field reset is included in measurement.",
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return true;

        void Measure(string name, Action operation, long maxBytes)
        {
            for (var i = 0; i < 2000; i++) operation();
            var samples = new List<(double Nanoseconds, double Bytes)>();
            for (var round = 0; round < 5; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 10000; i++) operation();
                watch.Stop();
                samples.Add((watch.Elapsed.TotalNanoseconds / 10000, (GC.GetAllocatedBytesForCurrentThread() - before) / 10000.0));
            }
            var ns = samples.Select(sample => sample.Nanoseconds).Order().ElementAt(2);
            var bytes = samples.Select(sample => sample.Bytes).Order().ElementAt(2);
            rows.Add(new { name, medianNanoseconds = ns, allocatedBytes = bytes, maxBytes });
            if (verify && bytes > maxBytes + 1) throw new InvalidOperationException($"{name}: {bytes} B exceeds {maxBytes} B.");
        }
    }

    private static IHttpContext Context(Stream source)
    {
        var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
        ((ConnectionType.GetField("_connectionSync", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).SetValue(connection, new object());
        ((ConnectionType.GetField("<Stream>k__BackingField", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).SetValue(connection, source);
        ((ConnectionType.GetMethod("Init", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).Invoke(connection, null);
        return (IHttpContext)((((ConnectionType.GetField("_context", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetValue(connection)) ?? throw new System.InvalidOperationException("Expected a non-null test value."));
    }
}
