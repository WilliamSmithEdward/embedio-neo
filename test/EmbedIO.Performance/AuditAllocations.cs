using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using EmbedIO;

internal static class AuditAllocations
{
    private delegate bool ReadTrailers(Stream stream, out IReadOnlyList<KeyValuePair<string, string>> trailers);

    internal static bool Run(string[] args)
    {
        var verify = args.Contains("--verify-audit-allocations", StringComparer.Ordinal);
        if (!verify && !args.Contains("--audit-allocations", StringComparer.Ordinal)) return false;
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        var assembly = typeof(WebServer).Assembly;
        var eventType = assembly.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true) ?? throw new InvalidOperationException("Missing message type.");
        var opcodeType = assembly.GetType("EmbedIO.WebSockets.Opcode", true) ?? throw new InvalidOperationException("Missing opcode type.");
        var constructor = eventType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { opcodeType, typeof(byte[]) }, null)
            ?? throw new InvalidOperationException("Missing message constructor.");
        var bytes = Expression.Parameter(typeof(byte[]));
        var create = Expression.Lambda<Func<byte[], object>>(Expression.New(constructor,
            Expression.Constant(Enum.Parse(opcodeType, "Text"), opcodeType), bytes), bytes).Compile();
        var message = Expression.Parameter(typeof(object));
        var read = Expression.Lambda<Func<object, byte[]>>(Expression.Property(Expression.Convert(message, eventType), "RawData"), message).Compile();
        var data = Encoding.UTF8.GetBytes(new string('x', 4096));
        Measure("websocket-text-event-4k", () =>
        {
            using var current = (IDisposable)create(data);
            return read(current).Length;
        }, 4096, verify ? 48 : null);
        var bindingType = assembly.GetType("EmbedIO.WebApi.RequestFieldBinding", true) ?? throw new InvalidOperationException("Missing binding type.");
        var getString = (bindingType.GetMethod("GetString", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing binding method."))
            .CreateDelegate<Func<NameValueCollection, string, bool, string, string?>>();
        var fields = new NameValueCollection();
        for (var i = 0; i < 100; i++) fields.Add("field" + i, "value");
        fields.Add("selected", "last");
        Measure("optional-field-100-keys", () => getString(fields, "selected", false, "query")?.Length ?? 0, 4, verify ? 64 : null);
        var headerType = assembly.GetType("EmbedIO.Net.Internal.HeaderUtility", true) ?? throw new InvalidOperationException("Missing header utility.");
        var getCharset = (headerType.GetMethod("GetCharset", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("Missing charset parser.")).CreateDelegate<Func<string?, string?>>();
        Measure("charset-after-parameters", () => getCharset(" text/plain ; format=flowed ; boundary=example ; charset=utf-8 ")?.Length ?? 0, 5, null);
        Measure("charset-absent", () => getCharset(" text/plain ; format=flowed ; boundary=example ")?.Length ?? 0, 0, null);
        MeasureTrailers(assembly);
        return true;
    }

    private static void MeasureTrailers(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Net.Internal.ChunkedRequestStream", true) ?? throw new InvalidOperationException("Missing chunked stream.");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Stream), typeof(byte[]), typeof(int), typeof(int) }, null) ?? throw new InvalidOperationException("Missing chunked constructor.");
        var input = Expression.Parameter(typeof(byte[]));
        var create = Expression.Lambda<Func<byte[], Stream>>(Expression.New(constructor,
            Expression.Constant(Stream.Null, typeof(Stream)), input, Expression.Constant(0), Expression.ArrayLength(input)), input).Compile();
        var stream = Expression.Parameter(typeof(Stream));
        var trailers = Expression.Parameter(typeof(IReadOnlyList<KeyValuePair<string, string>>).MakeByRefType());
        var method = type.GetMethod("TryGetTrailers", BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException("Missing trailers method.");
        var read = Expression.Lambda<ReadTrailers>(Expression.Call(Expression.Convert(stream, type), method, trailers), stream, trailers).Compile();
        var wire = Encoding.ASCII.GetBytes("0\r\nX-One: \t first \t\r\nX-Two: \t second \t\r\n\r\n");
        var destination = new byte[1];
        Measure("chunked-two-trailers", () =>
        {
            using var current = create(wire);
            if (current.Read(destination, 0, 1) != 0 || !read(current, out var fields)
                || fields.Count != 2 || fields[0].Key != "X-One" || fields[0].Value != "first"
                || fields[1].Key != "X-Two" || fields[1].Value != "second")
                throw new InvalidOperationException("Unexpected trailer parse result.");
            return fields[0].Value.Length + fields[1].Value.Length;
        }, 11, null);
    }

    private static void Measure(string name, Func<int> operation, int expected, int? maxBytes)
    {
        const int iterations = 100000;
        for (var i = 0; i < 1000; i++) _ = operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var checksum = 0L;
        for (var i = 0; i < iterations; i++) checksum += operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{name}: {elapsed.TotalNanoseconds / iterations:F1} ns/op; {(double)allocated / iterations:F1} B/op; checksum={checksum}");
        if (checksum != (long)expected * iterations) throw new InvalidOperationException($"{name} returned an unexpected checksum.");
        if (maxBytes.HasValue && allocated > (long)maxBytes.Value * iterations)
            throw new InvalidOperationException($"{name} exceeded its {maxBytes.Value} B/op allocation budget.");
    }
}
