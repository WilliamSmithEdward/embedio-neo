# Migrating from EmbedIO-Neo v1 to v2

This guide explains the API and behavior changes to consider when upgrading from
EmbedIO-Neo v1 to v2. Neo v2 is unreleased; these changes are not available in
the published v1 package.

The comparison baseline is the published source tag
[v1.0.3](https://github.com/WilliamSmithEdward/embedio-neo/tree/v1.0.3).
If you use an earlier v1 patch, also review the
[general migration notes](migration.md): they include historical upstream-to-Neo
and earlier v1 changes that are not new in v2.

## Minimum HTTPS TLS version

Neo v2 requires TLS 1.2 or later for HTTPS. TLS 1.0 and TLS 1.1 clients can no
longer connect, even when local OS policy would otherwise allow those versions.
TLS 1.3 remains enabled where the runtime and security provider support it; this
is a minimum-version change, not a restriction to TLS 1.2 alone. The Standard
asset resolves the executing runtime's public TLS 1.3 flag rather than imposing
an unknown protocol value on an older runtime.

The same minimum applies with an explicit `ServerTlsPolicy`. Its default
`EnabledProtocols = SslProtocols.None` selects supported TLS 1.2/1.3, rather than
allowing legacy protocols through OS defaults. Explicit masks may choose TLS 1.2,
TLS 1.3 or both; legacy and unknown bits throw before changing the policy.

Update old client runtimes and embedded-device TLS stacks. Check reverse proxies,
gateways and inspection appliances too: their connection to this server must use
TLS 1.2 or later, regardless of what version the end user's browser negotiates
with the intermediary. Plain HTTP and application API signatures are unchanged.
OS certificate, cipher and trust policy still apply; the minimum does not make a
weak cipher or untrusted certificate acceptable.

The IETF deprecated TLS 1.0 and TLS 1.1 in [RFC 8996](https://www.rfc-editor.org/rfc/rfc8996.html).
Check that your target runtime and security provider can negotiate the intended
version; source configuration alone does not establish support for either allowed
version in every deployment.

## Rebuild and update source references

V2 replaces the HTTP engine and changes some APIs. Rebuild dependent assemblies;
do not treat the major upgrade as a binary-compatible drop-in. Changes from
v1.0.3 include:

| Neo v1.0.3 API | V2 source migration |
| --- | --- |
| IHttpRequest.RawUrl | Use IHttpRequest.RawTarget. It retains the received request-target text; update custom adapters too. |
| Validate.Url returns string | It returns Uri. Use the Uri directly, or ToString when text is required. |
| RedirectModule.RedirectUrl is string | It is Uri; existing string constructors remain available. |
| ITestWebServer.BaseUrl is string | It is Uri; update custom test-server implementations and text comparisons. |

For a listener that uses a rotating certificate source, call
`EmbedIO.Net.HttpListener.CreateWithCertificateSource(source)`. Replace calls to
`new HttpListener(source)` with this factory. The source and its certificates
remain application-owned; a null source throws `ArgumentNullException`.
The two-argument `HttpListener(source, tlsPolicy)` constructor and
`WebServerOptions.CertificateSource` remain available. Published v1 calls such as
`new EmbedIO.Net.HttpListener(null)` and `new EmbedIO.Net.HttpListener(default)`
keep selecting the optional certificate constructor, avoiding ambiguous overloads.

Review the [API migration notes](migration.md#warning-free-api-cleanup-unreleased-owner-approved)
for named-argument changes, URI overloads, nullability annotations, error parameter
names, cache ownership and exception-recovery boundaries. Specify the intended
argument type when passing null to overloaded string/Uri entry points.

The System.Text.Json migration and SWAN removal already occurred in Neo v1;
upgrading to v2 does not introduce them again. Still run serialization contract
tests for custom converters, unsupported payloads, numeric precision and exact
response bytes against the final v2 package. The
[JSON compatibility guide](../user-reports/json-migration-compatibility.md)
describes the behavior and its limits.

## Unhandled exceptions return an empty 500

Neo v2 uses `ExceptionHandler.EmptyResponse` as the default unhandled-exception
handler. Before response headers are committed, an otherwise unhandled application
exception produces an empty `500 Internal Server Error` response. The handler does
not add exception type or message headers. Neo v1's default HTML page included the
exception type and
message even when `ExceptionHandler.IncludeStackTraces` was false.

Exceptions still reach server logging. Custom exception handlers and explicit
application-authored `HttpException` responses retain their behavior. An exception
after response commitment still terminates that response; it cannot replace its
status or body with a 500.

To retain the detailed HTML page explicitly, configure the server or module:

```csharp
server.HandleUnhandledException(ExceptionHandler.HtmlResponse);
```

`ExceptionHandler.IncludeStackTraces` and `ContactInformation` still configure that
HTML handler. `ExceptionHandler.EmptyResponseWithHeaders` remains an explicit
alternative that exposes the exception type and message in diagnostic headers.
Use these detailed handlers only where exposing application diagnostics is intended.
For structured API errors, the [opt-in problem-details handler](../guides/problem-details.md)
remains available. Update clients that parse the old default HTML page before
adopting v2.

## Direct HTTP/1 response flushing

Calling `Response.OutputStream.Flush()` or `FlushAsync()` on an open HTTP/1
response now sends its final headers before the first body write. Flushes run in
the same order as body writes and do not finish the response. Set status, cookies,
content length and other response metadata before the first successful flush;
the guarded response properties cannot be changed after headers are sent.

A flush canceled while waiting for writer ownership never overtakes that writer;
a pre-canceled first flush leaves headers mutable. Transport failure makes further
output unusable, following `IgnoreWriteExceptions`.
Flushing disposed output reports `ObjectDisposedException`. Explicit HEAD flushes
also commit representation headers, while HEAD writes and completion still emit
no body bytes. Empty writes keep their existing behavior.

Automatic server cleanup completes direct HTTP/1 output without calling the
application flush path. Buffered serializers and other output wrappers retain
their flushing behavior, including empty responses and final framing. Test custom
streaming code against the v2 package before upgrading.

## Explicit JSON metadata method names

The Native AOT APIs use distinct names: `Json.SerializeWithMetadata`,
`Json.DeserializeWithMetadata`, `GetRequestDataWithMetadataAsync` and
`SendDataWithMetadataAsync`. Use these names for explicit JSON metadata; they keep
null-options calls unambiguous and preserve serializer callback selection for null
data. No published v1 metadata API is renamed.

Existing `Json.Deserialize<T>(json, null)`, options-based serialization and
`SendDataAsync(serializer, null)` retain their signatures and behavior. Explicit
metadata still uses its own options and supports source-generated JSON in trimmed
and Native AOT applications. See [the Native AOT guide](../guides/trimming-and-native-aot.md).

## Listener selection and deployment

V2 replaces the inherited Mono listener with the new HTTP engine and removes the
Microsoft `System.Net.HttpListener` backend.

Remove `WithMicrosoftHttpListener()` and `HttpListenerMode.Microsoft` from source
configuration. Applications that persisted its numeric value, 1, must migrate
their configuration explicitly: v2 rejects that selection instead of silently
choosing another implementation. The remaining mode values stay 0, 2 and 3.
The public runtime adapters `EmbedIO.Net.Internal.SystemHttpRequest` and
`SystemHttpResponse` are also removed. Use `IHttpRequest` and `IHttpResponse`
instead of constructing adapters around a `System.Net.HttpListenerContext`.
HTTP.sys URL reservations and kernel certificate bindings are specific to the
removed backend; use the replacement engine's HTTPS certificate configuration and
validate deployment guidance for your application model.

HTTP/3 uses `System.Net.Quic` for transport. This is distinct from the removed
`System.Net.HttpListener` backend. The native MsQuic application provider is not
available for public selection; do not depend on it as the default transport.
Keeping the EmbedIO mode name does not retain the old parser's permissive behavior.

Choose the mode appropriate for your deployment:

| Mode | Behavior | Migration action |
| --- | --- | --- |
| EmbedIO | HTTP/1 and HTTP/2 over TCP. | Exercise existing routes and clients against the new engine. |
| Microsoft (v1 only) | Removed in v2. | Remove the mode selection and migrate backend-specific deployment configuration. |
| EmbedIOHttp3 | Opt-in HTTP/3 over UDP, without a TCP listener. | Use the .NET 10 asset, supported native QUIC, HTTPS and a certificate with its private key. |
| EmbedIOCombined | HTTP/1 and HTTP/2 over TCP alongside HTTP/3 over UDP on the same HTTPS prefixes. | Permit both transports; startup requires both bindings and has no silent TCP-only fallback. |

Library targets remain .NET Standard 2.0 and .NET 10. HTTP/3 and combined mode
require the .NET 10 asset and runtime/native QUIC support; the legacy asset cannot
provide them. Library namespaces and package identities remain preserved.
HTTP/2 over TLS requires the ALPN-capable .NET 10 asset. Cleartext HTTP/2 support
on .NET Standard does not provide TLS negotiation support.

Follow the [HTTP/3 deployment guide](../guides/http3.md) for certificate lifetime,
key provisioning, hostname validation, native libraries and platform limits.
HTTP/3 availability does not imply automatic Alt-Svc discovery or an available
WebTransport server. Native MsQuic transport, unreliable datagrams and WebTransport
are not yet available as supported public deployment options. Consult the
[capability status](../project/http-engine.md) before selecting those features.

## Request framing and client behavior

The v2 managed parser deliberately rejects malformed and ambiguous input. Send
CRLF line endings, valid field syntax and one unambiguous body framing scheme.
Remove conflicting Content-Length values, Content-Length combined with
Transfer-Encoding, duplicate Host fields and unsupported transfer codings from
clients or proxies. Equal repeated decimal Content-Length remains accepted.
Do not depend on an invalid request being normalized into a usable request.

Chunked uploads are now decoded. HasEntityBody is true and ContentLength64 can
be -1: read the input stream to completion rather than treating an unknown length
as an empty body. Chunk trailers are validated separately and are not merged
into ordinary request headers. Finish body reads before response closure and do
not concurrently read the same request stream.

Truncated fixed-length bodies fail instead of returning a misleading clean EOF.
Malformed body framing can produce a generic 400 before response output starts;
a response that has already started is aborted instead of receiving a second
status or a clean terminator. Independent clients and requests remain isolated.
Request-head budget failures use 414 for an oversized target and 431 for oversized
fields. Update tests that assert the old permissive input or generic error status.
See [framing details](migration.md#managed-http-framing-unreleased) and
[body-failure handling](migration.md#incomplete-and-malformed-http1-request-bodies-unreleased).

The inherited 100-request keep-alive cap is removed. The idle timeout remains
15 seconds, and explicit Connection: close, cancellation and drain still apply.
If an application needs connection rotation, request closure explicitly; do not
use the old request count or Keep-Alive max value as a signal.

## Response statuses, output and shutdown

Neo v2 restricts final-response StatusCode assignments to 200-599.
Manual assignments of 100-199 or 600-999 will throw before changing the response.
Valid unregistered codes such as 471 remain supported. Map internal error codes
to a valid HTTP status and carry the internal identifier in the payload.

Use the optional IHttpResponseSections.SendInformationalAsync for supported
interim responses, followed by a final response. Use AcceptWebSocketAsync or an
authorized AcceptTunnelAsync for a negotiated 101 switch. Automatic 100 Continue
and negotiated HTTP/1 handshakes remain supported. HTTP/1.0 does not receive
informational responses, and HTTP/2/HTTP/3 do not use 101. This follows
[RFC 9110 section 15](https://www.rfc-editor.org/rfc/rfc9110.html#section-15).

Bodyless statuses and HEAD cannot emit representation bytes. Configure metadata
before committing output; a 304 representation length must describe the selected
representation rather than a body sent with that response.

HTTP/1 also suppresses attempted content writes
for 205 Reset Content and commits Content-Length: 0 without chunk framing. The
previous HTTP/1 model emitted those bytes, unlike HTTP/2 and HTTP/3. Applications
which used 205 with a payload should use a body-capable status such as 200 instead.
See [RFC 9110 section 15.3.6](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.3.6).

For HTTP/1 responses, declare Content-Length only when it matches the encoded
bytes actually sent, including any transformation, or use automatic/chunked
framing. A write exceeding that length throws ProtocolViolationException before
submitting those bytes. Closing
with an incomplete declared body closes the connection; it no longer rewrites an
explicit positive length to zero or permits another response on that connection.
HEAD and bodyless representation metadata retain their separate semantics.

A failed transport submission, including cancellation during output, prevents
later body/footer bytes and connection reuse. IgnoreWriteExceptions continues to
suppress managed transport-write exceptions; it cannot restore failed framing.
Cancellation before obtaining the writer gate leaves otherwise healthy output
usable.

Exception handlers now run only while the response is replaceable, that is,
before its final headers reach the wire, and they start from a response whose
Content-Length, Content-Encoding, Content-Range, ETag and chunked framing from
the failed handler have been removed. An exception thrown after a committed
write no longer invokes `OnUnhandledException` or `OnHttpException`; the engine
closes an HTTP/1 body without its final chunk or short of its declared length,
and resets an HTTP/2 or HTTP/3 stream, so the client sees an incomplete response
instead of a truncated body framed as complete. Applications that need an error
page for a failing body should produce the body before the first write; see
[deferred responses](../user-reports/deferred-responses.md).

Synchronous Dispose of managed HTTP/2 and HTTP/3 response output now starts
closure without blocking a worker on network completion. Await application writes
and keep the handler alive until its work finishes; context completion awaits
closure. Use awaited tunnel completion/disposal when you need to observe errors.
Do not start detached writes and assume synchronous Dispose waits for them.
See [multiplexed disposal](migration.md#multiplexed-response-stream-disposal-unreleased).

HTTP/3 preserves response output after an application cancels its own pending body
or tunnel read. Input remains unusable after that
in-flight cancellation because a frame may have been partly consumed; catch the
cancellation, answer or complete output, and finish the request. Peer reset,
connection failure and output cancellation still end the request. A token canceled
before a read begins does not abandon input.

HTTP/1 invokes request completion callbacks once, even when infrastructure or a
plug-in closes the same context repeatedly or
concurrently. Their existing last-registered-first order remains. Do not use
repeated context Close calls as an application notification mechanism; use an
explicit application callback for that purpose. Registration after completion
has begun remains invalid.

The token that modules receive in `OnStart`, that a session manager receives in
`Start` and that `IHttpContext.CancellationToken` exposes is now a server-owned
token linked to the one passed to `RunAsync`. It is also canceled when the server
is disposed and when `RunAsync` completes, so a handler that awaits only its
context token ends with the server instead of running after its connection was
aborted, and `LocalSessionManager` purging stops with the server. The caller's
token is never canceled by the server. Work that must outlive the server needs
its own token. `Listener.Stop()` on a running server now ends `RunAsync`
normally rather than faulting it with `HttpListenerException` 995, repeated
disposal is a no-op, and `RunAsync` on a stopped server reports that it cannot
be restarted. See [server lifetime](../user-reports/server-lifetime.md).

`WebServerOptions.UrlPrefixes`, `Modules.WithSafeNames` and `Modules.Named` now
return read-only wrappers instead of the backing `List` and `Dictionary`. Their
declared interface types are unchanged and reading or enumerating them behaves as
before. Code that cast these views to `List<T>` or `Dictionary<TKey, TValue>` gets
`InvalidCastException`, and casting to `IList<T>` or `IDictionary<TKey, TValue>`
yields a read-only collection whose mutators throw `NotSupportedException`. Use
`AddUrlPrefix` and `WithModule` before startup instead. See
[configuration locking](../user-reports/configuration-locking.md).

Use DrainAsync for bounded graceful shutdown instead of assuming Stop waits for
active responses. Stop/disposal and forced drain cancellation terminate remaining
work. HTTP/2 and HTTP/3 have different peer-close policies within that deadline;
test uploads, streams and tunnels against your shutdown policy.

Explicit WebServer.Dispose may race a pending accept. The server run task ends
normally for the expected disposed-listener or stopped-listener error in that
race; unrelated listener errors still propagate. Await RunAsync during cleanup
to observe completion. Disposal still aborts active work; use DrainAsync when
responses must finish within a deadline.

## WebSocket applications

The optional protected `WebSocketModule.ReceiveLimits` property accepts immutable
`WebSocketReceiveLimits` for outstanding message counts and logical payload bytes,
per connection and per module. It defaults to null; existing applications retain
their queue and callback behavior. Configured exhaustion closes with 1008, while
the existing `MaxMessageSize` check retains 1009. Reservations cover partial and
queued messages and the actual unfinished callback Task; they are not total heap
limits or application-retained data limits. Configure before startup. See
[outstanding-delivery limits](../guides/websocket-receive-limits.md).

Managed WebSocket framing and UTF-8 validation are stricter. Configure an
appropriate MaxMessageSize explicitly; the managed engine now enforces it,
including HTTP/2 extended CONNECT. Oversized messages close with 1009 and invalid
text with 1007. After the close frame write completes, acknowledgement waits up to
the existing one-second timeout; this is not an overall transport-write deadline.
Messages accepted before a peer close are delivered before disconnection. Outgoing
frame sizes can differ
from v1; clients must process WebSocket messages independently of frame boundaries.
See [WebSocket migration details](migration.md#managed-websocket-framing-unreleased).

Keep asynchronous handler lifetime and application readiness explicit. Do not
broadcast merely because a client handshake completed if your application needs
module registration first; use a readiness message as described in the
[overlap and close guide](../user-reports/websocket-overlap-and-close.md).
ReceiveLimits is disabled by default. Configure it explicitly when your
application needs bounded outstanding delivery.

## Optional v2 capabilities

The new APIs are optional capabilities, not requirements for existing handlers:

- IHttpResponseSections supports awaited informational responses and declared
  trailers on supporting managed backends. Declare trailers before output starts.
- IHttpTunnelContext and HttpTunnel support application-authorized CONNECT or
  Upgrade carriers. Accepting a carrier does not open a connection to a destination.
- HttpCapsuleChannel streams reliable capsule framing; it does not by itself
  provide unreliable QUIC datagrams, a proxy, or an integrated WebTransport server.
- HttpVerbs.Query enables explicit QUERY routes; existing verbs retain their values.

Use capability checks on custom contexts and adapters. Existing context/response
interfaces do not require every implementation to advertise optional features.
See the [capsule/tunnel guide](../guides/capsule-tunnels.md) and
[response field-section guide](../guides/response-field-sections.md).

## Cookie attribute parsing corrections

The original v2 cookie parser distinguishes CommentURL from Comment. Applications
reading Cookie.CommentUri now receive the supplied URI instead of finding that
text in Cookie.Comment. An Expires date without a weekday comma no longer consumes
the following Path attribute; that path and the actual expiry are retained.
Check application assertions or workarounds that depended on either parsing bug.
Cookie replacement identity, lookup ranking and mutation visibility are retained.

## Concurrent stream-property access

The v2 TCP coordinator publishes one body stream and one response-output stream
per request even when first property access races. This prevents duplicate transport
owners; it does not authorize concurrent payload reads or writes without the
stream's documented synchronization.

## Validate an application upgrade

1. Rebuild the application and custom adapters against the selected v2 package.
   Verify the selected target asset, listener mode, certificate and network access.
2. Exercise normal routes, authentication, sessions, static files, compression,
   HEAD/ranges, chunked uploads, cancellations and malformed-input handling with
   the application's actual clients and reverse proxy.
3. Exercise WebSocket initialization, message limits, fragmentation and closure;
   test tunnel half-close and shutdown if those capabilities are used.
4. Run representative throughput and sustained-load tests on an otherwise idle
   host. Verify payload bytes, latency, errors and resource release after drain.
   Use results from your deployment; benchmarks do not guarantee performance for
   every application.
5. Check the final release notes and supported capabilities before rollout.
   Wait for the published v2 package and its support policy.

## Additive HTTP datagram API

The HTTP datagram APIs include `HttpTunnel.Datagrams`, `HttpDatagramChannel`,
`HttpDatagram` and `HttpDatagramSendStatus`. Existing context interfaces and tunnel
constructors are unchanged. The surface exists on both current library targets;
unsupported backends return null. These APIs require a supporting native HTTP/3
provider, which is not yet available for public selection. Do not assume that
selecting HTTP/3 enables datagrams.

Applications must explicitly accept and authorize the extension, dispose received
packets after using their memory, and handle unreliable submission results. No
automatic capsule fallback or retransmission is introduced. Output completion
preserves remaining incoming delivery; full closure releases unread packet owners.
See [the carrier guide](../guides/capsule-tunnels.md#native-http-datagrams) before
adapting a protocol handler. This addition does not change WebSocket queue defaults.

## Actual request-body byte limits

`MaximumRequestBodyBytes` remains optional and unlimited by default. When
configured, the owned listener now enforces it on actual entity bytes, including
direct input-stream reads and unknown-length uploads. The raw budget counts
encoded bytes after HTTP framing and before content decoding; the separate
decompressed budget applies to compressed-request helpers. Chunk syntax, padding,
frame headers and request trailers do not count against the raw budget.

The routed server and its first matching route supply an immutable policy before
automatic `100 Continue` or request admission. An excessive declared length uses
the normal 413 error callback before module dispatch. A simultaneous header-limit
failure retains 431 precedence. Automatic refusal does not drain or invite the
upload; a custom error callback that explicitly reads input still observes the
captured limit and can send deferred `100 Continue`.

For unknown-length input, an in-limit prefix can reach application code before
an excess byte is detected. Complete input validation before committing changes
that cannot be rolled back. Reaching the numeric boundary does not prove EOF or
make trailers available. Unread input is validated during completion when a raw
limit is enabled. If output has already committed, an over-limit body terminates
the affected connection or stream rather than replacing the response status.

Negotiated CONNECT transport is excluded. Custom context adapters retain their
existing policy path; actual-byte enforcement depends on their own stream
integration. See [request-body byte limits](../guides/request-body-limits.md)
for configuration, route inheritance and stream ownership.

## Optional request body read deadlines

`RequestBodyReadTimeout` is disabled by default. When enabled, a stalled body
read reports HTTP 408; caller cancellation retains the caller's cancellation
token. A timed-out request cannot resume reading, and a canceled read cannot be
replaced until its underlying source has finished. Applications should finish
handling that request rather than retrying its input operation.

The deadline reader uses private pooled storage, in chunks of at most 64 KiB,
and copies data into application memory only after a successful read. If a source
ignores cancellation, that storage remains owned until its actual read settles;
request retirement does not return still-active storage to the pool. Enabling
this optional deadline therefore adds a copy to body reads. Transport retirement
remains responsible for ending the underlying I/O.

## Additive request trailer snapshots

The built-in request adapters implement the optional `IHttpRequestTrailers`
interface. Existing implementations of `IHttpRequest` need no new members.
`TryGetTrailers` never reads or drains input; after complete valid consumption it
returns an immutable ordered list kept separate from initial headers. Empty success
means no trailers, while false means unavailable, incomplete, abandoned or failed
input. Declaration does not authorize a field, and trailer metadata never changes
routing or authentication. See [Request trailers](../guides/request-trailers.md)
for the completion and ownership contract.

## Malformed HTTP/1 request rejection

HTTP/1 stages transport shutdown after rejecting a malformed or
oversized request head with HTTP 400, 414 or 431. It attempts the complete error
response, completes output, and discards remaining raw input without parsing or
application dispatch. The existing connection owner and admission slot remain
held until physical transport retirement. Stop, disposal and forced closure can
still retire that transport promptly.

The whole error-write, output-shutdown and discard interval has a best-effort
one-second budget. An already earlier configured request-header deadline wins;
extra input does not renew this interval. A blocked or failed write, expired
deadline, or concurrent stop can prevent a complete error response. Applications
must not depend on receiving an error reply after those failures. Valid request
handling and normal response closure are unchanged.

TLS output shutdown uses public runtime APIs when available. A .NET Standard
host lacking public TLS shutdown can only flush and half-close TCP in this path;
it cannot promise TLS close_notify. Check whether your target runtime exposes
that API. Accepted tunnels require public TLS-shutdown support.

## Shared HTTPS endpoints require compatible TLS policies

Neo v2 refuses registration when two logical listeners would share one TCP
address and port with incompatible `ServerTlsPolicy` settings. Previously the
physical endpoint opener's policy controlled both listeners' handshakes, which
could allow an anonymous request into a listener configured to require a client
certificate. Compatible defaults and fixed-certificate sharing remain supported.

Move listeners with different requirements to separate ports, or configure the
same policy (or equivalent settings with the same callback delegate instances)
on every listener sharing an endpoint. Protocols are compared after applying the
same runtime normalization as the TLS handshake: `None` and an explicit mask
matching the supported default versions are compatible. Rejection rolls back
only the rejected registration; existing listeners remain available. See
[TLS policy](../guides/tls-policy.md#sharing-a-tcp-listening-endpoint).

## Encoded separators in served paths

Neo v2 decodes a served path one segment at a time. `FileSystemProvider` and
`ZipFileProvider` previously decoded the whole path at once, so a percent-encoded
separator became a real separator after the module pipeline had already selected its
modules from the undecoded path. A request for `/admin%2Fsecret.txt` mapped to
`admin/secret.txt` although the modules mounted on `/admin` never matched it. RFC 3986,
Section 2.2 is explicit that a reserved character in its encoded form is not equivalent
to the character itself, so an encoded separator stays inside its own segment.

A segment that decodes to something which is not a single usable segment now names no
resource, and the request becomes an ordinary mapping failure: a decoded `/` or `\`, a
`.` or `..` segment, and control characters are all refused. Escapes that stay inside
their segment are unaffected, so `/two%20words.txt` and `/sub/hello%20world.txt` resolve
exactly as before, and containment inside the served root is unchanged.

An application that relied on a percent-encoded separator to select a nested resource
must send the real separator instead. On Unix, a file whose name contains a backslash is
no longer reachable through `%5C`; rename it if it has to be served.

<a id="release-preparation-still-required"></a>

## Before deployment

Neo v2 has not been released. Confirm the final package, release notes and platform
support before deployment. Features described as unavailable above must not be
assumed available merely because their API types exist.

## HTTP/1 idle closure

A complete, fully consumed HTTP/1 exchange waiting for its successor attempts
orderly send shutdown when the
existing timeout expires and no successor input has been admitted. Clients can
receive EOF where v1 commonly produced a reset exception. Handle either terminal
outcome as the end of that connection; do not depend on an idle reset exception
or blindly retry requests whose delivery is uncertain.

The fifteen-second default and configured deadlines do not change. Partial
headers, unread bodies, unfinished responses and peer failures retain abort
behavior. TLS uses public runtime shutdown support where available; interrupted
or failed alert writes retire without a new grace period. Older runtimes without
that API retain their existing TLS close behavior. Orderly closure is not
guaranteed when a peer races expiry or faults. See the
[idle closure guide](../project/http1-idle-closure.md) for details and limitations.

## Explicit extended-CONNECT acceptance

In the v2 development engine, an HTTP/2 or HTTP/3 extended-CONNECT handler must
call `IHttpTunnelContext.AcceptTunnelAsync` before committing a successful
response. Application dispatch still occurs: inspect the requested protocol and
authorize it before acceptance. Ordinary 2xx output without a handoff becomes an
empty 501 response before headers or payload are sent. Explicit application
errors retain their status and body. Plain CONNECT and HTTP/1 are unchanged;
WebSockets use the existing WebSocket acceptance API.

This approved change also affects deliberate custom extension implementations
that previously wrote a 2xx response and used Request/Response streams directly.
Migrate those handlers to the returned HttpTunnel stream:

```csharp
if (context is not IHttpTunnelContext capability)
    throw new HttpException(System.Net.HttpStatusCode.NotImplemented);
// Inspect the offered protocol and authorize this request before accepting.
var tunnel = await capability.AcceptTunnelAsync("example-tunnel", false, context.CancellationToken);
await tunnel.Stream.CopyToAsync(tunnel.Stream, context.CancellationToken);
await tunnel.CompleteOutputAsync(context.CancellationToken);
```

The example implements an application-specific byte echo protocol, not a proxy.
Keep the handler alive while its accepted stream operations are running. Use
HttpTunnel completion and closure instead of independently disposing the request
or response streams. Capsule framing remains explicitly opt-in. See
[capsule and tunnel carriers](../guides/capsule-tunnels.md) for ownership,
deadlines and independent send/receive completion.

## Request and response corrections

These approved v2 corrections change observable behavior that an application or
its tests may have relied on:

- A `+` in a route parameter is now a literal `+`. `/users/a+b` binds `a+b`
  instead of `a b`; a space must be sent as `%20`, as URL paths require. Query
  strings and form bodies still decode `+` as a space.
- `[QueryField(true)]` and `[FormField(true)]` find a field whose name differs only
  in case, the same way binding reads its value. `?Id=5` no longer returns 400 for
  a required `id`.
- A byte range whose last position is at or past the end of a file selects the
  remainder and returns 206. A range that starts at or past the end still returns 416.
- `CorsModule` with a restricted origin list sends `Vary: Origin` on every response
  it processes, so shared caches keep each origin's answer separate.
- The Basic challenge is `Basic realm="...", charset="UTF-8"`. Clients that matched
  the previous comma-less text exactly must accept the standard form.
- `ISession.Delete` and `Regenerate` end the session even when it holds data, and
  an expired session is not resumed before the purge runs.
- A module whose own exception handler answers a request ends dispatch; later
  modules no longer run for that request.
- `If-None-Match: *` and entity-tag lists match, and `If-Modified-Since` and date
  `If-Range` values compare whole seconds.
- Complete WebSocket messages report `EndOfMessage` as true.
- Directory listings requested without a trailing slash link to entries inside the
  directory instead of its parent.
- On HTTP/1, a request field sent on several lines reads as one value that combines
  the lines in order, separated by `, ` (RFC 9110, Section 5.3); Cookie lines join
  with `; ` and all of their cookies appear in `Request.Cookies`. Only the last line
  was kept before. HTTP/2 and HTTP/3 already combined repeated fields.
- A quality value's weight name is case-insensitive (RFC 9110, Section 12.4.2):
  a client that sends `Q=` now has its weight honored, so `Accept-Encoding: gzip;Q=0`
  refuses gzip. An uppercase `Q` used to be ignored, and the coding was accepted at
  full weight.
- Statuses 103, 208, 226, 308, 421, 426, 428, 429, 431, 451, 506, 508, 510 and 511
  have the reason phrase `HttpStatusDescription` returns, such as `Too Many Requests`
  for 429. It appears in the HTTP/1 status line, as the default `StatusDescription`,
  as the default problem-details `title` and as the default OpenAPI response
  description. These statuses used to have an empty phrase, no default problem title
  and an OpenAPI description such as `Status 429`.

## Brotli preferred at equal weight

On the .NET 10 asset, a client that accepts Brotli at the same weight as gzip or
deflate now gets Brotli. Browsers send `Accept-Encoding: gzip, deflate, br` and
previously got gzip, because a tie went to the coding the client listed first.
Client weights still decide: a client that needs gzip can send
`gzip;q=1, br;q=0.5`. Ties with `identity`, the choice between gzip and deflate,
and the .NET Standard 2.0 asset are unchanged.

Dynamic responses from `OpenResponseStream`, `OpenResponseText` and the helpers built
on them use Brotli quality 1 (`CompressionLevel.Fastest`): a quarter to half the CPU
of gzip's default level, and 6 to 17% larger on a JSON document. Static files keep
quality 4, about the size of the previous gzip responses. Tests or caches that expect
`Content-Encoding: gzip` for these clients, or a gzip entity tag from `FileModule`,
see `br` instead. See
[Brotli responses](../guides/getting-started/files.md#brotli-responses-in-the-modern-engine-development-branch).
- An HTTP/1 client that declares a request body, receives its response and then stops
  sending loses its connection once the keep-alive timeout elapses. The server discards
  a body no handler read before reusing the connection, and that discard is now bounded
  by `KeepAliveTimeout`; it previously waited with no deadline at all. A client that
  finishes sending its body is unaffected.
- An HTTP/1 connection that answered `101 Switching Protocols` is never reused for
  another HTTP/1 message (RFC 9110, Section 7.8). This matters only to a handler that
  accepts an upgrade through `IHttpContextImpl.AcceptWebSocketAsync` and returns while
  the socket is still open: the connection is now closed instead of being recycled, and
  such a handler must await its socket. `WebSocketModule` already does.
