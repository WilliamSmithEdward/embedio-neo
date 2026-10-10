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
tunnels reject trailers. After declaration, incompatible bodyless status, tunnel
and HTTP/1 framing changes are rejected before changing the response. Fields that control framing, routing, authentication,
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

## Independent peer evidence and limits

The immutable-source campaign at827e55b used image
`sha256:cad57be0903a303f62b6492f695d658256f0c728af9a9c5454c839093fb29df3`,
SDK10.0.401/runtime10.0.12, hyper-h2 4.4.1 and aioquic1.3.0. Twenty-four
response vectors passed across HTTP/1 cleartext/TLS, HTTP/2 cleartext/TLS and
HTTP/3: empty, three-byte and196608-byte bodies, unknown/fixed lengths where
applicable, two ordered103 sections, exact digest trailers and healthy reuse.
The HTTP/3 peer explicitly advertises a65536-byte stream window.

The initial unmodified aioquic1.3.0 run failed on HTTP/3: its response parser
advances to final-header state after every HEADERS block, including103, then
rejects the next `:status` as a trailer pseudo-header. The test driver retains
this default behavior. Its optional `--h3-interim-adapter` resets that state only
for interim responses; final/trailer field validation and independent QPACK
decoding remain active, and all ordering/body/digest assertions remain enforced.
The passing HTTP/3 result is explicitly an adapted-peer result, not an unmodified
aioquic interoperability claim. The unmodified .NET HTTP/3 client separately
passes two103 sections followed by final metadata, body and trailers.

The pinned source was inspected at
[aioquic1.3.0](https://github.com/aiortc/aioquic/blob/1.3.0/src/aioquic/h3/connection.py).
The initial failure and adapted run are preserved under ignored
`TestResults/response-sections-peers` and`response-sections-peers-adapted`.
No production implementation code was copied or changed to accommodate the peer.
These vectors do not prove browser, soak, reset/backpressure or whole-engine fuzz
acceptance. Hosted final-head checks remain required.
## Delayed completion and abandonment

Four real-client cases cover HTTP/1.1 and HTTP/2. A handler can send its body,
wait before setting trailers, and finish with exactly one ending section. Before
completion, the client sees no trailer fields. An HTTP/2 client abandoning an
incomplete body resets the stream, cancels the waiting handler and leaves the
server available for a subsequent request.

HTTP/1 abandonment is tested separately: the handler is allowed to finish and
cleanup leaves service available. The listener does not promptly detect a peer
disconnect while an HTTP/1 handler waits without transport activity; this case
does not prove prompt disconnect cancellation. Applications should bound their
own waits. These cases do not establish slow-reader backpressure or soak limits.

Two HTTP/2 cases additionally observe a send-flow waiter while an unread 8 MiB
response reserves trailers. A sibling request finishes while that writer remains
blocked. Draining delivers the exact body and trailers; resetting interrupts the
writer. Both paths leave a subsequent request usable and no pending send-flow
waiters. This is bounded HTTP/2 backpressure evidence, not HTTP/1, HTTP/3 or soak
acceptance.

Four HTTP/3 cases use a real QUIC peer with a 64 KiB stream receive window.
They hold an unread 8 MiB response while a sibling completes, then drain the
exact body followed by a single ending HEADERS section or abort reads to release
the outstanding writer. Single application writes and 16 KiB writes are covered.
A subsequent stream succeeds on the same connection. Trailer fields in this
fixture are decoded by the engine's QPACK decoder, so this is transport/lifetime
evidence; independent QPACK evidence remains the separate peer campaign above.

Hosted validation at 4029387 identified two fixture corrections: the QUIC
trailer fixture must import its synthetic certificate with Exportable for the
non-Schannel backend, as the existing QUIC fixtures do. If a backpressured write
fails, context cleanup can fail too; the fixture now preserves the original
write failure and records recoverable cleanup errors. The reset must still
interrupt the writer, and sibling/subsequent stream assertions remain required.
Eleven adjacent Windows cases pass after these corrections. macOS/Linux
confirmation remains required.

The same head also timed out in the Windows full-consumption POST verification
with four workers and a new connection per request. That workload's failure shape
was previously recorded on fd58c90, before this response API work. Three fresh
local runs of the exact workload pass; they do not establish the cause or resolve
the intermittent failure. Its original timeout, diagnostics and artifacts are
retained, and no gate or timeout is relaxed.
