using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

internal sealed record LoadSettings(
    Protocol Protocol,
    bool Tls,
    int Port,
    BenchmarkRoute Route,
    int UploadBytes,
    int Connections,
    int Streams,
    int Pipeline,
    int RequestsPerConnection,
    string CertificateThumbprint)
{
    internal bool IsUpload => Route.Kind == RouteKind.Upload;
}

// Closed-loop load generator. Every response status, version, length and body byte
// is validated; the first failure aborts the run and is reported, never retried.
internal static class LoadClient
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        var protocol = Enum.Parse<Protocol>(options.Required("--protocol"), ignoreCase: true);
        if (!BenchmarkRoute.TryParse(options.Required("--path"), out var route)) throw new ArgumentException("Unknown benchmark path.");
        var settings = new LoadSettings(
            protocol,
            options.Has("--tls") || protocol == Protocol.Http3,
            options.Integer("--port", 0),
            route,
            options.Integer("--upload-bytes", 0),
            options.Integer("--connections", 16),
            options.Integer("--streams", 1),
            options.Integer("--pipeline", 1),
            options.Integer("--requests-per-connection", 0),
            options.Text("--certificate-thumbprint", string.Empty));
        if (settings.Connections < 1 || settings.Streams < 1 || settings.Pipeline < 1 || settings.RequestsPerConnection < 0)
            throw new ArgumentException("Connections, streams and pipeline must be positive.");
        if (settings.Protocol != Protocol.Http1 && settings.Pipeline != 1) throw new ArgumentException("Pipelining applies to HTTP/1.1 only.");
        if (settings.Protocol == Protocol.Http1 && settings.Streams != 1) throw new ArgumentException("HTTP/1.1 uses one stream per connection.");
        if (settings.IsUpload && settings.Pipeline != 1) throw new ArgumentException("Uploads are not pipelined.");
        if (settings.RequestsPerConnection % settings.Pipeline != 0) throw new ArgumentException("Requests per connection must be a multiple of the pipeline depth.");
        Payloads.Prepare(Math.Max(settings.UploadBytes, route.ResponseLength));

        var warmup = TimeSpan.FromSeconds(options.Number("--warmup", 5));
        var duration = TimeSpan.FromSeconds(options.Number("--duration", 15));
        using var abort = new CancellationTokenSource(warmup + duration + TimeSpan.FromMinutes(2));
        var warm = await ExerciseAsync(settings, warmup, abort.Token).ConfigureAwait(false);
        Control.Write("WARM " + JsonSerializer.Serialize(new { requests = warm.Requests, error = warm.Error }));
        if (warm.Error is not null) return 2;
        await Control.ExpectAsync("start", abort.Token).ConfigureAwait(false);

        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var result = await ExerciseAsync(settings, duration, abort.Token).ConfigureAwait(false);
        process.Refresh();
        var clientCpu = (process.TotalProcessorTime - cpu).TotalSeconds;
        var clientAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Control.Write("DONE");
        await Control.ExpectAsync("report", abort.Token).ConfigureAwait(false);
        var latency = result.Latency;
        Control.Write(JsonSerializer.Serialize(new
        {
            requests = result.Requests,
            error = result.Error,
            measuredSeconds = result.Seconds,
            requestsPerSecond = result.Requests / result.Seconds,
            responseBytes = result.ResponseBytes,
            requestBodyBytes = result.RequestBodyBytes,
            connectionsOpened = result.ConnectionsOpened,
            serverInitiatedCloses = result.ServerCloses,
            unansweredPipelinedRequests = result.UnansweredRequests,
            latencyCount = latency.Count,
            p50Milliseconds = latency.PercentileMilliseconds(0.50),
            p90Milliseconds = latency.PercentileMilliseconds(0.90),
            p95Milliseconds = latency.PercentileMilliseconds(0.95),
            p99Milliseconds = latency.PercentileMilliseconds(0.99),
            p999Milliseconds = latency.PercentileMilliseconds(0.999),
            maxMilliseconds = latency.MaxUnits / 10_000.0,
            clientCpuSeconds = clientCpu,
            clientAllocatedBytes = clientAllocated,
            clientProcessorCount = Environment.ProcessorCount,
            histogramUnits = "100ns upper bounds",
            histogram = latency.NonEmptyBuckets().ToArray(),
        }));
        return result.Error is null ? 0 : 3;
    }

    private static async Task<RunResult> ExerciseAsync(LoadSettings settings, TimeSpan duration, CancellationToken cancellation)
    {
        var state = new RunState(duration, cancellation);
        var workers = Enumerable.Range(0, settings.Connections).Select(_ => settings.Protocol == Protocol.Http1
            ? Http1Worker.RunAsync(settings, state)
            : MultiplexedWorker.RunAsync(settings, state)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var latency = new LatencyHistogram();
        foreach (var histogram in state.Histograms) latency.Add(histogram);
        return new RunResult(state.Requests, state.Clock.Elapsed.TotalSeconds, latency, state.ResponseBytes, state.RequestBodyBytes,
            state.ConnectionsOpened, state.ServerCloses, state.UnansweredRequests, state.Error);
    }

    internal static RemoteCertificateValidationCallback Pinned(string thumbprint)
        => (_, certificate, _, _) => certificate is not null
            && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);

    private sealed record RunResult(long Requests, double Seconds, LatencyHistogram Latency, long ResponseBytes, long RequestBodyBytes,
        long ConnectionsOpened, long ServerCloses, long UnansweredRequests, string? Error);
}

