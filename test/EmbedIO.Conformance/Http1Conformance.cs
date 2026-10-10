using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Black-box HTTP/1.1 requirement checks against a running server. Each case cites
// the requirement it checks. "policy" records behavior where the RFC permits a
// choice; only "violation" outcomes are conformance failures.
internal static class Http1Conformance
{
    internal sealed record Outcome(string Id, string Reference, string Level, string Result, string Detail, double Milliseconds);

    private sealed record Case(string Id, string Reference, string Level, Func<Target, (string Result, string Detail)> Run);

    internal sealed record Target(string Host, int Port, TimeSpan Timeout)
    {
        internal RawHttp1 Connect() => new(Host, Port, Timeout);
    }

    private const string Conforms = "conforms";
    private const string Violation = "violation";
    private const string Policy = "policy";

    private static (string, string) Expect(bool ok, string detail) => (ok ? Conforms : Violation, detail);

    // Server-error bodies are included so error pages can be reviewed for leaked detail.
    private static string Describe(Http1Response r) => $"{r.Status} {r.Reason}; CL={r.Header("Content-Length") ?? "-"}; TE={r.Header("Transfer-Encoding") ?? "-"}; body={r.Body.Length}B"
        + (r.Status >= 500 ? "; text=" + Ascii(r.BodyText[..Math.Min(r.BodyText.Length, 400)]) : string.Empty);

    // Sends one request, reads the response, and reports whether the server closed afterward.
    private static (Http1Response? Response, bool Closed, string Error) Exchange(Target target, string request, bool head = false, bool waitClose = true)
    {
        using var client = target.Connect();
        client.Send(request);
        Http1Response? response = null;
        try { response = client.Read(head); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return (null, true, error.GetType().Name + ": " + error.Message);
        }
        var closed = waitClose && client.WaitClosed(out _);
        return (response, closed, string.Empty);
    }

    // A 4xx response followed by closure, or an immediate closure, rejects the message.
    private static (string, string) ExpectRejected(Target target, string request, params int[] statuses)
    {
        var (response, closed, error) = Exchange(target, request);
        if (response == null) return (Conforms, "closed without response: " + error);
        var accepted = statuses.Length == 0 ? response.Status is >= 400 and < 500 : statuses.Contains(response.Status);
        return Expect(accepted && closed, Describe(response) + (closed ? "; closed" : "; connection left open"));
    }

