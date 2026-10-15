# Replace a failed response before it is committed

[Neo #42](https://github.com/WilliamSmithEdward/embedio-neo/issues/42) evaluates
the roadmap item from [upstream #546](https://github.com/unosquare/embedio/issues/546):
delay transmission so that an exception handler can replace a response whose
body generation failed. This guide records the commitment contract that the Neo
v2 engine now enforces, the scenarios that were measured, and the pattern that
gives an application deferred responses with the existing API.

## The commitment contract

A response is replaceable until its final headers reach the wire. Every public
send helper and every exception handler works inside that window:

- Before commitment, `OnUnhandledException` and `OnHttpException` run with a
  fresh empty response. Stale `Content-Length`, `Content-Encoding`,
  `Content-Range`, `ETag` and chunked framing left behind by the failed handler
  are removed before the handler writes its own body, so a replacement body is
  framed by its own length rather than by the length the failed handler declared.
  Headers that do not describe the body, such as CORS or custom diagnostics,
  are kept.
- After commitment, no handler runs. The engine ends the response in a way the
  client can detect: an HTTP/1.1 chunked body closes without the final `0`
  chunk, a declared `Content-Length` body closes short, and an HTTP/2 or HTTP/3
  stream is reset instead of ending the partial body with END_STREAM. Other
  requests on the same HTTP/2 connection and later HTTP/1.1 connections are
  unaffected.
- A response the helpers already completed, for example `SendStringAsync`
  followed by a failing cleanup step, is delivered intact. Only an incomplete,
  committed response is aborted.

Previously the engine terminated a failed chunked body with a clean final chunk
and ended a failed HTTP/2 stream normally. A client received a truncated body
that looked complete. The exception handler also inherited the failed handler's
`Content-Length`, so an `HttpException` thrown after `ContentLength64` was set
produced a short error body that clients rejected. Both corrections live on the
Neo v2 engine development branch and are unreleased; published 1.0.3 retains the
earlier behavior.

## Where commitment happens

| Operation | Commits when |
| --- | --- |
| `Response.OutputStream.Write`/`WriteAsync` | The first write, together with the headers. |
| `Response.OutputStream.Flush` / `FlushAsync` | A successful first flush commits final headers, including on HTTP/1 with no body bytes. Set response metadata before flushing. |
| `OpenResponseStream(buffered: false)` | The first write through the returned stream, or the first `Flush` of a compressing wrapper. |
| `OpenResponseStream(buffered: true)` | Disposal of the returned stream, which sets `Content-Length` and copies the buffer. |
| `SendStringAsync`, `SendDataAsync`, `SendStandardHtmlAsync` | Inside the call; serialization runs fully in memory first. |
| `SendInformationalAsync` | Not a commitment; the final response remains replaceable. |

JSON serialization failures therefore happen before commitment: a property
getter that throws inside `SendDataAsync` reaches the exception handler with a
replaceable response.

## Deferred responses with the existing API

Buffering alone is not rollback. The buffered stream commits when it is
disposed, and a `using` declaration disposes it while the exception unwinds,
before any handler runs. The partial buffer then reaches the client framed as a
complete body:

```csharp
// Commits the partial buffer during unwinding; the handler cannot replace it.
using var buffered = context.OpenResponseStream(buffered: true);
await buffered.WriteAsync(part, 0, part.Length);
throw new InvalidOperationException("after buffering");
```

Dispose the buffered stream only on success. Nothing is committed on the failure
path, and the configured exception handler replaces the response:

```csharp
var deferred = context.OpenResponseStream(buffered: true);
await WriteReportAsync(deferred);   // may throw; nothing has reached the client
deferred.Dispose();                 // commits Content-Length and the body once
```

Compression negotiated by `OpenResponseStream` is applied inside the buffer, so
the deferred body is sent with its negotiated `Content-Encoding` and length.

### Bounded memory

The buffered stream grows with the body. Bound it in the application when the
body size is not known in advance, and fall back to the error response when the
bound is exceeded:

```csharp
const int Limit = 4 * 1024 * 1024;
using var buffer = new MemoryStream(new byte[Limit], 0, Limit, writable: true);
buffer.SetLength(0);
await WriteReportAsync(buffer); // Growing past the fixed capacity throws before commitment.
context.Response.ContentType = MimeType.Json;
await context.SendDataAsync(ResponseSerializer.None(bufferResponse: true), buffer.ToArray());
```

This allocates the 4 MiB backing array up front and prevents the report stream
from growing beyond it. An oversized write throws `NotSupportedException` before
response commitment and reaches the normal exception handler. This bounds only
the report buffer, not allocations inside `WriteReportAsync`, the `ToArray` copy
or subsequent response serialization/buffering.

### Streaming escape path

Large or long-lived bodies should keep streaming directly through
`Response.OutputStream` or `OpenResponseStream(buffered: false)`. Validate inputs
and acquire resources before the first write, because after that write the
response is committed and a failure ends it visibly instead of producing an
error page. Server cancellation after commitment behaves the same way: the body
is not terminated, so the client sees an incomplete response.

## Measured scenarios

`test/EmbedIO.Tests/Issues/Issue42_DeferredResponses.cs` covers the acceptance
criteria of #42 against the real TCP engine with raw HTTP/1.1 sockets and an
HTTP/2 client:

| Scenario | HTTP/1.1 | HTTP/2 |
| --- | --- | --- |
| Unhandled exception before headers | 500 replaces status and body; prepared custom headers survive | same |
| `HttpException` after `ContentLength64` was set | status replaced, stale length removed | same |
| Serialization failure in `SendDataAsync` | 500 replaces | same |
| Buffered stream disposed only on success | handler replaces, with and without gzip | n/a |
| Buffered stream disposed while unwinding | partial body committed as complete | n/a |
| Exception or `HttpException` after a chunked write | no final chunk, server healthy | stream reset, connection healthy |
| Module-level handler after commitment | handler not invoked, body not terminated | n/a |
| Exception after a declared-length write | connection closes short of the declared length | n/a |
| Exception after a compressed, flushed write | gzip chunked body left unterminated | n/a |
| Server cancellation after a flushed write | body left unterminated | n/a |
| Client disconnect during streaming writes | handler observes the I/O failure, server healthy | n/a |
| Completed helper response followed by a failure | body delivered intact | n/a |

HTTP/3 shares the multiplexed abort path with HTTP/2 but was not exercised by
this fixture; its stream reset follows the same `IMultiplexedTunnelControl`
route already covered by the tunnel tests.

## Decision

No new public API is added by this evaluation. The existing buffered stream,
disposed only on success, already provides deferred responses, and the engine
now guarantees that a failure after commitment is visible to the client. An
additive helper that owns that dispose-on-success rule with a configurable
memory bound remains a reasonable follow-up, but it changes the public surface
and therefore needs its own approval, scope and release notes.