internal sealed class RunState(TimeSpan duration, CancellationToken cancellation)
{
    private readonly CancellationTokenSource _failed = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    private readonly List<LatencyHistogram> _histograms = [];
    private long _requests;
    private long _responseBytes;
    private long _requestBodyBytes;
    private long _connectionsOpened;
    private long _serverCloses;
    private long _unanswered;
    private string? _error;

    internal Stopwatch Clock { get; } = Stopwatch.StartNew();

    internal CancellationToken Token => _failed.Token;

    internal bool Running => Clock.Elapsed < duration && !_failed.IsCancellationRequested;

    internal long Requests => Interlocked.Read(ref _requests);

    internal long ResponseBytes => Interlocked.Read(ref _responseBytes);

    internal long RequestBodyBytes => Interlocked.Read(ref _requestBodyBytes);

    internal long ConnectionsOpened => Interlocked.Read(ref _connectionsOpened);

    internal long ServerCloses => Interlocked.Read(ref _serverCloses);

    internal string? Error => Volatile.Read(ref _error);

    internal IReadOnlyList<LatencyHistogram> Histograms => _histograms;

    internal LatencyHistogram CreateHistogram()
    {
        var histogram = new LatencyHistogram();
        lock (_histograms) _histograms.Add(histogram);
        return histogram;
    }

    internal void Completed(long responseBytes, long requestBodyBytes)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _responseBytes, responseBytes);
        Interlocked.Add(ref _requestBodyBytes, requestBodyBytes);
    }

    internal void Opened() => Interlocked.Increment(ref _connectionsOpened);

    internal long UnansweredRequests => Interlocked.Read(ref _unanswered);

    internal void ServerClosed(int unanswered)
    {
        Interlocked.Increment(ref _serverCloses);
        Interlocked.Add(ref _unanswered, unanswered);
    }

    // Records the first failure and stops every worker; nothing is retried.
    internal void Fail(Exception exception)
    {
        if (exception is OperationCanceledException && _failed.IsCancellationRequested) return;
        Interlocked.CompareExchange(ref _error, Describe(exception), null);
        _failed.Cancel();
    }

    // The whole inner chain: HTTP/2 aborts carry the protocol or socket cause inside.
    private static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length != 0) text.Append(" ---> ");
            text.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (current is HttpProtocolException protocol) text.Append(" (HTTP/2 error code ").Append(protocol.ErrorCode).Append(')');
            if (current is SocketException socket) text.Append(" (socket ").Append(socket.SocketErrorCode).Append(')');
        }
        return text.ToString();
    }
}

