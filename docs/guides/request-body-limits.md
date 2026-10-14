# Request-body byte limits

Configure limits before the server options lock. These example values are application choices:

```csharp
using EmbedIO;

var options = new WebServerOptions()
    .WithMaximumRequestBodyBytes(1024 * 1024)
    .WithSupportCompressedRequests(true)
    .WithMaximumDecompressedRequestBodyBytes(4 * 1024 * 1024);
```

The raw limit defaults to 30,000,000 bytes, Kestrel's `MaxRequestBodySize`. The decoded limit follows the request's raw limit, including a route's, until it is set. Null removes either limit. The raw limit accepts zero for empty entities only and rejects negative values. It counts encoded entity bytes after transport framing removal and before content decoding. Chunk syntax, HTTP/2 padding, HTTP/3 frame metadata and trailers do not consume that budget. The decoded limit separately caps output from the compressed-request helpers; it does not bound decoder memory.

The owned HTTP/1, HTTP/2 and HTTP/3 listeners capture the selected server and route's immutable policy before sending `100 Continue` or entering request admission. Route policies use the existing first-match order. A route's null value inherits the server value; `long.MaxValue` can lift a configured raw limit. On shared endpoints, the request's routed owner supplies its policy, rather than the server that first opened the endpoint.

An excessive known `Content-Length` goes through the selected server's existing 413 error pipeline before module dispatch, input reads, admission-slot waiting or automatic `100 Continue`. Configured `OnHttpException` handlers retain control of formatting. If header and body budgets are both exceeded, the header policy's 431 takes precedence.

Automatic refusal does not invite or drain the rejected upload. A custom error handler can explicitly read input, which can send a deferred `100 Continue`; those reads still obey the captured raw-byte budget. Disposing that application view does not close its borrowed transport before the error response is written.

Unknown-length input can return an in-limit prefix before the first excess byte raises a sticky 413. At the numeric boundary, a private one-byte read distinguishes valid EOF from excess without returning the probe byte as application data. Finish input validation before committing transactional changes. Request helpers and direct reads share the same raw counter.

While a raw limit applies, the rest of a body the handler opened is also validated during request completion, before the response ends. A body the handler never opened does not hold back the response: HTTP/1 discards it after the response under the connection's keep-alive deadline and this limit, and closes the connection instead of reusing it when either ends the discard; HTTP/2 and HTTP/3 let the transport end the stream. Before response commitment, a body-limit failure can use normal error formatting. After a different final response has committed, only the affected HTTP/1 connection or HTTP/2/3 request is terminated; no second final status can replace bytes already sent. HTTP/1 does not reuse an over-limit input, while multiplexed sibling requests remain independent.

CONNECT carries a negotiated transport rather than an ordinary entity and is excluded from this raw-body policy. Original framing validation still owns declared-length and chunk errors, trailer validation and connection read-ahead. Merely reaching the configured maximum does not establish validated EOF or complete request trailers.

Custom `WebServerBase` context adapters retain their existing policy path, including declared-length rejection where applicable. Automatic owned-stream accounting is not imposed on arbitrary third-party request implementations; their integration must provide its own stream ownership and actual-byte enforcement.

See [request limits, admission and deadlines](request-limits.md) for header budgets, queueing and timeouts. This work is part of the unreleased Neo v2 candidate. Compiling the .NET Standard asset is separate from execution evidence on an older runtime.
