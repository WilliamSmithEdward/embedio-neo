# Informational responses and response trailers

This unreleased managed-engine increment adds the optional `IHttpResponseSections`
capability on .NET Standard 2.0 and .NET 10. Existing `IHttpResponse` implementations
remain compatible. Detect the capability before opting in; the Microsoft backend
continues to expose its existing response contract.

Use `SendInformationalAsync` for a non-final 1xx response such as 103 Early Hints.
It copies the supplied fields and preserves final status, headers and body state.
It rejects 101 (use the explicit Upgrade/WebSocket handshake), final status codes,
body framing, HTTP/1.0 and calls after final headers. Automatic 100 Continue remains
unchanged; the application cannot delay or suppress that automatic response in this increment.
Do not use the final `StatusCode` property to send an interim section.

Declare trailer names before final headers, then set their values after awaited
body writes and before output disposal or handler completion. A failed declaration
or snapshot does not replace a valid prior configuration. Collections are copied;
concurrent mutation of a supplied collection is unsupported. Trailer metadata is
not merged into final headers.

For example, an ActionModule handler can send a digest after its content. The
`Content-Digest` syntax and trailer permission come from
[RFC 9530](https://www.rfc-editor.org/rfc/rfc9530.html).

```csharp
static async Task SendWithDigestAsync(IHttpContext context)
{
    if (context.Response is not IHttpResponseSections sections)
    {
        context.Response.StatusCode = 501;
        await context.SendStringAsync("Response sections are unavailable on this backend.",
            "text/plain", WebServer.Utf8NoBomEncoding);
        return;
    }

    await sections.SendInformationalAsync(103,
        new WebHeaderCollection { ["Link"] = "</style.css>; rel=preload; as=style" },
        context.CancellationToken);
    sections.DeclareTrailers("Content-Digest");
    context.Response.ContentType = "text/plain";
    var payload = Encoding.UTF8.GetBytes("Hello from a streamed response.\n");
    await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, context.CancellationToken);
    using var sha256 = SHA256.Create();
    var digest = Convert.ToBase64String(sha256.ComputeHash(payload));
    sections.SetTrailers(new WebHeaderCollection { ["Content-Digest"] = "sha-256=:" + digest + ":" });
}
```

The method uses `System`, `System.Net`, `System.Text`,
`System.Security.Cryptography`, `System.Threading.Tasks` and `EmbedIO`. It writes
raw bytes without applying content compression, so the digest describes exactly
the transmitted content. The server sends the ending trailer section when the
handler completes; applications should not close the output before setting it.
Intermediaries can discard trailers: do not rely on them for routing, authorization
or information essential to interpreting the body.

HTTP/1.1 selects chunked framing and rejects a previously configured Content-Length.
HTTP/2 and HTTP/3 can retain Content-Length, but reserve their ending HEADERS/FIN
until the trailer snapshot is sent. HTTP/1.0, HEAD, bodyless statuses and accepted
tunnels reject trailers. Fields that control framing, routing, authentication,
content format or early response handling are rejected. Applications remain
responsible for knowing that each field's definition permits trailer use under
[RFC 9110 section 6.5.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.5.1).

Declarations and field snapshots use a 32 KiB bound, counting field overhead for
snapshots; the HTTP/1 ending section also has a 32 KiB serialized bound. Finish
configuration before returning from the handler. Multiplexed configuration rejects
calls concurrent with an active output operation rather than blocking a worker.

Current evidence: both target builds pass; real HTTP/1 wire tests and independent
HTTP/2/HTTP/3 clients cover interim state, ending trailers, immutable snapshots and
coalesced fixed-length bodies. Wider cancellation, malformed fields, compatibility,
resource, independent peer and exact-head platform/scanner gates remain in progress.
This guide does not claim the increment has merged or shipped.
