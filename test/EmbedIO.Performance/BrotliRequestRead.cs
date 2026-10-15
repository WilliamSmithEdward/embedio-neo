using System.Diagnostics;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EmbedIO;

internal static class BrotliRequestRead
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--brotli-request-read", StringComparer.Ordinal)) return false;
        const int warmup = 128;
        const int iterations = 1024;
        const int rounds = 5;
        var assembly = typeof(WebServer).Assembly;
        var type = assembly.GetType("EmbedIO.Internal.BrotliRequestStream")
            ?? throw new InvalidOperationException("This benchmark requires the .NET 10 request codec.");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Stream) }, null)
            ?? throw new InvalidOperationException("Missing request codec constructor.");
        var parameter = Expression.Parameter(typeof(Stream));
        var create = Expression.Lambda<Func<Stream, Stream>>(Expression.Convert(Expression.New(constructor, parameter), typeof(Stream)), parameter).Compile();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            benchmark = "brotli-request-read",
            warmup,
            iterations,
            rounds,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            runnerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrotliRequestRead).Assembly.Location))),
            source = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            scope = "Single-thread component; includes construction, runtime decoder, byte validation and disposal; excludes fixture creation, reflection setup, native allocation accounting and transport."
        }));
        foreach (var length in new[] { 0, 64, 4096 })
        {
            var expected = new byte[length];
            new Random(20261008).NextBytes(expected);
            using var encoded = new MemoryStream();
            using (var compressor = new BrotliStream(encoded, CompressionMode.Compress, true)) compressor.Write(expected);
            var wire = encoded.ToArray();
            var output = new byte[256];
            int Read(bool single)
            {
                using var input = new MemoryStream(wire, false);
                using var reader = create(input);
                var offset = 0;
                if (single)
                {
                    int next;
                    while ((next = reader.ReadByte()) >= 0)
                    {
                        if (offset == expected.Length || next != expected[offset]) throw new InvalidDataException("Single-byte content mismatch.");
                        offset++;
                    }
                }
                else
                {
                    int count;
                    while ((count = reader.Read(output)) != 0)
                    {
                        if (count > expected.Length - offset || !output.AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count)))
                            throw new InvalidDataException("Bulk content mismatch.");
                        offset += count;
                    }
                }
                if (offset != expected.Length) throw new InvalidDataException("Decoded length mismatch.");
                return offset;
            }
            for (var round = 0; round < rounds; round++)
            {
                for (var position = 0; position < 2; position++)
                {
                    var single = (round + position) % 2 != 0;
                    for (var i = 0; i < warmup; i++) Read(single);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var gc0 = GC.CollectionCount(0);
                    var gc1 = GC.CollectionCount(1);
                    var gc2 = GC.CollectionCount(2);
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    long bytes = 0;
                    for (var i = 0; i < iterations; i++) bytes += Read(single);
                    var elapsed = Stopwatch.GetElapsedTime(start);
                    var allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        length,
                        wireLength = wire.Length,
                        inputSha256 = Convert.ToHexString(SHA256.HashData(expected)),
                        wireSha256 = Convert.ToHexString(SHA256.HashData(wire)),
                        mode = single ? "single-byte" : "bulk-256",
                        round,
                        decodedBytes = bytes,
                        nsPerStream = elapsed.TotalNanoseconds / iterations,
                        allocatedBytesPerStream = allocation / (double)iterations,
                        gc0 = GC.CollectionCount(0) - gc0,
                        gc1 = GC.CollectionCount(1) - gc1,
                        gc2 = GC.CollectionCount(2) - gc2
                    }));
                }
            }
        }
        return true;
    }
}
