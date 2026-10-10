using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Minimal reproductions for the standards applicability audit
// (docs/project/http-standards-applicability.md). Each probe observes what an
// application gets from the public API on the wire. "gap" records a missing
// application-facing capability where the RFC itself permits the observed
// behavior; it is evidence for the audit, not a failure. Only "violation" and
// "error" fail a run.
internal static class ApplicabilityProbes
{
    private const string Conforms = "conforms";
    private const string Violation = "violation";
    private const string Policy = "policy";
    private const string Gap = "gap";

    private sealed record Probe(string Id, string Reference, string Level, Func<Http1Conformance.Target, Task<(string Result, string Detail)>> Run);

    private static string Describe(Http1Response r) => $"{r.Version} {r.Status}; body={r.Body.Length}B";

    // Reads interim and final responses; a timeout after interim responses is recorded, not thrown.
    private static (List<Http1Response> Interim, Http1Response? Final, string Error) ReadAll(RawHttp1 client)
    {
        var interim = new List<Http1Response>();
        try { return (interim, client.Read(false, interim), string.Empty); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return (interim, null, error.GetType().Name);
        }
    }

    private static string Interim(List<Http1Response> responses) =>
        responses.Count == 0 ? "no interim" : "interim=" + string.Join("+", responses.Select(r => r.Version + " " + r.Status));

