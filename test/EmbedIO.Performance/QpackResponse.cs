using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EmbedIO;

internal static class QpackResponse
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--qpack-response", StringComparer.Ordinal)) return false;
        const int iterations = 25000;
        var assembly = typeof(WebServer).Assembly;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            benchmark = "qpack-response",
            iterations,
            rounds = 7,
            warmup = 5000,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            assembly = assembly.Location,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            source = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            scope = "Single-thread codec/planner; dynamic mode includes immediate section ACK. Excludes field construction, connection gate, transport and application."
        }));
        var datasets = new (string Name, (string Name, string Value, bool Never)[] Fields)[]
        {
            ("status", new[] { (":status", "200", false) }),
            ("static", new[] { (":status", "200", false), ("content-type", "application/json", false), ("content-length", "0", false) }),
            ("repeated", new[] { (":status", "200", false), ("content-type", "application/json", false),
                ("server", "EmbedIO-Neo", false), ("date", "Thu, 08 Oct 2026 12:00:00 GMT", false),
                ("x-resource", "repeated-resource-name", false), ("content-length", "4096", false) }),
            ("sensitive", new[] { (":status", "200", false), ("set-cookie", "session=synthetic-benchmark-value; Secure; HttpOnly", false),
                ("x-private", "synthetic-private-value", true) })
        };
        var encoder = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoder", true) ?? throw new InvalidOperationException("Missing codec.");
        var literal = encoder.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing codec method.");
        var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new InvalidOperationException("Missing field.");
        foreach (var dataset in datasets)
        {
            var fields = Array.CreateInstance(fieldType, dataset.Fields.Length);
            for (var i = 0; i < dataset.Fields.Length; ++i)
                fields.SetValue(Activator.CreateInstance(fieldType, Hidden, null,
                    new object[] { dataset.Fields[i].Name, dataset.Fields[i].Value, dataset.Fields[i].Never }, null), i);
            var baseline = Compile(null, literal, fields, 65536, 65536);
            var modes = new[]
            {
                (Name: "stateless", Encode: baseline),
                (Name: "planner-capacity0", Encode: Planner(assembly, fields, false)),
                (Name: "planner-warm4096", Encode: Planner(assembly, fields, true))
            };
            foreach (var mode in modes)
                for (var i = 0; i < 5000; ++i) GC.KeepAlive(mode.Encode());
            for (var round = 0; round < 7; ++round)
            {
                // Rotate mode order to avoid always giving one implementation
                // the earliest tiered-JIT or host-load interval.
                for (var position = 0; position < modes.Length; ++position)
                {
                    var mode = modes[(round + position) % modes.Length];
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long wireBytes = 0;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < iterations; ++i) wireBytes += mode.Encode().Length;
                    var elapsed = Stopwatch.GetElapsedTime(start);
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        dataset = dataset.Name,
                        mode = mode.Name,
                        round,
                        nanoseconds = elapsed.TotalNanoseconds / iterations,
                        allocatedBytes = (double)bytes / iterations,
                        sectionBytes = (double)wireBytes / iterations
                    }));
                }
            }
        }
        return true;
    }
    private static Func<byte[]> Planner(Assembly assembly, Array fields, bool enabled)
    {
        object Create(string name, params object[] values) => Activator.CreateInstance(
            assembly.GetType("EmbedIO.Net.Internal.Http3." + name, true) ?? throw new InvalidOperationException("Missing encoder type."),
            Hidden, null, values, null) ?? throw new InvalidOperationException("Missing encoder constructor.");
        var feedback = Create("QpackEncoderFeedback", 256, 4096);
        var planner = Create("QpackResponseEncoder", feedback, 4096, 65536);
        var settingsType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3PeerSettings", true) ?? throw new InvalidOperationException("Missing settings.");
        var settings = settingsType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null,
            new object[] { enabled ? Convert.FromHexString("0150000700") : Array.Empty<byte>(), 1024 }) ?? throw new InvalidOperationException("Missing settings parser.");
        var encode = Compile(planner, planner.GetType().GetMethod("Encode", Hidden) ?? throw new InvalidOperationException("Missing planner."),
            0L, fields, settings, 65536, 65536);
        var dequeue = (planner.GetType().GetMethod("DequeueInstructions", Hidden) ?? throw new InvalidOperationException("Missing queue."))
            .CreateDelegate<Func<byte[]?>>(planner);
        var feed = (feedback.GetType().GetMethod("Feed", Hidden) ?? throw new InvalidOperationException("Missing feedback."))
            .CreateDelegate<Action<byte[], int, int>>(feedback);
        var first = encode();
        if (first[0] != 0) throw new InvalidOperationException("Cold response unexpectedly referenced dynamic state.");
        var inserts = 0;
        while (dequeue() != null) ++inserts;
        if (inserts >= 63) throw new InvalidOperationException("Benchmark setup needs a wider insert increment.");
        if (inserts != 0) feed(new[] { (byte)inserts }, 0, 1);
        var acknowledgment = new byte[] { 128 };
        return () =>
        {
            var bytes = encode();
            if (bytes[0] != 0) feed(acknowledgment, 0, 1);
            return bytes;
        };
    }
    private static Func<byte[]> Compile(object? target, MethodInfo method, params object[] args)
    {
        var parameters = method.GetParameters();
        var constants = args.Select((value, index) => Expression.Constant(value, parameters[index].ParameterType));
        var call = Expression.Call(target == null ? null : Expression.Constant(target), method, constants);
        return Expression.Lambda<Func<byte[]>>(call).Compile();
    }
}
