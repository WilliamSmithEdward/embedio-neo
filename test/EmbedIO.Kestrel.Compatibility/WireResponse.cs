using System.Globalization;
using System.Text;

internal sealed record WireResponse(int Status, string Body)
{
    internal static async Task<WireResponse> ReadAsync(Stream stream, CancellationToken token)
    {
        var head = new StringBuilder();
        var one = new byte[1];

        while (
            !head
                .ToString()
                .EndsWith("\r\n\r\n", StringComparison.Ordinal)
        )
        {
            if (head.Length > 32768 || await stream.ReadAsync(one, token) == 0)
                throw new IOException("Incomplete response headers.");

            head.Append((char)one[0]);
        }

        var lines = head
            .ToString()
            .Split("\r\n", StringSplitOptions.None);
        var status = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        var length = lines.FirstOrDefault(
            line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
        );

        if (length != null)
        {
            var bytes = new byte[int.Parse(
                length[(length.IndexOf(':', StringComparison.Ordinal) + 1)..],
                CultureInfo.InvariantCulture
            )];
            await stream.ReadExactlyAsync(bytes, token);
            return new(status, Encoding.UTF8.GetString(bytes));
        }

        if (!lines.Any(line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Expected an explicitly framed test response.");

        using var body = new MemoryStream();

        while (true)
        {
            var line = new StringBuilder();

            while (
                !line
                    .ToString()
                    .EndsWith("\r\n", StringComparison.Ordinal)
            )
            {
                if (line.Length > 128 || await stream.ReadAsync(one, token) == 0)
                    throw new IOException("Incomplete chunk.");

                line.Append((char)one[0]);
            }

            var count = int.Parse(
                line
                    .ToString()
                    .Trim(),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture
            );
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, token);
            body.Write(bytes);
            var ending = new byte[2];
            await stream.ReadExactlyAsync(ending, token);

            if (
                !ending
                    .AsSpan()
                    .SequenceEqual("\r\n"u8)
            ) throw new IOException("Invalid chunk ending.");

            if (count == 0) return new(status, Encoding.UTF8.GetString(body.ToArray()));
        }
    }
}
