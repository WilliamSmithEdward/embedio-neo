# Metrics and tracing

[Neo #137](https://github.com/WilliamSmithEdward/embedio-neo/issues/137) asks for
standard HTTP metrics and distributed tracing. EmbedIO publishes them through
`System.Diagnostics`, following the
[OpenTelemetry HTTP semantic conventions](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/).
Telemetry is off by default and needs no exporter or extra package; an application that
enables it collects the data with any `MeterListener` or `ActivityListener`, for example
the OpenTelemetry SDK.

## Enabling it

```csharp
using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:9696/")
        .WithMode(HttpListenerMode.EmbedIO)
        .WithTelemetry("public-api"))      // optional server name
    .WithWebApi("/api", m => m.WithController<ItemsController>());
```

`WithTelemetry` sets `EnableTelemetry` and, when given, `TelemetryServerName`. Both lock
with the options. With the OpenTelemetry SDK, subscribe to the meter and source named
`EmbedIO.Server`:

```csharp
using var meters = Sdk.CreateMeterProviderBuilder().AddMeter("EmbedIO.Server") /* exporter */ .Build();
using var traces = Sdk.CreateTracerProviderBuilder().AddSource("EmbedIO.Server") /* exporter */ .Build();
```

Telemetry is built on the .NET 10 base class library. The .NET Standard 2.0 asset has no
metrics or activity API without an extra package, so a server built on it throws
`PlatformNotSupportedException` when telemetry is enabled.

## Metrics

Meter `EmbedIO.Server`, versioned with the assembly.

| Instrument | Type | Unit | Attributes |
| --- | --- | --- | --- |
| `http.server.request.duration` | histogram | `s` | `http.request.method`, `url.scheme`, `server.port`, `network.protocol.name`, `network.protocol.version`, `http.response.status_code`, `http.route`, `error.type`, `embedio.server.name` |
| `http.server.active_requests` | up-down counter | `{request}` | `http.request.method`, `url.scheme`, `server.port`, `embedio.server.name` |
| `http.server.request.body.size` | histogram | `By` | as for duration |
| `http.server.response.body.size` | histogram | `By` | as for duration |
| `embedio.server.active_connections` | up-down counter | `{connection}` | `network.transport`, `server.port`, `embedio.server.name` |
| `embedio.server.connection.duration` | histogram | `s` | as for active connections, plus `network.protocol.version` |
| `embedio.server.active_websockets` | up-down counter | `{connection}` | `http.request.method`, `url.scheme`, `server.port`, `http.route`, `embedio.server.name` |
| `embedio.server.websocket.duration` | histogram | `s` | as for active WebSockets |

The `http.server.*` instruments follow the semantic conventions; the conventions define
no connection or WebSocket instruments, so those use the `embedio.server` prefix.

The duration histogram advertises the bucket boundaries the semantic conventions
recommend, from 5 ms to 10 s.

- **Duration** runs from dispatch until the response is complete and the context closed,
  so a streamed response is measured to its end. It includes time spent waiting for a
  request slot.
- **`http.request.method`** is one of `GET`, `HEAD`, `POST`, `PUT`, `DELETE`, `CONNECT`,
  `OPTIONS`, `TRACE`, `PATCH` or `QUERY`; any other method is `_OTHER`, so clients cannot
  add dimensions.
- **`http.route`** is the matched route template, never the requested path:
  `/api/items/{id}` for a Web API controller route under `/api`, `/v1/items/{id}` for a
  routing module inside a `/v1` module group. A module without its own routes, such as a
  file module, reports its base route. A request no module handled has no route.
- **`network.protocol.version`** is `1.0`, `1.1`, `2` or `3`.
- **`error.type`** is the exception type when a handler failed or was cancelled, such as
  `System.InvalidOperationException` for an unhandled exception or
  `System.Threading.Tasks.TaskCanceledException` for a handler stopped by the request
  deadline. Otherwise it is the status code for a 5xx response. An `HttpException` is an
  intended response: a 4xx one is not an error, and a 5xx one reports its status code.
  Cancellation while completing an already handled request, for example when the server
  stops, is not recorded as an error.

- **Body sizes.** The request size counts the body bytes the application read, so an
  unread or partly read body reports what was read; a request without a body records no
  request size. The response size counts the body bytes sent, which is zero for a `HEAD`
  response. Both are recorded with the duration's attributes.
- **Connections.** A TCP connection is counted from admission until its transport
  closes, whether it carries HTTP/1.1 or HTTP/2; its duration reports `1.1` or `2`. A
  QUIC connection is counted from a completed handshake until it closes, with
  `network.transport` `quic` and version `3`. Connections refused by
  `MaximumConnections` or rejected before their handshake completes are not counted. A
  port shared with another server reports through the server that opened it.
- **WebSockets.** A WebSocket is counted from the accepted handshake until the
  `WebSocketModule` finishes with it. The upgrade request's own duration also covers the
  whole WebSocket lifetime, because the request ends when the socket does.

No query string, header value, body content or user identity becomes a metric attribute.

## Server spans

Activity source `EmbedIO.Server`. When a listener is attached, each request gets a span
of kind `Server`.

- **Parent.** The span's parent comes from the request's `traceparent` and `tracestate`
  headers through `DistributedContextPropagator.Current`, which reads W3C trace context by
  default. Without them the span starts a new trace.
- **Handlers.** `Activity.Current` inside a handler is the server span, so spans and logs
  the application creates there join the request's trace.
- **Name.** `{method} {route}`, for example `GET /api/items/{id}`, or just the method when
  no route matched.
- **Attributes.** The metric attributes above, plus `url.path`, `client.address`,
  `user_agent.original` and `http.request.method_original` for `_OTHER` methods. The query
  string is not recorded, because it often carries secrets.
- **Status.** `Error` whenever `error.type` is set.

## Several servers

The meter and the activity source are shared by every server in the process. Each
measurement carries `server.port`, and `embedio.server.name` when the server has a name.
Give each server a fixed, low-cardinality name to tell them apart; the name is a metric
dimension.

## Cost

With telemetry disabled, requests run through the same single state machine as before;
routing records no route templates and no counting stream is added. The response writers
keep one byte counter in every mode.

A sequential measurement on one Windows workstation sent 5000 keep-alive GET requests
over one HTTP/1.1 connection per round, after 2000 warm-up requests, in six alternating
rounds per mode. Allocations, counted for the whole process including the client, were
steady:

| Mode | Allocated per request (median) |
| --- | --- |
| Telemetry off | 16,095 B |
| Enabled, nothing listening | 16,258 B (about 160 B more) |
| Enabled, meter and activity listeners attached | 17,116 B (about 1 KB more) |

Latency fell from round to round as the runtime warmed up, and in the last three rounds
the modes measured between 45 and 60 µs per request with no consistent order, so the
latency cost is below this measurement's resolution. The figures come from one machine
shared with other work and a single connection; they are not throughput or concurrency
results.

## Measured scenarios

`test/EmbedIO.Tests/Issues/Issue137_Telemetry.cs` drives the managed engine over real
connections with a `MeterListener` and an `ActivityListener`:

| Scenario | Result |
| --- | --- |
| Telemetry not enabled | nothing recorded for the server |
| Web API route over HTTP/1.1 and HTTP/2 | template route, method, scheme, protocol version, status, port; active requests +1 then -1; server span named `GET /api/items/{id}` |
| `traceparent` and `tracestate` sent | span joins the trace with a remote parent; `Activity.Current` in the handler is the span |
| Unhandled exception | 500, `error.type` is the exception type, span status `Error` |
| `HttpException` 403 and 503 | 403 has no `error.type`; 503 reports `503` |
| Handler stopped by the request deadline | 503 with the cancellation's exception type |
| Unmatched path, unknown method | 404 without `http.route`; `PROPFIND` reported as `_OTHER` |
| Routing module inside a module group | `/v1/items/{id}` |
| Streamed response with a 400 ms pause | duration covers the whole response |
| Two named servers | measurements separated by `embedio.server.name` |
| Options | off by default, empty name rejected, locked with the options |
| 12,345-byte POST and a GET over HTTP/1.1 and HTTP/2 | request size 12,345 only for the POST; response sizes 5 and 1 |
| Two requests on one connection, HTTP/1.1 and HTTP/2 | one connection counted +1 then -1; duration with version `1.1` or `2` |
| One HTTP/3 request | QUIC connection counted +1 then -1; version `3` |
| WebSocket echo held for 200 ms | active WebSockets +1 then -1; duration with route `/echo/` |
| Server without telemetry | no connection measurements for its port |

## Not covered

Concurrency and throughput overhead under load has not been measured. Request
instrumentation for HTTP/3 shares the dispatch path with HTTP/1.1 and HTTP/2; the HTTP/3
test covers its connection metrics.
