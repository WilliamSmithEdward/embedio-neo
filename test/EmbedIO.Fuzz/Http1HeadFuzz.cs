using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

internal static class Http1HeadFuzz
{
    private sealed record Result(string State, int Consumed, string[] Lines);
    private static readonly Type ReaderType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http1HeadReader", true)
        ?? throw new InvalidOperationException("Missing head reader.");
    private static readonly MethodInfo Read = ReaderType.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing read operation.");
    private static readonly MethodInfo Reset = ReaderType.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing reset operation.");

    internal static bool Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--http1-head") return false;
        if (args.Length != 3) throw new ArgumentException("--http1-head seed iterations");
        var seed = int.Parse(args[1]);
        var iterations = int.Parse(args[2]);
        if (iterations < 1 || iterations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var random = new Random(seed);
        var corpus = new[]
        {
            "GET / HTTP/1.1\r\nHost: localhost\r\n\r\nbody\0\r\n",
            "\r\nPOST / HTTP/1.1\r\nX: éÿ\r\nContent-Length: 4\r\n\r\nbody",
            "GET / HTTP/1.1\r\nX: partial\r", "\r\n\r\n", "", "\n", "\rX",
        };
        var reader = Activator.CreateInstance(ReaderType) ?? throw new InvalidOperationException("Missing reader instance.");
        var iteration = 0;
        var input = Array.Empty<byte>();
        var chunks = new List<int>();
        try
        {
            for (; iteration < iterations; ++iteration)
            {
                var candidate = iteration % 127 == 0
                    ? "GET / HTTP/1.1\r\nX: " + new string('a', random.Next(32740, 32760)) + "\r\n\r\nbody"
                    : corpus[random.Next(corpus.Length)];
                var bytes = new List<byte>(Encoding.Latin1.GetBytes(candidate));
                for (var mutations = random.Next(12); mutations > 0; --mutations)
                {
                    var index = random.Next(bytes.Count + 1);
                    switch (random.Next(3))
                    {
                        case 0: bytes.Insert(index, (byte)random.Next(256)); break;
                        case 1: if (index < bytes.Count) bytes.RemoveAt(index); break;
                        default: if (index < bytes.Count) bytes[index] = (byte)random.Next(256); break;
                    }
                }
                input = bytes.ToArray();
                var expected = Reference(input);
                foreach (var fragmented in new[] { false, true })
                {
                    Reset.Invoke(reader, null);
                    chunks.Clear();
                    var actual = Parse(reader, input, fragmented ? random : null, chunks);
                    if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(expected))
                        throw new InvalidDataException($"Oracle mismatch: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(actual)}");
                    // A failed parser cannot be revived by another read, and a completed
                    // parser must not consume bytes from the next request without reset.
                    if (actual.State == "Rejected")
                    {
                        try { Invoke(reader, new byte[] { 13, 10 }, 0, 2); }
                        catch (InvalidDataException) { continue; }
                        throw new InvalidDataException("Rejected reader accepted additional input.");
                    }
                    if (actual.State == "Complete")
                    {
                        var terminal = Invoke(reader, new byte[] { 0, 13, 10 }, 0, 3);
                        if (terminal.State != "Complete" || terminal.Used != 0)
                            throw new InvalidDataException("Completed reader consumed additional input.");
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
                inputHex = Convert.ToHexString(input),
                chunks,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                assembly = typeof(EmbedIO.WebServer).Assembly.ManifestModule.ModuleVersionId,
            }));
            throw;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            target = "http1-head",
            seed,
            iterations,
            deliveries = iterations * 2,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            assembly = typeof(EmbedIO.WebServer).Assembly.ManifestModule.ModuleVersionId
        }));
        return true;
    }

    // Batch grammar oracle: no incremental state, transport or production helpers.
    // It checks framing only; arbitrary line contents are intentionally accepted here.
    private static Result Reference(byte[] input)
    {
        var text = Encoding.Latin1.GetString(input);
        var lines = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf("\r\n", position, StringComparison.Ordinal);
            var stop = end < 0 ? text.Length : end;
            var line = text.Substring(position, stop - position);
            var pendingCr = end < 0 && line.EndsWith("\r", StringComparison.Ordinal);
            var content = pendingCr ? line.Substring(0, line.Length - 1) : line;
            if (content.IndexOfAny(new[] { '\r', '\n' }) >= 0 || (end < 0 ? stop : end + 2) > 32768)
                return new Result("Rejected", 0, Array.Empty<string>());
            if (end < 0) return new Result("NeedMoreData", input.Length, lines.ToArray());
            position = end + 2;
            if (line.Length == 0)
            {
                if (lines.Count != 0) return new Result("Complete", position, lines.ToArray());
            }
            else lines.Add((lines.Count == 0 ? "RequestLine:" : "Header:") + line);
        }
        return new Result("NeedMoreData", input.Length, lines.ToArray());
    }

    private static Result Parse(object reader, byte[] input, Random? random, List<int> chunks)
    {
        var offset = 0;
        var lines = new List<string>();
        try
        {
            while (offset < input.Length)
            {
                var count = Math.Min(input.Length - offset, random == null ? input.Length : random.Next(1, 65));
                chunks.Add(count);
                var end = offset + count;
                while (offset < end)
                {
                    var item = Invoke(reader, input, offset, end - offset);
                    if (item.Used <= 0 || item.Used > end - offset) throw new InvalidOperationException("Reader failed to make bounded progress.");
                    offset += item.Used;
                    if (item.State == "Complete") return new Result("Complete", offset, lines.ToArray());
                    if (item.State == "NeedMoreData")
                    {
                        if (offset != end) throw new InvalidOperationException("Reader left partial input unconsumed.");
                    }
                    else lines.Add(item.State + ":" + item.Line);
                }
            }
            return new Result("NeedMoreData", offset, lines.ToArray());
        }
        catch (InvalidDataException)
        {
            return new Result("Rejected", 0, Array.Empty<string>());
        }
    }

    private static (string State, int Used, string? Line) Invoke(object reader, byte[] input, int offset, int count)
    {
        object?[] args = { input, offset, count, 0, null };
        object? result;
        try { result = Read.Invoke(reader, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        return (result?.ToString() ?? throw new InvalidOperationException("Missing result."),
            (int)(args[3] ?? throw new InvalidOperationException("Missing consumption.")), (string?)args[4]);
    }
}