internal static class Http1Worker
{
    internal static async Task RunAsync(LoadSettings settings, RunState state)
    {
        var histogram = state.CreateHistogram();
        var expected = settings.IsUpload ? Payloads.UploadAcknowledgement(settings.UploadBytes) : Payloads.Get(settings.Route.ResponseLength);
        if (settings.Route.Kind == RouteKind.Plaintext) expected = Payloads.Plaintext;
        var host = "localhost:" + settings.Port.ToString(CultureInfo.InvariantCulture);
        var head = settings.IsUpload
            ? $"POST /upload HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/octet-stream\r\nContent-Length: {settings.UploadBytes}\r\n\r\n"
            : $"GET {settings.Route.Path} HTTP/1.1\r\nHost: {host}\r\n\r\n";
        var body = settings.IsUpload ? Payloads.Get(settings.UploadBytes) : [];
        var single = Encoding.ASCII.GetBytes(head).Concat(body).ToArray();
        var closing = Encoding.ASCII.GetBytes(head.Replace("\r\n\r\n", "\r\nConnection: close\r\n\r\n", StringComparison.Ordinal)).Concat(body).ToArray();
        // A whole pipelined batch goes out in one write (one TLS record sequence).
        var batch = Enumerable.Repeat(single, settings.Pipeline).SelectMany(bytes => bytes).ToArray();
        var finalBatch = Enumerable.Repeat(single, settings.Pipeline - 1).Append(closing).SelectMany(bytes => bytes).ToArray();
        try
        {
            while (state.Running)
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, settings.Port, state.Token).ConfigureAwait(false);
                state.Opened();
                await using var network = new NetworkStream(tcp, ownsSocket: false);
                Stream stream = network;
                SslStream? tls = null;
                if (settings.Tls)
                {
                    tls = new SslStream(network, leaveInnerStreamOpen: true, LoadClient.Pinned(settings.CertificateThumbprint));
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        EnabledSslProtocols = SslProtocols.None,
                    }, state.Token).ConfigureAwait(false);
                    stream = tls;
                }

                await using (tls)
                {
                    var reader = new Http1ResponseReader(stream);
                    var sent = 0;
                    var open = true;
                    while (open && state.Running)
                    {
                        var lastOnConnection = settings.RequestsPerConnection > 0 && sent + settings.Pipeline == settings.RequestsPerConnection;
                        var started = Stopwatch.GetTimestamp();
                        await stream.WriteAsync(lastOnConnection ? finalBatch : batch, state.Token).ConfigureAwait(false);
                        await stream.FlushAsync(state.Token).ConfigureAwait(false);
                        sent += settings.Pipeline;
                        for (var index = 0; index < settings.Pipeline && open; index++)
                        {
                            var response = await reader.ReadAsync(expected, state.Token).ConfigureAwait(false);
                            histogram.RecordTicks(Stopwatch.GetTimestamp() - started);
                            state.Completed(response.BodyBytes, body.Length);
                            if (response.Close)
                            {
                                // A server may close early (for example a per-connection request
                                // limit). Later pipelined requests stay unanswered and uncounted.
                                if (!lastOnConnection) state.ServerClosed(settings.Pipeline - index - 1);
                                await reader.ExpectEndAsync(state.Token).ConfigureAwait(false);
                                open = false;
                            }
                        }

                        if (lastOnConnection && open) throw new InvalidDataException("Server ignored Connection: close.");
                    }
                }

                if (tcp.Connected) tcp.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or AuthenticationException or OperationCanceledException)
        {
            state.Fail(exception);
        }
    }
}

