using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EmbedIO;

internal static class RequestBodyRead
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--request-body-read", StringComparer.Ordinal)) return false;
        var assembly = typeof(WebServer).Assembly;
        var type = assembly.GetType("EmbedIO.Net.Internal.RequestStream", true) ?? throw new TypeLoadException();
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Stream), typeof(byte[]), typeof(int), typeof(int), typeof(long) }, null)
            ?? throw new MissingMethodException("Request body constructor.");
        var parameters = new[] { Expression.Parameter(typeof(Stream)), Expression.Parameter(typeof(byte[])),
            Expression.Parameter(typeof(int)), Expression.Parameter(typeof(int)), Expression.Parameter(typeof(long)) };
        var create = Expression.Lambda<Func<Stream, byte[], int, int, long, Stream>>(
            Expression.Convert(Expression.New(constructor, parameters), typeof(Stream)), parameters).Compile();
        var readerParameter = Expression.Parameter(typeof(Stream));
        var remainderGetter = type.GetProperty("BufferedRemainder", BindingFlags.Instance | BindingFlags.NonPublic)?.GetGetMethod(true)
            ?? throw new MissingMemberException("BufferedRemainder");
        var remainder = Expression.Lambda<Func<Stream, ArraySegment<byte>>>(
            Expression.Call(Expression.Convert(readerParameter, type), remainderGetter), readerParameter).Compile();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            benchmark = "request-body-read",
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            runnerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RequestBodyRead).Assembly.Location))),
            source = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            scope = "Single-thread body-reader construction, reads, byte validation, boundary handoff and disposal; reusable MemoryStream transport; no network, delegate compilation or fixture allocation in samples."
        }));
        foreach (var length in new[] { 0, 64, 65536 })
            foreach (var placement in new[] { "buffered", "split", "transport" })
                foreach (var path in new[] { "sync", "array", "memory" })
                {
                    var body = new byte[length];
                    new Random(20261010).NextBytes(body);
                    var tail = new byte[] { 0x47, 0x45, 0x54, 0x20 };
                    var buffered = placement == "buffered" ? length : placement == "split" ? length / 2 : 0;
                    var whole = buffered == length;
                    var prefix = new byte[7 + buffered + (whole ? tail.Length : 0)];
                    body.AsSpan(0, buffered).CopyTo(prefix.AsSpan(7));
                    if (whole) tail.CopyTo(prefix, 7 + buffered);
                    var wire = new byte[length - buffered + (whole ? 0 : tail.Length)];
                    body.AsSpan(buffered).CopyTo(wire);
                    if (!whole) tail.CopyTo(wire, length - buffered);
                    using var transport = new MemoryStream(wire, false);
                    var destination = new byte[8192];
                    void Consume()
                    {
                        transport.Position = 0;
                        using var reader = create(transport, prefix, 7, prefix.Length - 7, length);
                        var consumed = 0;
                        int count;
                        do
                        {
                            count = path == "sync" ? reader.Read(destination, 0, destination.Length)
                                : path == "array" ? reader.ReadAsync(destination, 0, destination.Length).GetAwaiter().GetResult()
                                : reader.ReadAsync(destination.AsMemory()).GetAwaiter().GetResult();
                            if (count > length - consumed || !destination.AsSpan(0, count).SequenceEqual(body.AsSpan(consumed, count)))
                                throw new InvalidDataException("Body content changed.");
                            consumed += count;
                        } while (count != 0);
                        if (consumed != length || transport.Position != length - buffered)
                            throw new InvalidDataException("Body boundary changed.");
                        var remaining = whole ? remainder(reader).AsSpan() : wire.AsSpan((int)transport.Position);
                        if (!remaining.SequenceEqual(tail)) throw new InvalidDataException("Following request changed.");
                    }
                    for (var warmup = 0; warmup < 512; warmup++) Consume();
                    var iterations = length < 1024 ? 200000 : 2048;
                    for (var round = 0; round < 3; round++)
                    {
                        var allocated = GC.GetAllocatedBytesForCurrentThread();
                        var timer = Stopwatch.StartNew();
                        for (var iteration = 0; iteration < iterations; iteration++) Consume();
                        timer.Stop();
                        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            length,
                            placement,
                            path,
                            round,
                            iterations,
                            nanosecondsPerBody = timer.Elapsed.TotalNanoseconds / iterations,
                            bytesPerBody = (double)bytes / iterations
                        }));
                    }
                }
        return true;
    }
}