    private static readonly Case[] Cases =
    {
        new("basic-get", "RFC9112 3; RFC9110 6.6.1", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, e);
            return Expect(r.Status == 200 && r.BodyText == "hello" && r.Header("Date") != null, Describe(r) + "; Date=" + (r.Header("Date") ?? "missing"));
        }),
        new("unmatched-host", "RFC9110 7.4; 15.5.20", "SHOULD", t =>
        {
            // A Host no prefix serves deserves a status (404/421/400), not a silent close.
            var (r, closed, e) = Exchange(t, "GET /plain HTTP/1.1\r\nHost: unmatched.example\r\n\r\n");
            if (r == null) return (Policy, "closed without any response: " + e);
            return (r.Status is 400 or 404 or 421 ? Conforms : Policy, Describe(r) + (closed ? "; closed" : string.Empty));
        }),
        new("missing-host","RFC9112 3.2", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\n\r\n", 400)),
        new("duplicate-host", "RFC9112 3.2", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400)),
        new("invalid-host", "RFC9112 3.2", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\nHost: a b\r\n\r\n", 400)),
        new("space-before-colon", "RFC9112 5.1", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\nHost : a\r\n\r\n", 400)),
        new("obs-fold", "RFC9112 5.2", "MUST", t =>
        {
            // Reject with 400, or replace the fold with SP; either keeps a single interpretation.
            var (r, _, e) = Exchange(t, "GET /echo HTTP/1.1\r\nHost: a\r\nX-A: one\r\n two\r\n\r\n", waitClose: false);
            if (r == null) return (Conforms, "closed: " + e);
            return Expect(r.Status is 400 or 200, Describe(r));
        }),
        new("bare-cr-in-field", "RFC9112 2.2", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\nHost: a\r\nX-A: b\rc\r\n\r\n", 400)),
        new("nul-in-field", "RFC9110 5.5", "MUST", t => ExpectRejected(t, "GET /plain HTTP/1.1\r\nHost: a\r\nX-A: b\0c\r\n\r\n", 400)),
        new("bare-lf-line-ending", "RFC9112 2.2", "MAY", t =>
        {
            var (r, closed, e) = Exchange(t, "GET /plain HTTP/1.1\nHost: a\n\n");
            return (Policy, r == null ? "closed: " + e : Describe(r) + (closed ? "; closed" : string.Empty));
        }),
        new("leading-crlf", "RFC9112 2.2", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            return (r?.Status == 200 ? Conforms : Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("cl-and-te", "RFC9112 6.1", "MUST", t =>
        {
            // Either reject, or use chunked framing; in both cases the connection MUST close.
            const string smuggled = "GET /plain HTTP/1.1\r\nHost: a\r\n\r\n";
            var request = "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n" + smuggled;
            using var client = t.Connect();
            client.Send(request);
            Http1Response r;
            try { r = client.Read(false); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return (Conforms, "closed without response"); }
            var closed = client.WaitClosed(out var extra);
            return Expect((r.Status is >= 400 and < 500 || r.Header("X-Body-Length") == "0") && closed,
                Describe(r) + (closed ? "; closed" : $"; open, {extra} extra bytes (smuggled request answered)"));
        }),
        new("te-not-chunked-final", "RFC9112 6.3", "MUST", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: gzip\r\n\r\nabc", 400)),
        new("te-chunked-twice", "RFC9112 7", "MUST", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked, chunked\r\n\r\n0\r\n\r\n", 400)),
        new("te-on-http10", "RFC9112 6.1", "MUST", t =>
        {
            // Framing is treated as faulty and the connection closes after processing.
            var (r, closed, e) = Exchange(t, "POST /echo HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n");
            if (r == null) return (Conforms, "closed: " + e);
            return Expect(closed && r.Status != 500, Describe(r) + (closed ? "; closed" : "; open"));
        }),
        new("cl-equal-repeated-lines", "RFC9110 8.6", "MAY", t =>
        {
            // The engine document states equal repeated Content-Length lines are accepted.
            var (r, _, e) = Exchange(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 3\r\nContent-Length: 3\r\n\r\nabc", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("cl-differing-list", "RFC9112 6.3", "MUST", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 3, 4\r\n\r\nabcd", 400)),
        new("cl-equal-list", "RFC9110 8.6", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 3, 3\r\n\r\nabc", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("cl-signed", "RFC9110 8.6", "MUST", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: +3\r\n\r\nabc", 400)),
        new("cl-leading-zero", "RFC9110 8.6", "MUST", t =>
        {
            // 1*DIGIT permits leading zeros; the value is still 3.
            var (r, _, e) = Exchange(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 003\r\n\r\nabc", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.Header("X-Body-Length") == "3", Describe(r));
        }),
        new("cl-overflow", "RFC9110 8.6", "MUST", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 99999999999999999999999\r\n\r\n")),
        new("chunked-extensions-trailers", "RFC9112 7.1", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n3;ext=\"v\"\r\nabc\r\n2;x\r\nde\r\n0\r\nX-Trailer: t\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.BodyText == "abcde", Describe(r));
        }),
        new("chunk-size-overflow", "RFC9112 7.1; 2.2", "SHOULD", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\nFFFFFFFFFFFFFFFFFF\r\nabc\r\n0\r\n\r\n")),
        new("chunk-missing-crlf", "RFC9112 7.1; 2.2", "SHOULD", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabcX0\r\n\r\n")),
        new("chunk-size-whitespace", "RFC9112 7.1; 2.2", "SHOULD", t => ExpectRejected(t, "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n 3\r\nabc\r\n0\r\n\r\n")),
        new("pipeline-order", "RFC9112 9.3.2", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 1\r\n\r\nA"
                + "GET /plain HTTP/1.1\r\nHost: a\r\n\r\n"
                + "POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nBC\r\n0\r\n\r\n");
            var a = client.Read(false); var b = client.Read(false); var c = client.Read(false);
            return Expect(a.BodyText == "A" && b.BodyText == "hello" && c.BodyText == "BC", $"{a.BodyText}|{b.BodyText}|{c.BodyText}");
        }),
        new("head-no-body-then-pipeline", "RFC9110 9.3.2; RFC9112 6.3", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("HEAD /plain HTTP/1.1\r\nHost: a\r\n\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n");
            var head = client.Read(true); var get = client.Read(false);
            return Expect(head.Status == 200 && get.BodyText == "hello", Describe(head) + " then " + Describe(get));
        }),
        new("http10-no-chunked", "RFC9112 6.1", "MUST NOT", t =>
        {
            var (r, closed, e) = Exchange(t, "GET /stream?n=3000&chunk=1000 HTTP/1.0\r\n\r\n");
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Header("Transfer-Encoding") == null && r.Body.Length == 3000 && closed, Describe(r));
        }),
        new("connection-close", "RFC9112 9.6", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("GET /plain HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n");
            var r = client.Read(false);
            var closed = client.WaitClosed(out var extra);
            return Expect(r.Status == 200 && closed, Describe(r) + (closed ? "; closed, second request ignored" : $"; {extra} extra bytes"));
        }),
        new("expect-100-continue", "RFC9110 10.1.1", "SHOULD", t =>
        {
            using var client = t.Connect();
            client.Send("POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 3\r\nExpect: 100-continue\r\n\r\n");
            var interim = new List<Http1Response>();
            // A server may skip 100 and wait for the body; send it after a short pause.
            var reader = Task.Run(() => client.Read(false, interim));
            Thread.Sleep(300);
            client.Send("abc");
            var r = reader.GetAwaiter().GetResult();
            return Expect(r.BodyText == "abc", Describe(r) + $"; interim={string.Join(',', interim.Select(i => i.Status))}");
        }),
        new("expect-unknown", "RFC9110 10.1.1", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain HTTP/1.1\r\nHost: a\r\nExpect: x-unknown\r\n\r\n", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("method-case-sensitive", "RFC9110 9.1", "MUST", t =>
        {
            // "get" is a different, unregistered method; a GET-only route must not serve it.
            var (r, _, e) = Exchange(t, "get /get-only HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            if (r == null) return (Conforms, "closed: " + e);
            return Expect(r.BodyText != "get", Describe(r));
        }),
        new("unknown-method", "RFC9110 9.1", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "FROB /plain HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            if (r == null) return (Policy, "closed: " + e);
            return (r.Status == 501 ? Conforms : Policy, Describe(r));
        }),
        new("absolute-form", "RFC9112 3.2.2", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, $"GET http://{t.Host}:{t.Port}/echo HTTP/1.1\r\nHost: ignored.example\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200, Describe(r) + "; target=" + r.Header("X-Target"));
        }),
        new("asterisk-form-options", "RFC9112 3.2.4", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "OPTIONS * HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return (r.Status is >= 200 and < 300 ? Conforms : Policy, Describe(r));
        }),
        new("asterisk-form-get", "RFC9112 3.2.4", "MUST", t => ExpectRejected(t, "GET * HTTP/1.1\r\nHost: a\r\n\r\n", 400)),
        new("version-http12", "RFC9110 2.5", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain HTTP/1.2\r\nHost: a\r\n\r\n", waitClose: false);
            return (r?.Status == 200 ? Conforms : Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("version-http20-text", "RFC9110 2.5", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain HTTP/2.0\r\nHost: a\r\n\r\n", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("version-lowercase", "RFC9112 2.3", "MUST", t =>
        {
            // HTTP-name is case-sensitive "HTTP".
            var (r, _, e) = Exchange(t, "GET /plain http/1.1\r\nHost: a\r\n\r\n");
            if (r == null) return (Conforms, "closed: " + e);
            return Expect(r.Status == 400, Describe(r));
        }),
        new("long-target", "RFC9112 3", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain?" + new string('a', 40_000) + " HTTP/1.1\r\nHost: a\r\n\r\n");
            if (r == null) return (Error, "No required limit status received: " + e);
            return Expect(r.Status == 414, Describe(r));
        }),
        new("large-header", "RFC6585 5", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "GET /plain HTTP/1.1\r\nHost: a\r\nX-Big: " + new string('b', 40_000) + "\r\n\r\n");
            if (r == null) return (Policy, "closed: " + e);
            return (r.Status == 431 ? Conforms : Policy, Describe(r));
        }),
        new("h2c-upgrade-ignored", "RFC9113 3.1", "MAY", t =>
        {
            // RFC 9113 removed h2c Upgrade; a server answers in HTTP/1.1.
            var (r, _, e) = Exchange(t, "GET /plain HTTP/1.1\r\nHost: a\r\nConnection: Upgrade, HTTP2-Settings\r\nUpgrade: h2c\r\nHTTP2-Settings: AAMAAABkAAQAoAAAAAIAAAAA\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.BodyText == "hello", Describe(r));
        }),
        new("connect-rejected-closes", "RFC9931 / RFC9112 9.6", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\n\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n");
            Http1Response r;
            try { r = client.Read(false); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return (Conforms, "closed"); }
            var closed = client.WaitClosed(out var extra);
            return Expect(r.Status >= 400 && closed, Describe(r) + (closed ? "; closed" : $"; {extra} extra bytes after rejection"));
        }),
        new("range-single", "RFC9110 14.1.2; 15.3.7", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=10-19\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            var expected = ConformanceServer.FileBytes().AsSpan(10, 10).ToArray();
            return Expect(r.Status == 206 && r.Body.SequenceEqual(expected) && r.Header("Content-Range") == $"bytes 10-19/{ConformanceServer.FileLength}", Describe(r) + "; " + r.Header("Content-Range"));
        }),
        new("range-suffix", "RFC9110 14.1.2", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=-5\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            var expected = ConformanceServer.FileBytes()[^5..];
            return Expect(r.Status == 206 && r.Body.SequenceEqual(expected), Describe(r) + "; " + r.Header("Content-Range"));
        }),
        new("range-ows-erratum-7306", "RFC9110 14.1.1 (erratum 7306)", "MUST", t =>
        {
            // Verified erratum 7306 permits OWS after "=".
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes= 10-19\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 206 && r.Body.Length == 10, Describe(r) + "; " + r.Header("Content-Range"));
        }),
        new("range-unsatisfiable", "RFC9110 15.5.17", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=200000-\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 416 && r.Header("Content-Range") == $"bytes */{ConformanceServer.FileLength}", Describe(r) + "; " + r.Header("Content-Range"));
        }),
        new("range-multipart", "RFC9110 14.6", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=0-1,5-6\r\n\r\n", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r) + "; type=" + r.Header("Content-Type"));
        }),
        new("range-head", "RFC9110 14.2", "MUST", t =>
        {
            // Range is defined only for GET; other methods MUST ignore it.
            var (r, _, e) = Exchange(t, "HEAD /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=0-9\r\n\r\n", head: true, waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.Header("Content-Range") == null, Describe(r));
        }),
        new("if-range-mismatch", "RFC9110 13.1.5", "MUST", t =>
        {
            // A validator that does not match means the full representation is sent.
            var (r, _, e) = Exchange(t, "GET /files/data.bin HTTP/1.1\r\nHost: a\r\nRange: bytes=0-9\r\nIf-Range: \"no-match\"\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.Body.Length == ConformanceServer.FileLength, Describe(r));
        }),
        new("query-method", "RFC10008 2", "MUST", t =>
        {
            var (r, _, e) = Exchange(t, "QUERY /query HTTP/1.1\r\nHost: a\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nq=abc", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            return Expect(r.Status == 200 && r.BodyText == "q=abc" && r.Header("X-Method") == "QUERY", Describe(r));
        }),
        new("query-missing-content-type", "RFC10008 2", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "QUERY /query HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\n\r\nq=abc", waitClose: false);
            return (r?.Status is 400 or 415 ? Conforms : Policy, r == null ? "closed: " + e : Describe(r));
        }),
        new("not-modified-no-body", "RFC9110 15.4.5", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("GET /files/data.bin HTTP/1.1\r\nHost: a\r\n\r\n");
            var first = client.Read(false);
            var etag = first.Header("ETag");
            if (etag == null) return (Policy, "no ETag on static file");
            client.Send($"GET /files/data.bin HTTP/1.1\r\nHost: a\r\nIf-None-Match: {etag}\r\n\r\nGET /plain HTTP/1.1\r\nHost: a\r\n\r\n");
            var second = client.Read(false); var third = client.Read(false);
            return Expect(second.Status == 304 && third.BodyText == "hello", Describe(second) + " then " + Describe(third));
        }),
        new("gzip-negotiation", "RFC9110 12.5.3", "MAY", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/text.txt HTTP/1.1\r\nHost: a\r\nAccept-Encoding: gzip\r\n\r\n", waitClose: false);
            if (r == null) return (Violation, "closed: " + e);
            var coding = r.Header("Content-Encoding");
            var ok = coding == null || coding == "gzip";
            if (coding == "gzip")
            {
                using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(r.Body), System.IO.Compression.CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gzip.CopyTo(plain);
                ok = plain.Length == 2000 * 23;
            }
            return Expect(ok && r.Header("Vary")?.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase) == true, Describe(r) + $"; CE={coding}; Vary={r.Header("Vary")}");
        }),
        new("accept-encoding-identity-refused", "RFC9110 12.5.3", "SHOULD", t =>
        {
            var (r, _, e) = Exchange(t, "GET /files/text.txt HTTP/1.1\r\nHost: a\r\nAccept-Encoding: gzip;q=0, identity;q=0, *;q=0\r\n\r\n", waitClose: false);
            return (Policy, r == null ? "closed: " + e : Describe(r) + "; CE=" + r.Header("Content-Encoding"));
        }),
        new("incomplete-body-then-eof", "RFC9112 8", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("POST /echo HTTP/1.1\r\nHost: a\r\nContent-Length: 10\r\n\r\nabc");
            client.ShutdownSend();
            try
            {
                var r = client.Read(false);
                return Expect(r.Status >= 400, $"responded after truncated body: {Describe(r)}; application saw {r.Header("X-Body-Length") ?? "?"} bytes");
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { return (Conforms, "closed without a success response"); }
        }),
        new("incomplete-chunked-then-eof", "RFC9112 8", "MUST", t =>
        {
            using var client = t.Connect();
            client.Send("POST /echo HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n");
            client.ShutdownSend();
            try
            {
                var r = client.Read(false);
                // An error response is permitted; it should be a client error, not success.
                return (r.Status is >= 400 and < 500 ? Conforms : r.Status >= 500 ? Policy : Violation,
                    $"responded after truncated chunked body: {Describe(r)}");
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { return (Conforms, "closed without a success response"); }
        }),
    };

    internal static List<Outcome> Run(Target target, string? filter)
    {
        var results = new List<Outcome>();
        foreach (var item in Cases.Where(c => filter == null || c.Id.Contains(filter, StringComparison.Ordinal)))
        {
            var clock = Stopwatch.StartNew();
            (string Result, string Detail) outcome;
            try { outcome = item.Run(target); }
            catch (Exception error) when (IsDriverFailure(error)) { outcome = ("error", error.GetType().Name + ": " + error.Message); }
            var health = Healthy(target);
            if (health != null) outcome = (Violation, outcome.Detail + "; UNHEALTHY AFTER CASE: " + health);
            results.Add(new Outcome(item.Id, item.Reference, item.Level, outcome.Result, outcome.Detail, clock.Elapsed.TotalMilliseconds));
        }
        return results;
    }

    // A fresh connection must still be served after every case.
    internal static string? Healthy(Target target)
    {
        try
        {
            var (r, _, e) = Exchange(target, "GET /plain HTTP/1.1\r\nHost: a\r\n\r\n", waitClose: false);
            return r?.BodyText == "hello" ? null : r == null ? e : Describe(r);
        }
        catch (Exception error) when (IsDriverFailure(error)) { return error.GetType().Name + ": " + error.Message; }
    }

    // Failures a driver records as an outcome; anything else (for example out of memory) propagates.
    internal static bool IsDriverFailure(Exception error) => error is IOException or InvalidDataException or System.Net.Sockets.SocketException
        or TimeoutException or InvalidOperationException or AggregateException or FormatException or JsonException or ObjectDisposedException or ArgumentException;

    internal static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    internal static string ToJson(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    internal static string Summary(IEnumerable<Outcome> outcomes) => string.Join(", ",
        outcomes.GroupBy(o => o.Result).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));

    internal static string Line(Outcome o) => $"{o.Result,-10} {o.Id,-34} {o.Level,-8} {o.Reference,-28} {o.Detail}";

    internal static string Ascii(string s) => new(s.Select(c => c is >= ' ' and < (char)127 ? c : '?').ToArray());

    internal static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);
}