// Parses HTTP/1.1 responses with Content-Length or chunked framing and compares
// every decoded body byte with the expected payload.
internal sealed class Http1ResponseReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[65536];
    private int _start;
    private int _end;

    internal readonly record struct Response(long BodyBytes, bool Close);

    internal async Task<Response> ReadAsync(byte[] expected, CancellationToken cancellation)
    {
        int boundary;
        while ((boundary = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0) await FillAsync(cancellation).ConfigureAwait(false);
        // Parsed in place without strings: the client must stay cheaper than the servers it drives.
        var (length, chunked, close) = ParseHead(_buffer.AsSpan(_start, boundary + 2));
        _start += boundary + 4;

        if (chunked == (length >= 0)) throw new InvalidDataException("Response must use exactly one length framing.");
        long received = 0;
        if (!chunked)
        {
            if (length != expected.Length) throw new InvalidDataException($"Content-Length {length}, expected {expected.Length}.");
            await ReadBodyAsync(expected, 0, length, cancellation).ConfigureAwait(false);
            received = length;
        }
        else
        {
            while (true)
            {
                var line = await ReadLineAsync(cancellation).ConfigureAwait(false);
                var semicolon = line.IndexOf(';', StringComparison.Ordinal);
                var size = long.Parse(semicolon < 0 ? line : line[..semicolon], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    while ((await ReadLineAsync(cancellation).ConfigureAwait(false)).Length != 0)
                    {
                        // Trailer fields are not expected but are permitted by the framing.
                    }

                    break;
                }

                if (received + size > expected.Length) throw new InvalidDataException("Chunked body longer than expected.");
                await ReadBodyAsync(expected, received, size, cancellation).ConfigureAwait(false);
                received += size;
                if ((await ReadLineAsync(cancellation).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("Malformed chunk terminator.");
            }

            if (received != expected.Length) throw new InvalidDataException($"Chunked body {received} bytes, expected {expected.Length}.");
        }

        return new Response(received, close);
    }

    private static (long Length, bool Chunked, bool Close) ParseHead(ReadOnlySpan<byte> head)
    {
        if (!head.StartsWith("HTTP/1.1 200 "u8))
            throw new InvalidDataException("Unexpected status: " + Encoding.ASCII.GetString(head[..Math.Max(0, head.IndexOf("\r\n"u8))]));
        long length = -1;
        var chunked = false;
        var close = false;
        head = head[(head.IndexOf("\r\n"u8) + 2)..];
        while (!head.IsEmpty)
        {
            var end = head.IndexOf("\r\n"u8);
            var field = head[..end];
            head = head[(end + 2)..];
            var colon = field.IndexOf((byte)':');
            if (colon <= 0) throw new InvalidDataException("Malformed response field.");
            var name = field[..colon];
            var value = field[(colon + 1)..].Trim((byte)' ');
            if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                if (!System.Buffers.Text.Utf8Parser.TryParse(value, out length, out var consumed) || consumed != value.Length) throw new InvalidDataException("Malformed Content-Length.");
            }
            else if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
            {
                chunked = Ascii.EqualsIgnoreCase(value, "chunked"u8) ? true : throw new InvalidDataException("Unexpected transfer coding.");
            }
            else if (Ascii.EqualsIgnoreCase(name, "Content-Encoding"u8))
            {
                throw new InvalidDataException("Unexpected content coding.");
            }
            else if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
            {
                close = ContainsIgnoreCase(value, "close"u8);
            }
        }

        return (length, chunked, close);
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> token)
    {
        for (var index = 0; index + token.Length <= value.Length; index++)
        {
            if (Ascii.EqualsIgnoreCase(value.Slice(index, token.Length), token)) return true;
        }

        return false;
    }

    internal async Task ExpectEndAsync(CancellationToken cancellation)
    {
        if (_start != _end || await stream.ReadAsync(_buffer.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Unexpected data after the final response.");
    }

    private async Task ReadBodyAsync(byte[] expected, long offset, long count, CancellationToken cancellation)
    {
        while (count > 0)
        {
            if (_start == _end) await FillAsync(cancellation).ConfigureAwait(false);
            var take = (int)Math.Min(count, _end - _start);
            if (!_buffer.AsSpan(_start, take).SequenceEqual(expected.AsSpan((int)offset, take))) throw new InvalidDataException("Response body mismatch.");
            _start += take;
            offset += take;
            count -= take;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        int end;
        while ((end = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8)) < 0) await FillAsync(cancellation).ConfigureAwait(false);
        var line = Encoding.ASCII.GetString(_buffer, _start, end);
        _start += end + 2;
        return line;
    }

    private async Task FillAsync(CancellationToken cancellation)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length) throw new InvalidDataException("Response head exceeds the client buffer.");
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellation).ConfigureAwait(false);
        if (read == 0) throw new EndOfStreamException("Connection closed before the response completed.");
        _end += read;
    }
}

