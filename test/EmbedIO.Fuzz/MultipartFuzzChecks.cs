using System;
using System.Globalization;
using System.IO;
using System.Text;
using EmbedIO;

internal static class MultipartFuzzChecks
{
    internal static void Check(ReadOnlySpan<byte> data)
    {
        // Bound each iteration independently of the fuzzer's input-size settings.
        var bytes = data.Slice(0, Math.Min(data.Length, 65536)).ToArray();
        var ordinary = Parse(bytes, bytes.Length + 1);
        var fragmented = Parse(bytes, 1 + (bytes.Length == 0 ? 0 : bytes[0] % 31));
        if (ordinary != fragmented)
            throw new InvalidOperationException("Multipart parsing depends on transport fragmentation.");

        CheckRoundTrip(bytes);
    }

    internal static bool CheckText(string text)
    {
        Check(Encoding.UTF8.GetBytes(text));
        return true;
    }

    private static string Parse(byte[] bytes, int chunkSize)
    {
        using var input = new FragmentedStream(bytes, chunkSize);
        using var reader = new MultipartReader(input, "fuzz-boundary", new MultipartOptions
        {
            MaxTotalBytes = 65536,
            MaxFileBytes = 1024,
            MaxFieldBytes = 256,
            MaxParts = 16,
            MaxFiles = 8,
            MaxFields = 8,
            MaxHeaderBytes = 4096,
            MaxHeaders = 16,
        });
        var result = new StringBuilder();
        try
        {
            MultipartSection? section;
            while ((section = reader.ReadNextSectionAsync().GetAwaiter().GetResult()) != null)
            {
                using var body = new MemoryStream();
                section.Body.CopyToAsync(body).GetAwaiter().GetResult();
                result.Append(section.Name.Length).Append(':').Append(section.Name)
                    .Append('|').Append(section.FileName).Append('|').Append(section.ContentType)
                    .Append('|').Append(Convert.ToBase64String(body.ToArray())).Append(';');
            }

            return "ok:" + result;
        }
        catch (HttpException exception) when (exception.StatusCode is 400 or 413)
        {
            // Rejecting malformed/oversized input is expected. All other failures escape.
            return "error:" + exception.StatusCode;
        }
    }

    private static void CheckRoundTrip(byte[] payload)
    {
        // Select a boundary absent from the payload, without modifying arbitrary binary data.
        var boundary = "roundtrip";
        var representation = Encoding.Latin1.GetString(payload);
        var attempt = 0;
        while (representation.Contains(boundary, StringComparison.Ordinal))
            boundary = "roundtrip-" + (++attempt).ToString(CultureInfo.InvariantCulture);

        using var encoded = new MemoryStream();
        var prefix = Encoding.ASCII.GetBytes("--" + boundary
            + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"data.bin\""
            + "\r\nContent-Type: application/octet-stream\r\n\r\n");
        encoded.Write(prefix, 0, prefix.Length);
        encoded.Write(payload, 0, payload.Length);
        var suffix = Encoding.ASCII.GetBytes("\r\n--" + boundary
            + "\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\ndone\r\n--"
            + boundary + "--\r\n");
        encoded.Write(suffix, 0, suffix.Length);
        using var input = new FragmentedStream(encoded.ToArray(), 1 + payload.Length % 37);
        using var reader = new MultipartReader(input, boundary);
        var section = reader.ReadNextSectionAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Multipart file disappeared.");
        using var decoded = new MemoryStream();
        section.Body.CopyToAsync(decoded).GetAwaiter().GetResult();
        if (section.Name != "file" || section.FileName != "data.bin"
            || section.ContentType != "application/octet-stream"
            || !payload.AsSpan().SequenceEqual(decoded.ToArray()))
            throw new InvalidOperationException("Multipart binary round trip differs from the supplied payload.");

        var field = reader.ReadNextSectionAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Multipart trailing field disappeared.");
        using var text = new StreamReader(field.Body, Encoding.UTF8, false, 128, true);
        if (field.Name != "note" || field.FileName != null
            || text.ReadToEndAsync().GetAwaiter().GetResult() != "done"
            || reader.ReadNextSectionAsync().GetAwaiter().GetResult() != null)
            throw new InvalidOperationException("Multipart field after a binary file changed.");

        // A body lacking its closing delimiter must never be accepted, even if
        // every file byte was delivered before EOF.
        using var truncatedInput = new FragmentedStream(encoded.ToArray().AsSpan(0, prefix.Length + payload.Length).ToArray(), 7);
        using var truncatedReader = new MultipartReader(truncatedInput, boundary);
        try
        {
            while (truncatedReader.ReadNextSectionAsync().GetAwaiter().GetResult() != null) { }
        }
        catch (HttpException exception) when (exception.StatusCode == 400)
        {
            return;
        }

        throw new InvalidOperationException("Multipart input without a closing delimiter was accepted.");
    }

    private sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, chunkSize));

        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count,
            System.Threading.CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(count, chunkSize), cancellationToken);

        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer,
            System.Threading.CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, chunkSize)), cancellationToken);
    }
}
