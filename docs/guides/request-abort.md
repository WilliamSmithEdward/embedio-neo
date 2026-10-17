# Request-abort cancellation

Requires 2.9.0 or later. Request-abort observation is opt-in; existing
`IHttpContext.CancellationToken`, unannotated controller parameters, listener
settings and request deadlines keep their existing behavior.

## Acquire and pass the token

```csharp
var token = context.GetRequestAbortedToken();
await LoadReportAsync(token);
await context.SendStringAsync("complete", "text/plain", WebServer.Utf8NoBomEncoding);
```

Acquire during the active HTTP request and pass the token to work that supports
cancellation: database calls, `HttpClient`, asynchronous enumerators and stream
reads/copies. Do not dispose the token's source; the request owns it. Repeated
acquisitions for the same mode return the same token. A completed request's token
is never reused for another request. Successful response completion detaches its
parent registration and leaves an uncanceled token healthy, even if the server
stops later. Cancellation already requested before completion is retained.

The token observes the context cancellation token **current at first acquisition**
(including the server shutdown/deadline token installed before the handler),
observed request/connection failures, and HTTP/2 or HTTP/3 stream resets. Replacing
`context.CancellationToken` later does not rebind an already acquired token.
Cancellation callbacks run away from transport and shutdown callback stacks;
keep callbacks short and cooperative. Cancellation can race completion and does
not prove whether a client received or committed any part of the response.

`IHttpRequestAbortFeature` is an optional capability. Existing implementations of
`IHttpContext` need no new members. Use
`context.TryGetRequestAbortedToken(out var token)` with third-party contexts;
unsupported contexts return false, while the direct getter throws
`NotSupportedException`. First acquisition after completion or protocol handoff
throws `InvalidOperationException`; previously acquired tokens remain retrievable.

## HTTP/1 half-close and browser-style endpoints

A TCP FIN means the client ended its input. A well-formed HTTP/1 client may
half-close its send direction and continue reading the complete response;
[RFC 9112 section 9.6](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.6)
explicitly allows that. The default token preserves this behavior.

For clients whose input closure should stop application work, explicitly choose:

```csharp
var token = context.GetRequestAbortedToken(cancelOnHttp1InputEnd: true);
await WaitForApplicationEventAsync(token);
```

This mode treats HTTP/1 input EOF as cancellation **even for a valid half-close**.
Use it only when the endpoint's client contract allows that interpretation. A
Windows .NET 10.0.13 probe observed `HttpClient` cancellation sending FIN; it did
not establish that all clients, operating systems or proxies do so. The normal
and input-end modes have independent stable tokens; input EOF alone cancels only
the latter. A reset or observed failed/incomplete request body cancels both.
Merely acquiring a token does not automatically abort a response. If the handler
throws `OperationCanceledException` for an observed abort, the server terminates
an incomplete response rather than adding a successful body terminator.

HTTP/1 uses one lazy, non-consuming socket observer per opted-in connection,
checking active work at roughly 50 ms intervals. It never drains request bodies,
pipelined requests or TLS ciphertext. No observer or cancellation source is
created for routes that never acquire this token. Unread data, TLS buffering,
TLS close notifications, proxies, thread scheduling and silent network loss can
delay or prevent observation until another transport operation. An incomplete
upload's EOF/read error also signals cancellation when the body reader observes
it. Keep finite application deadlines; this is not an instantaneous disconnect
or delivery-confirmation API.

The HTTP/1-specific flag is ignored for HTTP/2 and HTTP/3. Normal request
END_STREAM/FIN and a successfully completed response do not mean abandonment;
a reset cancels only the affected request, while a connection failure can affect
all of its streams. Protocol handoff ends HTTP-request observation. WebSockets,
tunnels and WebTransport retain their own lifetime APIs.

## Controller binding

```csharp
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

public sealed class EventsController : WebApiController
{
    [Route(HttpVerbs.Get, "/wait")]
    public async Task<string> Wait(
        [RequestAborted(CancelOnHttp1InputEnd = true)] CancellationToken token)
    {
        await Task.Delay(1000, token);
        return "ready";
    }
}
```

`[RequestAborted]` without the property uses the half-close-safe default. The
attribute requires a `CancellationToken` parameter. Reflection registration
rejects other types; generated bindings report a build diagnostic. Generated
bindings support Native AOT and omit this infrastructure parameter from OpenAPI.
An unannotated token retains its existing route/default binding; it does not
implicitly acquire the new token.

## Streaming and uploads

Pass the token explicitly through existing APIs:

```csharp
var token = context.GetRequestAbortedToken(cancelOnHttp1InputEnd: true);
var events = context.OpenEventStream();
await events.WriteAsync("ready", cancellationToken: token);

// In a separate multipart endpoint:
using var reader = context.OpenMultipartReader();
var section = await reader.ReadNextSectionAsync(token);
if (section != null)
    await section.Body.CopyToAsync(destination, token);
```

For streamed JSON, pass it to `JsonStream.Array(...).WriteAsync(context, token)`
or `JsonStream.Ndjson(...).WriteAsync(context, token)` and honor cancellation in
the asynchronous source. Existing streaming defaults are unchanged. Controller
binding occurs during argument binding; it does not retroactively cancel earlier
authentication, body binding or application work that never received this token.
Clean up temporary uploads in `finally` and commit them only after full validation.

`TestWebServer` supports the capability using the caller/context token, with no
physical disconnect or HTTP/1 input-end semantics. Real-wire tests cover reset,
half-close, keep-alive reuse, TLS upload integrity, streaming, callback failures
and HTTP/2–3 stream isolation. The `request-abort` state machine holds three
physical connections concurrently, checks a separate owner/generation ledger,
retains replayable traces, and has a foreign-token negative control. See
[stateful fuzzing](stateful-surface-fuzzing.md).
