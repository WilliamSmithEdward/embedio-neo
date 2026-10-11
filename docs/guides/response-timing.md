# Response-time diagnostics

[Upstream EmbedIO #407](https://github.com/unosquare/embedio/issues/407) described fast REST responses with logged durations that followed the delay between requests. The reporter used EmbedIO 3.1.3 on Windows 10/.NET Core 3.0 and confirmed the symptom persisted without session management. That original application and runtime are not reproduced here.

## What the completion log measures

The existing completion entry retains its format:

```text
[WebServer] [request-id] GET /api/statistics: "200 OK" sent in 12ms (chunked)
```

Its duration measures the server handling operation: from entry into the WebServer request pipeline through awaited handlers, response flush, request cleanup and context completion callbacks. It excludes time spent waiting for a request to arrive on an idle connection, request-header parsing and waiting before the WebServer begins handling the admitted context. Reading an upload inside a handler is part of handling and remains included.

This is not end-to-end client latency or time to first byte. DNS, connection establishment, TLS negotiation and client processing are outside this interval. Conversely, the client can receive a response before a slow `OnClose` callback finishes; that callback remains included in the server's completion duration. Do not compare this value directly with Postman's elapsed time as if both tools observed identical boundaries.

## Why the previous value was inflated

HTTP/1 creates the next context before waiting for its request. The previous completion log read `IHttpContext.Age`, so the idle interval and header wait became part of the reported processing time. A new monotonic timestamp local to each handling operation avoids that attribution. It uses .NET's Stopwatch timestamp clock and adds no dependency, new API or clock object allocation.

`IHttpContext.Age` retains its documented meaning: milliseconds since that context was created. It is not reset at admission or at handler entry. Existing consumers can continue to use it as context lifetime; use the completion log for server handling duration.

The same WebServer pipeline handles HTTP/1 and multiplexed requests. Each operation has its own timing value, preserving independent request IDs, status/body metadata, diagnostic filtering and completion ordering. An application that bypasses WebServer and directly uses the listener is responsible for its own diagnostics.

## Validation scope

Eight of fourteen controlled cases fail against unchanged modern source: TCP/TLS keep-alive idle gaps with and without sessions, partial-header waiting, and awaited JSON controller calls. Six positive controls already pass: handler body-read delays, completion callbacks after client receipt, and HTTP/2 controller compatibility. The candidate passes all fourteen while retaining public context-age semantics.

The fixture observes the next HTTP/1 context before imposing the idle gap, reuses the same client port and compares the measured gap with context age minus logged handling time. It does not rely on a fast-machine upper response-time limit or client reconnect retries. Session controls retain the first response cookie. Client/server deadlines and production shutdown behavior remain unchanged. Historical SWAN/session-callback hypotheses are preserved as discussion rather than asserted causes; SWAN is no longer part of the current runtime.

Tracked in [Neo #315](https://github.com/WilliamSmithEdward/embedio-neo/issues/315). Integration requires an all-green exact-head check set; this guide does not claim the original 2019 environment was tested or a new release published.
