# Request limits, admission and deadlines

[Neo #136](https://github.com/WilliamSmithEdward/embedio-neo/issues/136) asks for
resource policies for HTTP requests. This guide covers them: limits on request headers
and bodies, the number of connections and of requests handled at once, how long a
connection, a body read and a request may take, and per-route policies that replace the
server-wide values under a base route.

| Option | Default | Rejection |
| --- | --- | --- |
| `MaximumRequestHeaderBytes` | 32 KiB (32,768 bytes) | HTTP 431 |
| `MaximumRequestHeaderCount` | 100 field lines | HTTP 431 |
| `MaximumRequestBodyBytes` | 30,000,000 bytes (about 28.6 MiB) | HTTP 413 |
| `MinimumRequestBodyDataRate` | 240 bytes per second after a 5-second grace period | HTTP 408 |
| `RequestBodyReadTimeout` | null (no deadline) | HTTP 408 |
| `MaximumConcurrentRequests` with `RequestQueueLimit` | null (unlimited), queue 0 | HTTP 503 |
| `RequestTimeout` | null (no deadline) | HTTP 503 |

## Defaults

The header and body limits are on by default, with values modelled on
Kestrel's `KestrelServerLimits` in ASP.NET Core 10.0.13. Before v2 they were all off.

| EmbedIO option | Default | Kestrel equivalent |
| --- | --- | --- |
| `MaximumRequestBodyBytes` | 30,000,000 bytes | `MaxRequestBodySize`, 30,000,000 bytes |
| `MaximumRequestHeaderBytes` | 32 KiB | `MaxRequestHeadersTotalSize`, 32 KiB |
| `MaximumRequestHeaderCount` | 100 | `MaxRequestHeaderCount`, 100 |
| `RequestHeadersTimeout` (TCP connections and HTTP/3 streams) | 30 seconds | `RequestHeadersTimeout`, 30 seconds |
| `MinimumRequestBodyDataRate` | 240 bytes per second after a 5-second grace period | `MinRequestBodyDataRate`, the same |
| `RequestBodyReadTimeout` | null (no deadline) | none: Kestrel has no per-read deadline either |
| `WebSocketModule.MaxMessageSize` | 4 MiB | none: ASP.NET Core's `WebSocketOptions` has no message size limit |

- Each default can be replaced in the same way as before, server-wide or, for the body
  limit, by a [route policy](#per-route-policies). Null removes a limit, as it always
  did: `WithMaximumRequestBodyBytes(null)`, `WithRequestHeaderLimits(null, null)`. For
  `MaxMessageSize`, zero disables the check, as before.
- The 32 KiB header default equals the fixed ceiling of each protocol parser, which
  applies first, so it refuses nothing the server accepted before; lowering it takes
  effect.
- With `SupportCompressedRequests`, the decoded body limit follows the request's body
  limit, including a route's, until `MaximumDecompressedRequestBodyBytes` is set.
- `MaximumConcurrentRequests` and `RequestTimeout` stay off. The right values depend on
  what the handlers do, and a request deadline would cut off long downloads, streamed
  responses and server-sent events. Set them for a server that faces untrusted clients,
  and lift the deadline per route where requests legitimately run long.

**The data rate is the one default that bounds time.** The body defaults bound its
size: 30,000,000 bytes as transferred and, with `SupportCompressedRequests`, a decoded
limit that follows it. Setting `MaximumDecompressedRequestBodyBytes` explicitly to
null opts out of that decoded limit: the transferred bytes keep their own limit, but
decoded output becomes unbounded, the shape of a decompression bomb. The header
defaults bound the head: 32 KiB, 100 field lines, and 30 seconds for it to arrive on
a TCP connection. `MinimumRequestBodyDataRate` bounds how long a body the application
is reading may take: a client that sends its body a byte at a time, or sends a head and
then stalls once the handler reads, is refused with 408 about five seconds after the
handler started waiting (see [minimum body data rate](#minimum-body-data-rate)).
`RequestBodyReadTimeout`, `RequestTimeout` and `MaximumConcurrentRequests` are null;
set them where a route needs a per-read deadline, a whole-request deadline or a
concurrency cap. The keep-alive deadline bounds only the discard of a body the handler
never read on HTTP/1.1 (see [request body bytes](#request-body-bytes)).

They live on `WebServerOptionsBase`, next to the existing
`MaximumDecompressedRequestBodyBytes`, and are enforced in the server's shared
dispatch path. That path serves the managed HTTP/1.1 listener, HTTP/2 and HTTP/3
alike; each HTTP/2 or HTTP/3 stream counts as one request.

Three more options bound connections rather than requests. They live on
`WebServerOptions` and are enforced by the managed TCP listener, so they cover
HTTP/1.1 and HTTP/2; `RequestHeadersTimeout` also bounds each HTTP/3 stream's
header. See [connection limits](#connection-limits).

| Option | Default | When exceeded |
| --- | --- | --- |
| `MaximumConnections` | null (unlimited) | new connection closed |
| `RequestHeadersTimeout` | null (30 seconds) | connection closed; HTTP/3 stream reset |
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
        .WithMinimumRequestBodyDataRate(1024, TimeSpan.FromSeconds(10)) // 1 KiB/s after 10 s
        .WithRequestBodyReadTimeout(TimeSpan.FromSeconds(15)) // per read
        .WithMaximumConcurrentRequests(64, queueLimit: 128)   // 64 at once, 128 waiting
        .WithRequestTimeout(TimeSpan.FromSeconds(30)))
    .WithWebApi("/api", m => m.WithController<ImportController>());
```

Rejections are ordinary HTTP exceptions, so `OnHttpException` renders them; the
default handler produces an HTML error page and `HttpExceptionHandler.PlainTextResponse`
a plain-text one.

The owned listeners reject oversized headers and known oversized bodies before
admission to a request slot. The shared dispatch path also checks declared body
length after admission, covering custom listener adapters. These checks all run
before any module; oversized headers never take or wait for a slot.

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
  oversized HTTP/1.1 request target receives 414 and oversized header fields receive
  431, while an HTTP/2 or
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

**Unread bodies.** When the handler returns, the server reads the rest of a body the
handler opened, so an excess byte the handler never read still fails the request with
413 while the response is not committed; that drain is held to the minimum data rate,
and each of its reads waits at most the read deadline, when one is set. A body the
handler never opened does not hold back the response. On HTTP/1.1 the connection
discards it after the response, under the keep-alive deadline, the byte limit and any
read deadline, but not the data rate, and closes instead of carrying another request
when one of them ends the discard. On HTTP/2 and HTTP/3 the transport ends it after the
response:
HTTP/2 resets the stream with `NO_ERROR` and HTTP/3 sends `STOP_SENDING` with
`H3_NO_ERROR` (RFC 9113 section 8.1, RFC 9114 section 4.1).

### `100 Continue`

A client that sends `Expect: 100-continue` waits for an interim `100 Continue` response
before uploading its body. While a body limit applies, which by default it does, the
engine holds `100 Continue` back and sends it when a handler first reads the body. With
`MaximumRequestBodyBytes` set to null and no route setting a limit, the engine sends it
as soon as the request head arrives, as v1 did. While it is held back:

- A request refused before dispatch, whether by its declared length, its headers, a
  full queue or a deadline, is answered without `100 Continue`, so the client never
  uploads the refused body.
- A handler that reads the body triggers `100 Continue` on its first read, and the
  client starts sending immediately rather than after its own fallback timer.
- On HTTP/1.1, a response that completes while `100 Continue` is still owed is sent
  with `Connection: close`, and the connection closes instead of waiting for a body
  the client was never invited to send. A handler that ignores the body of an
  `Expect: 100-continue` request therefore closes the connection. .NET's `HttpClient`
  sends the body anyway after a 2xx response, and can then see the close as a reset.
  HTTP/2 and HTTP/3 reset the stream as for any unread body.
- A handler that starts its response before reading the body cannot send
  `100 Continue` any more. The client sends its body only when its own expectation
  timer expires (one second by default in .NET), and on HTTP/1.1 the connection is
  not reused.

**Known limitation.** A client that does not send `Expect: 100-continue` uploads at
once. An HTTP/1.1 client that is still sending when the server answers 413 and
closes often sees a connection reset instead of the response. That is TCP behavior
when the server closes with unread data; HTTP/2 and HTTP/3 clients receive the 413.

### Minimum body data rate

`MinimumRequestBodyDataRate` refuses a body that arrives too slowly while the
application is reading it. The default, 240 bytes per second after a 5-second grace
period, is Kestrel's `MinRequestBodyDataRate`. It applies to every protocol and
listener mode; `CONNECT` tunnels are not request bodies and are not measured. Null
disables it server-wide; a route policy replaces it, or lifts it with
`MinimumDataRate.None`.

- The rate is measured only while a read of `Request.InputStream`, or of a helper that
  reads through it, is waiting for data. Time the handler spends not reading, for
  example before it opens the body or between reads, does not count, and neither does
  a body the handler never opens. The grace period starts with the first read of each
  body.
- A shared heartbeat checks every body with a pending read once per second. Once the
  time spent waiting exceeds the grace period, a body whose bytes read divided by that
  time fall below the rate is refused: the pending read throws a 408 `HttpException`,
  and so does every later read of the same body. A handler that lets it propagate
  answers 408. The check granularity is the heartbeat: a refusal lands between the
  grace period and one second after it.
- On HTTP/1.1 the response carries `Connection: close` and the connection is not
  reused, because the canceled read left the transport at an unknown point of the body.
  The 408 is written before the connection closes, but, as with the 413 above, an
  HTTP/1.1 client that is still sending at that moment can see a connection reset
  instead of the response. Kestrel closes the same way, without a lingering read.
  On HTTP/2 and HTTP/3 the stream is reset after the response and the connection
  carries on.
- An HTTP/2 body that is waiting because the connection's receive window is held by
  other streams' unread bodies, rather than because its client is slow, is not refused
  while that window is low.
- The cost is two timestamps per read and no buffer copy or per-read timer; the source is
  read with the body's own token so the heartbeat can end a pending read, and the
  caller's token is linked to that token once per body, not per read. A synchronous
  `Read` goes through the same path and blocks on it. Measured in
  [the load benchmark](#measured-cost), small requests are unchanged, HTTP/2 and HTTP/3
  1 MiB uploads are at parity, and HTTP/1.1 uploads cost about 1%.
- Unlike the read deadline, the rate stops a client that sends a byte just before each
  deadline. Unlike the request deadline, it does not depend on the handler observing
  the context token.

#### Measured cost

`test/EmbedIO.LoadBenchmark`, the default rate against the same core without it (main
`8083fed5`), runtime 10.0.13, two rounds in each harness role with the roles alternated,
server on CPUs 0-3 and client on 4-11, samples with outlying background CPU excluded.
The scorecard tool is the one Perf Hunt used for #384. Values are baseline to candidate.

| Scenario | Requests/s | Server CPU us/req | Server B/req |
| --- | --- | --- | --- |
| 13 B GET, 64 connections | 216,599 to 215,923 (0.997) | 18.0 to 18.1 | 5,118 to 5,154 |
| TLS 13 B GET, 64 connections | 203,236 to 202,988 (0.999) | 19.3 to 19.2 | 5,170 to 5,196 |
| HTTP/2 TLS 13 B GET, 8 x 32 | 462,881 to 460,860 (0.996) | 8.3 to 8.4 | 9,226 to 9,257 |
| HTTP/3 13 B GET, 8 x 32 | 306,181 to 307,796 (1.005) | 19.8 to 19.6 | 11,953 to 12,008 |
| 1 MiB upload, 16 connections | 13,833 to 13,700 (0.990) | 284.4 to 287.5 | 6,231 to 6,608 |
| TLS 1 MiB upload, 16 connections | 8,913 to 8,826 (0.990) | 439.6 to 442.9 | 6,586 to 6,983 |
| HTTP/2 TLS 1 MiB upload, 4 x 4 | 2,067 to 2,086 (1.009) | 1,504.1 to 1,531.6 | 150,111 to 150,566 |
| HTTP/3 1 MiB upload, 4 x 4 | 1,514 to 1,603 (1.059) | 2,392.5 to 2,292.7 | 96,508 to 96,947 |

Small requests carry no body and are unchanged within the run-to-run spread. HTTP/1.1
uploads cost about 1% throughput and 1% CPU, and about 380 bytes per request: the
body's cancellation source, its link to the caller's token and its heartbeat entry,
allocated once per body, not per read. HTTP/2 and HTTP/3 uploads are at parity. An
earlier revision that registered the caller's token on every read and let the read's
state machine box cost HTTP/3 uploads 17% of their throughput; that is why the link is
per body and the `ValueTask` read uses the pooling builder.

### Body read deadline

`RequestBodyReadTimeout` bounds how long one read of the request body may wait for
data. It applies to each read separately, so a slow but steady upload is not cut off;
a client that stops sending is. It is off by default; set it server-wide or for a route.
Only requests that carry a body are timed.

- A read that misses the deadline throws a 408 `HttpException`, and so does every
  later read of the same body. A handler that lets it propagate answers 408.
- On HTTP/1.1 the response carries `Connection: close` and the connection is not
  reused. On HTTP/2 and HTTP/3 the stream is reset after the response, and the
  connection carries on.
- It applies to every protocol and listener mode. `CONNECT` tunnels are not timed.
- It complements `RequestTimeout`: the read deadline catches a stalled upload even when
  the handler does not observe the context token, and the request deadline bounds the
  whole request.
- It does not stop a client that sends a byte just before each deadline. The
  [minimum body data rate](#minimum-body-data-rate), on by default, does.
- Each timed read lands in a private buffer and is then copied, so a read the deadline
  abandons never writes into the caller's buffer later. In the load benchmark this cost
  about 9% more server CPU per 1 MiB HTTP/1.1 upload, and HTTP/2 and HTTP/3 uploads
  allocated 85 to 100 KB more per MiB, which is why the deadline is off by default.

## Connection limits

These options on `WebServerOptions` apply to the managed TCP listener in the `EmbedIO`
and `EmbedIOCombined` modes. `RequestHeadersTimeout` also applies to HTTP/3 streams.
Setting `MaximumConnections` or `KeepAliveTimeout` on an `EmbedIOHttp3` server throws
`NotSupportedException`, because QUIC connections are not governed by them.

- **`MaximumConnections`** bounds the TCP connections the server keeps open across all
  of its endpoints, counting HTTP/1.1 and HTTP/2 connections alike. A connection
  accepted at the limit is closed at once, before any TLS handshake, without a
  response. A slot frees when an open connection closes, including idle keep-alive
  connections when their deadline passes.
- **`RequestHeadersTimeout`** bounds how long a new connection may take to deliver its
  first complete request head, including the TLS handshake and, on HTTP/2, the
  connection preface. A connection that misses it is closed without a response. Null
  applies the default of 30 seconds, Kestrel's `RequestHeadersTimeout`; before v2 the
  default was 90 seconds. On HTTP/3 it applies, as in Kestrel, to every stream the
  client opens, from the time the server accepts it: a request stream must deliver its
  complete headers and any other stream its stream header, including a WebTransport
  stream's session. A request stream that misses it is reset with `H3_REQUEST_REJECTED`,
  so the client can retry the request; a unidirectional stream gets
  `H3_STREAM_CREATION_ERROR`. One timer for the process checks the deadlines once a
  second.
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

## Per-route policies

A route policy replaces the server-wide body limit, minimum body data rate, body read
deadline and request deadline for requests under one base route. Typical uses are a
larger body limit for an upload endpoint, a tighter one for a JSON API, or no deadline
for a long report.

```csharp
using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:9696/")
        .WithMode(HttpListenerMode.EmbedIO)
        .WithMaximumRequestBodyBytes(64 * 1024)
        .WithRequestTimeout(TimeSpan.FromSeconds(10))
        .WithRouteRequestLimits("/api/uploads", p =>
        {
            p.MaximumRequestBodyBytes = 512L * 1024 * 1024;     // 512 MiB here
            p.RequestTimeout = Timeout.InfiniteTimeSpan;         // no request deadline
            p.RequestBodyReadTimeout = TimeSpan.FromSeconds(30); // but each read must progress
            p.MinimumRequestBodyDataRate = MinimumDataRate.None; // and the rate is lifted
        })
        .WithRouteRequestLimits("/api", p => p.MaximumRequestBodyBytes = 16 * 1024))
    .WithWebApi("/api", m => m.WithController<ImportController>());
```

- A policy property left null inherits the server-wide value. `long.MaxValue` lifts the
  body limit, `Timeout.InfiniteTimeSpan` lifts a deadline and `MinimumDataRate.None`
  lifts the data rate for the route.
- The base route is matched like a module's base route, against the request path before
  any module runs. It can contain parameters such as `/tenants/{id}`, and it also matches
  every path below it.
- Policies are checked in the order they were added and the first match applies, so add
  more specific routes first. A base route can have only one policy.
- `RouteRequestLimits` lists the policies; they lock with the options.
- The declared-length check, the `100 Continue` deferral, the byte count, the data
  rate, the read deadline and the request deadline all use the route's values. `100 Continue` is
  deferred when the server or any route sets a body limit, as the server's default
  limit does.
- Header limits, admission and connection limits stay server-wide: they apply before
  routing or to whole connections.

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

`test/EmbedIO.Tests/Issues/Issue136_RequestLimits.cs`, `Issue136_RequestLimits.Headers.cs`,
`Issue136_RequestLimits.Connections.cs`, `Issue136_RequestLimits.Routes.cs`,
`Issue136_RequestLimits.Defaults.cs` and `Issue136_RequestLimits.DataRate.cs` drive the
managed engine over real connections.
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
| `Expect: 100-continue` with the body limit set to null | `100 Continue` before dispatch, as in v1 | n/a | n/a |
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
| Client disconnects mid-upload | handler sees the failure; request and connection slots return | n/a | n/a |
| Incomplete first request head | closed at `RequestHeadersTimeout` | n/a | stream reset at `RequestHeadersTimeout` |
| Idle connection after a response | closed at `KeepAliveTimeout`, header rounded up | GOAWAY then close | n/a |
| Stream open longer than the idle deadline | n/a | completes normally | n/a |
| No connection options | `Keep-Alive: timeout=15`, unchanged | n/a | n/a |
| Route lifts the server body limit | route accepts 5000 bytes, other routes 413 | same | n/a |
| Route sets a body limit the server lacks | 413 on the route, others unlimited | same | n/a |
| Overlapping routes | first added applies | n/a | n/a |
| Parameterized base route | matches paths below it | n/a | n/a |
| Route request deadline lifted or shortened | long request completes; 503 elsewhere | n/a | n/a |
| Route body read deadline lifted | slow upload completes; 408 elsewhere | same | n/a |
| Route body limit with `Expect: 100-continue` | n/a | refused body never sent | n/a |
| Default body limit, declared length 30,000,001 | 413 before dispatch; 30,000,000 dispatched | 413, body never sent | same |
| Body limit set to null, declared length 30,000,001 | dispatched | n/a | n/a |
| Default header count | 100 field lines served, 101 refused with 431 | 101 refused with 431, 90 served | same |
| Header count replaced or removed | 120 allows 120 and refuses 121; null allows 150 | n/a | n/a |
| Default body read deadline | none | same | same |
| Body read deadline set to 5 s | 5 s on a request with a body | same | same |
| Body the handler never opened | n/a | response sent, body left to the transport | same, for a GET without `content-length` |
| Trickled body the handler never opened | response at once, connection closed at the keep-alive deadline | n/a | n/a |
| Handler disposes its body view under the read deadline | connection reused | n/a | n/a |
| Second read of a disposed body | `ArgumentException` from the text helper, `ObjectDisposedException` from the byte helper | n/a | n/a |
| Decoded limit with `SupportCompressedRequests` | follows the server or route body limit until set | n/a | n/a |
| Trickled body, handler reading | 408 and close after the grace period, asynchronous and synchronous reads | 408, connection carries on | same |
| Body at the rate | completes | completes | n/a |
| Trickled body the handler is not reading yet | not refused; read in full afterwards | n/a | n/a |
| 4 MiB upload under the default rate | completes | same | same |
| Rate null, route sets one; rate set, route lifts it | trickle served; 408 on the route, and the reverse | n/a | n/a |
| Trickled body the handler never opened, rate set | not monitored; discarded at the keep-alive deadline | n/a | n/a |
| Rate monitor alone | starves only a pending read past the grace period; back pressure excuses it; failing cancels the read with a sticky 408 | n/a | n/a |

The data-rate cases shorten the heartbeat to 200 ms through reflection and restore it.

The .NET HTTP/1.1 client aborts its own connection after a refusal that arrives
without `100 Continue`, so the HTTP/1.1 refusal case is checked on the wire with a raw
socket. `TestWebServer` locks its options in its constructor and has no way to set
these limits.

## Not covered

HTTP/3-specific limits, including QUIC connection counts and idle deadlines, are tracked
in #135. Route policies do not set per-route concurrency; `MaximumConcurrentRequests`
stays server-wide.