    private static readonly Probe[] Probes =
    {
        new("h1-app-1xx-to-http10", "RFC9110 15.2", "MUST NOT", t =>
        {
            using var client = t.Connect();
            client.Send("GET /probe/status-103 HTTP/1.0\r\nHost: a\r\n\r\n");
            var (interim, final, error) = ReadAll(client);
            var sent1xx = interim.Count > 0;
            return Task.FromResult((sent1xx ? Violation : Conforms,
                $"{Interim(interim)}; final={(final == null ? "none (" + error + ")" : Describe(final))}"));
        }),
        new("h1-app-1xx-without-final", "RFC9110 15; 15.2", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("GET /probe/status-103 HTTP/1.1\r\nHost: a\r\n\r\n");
            var (interim, final, error) = ReadAll(client);
            return Task.FromResult((final == null || final.Status < 200 ? Violation : Gap,
                $"{Interim(interim)}; final={(final == null ? "none (" + error + ")" : Describe(final))}; no public API sends an interim response before a final one"));
        }),
        new("h1-expect-app-rejects", "RFC9110 10.1.1", "MAY", t =>
        {
            using var client = t.Connect();
            client.Send("POST /probe/reject HTTP/1.1\r\nHost: a\r\nExpect: 100-continue\r\nContent-Length: 5\r\n\r\n");
            var (interim, final, error) = ReadAll(client);
            var continued = interim.Any(r => r.Status == 100);
            return Task.FromResult((continued ? Gap : Policy,
                $"{Interim(interim)}; final={(final == null ? "none (" + error + ")" : Describe(final))}; "
                + (continued ? "100 sent before the application could choose a final status" : "application status sent without 100")));
        }),
        new("h1-request-trailer-visibility", "RFC9110 6.5; 6.5.1", "MAY", t =>
        {
            using var client = t.Connect();
            client.Send("POST /probe/fields HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\nTrailer: X-Probe-Trailer\r\n\r\n"
                + "3\r\nabc\r\n0\r\nX-Probe-Trailer: 1\r\n\r\n");
            var (_, final, error) = ReadAll(client);
            if (final == null) return Task.FromResult(("error", error));
            var visible = final.BodyText.Contains("x-probe-trailer", StringComparison.Ordinal);
            return Task.FromResult((visible ? Conforms : Gap, $"{Describe(final)}; {final.BodyText}; trailer {(visible ? "visible" : "consumed, not exposed")}"));
        }),
        new("h1-query-without-content-type", "RFC10008 2; 2.1", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("QUERY /query HTTP/1.1\r\nHost: a\r\nContent-Length: 2\r\n\r\n{}");
            var (_, final, error) = ReadAll(client);
            if (final == null) return Task.FromResult((Conforms, "closed without response: " + error));
            return Task.FromResult((final.Status is >= 400 and < 500 ? Conforms : Violation, Describe(final)));
        }),
        new("h1-query-empty-content", "RFC10008 2", "MUST (inconsistent content)", t =>
        {
            using var client = t.Connect();
            client.Send("QUERY /query HTTP/1.1\r\nHost: a\r\nContent-Type: application/json\r\nContent-Length: 0\r\n\r\n");
            var (_, final, error) = ReadAll(client);
            return Task.FromResult((Policy, final == null ? "none (" + error + ")" : Describe(final) + "; consistency with the media type is the resource's decision"));
        }),
        new("h1-unknown-upgrade-ignored", "RFC9110 7.8; RFC9931", "MAY", t =>
        {
            using var client = t.Connect();
            client.Send("GET /plain HTTP/1.1\r\nHost: a\r\nUpgrade: x-probe\r\nConnection: Upgrade\r\n\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n");
            var (_, first, error) = ReadAll(client);
            if (first == null) return Task.FromResult((Policy, "closed: " + error));
            var (_, second, error2) = ReadAll(client);
            return Task.FromResult((first.Status == 200 && second?.Status == 200 ? Policy : Violation,
                $"first={Describe(first)}; pipelined successor={(second == null ? "none (" + error2 + ")" : Describe(second))}"));
        }),
        new("h2-server-settings", "RFC9113 6.5.2; RFC8441 3; RFC9218 2.1; RFC9297 2.1.1", "record", t => Task.FromResult(ServerSettings(t))),
        new("h2-extended-connect-unknown-protocol", "RFC8441 4-5; RFC9110 9.3.6", "MUST (2xx means tunnel)", t => Task.FromResult(RawConnect(t, "x-probe-unknown"))),
        new("h2-extended-connect-webtransport", "draft-ietf-webtrans-http2-15; RFC8441 4", "draft", t => Task.FromResult(RawConnect(t, "webtransport"))),
        new("h2-plain-connect-to-handler", "RFC9113 8.5; RFC9110 9.3.6", "MUST (2xx means tunnel)", t => Task.FromResult(RawConnect(t, null))),
        new("h2-app-1xx-public-api", "RFC9110 15.2; RFC8297", "API", async t =>
        {
            using var client = H2Client();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{t.Host}:{t.Port}/probe/status-103") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            try
            {
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return (Gap, $"final={(int)response.StatusCode} body={body.Length}B; the application's 103 became the only header block or was replaced");
            }
            catch (HttpRequestException error) { return (Gap, "request failed: " + error.InnerException?.GetType().Name + " " + error.Message); }
        }),
    };

    private static HttpClient H2Client() => new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(5) };

    // Static-table :status entries (RFC 7541 Appendix A, indices 8-14).
    private static readonly int[] StaticStatus = { 200, 204, 206, 304, 400, 404, 500 };

    private static byte[] Frame(byte type, byte flags, int stream, byte[] payload)
    {
        var frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16); frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)payload.Length;
        frame[3] = type; frame[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), stream);
        payload.CopyTo(frame, 9);
        return frame;
    }

    // HPACK literal without indexing, new name, no Huffman: enough for short request fields.
    private static void Literal(List<byte> block, string name, string value)
    {
        block.Add(0);
        block.Add((byte)name.Length); block.AddRange(Encoding.ASCII.GetBytes(name));
        block.Add((byte)value.Length); block.AddRange(Encoding.ASCII.GetBytes(value));
    }

    // Sends one CONNECT (extended when protocol is set) to /plain, a handler that answers
    // 200 "hello" to every method, and reports how the stream ends. Written from RFC 9113
    // and RFC 8441 with no client library, so the request is exactly as specified.
    private static (string, string) RawConnect(Http1Conformance.Target t, string? protocol)
    {
        using var tcp = new TcpClient { ReceiveTimeout = (int)t.Timeout.TotalMilliseconds };
        tcp.Connect(t.Host, t.Port);
        using var stream = tcp.GetStream();
        stream.Write(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
        stream.Write(Frame(4, 0, 0, Array.Empty<byte>()));
        var authority = $"{t.Host}:{t.Port}";
        var block = new List<byte>();
        Literal(block, ":method", "CONNECT");
        if (protocol != null)
        {
            Literal(block, ":protocol", protocol);
            Literal(block, ":scheme", "http");
            Literal(block, ":path", "/plain");
        }
        Literal(block, ":authority", authority);
        var header = new byte[9];
        var sentRequest = false;
        for (var frames = 0; frames < 16; frames++)
        {
            stream.ReadExactly(header);
            var length = (header[0] << 16) | (header[1] << 8) | header[2];
            var payload = new byte[length];
            stream.ReadExactly(payload);
            var type = header[3];
            var id = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & 0x7fffffff;
            if (type == 4 && (header[4] & 1) == 0)
            {
                stream.Write(Frame(4, 1, 0, Array.Empty<byte>()));
                if (!sentRequest) { stream.Write(Frame(1, 4, 1, block.ToArray())); sentRequest = true; }
                continue;
            }
            if (id != 1 && type != 7) continue;
            var request = protocol == null ? $"plain CONNECT {authority}" : $":protocol={protocol} :path=/plain";
            if (type == 3)
                return (Conforms, $"{request}; RST_STREAM error=0x{BinaryPrimitives.ReadUInt32BigEndian(payload):x}");
            if (type == 7)
                return (Conforms, $"{request}; GOAWAY error=0x{BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)):x}");
            if (type != 1) continue;
            var first = payload.Length > 0 ? payload[0] : (byte)0;
            int? status = first is >= 0x88 and <= 0x8e ? StaticStatus[first - 0x88] : null;
            var described = status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "non-static :status, first HPACK byte 0x" + first.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
            // A 2xx answer to CONNECT tells the client the stream is now a tunnel for the
            // requested protocol; an ordinary resource handler cannot have meant that.
            return (status is >= 200 and < 300 ? Violation : Conforms, $"{request}; HEADERS status={described}; handler=/plain (ActionModule, any verb)");
        }
        return ("error", "no response on stream 1 within sixteen frames");
    }

    // Reads the server's first SETTINGS frame over prior-knowledge h2c with raw frames.
    private static (string, string) ServerSettings(Http1Conformance.Target t)
    {
        using var tcp = new TcpClient { ReceiveTimeout = (int)t.Timeout.TotalMilliseconds };
        tcp.Connect(t.Host, t.Port);
        using var stream = tcp.GetStream();
        stream.Write(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
        stream.Write(new byte[] { 0, 0, 0, 4, 0, 0, 0, 0, 0 });
        var header = new byte[9];
        for (var frames = 0; frames < 8; frames++)
        {
            stream.ReadExactly(header);
            var length = (header[0] << 16) | (header[1] << 8) | header[2];
            var payload = new byte[length];
            stream.ReadExactly(payload);
            if (header[3] != 4 || (header[4] & 1) != 0) continue;
            var settings = new List<string>();
            var ids = new HashSet<int>();
            for (var i = 0; i + 6 <= payload.Length; i += 6)
            {
                var id = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i));
                ids.Add(id);
                settings.Add($"0x{id:x}={BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2))}");
            }
            return (Policy, string.Join(" ", settings) + $"; ENABLE_CONNECT_PROTOCOL={(ids.Contains(8) ? "sent" : "absent")}; NO_RFC7540_PRIORITIES={(ids.Contains(9) ? "sent" : "absent")}; 0x33={(ids.Contains(0x33) ? "sent" : "absent")}");
        }
        return ("error", "no SETTINGS frame within eight frames");
    }

    internal static async Task<List<Http1Conformance.Outcome>> RunAsync(Http1Conformance.Target target, string? filter)
    {
        var results = new List<Http1Conformance.Outcome>();
        foreach (var probe in Probes.Where(p => filter == null || p.Id.Contains(filter, StringComparison.Ordinal)))
        {
            var clock = Stopwatch.StartNew();
            (string Result, string Detail) outcome;
            try { outcome = await probe.Run(target).ConfigureAwait(false); }
            catch (Exception error) when (Http1Conformance.IsDriverFailure(error) || error is HttpRequestException or TaskCanceledException)
            {
                outcome = ("error", error.GetType().Name + ": " + error.Message);
            }
            var health = Http1Conformance.Healthy(target);
            if (health != null) outcome = (Violation, outcome.Detail + "; UNHEALTHY AFTER PROBE: " + health);
            results.Add(new Http1Conformance.Outcome(probe.Id, probe.Reference, probe.Level, outcome.Result, outcome.Detail, clock.Elapsed.TotalMilliseconds));
        }
        return results;
    }
}
