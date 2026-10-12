# Request limits, admission and deadlines

[Neo #136](https://github.com/WilliamSmithEdward/embedio-neo/issues/136) asks for
opt-in resource policies for HTTP requests. This guide covers the first increment:
three server-wide options that bound request bodies, the number of requests handled
at once, and how long a request may be handled. All three default to off, so a
server that sets none of them behaves exactly as before.

| Option | Default | Rejection |
| --- | --- | --- |
| `MaximumRequestBodyBytes` | null (unlimited) | HTTP 413 |
| `MaximumConcurrentRequests` with `RequestQueueLimit` | null, queue 0 | HTTP 503 |
| `RequestTimeout` | null (no deadline) | HTTP 503 |

They live on `WebServerOptionsBase`, next to the existing
`MaximumDecompressedRequestBodyBytes`, and are enforced in the server's shared
dispatch path. That path serves the managed HTTP/1.1 listener, HTTP/2 and HTTP/3
alike; each HTTP/2 or HTTP/3 stream counts as one request.

## Example

```csharp
using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:9696/")
        .WithMode(HttpListenerMode.EmbedIO)
        .WithMaximumRequestBodyBytes(1024 * 1024)            // 1 MiB transferred
        .WithMaximumConcurrentRequests(64, queueLimit: 128)   // 64 at once, 128 waiting
        .WithRequestTimeout(TimeSpan.FromSeconds(30)))
    .WithWebApi("/api", m => m.WithController<ImportController>());
```

Rejections are ordinary HTTP exceptions, so `OnHttpException` renders them; the
default handler produces an HTML error page and `HttpExceptionHandler.PlainTextResponse`
a plain-text one.

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

**Known limitations.**

- The engine sends `100 Continue` before the request reaches the server, so a client
  that waits for it will start sending a body the server is about to refuse. The early
  413 and connection close still apply.
- An HTTP/1.1 client that is still sending when the server answers and closes often
  sees a connection reset instead of the 413 response. That is TCP behavior when the
  server closes with unread data; HTTP/2 clients receive the 413.

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

`test/EmbedIO.Tests/Issues/Issue136_RequestLimits.cs` drives the managed engine over
real connections:

| Scenario | HTTP/1.1 | HTTP/2 |
| --- | --- | --- |
| Declared length over the limit | 413 before dispatch, `Connection: close` | 413 before dispatch |
| Unknown-length body over the limit | covered by the chunked case | 413, exact-limit body then accepted on the same connection |
| Chunked body over the limit, client never finishes | 413 and close instead of draining | n/a |
| Zero limit | bodyless GET 200, any body 413 | n/a |
| Transferred and decoded limits together | independent, distinct messages | n/a |
| Slots and queue full | 503, module never entered, slots return | same |
| Queued request | waits, then served when a slot frees | n/a |
| Failing handlers | slots returned after each 500 | n/a |
| Two servers in one process | independent limits | n/a |
| Deadline with a cooperative handler | 503, handler observed cancellation | same |
| Handler ignoring the deadline | completes normally | n/a |
| Deadline after commitment | body left unterminated | n/a |
| Deadline while queued | 503, module never entered | n/a |
| False Content-Length, stalled upload | deadline interrupts the read, 503 | n/a |

HTTP/3 shares the dispatch path and the multiplexed request type with HTTP/2 but is
not exercised by this fixture. `TestWebServer` locks its options in its constructor
and has no way to set these limits.

## Not in this increment

The rest of #136 remains open: configurable header byte and field-count limits, active
connection limits, connection-level header, body and idle deadlines, and per-route
policies. HTTP/3-specific limits are tracked in #135.
