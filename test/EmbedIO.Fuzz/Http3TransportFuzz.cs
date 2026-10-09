using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Http3TransportFuzz
{
    private const int Maximum = 128;
    private static readonly Type ReaderType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3FrameReader", true)
        ?? throw new InvalidOperationException("Missing HTTP/3 reader.");
    private sealed record Frame(long Type, long Length, string Payload);
    private sealed record Result(string State, int Consumed, long Remaining, Frame[] Frames);

    internal static async Task<bool> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--http3-transport") return false;
        if (args.Length != 3) throw new ArgumentException("--http3-transport seed iterations");
        var seed = int.Parse(args[1]);
        var iterations = int.Parse(args[2]);
        if (iterations < 1 || iterations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var random = new Random(seed);
        var input = Array.Empty<byte>();
        var chunks = new List<int>();
        var iteration = 0;
        var mode = 0;
        try
        {
            for (; iteration < iterations; ++iteration)
            {
                input = Generate(random, iteration);
                for (mode = 0; mode < 3; ++mode)
                {
                    var expected = Reference(input, mode);
                    foreach (var fragmented in new[] { false, true })
                    {
                        chunks.Clear();
                        using var source = new FragmentStream(input, fragmented ? random : null, chunks);
                        var reader = Activator.CreateInstance(ReaderType, BindingFlags.Instance | BindingFlags.NonPublic,
                            null, new object[] { source }, null) ?? throw new InvalidOperationException("Missing reader constructor.");
                        var frames = new List<Frame>();
                        var state = "End";
                        while (true)
                        {
                            try
                            {
                                var header = await Invoke(reader, "ReadHeaderAsync", CancellationToken.None);
                                if (header == null) break;
                                var type = Get<long>(header, "Type");
                                var length = Get<long>(header, "Length");
                                using var payload = new MemoryStream();
                                if (mode == 0)
                                {
                                    var buffer = new byte[31];
                                    while (true)
                                    {
                                        var count = (int)(await Invoke(reader, "ReadPayloadAsync", buffer, 0, buffer.Length, CancellationToken.None)
                                            ?? throw new InvalidDataException("Missing payload count."));
                                        if (count == 0) break;
                                        payload.Write(buffer, 0, count);
                                    }
                                }
                                else if (mode == 1)
                                {
                                    var bytes = (byte[])(await Invoke(reader, "ReadBufferedPayloadAsync", Maximum, CancellationToken.None)
                                        ?? throw new InvalidDataException("Missing buffered payload."));
                                    payload.Write(bytes, 0, bytes.Length);
                                }
                                else await Invoke(reader, "SkipPayloadAsync", CancellationToken.None);
                                frames.Add(new Frame(type, length, Convert.ToHexString(payload.ToArray())));
                            }
                            catch (IOException error)
                            {
                                var code = Convert.ToInt64(error.GetType().GetProperty("ErrorCode")?.GetValue(error));
                                state = code switch { 0x106 => "Truncated", 0x107 => "Oversized", _ => throw new InvalidDataException("Unexpected HTTP/3 error.", error) };
                                break;
                            }
                        }
                        var actual = new Result(state, checked((int)source.Position), Get<long>(reader, "Remaining"), frames.ToArray());
                        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(expected))
                            throw new InvalidDataException($"Oracle mismatch: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(actual)}");
                        var consumed = source.Position;
                        if (state == "End")
                        {
                            if (await Invoke(reader, "ReadHeaderAsync", CancellationToken.None) != null)
                                throw new InvalidDataException("EOF became a frame.");
                        }
                        else
                        {
                            var rejected = false;
                            try { await Invoke(reader, "ReadHeaderAsync", CancellationToken.None); }
                            catch (IOException) { rejected = true; }
                            if (!rejected) throw new InvalidDataException("Failed reader resumed parsing.");
                        }
                        if (source.Position != consumed) throw new InvalidDataException("Terminal reader consumed more input.");
                    }
                }
            }
        }
        catch (Exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                seed,
                iteration,
                mode,
                inputHex = Convert.ToHexString(input),
                chunks,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                assembly = typeof(EmbedIO.WebServer).Assembly.ManifestModule.ModuleVersionId,
            }));
            throw;
        }
        Console.WriteLine($"Passed {iterations} HTTP/3 transport mutations across streamed, buffered and skipped payloads, contiguous and fragmented reads; seed {seed}.");
        return true;
    }

    private static T Get<T>(object value, string property)
        => (T)(value.GetType().GetProperty(property)?.GetValue(value) ?? throw new InvalidDataException("Missing reader property."));

    private static async Task<object?> Invoke(object reader, string method, params object[] args)
    {
        var operation = ReaderType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing reader operation.");
        var task = (Task)(operation.Invoke(reader, args) ?? throw new InvalidOperationException("Missing reader task."));
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static byte[] Generate(Random random, int iteration)
    {
        var bytes = new List<byte>();
        var frames = random.Next(1, 5);
        for (var index = 0; index < frames; ++index)
        {
            var typeWidth = 1 << random.Next(4);
            AddInteger(bytes, random.NextInt64(1L << (typeWidth * 8 - 2)), typeWidth);
            var length = iteration % 127 == 0 ? (1L << 62) - 1 : iteration % 17 == 0 ? random.Next(127, 130) : random.Next(33);
            var width = length > 16383 ? 8 : length > 63 ? 1 << random.Next(1, 4) : 1 << random.Next(4);
            AddInteger(bytes, length, width);
            if (length > 129) break;
            var payload = new byte[(int)length]; random.NextBytes(payload); bytes.AddRange(payload);
        }
        if (random.Next(3) == 0)
        {
            var length = random.Next(bytes.Count + 1);
            bytes.RemoveRange(length, bytes.Count - length);
        }
        for (var mutations = random.Next(4); mutations > 0; --mutations)
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

    private static void AddInteger(List<byte> bytes, long value, int width)
    {
        var encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        encoded[8 - width] |= (byte)(width == 1 ? 0 : width == 2 ? 64 : width == 4 ? 128 : 192);
        bytes.AddRange(encoded.AsSpan(8 - width).ToArray());
    }

    private static bool Integer(byte[] input, ref int offset, out long value)
    {
        value = 0;
        if (offset == input.Length) return false;
        var width = new[] { 1, 2, 4, 8 }[input[offset] / 64];
        if (input.Length - offset < width) { offset = input.Length; return false; }
        var padded = new byte[8];
        input.AsSpan(offset, width).CopyTo(padded.AsSpan(8 - width));
        value = (long)(BinaryPrimitives.ReadUInt64BigEndian(padded) & ((1UL << (width * 8 - 2)) - 1));
        offset += width;
        return true;
    }

    private static Result Reference(byte[] input, int mode)
    {
        var frames = new List<Frame>();
        var offset = 0;
        while (offset < input.Length)
        {
            if (!Integer(input, ref offset, out var type) || !Integer(input, ref offset, out var length))
                return new Result("Truncated", offset, 0, frames.ToArray());
            if (mode == 1 && length > Maximum) return new Result("Oversized", offset, length, frames.ToArray());
            if (length > input.Length - offset)
                return new Result("Truncated", input.Length, length - (input.Length - offset), frames.ToArray());
            frames.Add(new Frame(type, length, mode == 2 ? "" : Convert.ToHexString(input.AsSpan(offset, (int)length))));
            offset += (int)length;
        }
        return new Result("End", offset, 0, frames.ToArray());
    }

    private sealed class FragmentStream(byte[] input, Random? random, List<int> chunks) : MemoryStream(input, false)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (random != null && count > 0) count = Math.Min(count, random.Next(1, 17));
            var read = Read(buffer, offset, count);
            chunks.Add(read);
            return Task.FromResult(read);
        }
    }
}
