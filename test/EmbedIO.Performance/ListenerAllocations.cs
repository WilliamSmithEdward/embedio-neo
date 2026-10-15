using System.Diagnostics;
using System.Linq.Expressions;
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
            var remaining = input.GetType().GetField("_bytesLeft", PrivateInstance)
                ?? throw new InvalidOperationException("Missing fixed-length body accounting field.");
            object length = 4096L;
            input.CopyTo(Stream.Null);
            var flush = ((context.Request.GetType().GetMethod("FlushInput", PrivateInstance)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).CreateDelegate<Func<bool>>(context.Request);
            Measure(consumed ? "drain-consumed-4k" : "drain-unread-4k", () =>
            {
                if (!consumed) { source.Position = 0; remaining.SetValue(input, length); }
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
            var write = HeadWriter(context.Response);
            // The head is encoded into a pooled lease: no managed allocation at any head size
            // (main allocated 144 B to 49,432 B per head in these rows before the lease).
            HeadCheck headOnly = (buffer, length) => buffer.AsSpan(0, length).SequenceEqual(expected);
            Measure("headers-" + size, () =>
            {
                if (!write(0, headOnly)) throw new InvalidOperationException("Header bytes changed.");
            }, 64);
            // The head is encoded with room for the first body segment, which the response
            // output appends behind it in the same pooled array; there is no growth path.
            var body = Encoding.ASCII.GetBytes("Hello, World!");
            HeadCheck withBody = (buffer, length) =>
            {
                if (!buffer.AsSpan(0, length).SequenceEqual(expected)) return false;
                if (buffer.Length < length + body.Length) throw new InvalidOperationException("The head left no room for the first body segment.");
                body.CopyTo(buffer, length);
                var appended = buffer.AsSpan(length, body.Length).SequenceEqual(body);
                Array.Clear(buffer, length, body.Length);
                return appended;
            };
            Measure("headers-" + size + "-first-body-13-reserved", () =>
            {
                if (!write(body.Length, withBody)) throw new InvalidOperationException("Header or body bytes changed.");
            }, 64);
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

    private delegate bool HeadCheck(byte[] buffer, int length);

    // WriteHeaders returns an internal pooled head lease; the compiled call hands its bytes to
    // the check and disposes the lease, as the response output does after its first write.
    private static Func<int, HeadCheck, bool> HeadWriter(IHttpResponse response)
    {
        var method = response.GetType().GetMethod("WriteHeaders", PrivateInstance)
            ?? throw new InvalidOperationException("Missing response head encoder.");
        var leaseType = method.ReturnType;
        var bodyCount = Expression.Parameter(typeof(int), "bodyCount");
        var check = Expression.Parameter(typeof(HeadCheck), "check");
        var head = Expression.Variable(leaseType, "head");
        var buffer = leaseType.GetProperty("Buffer", PrivateInstance) ?? throw new InvalidOperationException("Missing head buffer.");
        var length = leaseType.GetProperty("Length", PrivateInstance) ?? throw new InvalidOperationException("Missing head length.");
        var dispose = leaseType.GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes) ?? throw new InvalidOperationException("Missing head release.");
        var block = Expression.Block(typeof(bool), new[] { head },
            Expression.Assign(head, Expression.Call(Expression.Constant(response), method, bodyCount)),
            Expression.TryFinally(
                Expression.Invoke(check, Expression.Property(head, buffer), Expression.Property(head, length)),
                Expression.Call(head, dispose)));
        return Expression.Lambda<Func<int, HeadCheck, bool>>(block, bodyCount, check).Compile();
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
