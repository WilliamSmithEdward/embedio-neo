using System.Net.Sockets;
using System.Text;

// Independent HTTP/1.1 response reader written from RFC 9112 Section 6.3; it shares
// no code with EmbedIO. It accepts only CRLF framing and reports every deviation.
internal sealed record Http1Response(int Status, string Reason, string Version, List<KeyValuePair<string, string>> Headers, byte[] Body, bool Chunked)
{
    internal string? Header(string name) => Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(h => h.Value).DefaultIfEmpty(null).Aggregate((a, b) => a == null ? b : b == null ? a : a + ", " + b);

    internal int Count(string name) => Headers.Count(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal string BodyText => Encoding.Latin1.GetString(Body);
}

internal sealed class RawHttp1 : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[65536];
    private int _start;
    private int _end;

    internal RawHttp1(string host, int port, TimeSpan timeout)
    {
        _client = new TcpClient { NoDelay = true, ReceiveTimeout = (int)timeout.TotalMilliseconds, SendTimeout = (int)timeout.TotalMilliseconds };
        _client.Connect(host, port);
        _stream = _client.GetStream();
        Authority = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Requests are written with the placeholder line "Host: a"; it becomes the real authority.
    internal string Authority { get; }

    internal static string Bind(string text, string authority) => text.Replace("\nHost: a\r\n", "\nHost: " + authority + "\r\n", StringComparison.Ordinal);

    internal static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    internal void Send(byte[] bytes) => _stream.Write(bytes);

    internal void Send(string text) => Send(Ascii(Bind(text, Authority)));

    // Sends in pieces; chunk sizes come from the caller so failures are replayable.
    internal void SendFragmented(byte[] bytes, IReadOnlyList<int> chunks, int delayMs)
    {
        var offset = 0;
        foreach (var size in chunks)
        {
            if (offset >= bytes.Length) break;
            var count = Math.Min(size, bytes.Length - offset);
            _stream.Write(bytes, offset, count);
            _stream.Flush();
            offset += count;
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
        if (offset < bytes.Length) _stream.Write(bytes, offset, bytes.Length - offset);
    }

    internal void ShutdownSend() => _client.Client.Shutdown(SocketShutdown.Send);

    // Closes with RST so the server observes an abortive disconnect.
    internal void Abort()
    {
        _client.Client.LingerState = new LingerOption(true, 0);
        _client.Close();
    }

    // True when the peer has closed (EOF) or reset within the timeout, with no extra bytes.
    internal bool WaitClosed(out int extraBytes)
    {
        extraBytes = _end - _start;
        try
        {
            while (true)
            {
                var read = _stream.Read(_buffer, 0, _buffer.Length);
                if (read == 0) return extraBytes == 0;
                extraBytes += read;
            }
        }
        catch (IOException error) when (error.InnerException is SocketException socket)
        {
            return socket.SocketErrorCode != SocketError.TimedOut && extraBytes == 0;
        }
    }

    private bool Fill()
    {
        if (_start > 0 && _start == _end) { _start = _end = 0; }
        if (_end == _buffer.Length)
        {
            if (_start == 0) throw new InvalidDataException("Response line or header exceeds 64 KiB.");
            Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        _end += read;
        return read > 0;
    }

    private string ReadLine()
    {
        while (true)
        {
            for (var i = _start; i + 1 < _end; i++)
            {
                if (_buffer[i] == '\n') throw new InvalidDataException("Bare LF in response framing.");
                if (_buffer[i] == '\r')
                {
                    if (_buffer[i + 1] != '\n') throw new InvalidDataException("Bare CR in response framing.");
                    var line = Encoding.Latin1.GetString(_buffer, _start, i - _start);
                    _start = i + 2;
                    return line;
                }
            }
            if (!Fill()) throw new EndOfStreamException("Connection closed inside response head.");
        }
    }

    private byte[] ReadExact(long count)
    {
        if (count > 256L << 20) throw new InvalidDataException("Response body exceeds the 256 MiB driver limit.");
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            if (_start == _end && !Fill()) throw new EndOfStreamException($"Connection closed after {offset} of {count} body bytes.");
            var take = (int)Math.Min(count - offset, _end - _start);
            Array.Copy(_buffer, _start, result, offset, take);
            _start += take;
            offset += take;
        }
        return result;
    }

    private byte[] ReadToEnd()
    {
        using var body = new MemoryStream();
        while (true)
        {
            body.Write(_buffer, _start, _end - _start);
            _start = _end = 0;
            if (!Fill()) return body.ToArray();
        }
    }

    // Reads one final response, returning any interim 1xx responses separately.
    internal Http1Response Read(bool headRequest, List<Http1Response>? interim = null)
    {
        while (true)
        {
            var response = ReadOne(headRequest);
            if (response.Status >= 200 || response.Status == 101) return response;
            interim?.Add(response);
        }
    }

    private Http1Response ReadOne(bool headRequest)
    {
        var statusLine = ReadLine();
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || parts[1].Length != 3 || !int.TryParse(parts[1], out var status))
            throw new InvalidDataException($"Malformed status line: {statusLine}");
        var headers = new List<KeyValuePair<string, string>>();
        while (true)
        {
            var line = ReadLine();
            if (line.Length == 0) break;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || line[colon - 1] == ' ' || line[colon - 1] == '\t' || line[0] == ' ' || line[0] == '\t')
                throw new InvalidDataException($"Malformed response field line: {line}");
            headers.Add(new(line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
        }
        var response = new Http1Response(status, parts.Length > 2 ? parts[2] : string.Empty, parts[0], headers, Array.Empty<byte>(), false);
        if (headRequest || status < 200 || status == 204 || status == 304) return response;
        var transfer = response.Header("Transfer-Encoding");
        if (transfer != null)
        {
            if (!transfer.Split(',').Last().Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase))
                return response with { Body = ReadToEnd() };
            return response with { Body = ReadChunked(), Chunked = true };
        }
        var length = response.Header("Content-Length");
        if (length != null)
        {
            var values = length.Split(',').Select(v => v.Trim()).Distinct().ToArray();
            if (values.Length != 1 || !long.TryParse(values[0], System.Globalization.NumberStyles.None, null, out var count))
                throw new InvalidDataException($"Invalid response Content-Length: {length}");
            return response with { Body = ReadExact(count) };
        }
        return response with { Body = ReadToEnd() };
    }

    private byte[] ReadChunked()
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = ReadLine();
            var semicolon = line.IndexOf(';', StringComparison.Ordinal);
            var size = (semicolon < 0 ? line : line[..semicolon]).Trim();
            if (size.Length == 0 || size.Length > 15 || !long.TryParse(size, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var count))
                throw new InvalidDataException($"Malformed chunk size: {line}");
            if (count == 0)
            {
                while (ReadLine().Length != 0) { }
                return body.ToArray();
            }
            body.Write(ReadExact(count));
            if (ReadLine().Length != 0) throw new InvalidDataException("Chunk data not followed by CRLF.");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
