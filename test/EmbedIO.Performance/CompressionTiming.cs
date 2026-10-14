using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using EmbedIO.PlatformTests;

// --compression-timing compares the response encoders on a JSON document of 1 KiB, 64 KiB and
// 1 MiB: gzip at zlib's default level (EmbedIO's dynamic gzip), gzip Fastest (the ASP.NET Core
// default) and Brotli qualities 1 to 5. Quality 1 is CompressionLevel.Fastest and quality 4 is
// what BrotliStream uses when given no level. Each operation creates the encoder, writes the
// document in one call and disposes it, as a compressed response does. Rounds interleave the
// encoders in a rotating order; the report gives the median of the rounds. --rounds sets their
// number (default 5). Managed allocations only: the native encoder state is not counted.
internal static class CompressionTiming
{
    private static readonly int[] Lengths = [1024, 65536, 1048576];

    private static readonly (string Name, Func<Stream, Stream> Create)[] Encoders =
    [
        ("gzip default", output => new GZipStream(output, CompressionMode.Compress, leaveOpen: true)),
        ("gzip Fastest", output => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)),
        ("br q1 Fastest", output => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)),
        ("br q2", output => new BrotliStream(output, new BrotliCompressionOptions { Quality = 2 }, leaveOpen: true)),
        ("br q3", output => new BrotliStream(output, new BrotliCompressionOptions { Quality = 3 }, leaveOpen: true)),
        ("br q4 default", output => new BrotliStream(output, CompressionMode.Compress, leaveOpen: true)),
        ("br q5", output => new BrotliStream(output, new BrotliCompressionOptions { Quality = 5 }, leaveOpen: true)),
    ];

    internal static bool Run(string[] args)
    {
        if (!args.Contains("--compression-timing", StringComparer.Ordinal)) return false;
        var index = Array.IndexOf(args, "--rounds");
        var rounds = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1], CultureInfo.InvariantCulture) : 5;
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} logical CPUs; {rounds} rounds");
        using var process = Process.GetCurrentProcess();
        foreach (var length in Lengths)
        {
            var document = CompressibleJson.Create(length);
            var sizes = Encoders.Select(encoder => Verify(document, encoder.Create, encoder.Name)).ToArray();
            var wall = Encoders.Select(_ => new List<double>()).ToArray();
            var cpu = Encoders.Select(_ => new List<double>()).ToArray();
            var allocated = Encoders.Select(_ => new List<double>()).ToArray();
            for (var round = 0; round < rounds; round++)
            {
                for (var step = 0; step < Encoders.Length; step++)
                {
                    var which = (round + step) % Encoders.Length;
                    var sample = Measure(document, Encoders[which].Create, sizes[which], process, round == 0);
                    wall[which].Add(sample.WallNanoseconds);
                    cpu[which].Add(sample.CpuNanoseconds);
                    allocated[which].Add(sample.AllocatedBytes);
                }
            }

            var newLine = Environment.NewLine;
            Console.WriteLine($"{newLine}JSON {length} bytes{newLine}| Encoder | Bytes | Ratio | us/op (wall) | us/op (process CPU) | MB/s | Managed B/op |{newLine}| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            for (var which = 0; which < Encoders.Length; which++)
            {
                var micros = Median(wall[which]) / 1000;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {Encoders[which].Name} | {sizes[which]} | {(double)sizes[which] / length:F3} | {micros:F2} | {Median(cpu[which]) / 1000:F2} | {length / micros:F0} | {Median(allocated[which]):F0} |"));
            }
        }

        return true;
    }

    // Compresses once into memory, decodes it back and checks every byte. Returns the compressed size.
    private static long Verify(byte[] document, Func<Stream, Stream> create, string name)
    {
        using var compressed = new MemoryStream();
        using (var encoder = create(compressed)) encoder.Write(document, 0, document.Length);
        compressed.Position = 0;
        using Stream decoder = name.StartsWith("gzip", StringComparison.Ordinal)
            ? new GZipStream(compressed, CompressionMode.Decompress)
            : new BrotliStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        decoder.CopyTo(decoded);
        if (!decoded.ToArray().AsSpan().SequenceEqual(document)) throw new InvalidOperationException(name + " did not round-trip.");
        return compressed.Length;
    }

    // Runs one encoder for at least 400 ms and 5 operations (after 300 ms of warmup in the first
    // round) and returns per-operation wall time, process CPU time and managed allocations.
    private static (double WallNanoseconds, double CpuNanoseconds, double AllocatedBytes) Measure(
        byte[] document, Func<Stream, Stream> create, long expectedSize, Process process, bool warm)
    {
        var sink = new CountingSink();
        if (warm)
        {
            var warming = Stopwatch.StartNew();
            while (warming.ElapsedMilliseconds < 300) Compress(document, create, sink, expectedSize);
        }

        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.StartNew();
        var operations = 0;
        while (started.ElapsedMilliseconds < 400 || operations < 5)
        {
            Compress(document, create, sink, expectedSize);
            operations++;
        }

        var elapsed = started.Elapsed;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        process.Refresh();
        var cpuTime = process.TotalProcessorTime - cpuBefore;
        return (elapsed.TotalNanoseconds / operations, cpuTime.TotalNanoseconds / operations, (double)allocated / operations);
    }

    private static void Compress(byte[] document, Func<Stream, Stream> create, CountingSink sink, long expectedSize)
    {
        sink.Count = 0;
        using (var encoder = create(sink)) encoder.Write(document, 0, document.Length);
        if (sink.Count != expectedSize) throw new InvalidOperationException($"Compressed size changed: {sink.Count}, expected {expectedSize}.");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    // Discards what the encoder writes and counts it, so only the encoder's own work is timed.
    private sealed class CountingSink : Stream
    {
        internal long Count;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Count;

        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Count += count;

        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
    }
}
