# Request limits, admission and deadlines

[Neo #136](https://github.com/WilliamSmithEdward/embedio-neo/issues/136) asks for
opt-in resource policies for HTTP requests. This guide covers the server-wide options
delivered so far: limits on request headers and bodies, the number of requests handled
at once, and how long a request may be handled. All of them default to off, so a
server that sets none of them behaves exactly as before.

| Option | Default | Rejection |
| --- | --- | --- |
| `MaximumRequestHeaderBytes` | null (no limit) | HTTP 431 |
| `MaximumRequestHeaderCount` | null (no limit) | HTTP 431 |
| `MaximumRequestBodyBytes` | null (unlimited) | HTTP 413 |
| `RequestBodyReadTimeout` | null (no read deadline) | HTTP 408 |
| `MaximumConcurrentRequests` with `RequestQueueLimit` | null, queue 0 | HTTP 503 |
| `RequestTimeout` | null (no deadline) | HTTP 503 |

They live on `WebServerOptionsBase`, next to the existing
`MaximumDecompressedRequestBodyBytes`, and are enforced in the server's shared
dispatch path. That path serves the managed HTTP/1.1 listener, HTTP/2 and HTTP/3
alike; each HTTP/2 or HTTP/3 stream counts as one request.

Three more options bound TCP connections rather than requests. They live on
`WebServerOptions` and are enforced by the managed TCP listener, so they cover
HTTP/1.1 and HTTP/2 but not HTTP/3; see [connection limits](#connection-limits).

| Option | Default | When exceeded |
| --- | --- | --- |
| `MaximumConnections` | null (unlimited) | new connection closed |
| `RequestHeadersTimeout` | null (90 seconds) | connection closed |
| `KeepAliveTimeout` | null (15 s on HTTP/1.1, none on HTTP/2) | connection closed, GOAWAY on HTTP/2 |

## Example

```csharp
using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:9696/")
        .WithMode(HttpListenerMode.EmbedIO)
        .WithConnectionLimits(1000,                           // open TCP connections
            requestHeadersTimeout: TimeSpan.FromSeconds(10),
            keepAliveTimeout: TimeSpan.FromSeconds(30))
        .WithRequestHeaderLimits(8 * 1024, maximumCount: 64)  // 8 KiB, 64 fields
        .WithMaximumRequestBodyBytes(1024 * 1024)            // 1 MiB transferred
        .WithRequestBodyReadTimeout(TimeSpan.FromSeconds(15)) // per read
        .WithMaximumConcurrentRequests(64, queueLimit: 128)   // 64 at once, 128 waiting
        .WithRequestTimeout(TimeSpan.FromSeconds(30)))
    .WithWebApi("/api", m => m.WithController<ImportController>());
```

Rejections are ordinary HTTP exceptions, so `OnHttpException` renders them; the
default handler produces an HTML error page and `HttpExceptionHandler.PlainTextResponse`
a plain-text one.

The checks run in this order before any module: header limits, then admission to a
request slot, then the declared body length. A request with oversized headers is
therefore refused without taking or waiting for a slot.

## Request header fields

`MaximumRequestHeaderBytes` bounds the summed name and value lengths of the request's
header fields. `MaximumRequestHeaderCount` bounds how many field lines the request
carries. A request over either limit is answered with 431 before any module runs.

- Every received field line counts, including repeated names. HTTP/1.1 stores some
  repeated fields as one value, but the limit sees each line.
- The HTTP/1.1 request line and the HTTP/2 and HTTP/3 pseudo-header fields (`:method`,
  `:path`, `:authority` and so on) are not counted. HTTP/1.1 `Host` is an ordinary
  field and counts; in HTTP/2 and HTTP/3 the authority is a pseudo-header.
- HTTP/2 and HTTP/3 clients may split `Cookie` into several fields. Each one counts,
  although the server joins them into one `Cookie` header.
- Each protocol parser keeps its fixed 32 KiB header ceiling, which applies first: an
  HTTP/1.1 head over 32 KiB is refused by the connection with 431, and an HTTP/2 or
  HTTP/3 header block over it fails at the protocol level. These options can only
  lower the effective limit.
- On HTTP/1.1, a refused request that carries a body is answered with
  `Connection: close`, because its body is never read.

## Request body bytes

`MaximumRequestBodyBytes` counts body bytes as transferred, before any content
decoding.

- A request that declares a larger `Content-Length` is answered with 413 before any
  module runs.
- A body without a declared length, a chunked HTTP/1.1 body or an HTTP/2 or HTTP/3
  stream without `content-length`, is counted as the handler reads it. The read
  that crosses the limit throws a 413 `HttpException`; a body of exactly the limit
  is accepted.
- The limit applies to `Request.InputStream` and to the request helpers that read
  through it, such as `OpenRequestStream` and `GetRequestBodyAsByteArrayAsync`.
- Zero allows only requests without body content.
- `CONNECT` tunnels and WebSockets are not request bodies and are not counted.

With `SupportCompressedRequests`, the two limits are independent:
`MaximumRequestBodyBytes` bounds what arrives on the wire and
`MaximumDecompressedRequestBodyBytes` bounds what decoding produces. A 900-byte gzip
body that decodes to 100 KB passes a 1000-byte transferred limit and is then held to
the decoded limit.

**Connection reuse.** On HTTP/1.1 either rejection sends `Connection: close` and the
connection is not reused. Keep-alive would otherwise drain the refused remainder,
which for a chunked body has no end the server controls. HTTP/2 and HTTP/3 reset the
stream when the response completes before its body, so other streams on the same
connection continue.

### `100 Continue`

A client that sends `Expect: 100-continue` waits for an interim `100 Continue` response
before uploading its body. Without a body limit the engine sends it as soon as the
request head arrives, as before. While `MaximumRequestBodyBytes` is set, the engine
holds it back and sends it when a handler first reads the body:

- A request refused before dispatch, whether by its declared length, its headers, a
  full queue or a deadline, is answered without `100 Continue`, so the client never
  uploads the refused body.
- A handler that reads the body triggers `100 Continue` on its first read, and the
  client starts sending immediately rather than after its own fallback timer.
- On HTTP/1.1, a response that completes while `100 Continue` is still owed is sent
  with `Connection: close`, and the connection closes instead of waiting for a body
  the client was never invited to send. HTTP/2 and HTTP/3 reset the stream as for
  any unread body.
- A handler that starts its response before reading the body cannot send
  `100 Continue` any more. The client sends its body only when its own expectation
  timer expires (one second by default in .NET), and on HTTP/1.1 the connection is
  not reused.

**Known limitation.** A client that does not send `Expect: 100-continue` uploads at
once. An HTTP/1.1 client that is still sending when the server answers 413 and
closes often sees a connection reset instead of the response. That is TCP behavior
when the server closes with unread data; HTTP/2 and HTTP/3 clients receive the 413.

### Body read deadline

`RequestBodyReadTimeout` bounds how long one read of the request body may wait for
data. It applies to each read separately, so a slow but steady upload is not cut off;
a client that stops sending is.

- A read that misses the deadline throws a 408 `HttpException`, and so does every
  later read of the same body. A handler that lets it propagate answers 408.
- On HTTP/1.1 the response carries `Connection: close` and the connection is not
  reused. On HTTP/2 and HTTP/3 the stream is reset after the response, and the
  connection carries on.
- It applies to every protocol and listener mode. `CONNECT` tunnels are not timed.
- It complements `RequestTimeout`: the read deadline catches a stalled upload even when
  the handler does not observe the context token, and the request deadline bounds the
  whole request.

## Connection limits

These options on `WebServerOptions` apply to the managed TCP listener in the `EmbedIO`
and `EmbedIOCombined` modes. Setting any of them on an `EmbedIOHttp3` server throws
`NotSupportedException`, because QUIC connections are not governed by them.

- **`MaximumConnections`** bounds the TCP connections the server keeps open across all
  of its endpoints, counting HTTP/1.1 and HTTP/2 connections alike. A connection
  accepted at the limit is closed at once, before any TLS handshake, without a
  response. A slot frees when an open connection closes, including idle keep-alive
  connections when their deadline passes.
- **`RequestHeadersTimeout`** bounds how long a new connection may take to deliver its
  first complete request head, including the TLS handshake and, on HTTP/2, the
  connection preface. A connection that misses it is closed without a response. The
  default stays at 90 seconds.
- **`KeepAliveTimeout`** bounds idle time between requests. On HTTP/1.1 it runs from
  the end of a response until the next request head is complete, replacing the fixed
  15 seconds, and the `Keep-Alive` response header advertises it in whole seconds,
  rounded up. On HTTP/2 it runs while the connection has no open stream; when it
  passes, the server sends GOAWAY and closes the connection gracefully. Without the
  option HTTP/2 connections have no idle deadline, as before.

An endpoint applies the connection limits of the server that opened it to every
connection it accepts. Two servers may share a port only when neither sets connection
limits; otherwise the second server fails to start with `HttpListenerException`, and
the first keeps serving.

## Concurrent requests and the queue

`MaximumConcurrentRequests` bounds how many requests are being handled at once,
measured from dispatch until the response is complete and the context closed. A
request that finds every slot busy waits in a queue of at most `RequestQueueLimit`
entries. When the queue is also full, it is answered with 503 before any module runs.
The default queue length is zero: no request waits.

A slot is released after the server's completion code runs, which is after the client
can already see the response. A client that sends its next request the moment it reads
the previous response can therefore find the slot still taken. With a zero-length
queue that request is refused; a queue of even one entry absorbs it. Use a non-zero
queue when clients send requests back to back near the limit.

Slots are returned on every path: normal completion, handler exceptions, rejections,
cancellation and server shutdown. The limits belong to one server instance; two
servers in the same process have separate slots.

## Request deadline

`RequestTimeout` cancels `IHttpContext.CancellationToken` when the request has been
handled for that long. The deadline starts at dispatch and includes time spent waiting
in the queue.

- Cancellation is cooperative. A handler that passes the token to its awaits stops at
  the deadline; one that ignores it runs to completion and its response is delivered.
- When a handler stops because of the deadline before its response is committed, the
  server answers 503 with a deadline message. A request that times out while still
  queued never reaches a module.
- After the response is committed, the server cannot replace it. It ends the response
  visibly, closing an HTTP/1.1 body without its terminator or resetting an HTTP/2 or
  HTTP/3 stream, as described in [deferred responses](../user-reports/deferred-responses.md).
- Reading the body with the context token lets the deadline interrupt a stalled upload,
  for example a client that declares 100 bytes and sends 10.
- Stopping or disposing the server still cancels the same token; that path is reported
  as cancellation, not as a 503.

## Measured scenarios

`test/EmbedIO.Tests/Issues/Issue136_RequestLimits.cs`, `Issue136_RequestLimits.Headers.cs`
and `Issue136_RequestLimits.Connections.cs` drive the managed engine over real connections.
HTTP/2 uses cleartext prior knowledge; HTTP/3 uses QUIC with a generated certificate
and is skipped on hosts without QUIC support.

| Scenario | HTTP/1.1 | HTTP/2 | HTTP/3 |
| --- | --- | --- | --- |
| Header bytes over the limit | 431 before dispatch, smaller request then served | same | same |
| Header fields over the count | 431 | same | same |
| Repeated field lines | each line counts | n/a | n/a |
| Header limit with all slots busy | 431 without waiting for a slot | n/a | n/a |
| Declared length over the limit | 413 before dispatch, `Connection: close` | 413 before dispatch | 413 |
| Unknown-length body over the limit | covered by the chunked case | 413, exact-limit body then accepted on the same connection | same |
| Chunked body over the limit, client never finishes | 413 and close instead of draining | n/a | n/a |
| `Expect: 100-continue`, body refused | 413 without `100 Continue` | client never sends the body | same |
| `Expect: 100-continue`, body read | `100 Continue` on first read | same | same |
| `Expect: 100-continue`, body never read | response with `Connection: close`, then close | n/a | n/a |
| `Expect: 100-continue` without a body limit | `100 Continue` before dispatch, unchanged | n/a | n/a |
| Zero limit | bodyless GET 200, any body 413 | n/a | n/a |
| Transferred and decoded limits together | independent, distinct messages | n/a | n/a |
| Slots and queue full | 503, module never entered, slots return | same | 503 |
| Queued request | waits, then served when a slot frees | n/a | n/a |
| Failing handlers | slots returned after each 500 | n/a | n/a |
| Two servers in one process | independent limits | n/a | n/a |
| Deadline with a cooperative handler | 503, handler observed cancellation | same | 503 |
| Handler ignoring the deadline | completes normally | n/a | n/a |
| Deadline after commitment | body left unterminated | n/a | n/a |
| Deadline while queued | 503, module never entered | n/a | n/a |
| False Content-Length, stalled upload | deadline interrupts the read, 503 | n/a | n/a |
| Stalled body read | 408 and `Connection: close` | 408 | 408 |
| Slow steady upload, each read within the deadline | completes | completes | n/a |
| Connections beyond `MaximumConnections` | closed unanswered; slot returns when one closes | n/a | n/a |
| Second server on a port with connection limits | fails to start, first keeps serving | n/a | n/a |
| Incomplete first request head | closed at `RequestHeadersTimeout` | n/a | n/a |
| Idle connection after a response | closed at `KeepAliveTimeout`, header rounded up | GOAWAY then close | n/a |
| Stream open longer than the idle deadline | n/a | completes normally | n/a |
| No connection options | `Keep-Alive: timeout=15`, unchanged | n/a | n/a |

The .NET HTTP/1.1 client aborts its own connection after a refusal that arrives
without `100 Continue`, so the HTTP/1.1 refusal case is checked on the wire with a raw
socket. `TestWebServer` locks its options in its constructor and has no way to set
these limits.

## Not in this increment

The rest of #136 remains open: per-route policies. HTTP/3-specific limits, including
QUIC connection counts and idle deadlines, are tracked in #135.
