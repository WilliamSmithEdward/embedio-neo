using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Http2TransportFuzz
{
    private const int Maximum = 16384;
    private static readonly Type TransportType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2FrameTransport", true)
        ?? throw new InvalidOperationException("Missing HTTP/2 transport.");
    private static readonly MethodInfo Read = TransportType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing frame reader.");
    private sealed record Frame(byte Type, byte Flags, int Stream, string Payload);
    private sealed record Result(string State, int Consumed, Frame[] Frames);

    internal static async Task<bool> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--http2-transport") return false;
        if (args.Length != 3) throw new ArgumentException("--http2-transport seed iterations");
        var seed = int.Parse(args[1]);
        var iterations = int.Parse(args[2]);
        if (iterations < 1 || iterations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var random = new Random(seed);
        var input = Array.Empty<byte>();
        var chunks = new List<int>();
        var iteration = 0;
        try
        {
            for (; iteration < iterations; ++iteration)
            {
                input = Generate(random, iteration);
                var expected = Reference(input);
                foreach (var fragmented in new[] { false, true })
                {
                    chunks.Clear();
                    using var source = new FragmentStream(input, fragmented ? random : null, chunks);
                    using var transport = (IDisposable)(Activator.CreateInstance(TransportType,
                        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { source, Maximum }, null)
                        ?? throw new InvalidOperationException("Missing transport constructor."));
                    var frames = new List<Frame>();
                    var state = "End";
                    while (true)
                    {
                        object? frame;
                        try { frame = await ReadFrame(transport); }
                        catch (IOException error)
                        {
                            state = error is EndOfStreamException ? "Truncated" : "Oversized";
                            if (state == "Oversized" && Convert.ToUInt32(error.GetType().GetProperty("ErrorCode")?.GetValue(error)) != 6)
                                throw new InvalidDataException("Expected FRAME_SIZE_ERROR.", error);
                            break;
                        }
                        if (frame == null) break;
                        frames.Add(new Frame(Get<byte>(frame, "Type"), Get<byte>(frame, "Flags"),
                            Get<int>(frame, "StreamId"), Convert.ToHexString(Get<byte[]>(frame, "Payload"))));
                    }
                    var actual = new Result(state, checked((int)source.Position), frames.ToArray());
                    if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(expected))
                        throw new InvalidDataException($"Oracle mismatch: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(actual)}");
                    var consumed = source.Position;
                    if (state == "End")
                    {
                        if (await ReadFrame(transport) != null) throw new InvalidDataException("EOF became a frame.");
                    }
                    else
                    {
                        var rejected = false;
                        try { await ReadFrame(transport); }
                        catch (IOException) { rejected = true; }
                        if (!rejected) throw new InvalidDataException("Failed transport resumed parsing.");
                    }
                    if (source.Position != consumed) throw new InvalidDataException("Terminal read consumed more input.");
                    transport.Dispose();
                    if (!source.CanRead) throw new InvalidDataException("Transport disposed its borrowed source.");
                }
            }
        }
        catch (Exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                seed,
                iteration,
                inputHex = Convert.ToHexString(input),
                chunks,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                assembly = typeof(EmbedIO.WebServer).Assembly.ManifestModule.ModuleVersionId,
            }));
            throw;
        }
        Console.WriteLine($"Passed {iterations} HTTP/2 transport mutations with contiguous and fragmented reads; seed {seed}.");
        return true;
    }

    private static T Get<T>(object value, string property)
        => (T)(value.GetType().GetProperty(property)?.GetValue(value) ?? throw new InvalidDataException("Missing frame property."));

    private static async Task<object?> ReadFrame(object transport)
    {
        var task = (Task)(Read.Invoke(transport, new object[] { CancellationToken.None }) ?? throw new InvalidOperationException("Missing read task."));
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static byte[] Generate(Random random, int iteration)
    {
        var bytes = new List<byte>();
        var count = random.Next(1, 5);
        for (var frame = 0; frame < count; ++frame)
        {
            var length = iteration % 127 == 0 ? random.Next(Maximum - 1, Maximum + 2) : random.Next(33);
            var header = new byte[9];
            random.NextBytes(header);
            header[0] = (byte)(length >> 16); header[1] = (byte)(length >> 8); header[2] = (byte)length;
            bytes.AddRange(header);
            var payload = new byte[length]; random.NextBytes(payload); bytes.AddRange(payload);
        }
        if (random.Next(3) == 0)
        {
            var length = random.Next(bytes.Count + 1);
            bytes.RemoveRange(length, bytes.Count - length);
        }
        for (var mutations = random.Next(5); mutations > 0; --mutations)
        {
            var index = random.Next(bytes.Count + 1);
            switch (random.Next(3))
            {
                case 0: bytes.Insert(index, (byte)random.Next(256)); break;
                case 1: if (index < bytes.Count) bytes.RemoveAt(index); break;
                default: if (index < bytes.Count) bytes[index] = (byte)random.Next(256); break;
            }
        }
        return bytes.ToArray();
    }

    // Batch decoding is deliberately independent of the production stream reader.
    // Shape and connection-state rules are outside this framing-only oracle.
    private static Result Reference(byte[] input)
    {
        var frames = new List<Frame>();
        var offset = 0;
        while (offset < input.Length)
        {
            if (input.Length - offset < 9) return new Result("Truncated", input.Length, frames.ToArray());
            var length = (int)(BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset, 4)) >> 8);
            var type = input[offset + 3];
            var flags = input[offset + 4];
            var stream = (int)(BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset + 5, 4)) & int.MaxValue);
            offset += 9;
            if (length > Maximum) return new Result("Oversized", offset, frames.ToArray());
            if (input.Length - offset < length) return new Result("Truncated", input.Length, frames.ToArray());
            frames.Add(new Frame(type, flags, stream, Convert.ToHexString(input.AsSpan(offset, length))));
            offset += length;
        }
        return new Result("End", offset, frames.ToArray());
    }

    private sealed class FragmentStream(byte[] input, Random? random, List<int> chunks) : MemoryStream(input, false)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (random != null && count > 0) count = Math.Min(count, random.Next(1, 65));
            var read = Read(buffer, offset, count);
            chunks.Add(read);
            return Task.FromResult(read);
        }
    }
}