// HTTP/2 and HTTP/3 through SocketsHttpHandler. Each worker owns one handler and
// therefore one connection, with a fixed number of concurrent streams on it.
internal static class MultiplexedWorker
{
    internal static async Task RunAsync(LoadSettings settings, RunState state)
    {
        var histogram = state.CreateHistogram();
        var version = settings.Protocol == Protocol.Http3 ? HttpVersion.Version30 : HttpVersion.Version20;
        var expected = settings.IsUpload ? Payloads.UploadAcknowledgement(settings.UploadBytes) : Payloads.Get(settings.Route.ResponseLength);
        if (settings.Route.Kind == RouteKind.Plaintext) expected = Payloads.Plaintext;
        var uploadBody = settings.IsUpload ? Payloads.Get(settings.UploadBytes) : null;
        var uri = new Uri($"{(settings.Tls ? "https" : "http")}://localhost:{settings.Port}{settings.Route.Path}");
        try
        {
            while (state.Running)
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    EnableMultipleHttp2Connections = false,
                    EnableMultipleHttp3Connections = false,
                    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = LoadClient.Pinned(settings.CertificateThumbprint) },
                };
                using var client = new HttpClient(handler, disposeHandler: false)
                {
                    DefaultRequestVersion = version,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = Timeout.InfiniteTimeSpan,
                };
                state.Opened();
                long claimed = 0;
                await Task.WhenAll(Enumerable.Range(0, settings.Streams).Select(async _ =>
                {
                    var buffer = new byte[65536];
                    while (state.Running && (settings.RequestsPerConnection == 0 || Interlocked.Increment(ref claimed) <= settings.RequestsPerConnection))
                    {
                        var started = Stopwatch.GetTimestamp();
                        // DefaultRequestVersion only applies to convenience methods; set it per message.
                        using var request = new HttpRequestMessage(uploadBody is null ? HttpMethod.Get : HttpMethod.Post, uri)
                        {
                            Version = version,
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        };
                        if (uploadBody is not null) request.Content = new ByteArrayContent(uploadBody);
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, state.Token).ConfigureAwait(false);
                        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Unexpected status " + (int)response.StatusCode);
                        if (response.Version != version) throw new InvalidDataException("Negotiated HTTP/" + response.Version);
                        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("Unexpected content coding.");
                        await using var content = await response.Content.ReadAsStreamAsync(state.Token).ConfigureAwait(false);
                        long offset = 0;
                        int read;
                        while ((read = await content.ReadAsync(buffer, state.Token).ConfigureAwait(false)) > 0)
                        {
                            if (offset + read > expected.Length || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan((int)offset, read)))
                                throw new InvalidDataException("Response body mismatch.");
                            offset += read;
                        }

                        if (offset != expected.Length) throw new InvalidDataException($"Body {offset} bytes, expected {expected.Length}.");
                        histogram.RecordTicks(Stopwatch.GetTimestamp() - started);
                        state.Completed(offset, uploadBody?.Length ?? 0);
                    }
                })).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or AuthenticationException)
        {
            state.Fail(exception);
        }
    }
}
