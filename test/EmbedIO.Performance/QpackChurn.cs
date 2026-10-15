using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;

internal static class QpackChurn
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--qpack-churn", StringComparer.Ordinal)) return false;
        const int iterations = 32768;
        const int warmup = 4096;
        const int rounds = 7;
        var assembly = typeof(WebServer).Assembly;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            benchmark = "qpack-churn",
            iterations,
            warmup,
            rounds,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            assembly = assembly.Location,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            source = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            scope = "Single-thread component with immediate insertion credit and section ACK; excludes field construction, transport, connection gate and application."
        }));
        var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new InvalidOperationException("Missing field.");
        var encoderType = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoder", true) ?? throw new InvalidOperationException("Missing encoder.");
        var literal = encoderType.GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing literal encoder.");
        foreach (var dataset in new[] { (Name: "unique", Burst: 1, Stable: false), (Name: "bursts", Burst: 8, Stable: false), (Name: "mixed", Burst: 1, Stable: true) })
        {
            var inputs = new Array[2048];
            var descriptions = new List<string[][]>();
            for (var index = 0; index < inputs.Length; ++index)
            {
                var fields = new List<string[]> { new[] { ":status", "200" }, new[] { "content-type", "application/json" } };
                if (dataset.Stable) fields.Add(new[] { "x-stable", "retained-resource-group" });
                fields.Add(new[] { "x-resource", "resource-" + index.ToString("D8", CultureInfo.InvariantCulture) + "-synthetic-payload" });
                descriptions.Add(fields.ToArray());
                var array = Array.CreateInstance(fieldType, fields.Count);
                for (var i = 0; i < fields.Count; ++i)
                    array.SetValue(Activator.CreateInstance(fieldType, Hidden, null, new object[] { fields[i][0], fields[i][1], false }, null), i);
                inputs[index] = array;
            }
            var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { dataset.Name, dataset.Burst, descriptions }))));
            var stateless = Compile(null, literal, fieldType.MakeArrayType(), null);
            for (var round = 0; round < rounds; ++round)
            {
                for (var position = 0; position < 2; ++position)
                {
                    var dynamic = (round + position) % 2 != 0;
                    // A fresh connection per round makes warmup/table history
                    // identical rather than dependent on previous mode order.
                    var planner = dynamic ? new Planner(assembly, fieldType.MakeArrayType()) : null;
                    (int Section, int Encoder) Encode(int index)
                    {
                        var input = inputs[index / dataset.Burst % inputs.Length];
                        return planner == null ? (stateless(input).Length, 0) : planner.Encode(input);
                    }
                    for (var i = 0; i < warmup; ++i) Encode(i);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long sectionBytes = 0, encoderBytes = 0;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < iterations; ++i)
                    {
                        var result = Encode(warmup + i);
                        sectionBytes += result.Section;
                        encoderBytes += result.Encoder;
                    }
                    var elapsed = Stopwatch.GetElapsedTime(start);
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        dataset = dataset.Name,
                        inputHash,
                        round,
                        mode = dynamic ? "planner4096" : "stateless",
                        nanoseconds = elapsed.TotalNanoseconds / iterations,
                        allocatedBytes = (double)bytes / iterations,
                        sectionBytes = (double)sectionBytes / iterations,
                        encoderBytes = (double)encoderBytes / iterations,
                        totalBytes = (double)(sectionBytes + encoderBytes) / iterations
                    }));
                }
            }
        }
        return true;
    }
    private sealed class Planner
    {
        private readonly Func<Array, byte[]> _encode;
        private readonly Func<byte[]?> _dequeue;
        private readonly Action<byte[], int, int> _feed;
        private readonly byte[] _increment = new byte[1];
        private readonly byte[] _ack = { 128 };
        internal Planner(Assembly assembly, Type fieldsType)
        {
            object Create(string name, params object[] values) => Activator.CreateInstance(
                assembly.GetType("EmbedIO.Net.Internal.Http3." + name, true) ?? throw new InvalidOperationException("Missing QPACK type."),
                Hidden, null, values, null) ?? throw new InvalidOperationException("Missing constructor.");
            var feedback = Create("QpackEncoderFeedback", 256, 4096);
            var owner = Create("QpackResponseEncoder", feedback, 4096, 65536);
            var settingsType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3PeerSettings", true) ?? throw new InvalidOperationException("Missing settings.");
            var peer = settingsType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null,
                new object[] { Convert.FromHexString("0150000700"), 1024 }) ?? throw new InvalidOperationException("Missing settings parser.");
            _encode = Compile(owner, owner.GetType().GetMethod("Encode", Hidden) ?? throw new InvalidOperationException("Missing planner."), fieldsType, peer);
            _dequeue = (owner.GetType().GetMethod("DequeueInstructions", Hidden) ?? throw new InvalidOperationException("Missing queue."))
                .CreateDelegate<Func<byte[]?>>(owner);
            _feed = (feedback.GetType().GetMethod("Feed", Hidden) ?? throw new InvalidOperationException("Missing feedback."))
                .CreateDelegate<Action<byte[], int, int>>(feedback);
        }
        internal (int Section, int Encoder) Encode(Array fields)
        {
            var section = _encode(fields);
            var count = 0;
            var encoderBytes = 0;
            byte[]? instruction;
            while ((instruction = _dequeue()) != null) { ++count; encoderBytes += instruction.Length; }
            if (count >= 63) throw new InvalidOperationException("Benchmark increment needs a wider integer.");
            if (count != 0) { _increment[0] = (byte)count; _feed(_increment, 0, 1); }
            if (section[0] != 0) _feed(_ack, 0, 1);
            return (section.Length, encoderBytes);
        }
    }
    private static Func<Array, byte[]> Compile(object? target, MethodInfo method, Type fieldsType, object? peer)
    {
        var input = Expression.Parameter(typeof(Array), "fields");
        var fields = Expression.Convert(input, fieldsType);
        Expression[] arguments = peer == null
            ? new Expression[] { fields, Expression.Constant(65536), Expression.Constant(65536) }
            : new Expression[] { Expression.Constant(0L), fields, Expression.Constant(peer, method.GetParameters()[2].ParameterType), Expression.Constant(65536), Expression.Constant(65536) };
        var call = Expression.Call(target == null ? null : Expression.Constant(target), method, arguments);
        return Expression.Lambda<Func<Array, byte[]>>(call, input).Compile();
    }
}
