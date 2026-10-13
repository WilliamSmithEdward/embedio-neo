# Migrating from EmbedIO-Neo v1 to v2

This guide tracks the owner-designated **Neo v2** release scope for the modern
HTTP engine program [#181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181)
and umbrella [PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182).
It is a living development guide. Neo v2 has not been published; no release date,
final package version, or completion of the default-listener transition is implied.

The comparison baseline is the published source tag
[v1.0.3](https://github.com/WilliamSmithEdward/embedio-neo/tree/v1.0.3).
If you use an earlier v1 patch, also review the
[general migration notes](migration.md): they include historical upstream-to-Neo
and earlier v1 changes that are not new in v2. Compare against the released tag,
not the unreleased main branch: source compatibility changes already present on
main can still be new to a v1 package consumer.

## Rebuild and update source references

The new, unreleased one-argument `HttpListener(ServerCertificateSource)`
constructor is replaced by
`EmbedIO.Net.HttpListener.CreateWithCertificateSource(source)`. Update code using
that temporary v2 constructor to the named factory. The source and its
certificates remain application-owned; the factory rejects a null source.
`HttpListener(source, tlsPolicy)` and `WebServerOptions.CertificateSource` remain
available. Published v1 calls such as `new EmbedIO.Net.HttpListener(null)` and
`new EmbedIO.Net.HttpListener(default)` keep selecting the optional certificate
constructor, avoiding ambiguity between unrelated reference types.

V2 includes the approved compiler/API cleanup as well as the engine replacement.
Rebuild dependent assemblies; do not treat the major upgrade as a binary-compatible
drop-in. Verified v1.0.3-to-current-source changes include:

| Neo v1.0.3 API | V2 source migration |
| --- | --- |
| IHttpRequest.RawUrl | Use IHttpRequest.RawTarget. It retains the received request-target text; update custom adapters too. |
| Validate.Url returns string | It returns Uri. Use the Uri directly, or ToString when text is required. |
| RedirectModule.RedirectUrl is string | It is Uri; existing string constructors remain available. |
| ITestWebServer.BaseUrl is string | It is Uri; update custom test-server implementations and text comparisons. |

Review the [approved API-cleanup notes](migration.md#warning-free-api-cleanup-unreleased-owner-approved)
for named-argument changes, URI overloads, nullability annotations, error parameter
names, cache ownership and exception-recovery boundaries. Specify the intended
argument type when passing null to overloaded string/Uri entry points.

The System.Text.Json migration and SWAN removal already occurred in Neo v1;
upgrading to v2 does not introduce them again. Still run serialization contract
tests for custom converters, unsupported payloads, numeric precision and exact
response bytes against the final v2 package. The
[JSON compatibility guide](../user-reports/json-migration-compatibility.md)
records the audited behavior and its limits.

## Unhandled exceptions return an empty 500

Neo v2 uses `ExceptionHandler.EmptyResponse` as the default unhandled-exception
handler. Before response headers are committed, an otherwise unhandled application
exception produces an empty `500 Internal Server Error` response. The handler does
not add exception type or message headers. Neo v1's default HTML page included the exception type and
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
remains available. This default change is unreleased; update clients that parse the
old default HTML page before adopting v2.

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
their flushing behavior, including empty responses and final framing. This
approved change is unreleased; test custom streaming code against the final v2
package before upgrading.

## Explicit JSON metadata method names

The unreleased Native AOT APIs use distinct names: `Json.SerializeWithMetadata`,
`Json.DeserializeWithMetadata`, `GetRequestDataWithMetadataAsync` and
`SendDataWithMetadataAsync`. William approved renaming only these new APIs after
their original overload names made existing null-options calls ambiguous and
changed serializer callback selection for null data. Update code using the earlier
v2 branch metadata overloads to these names. No published v1 metadata API is renamed.

Existing `Json.Deserialize<T>(json, null)`, options-based serialization and
`SendDataAsync(serializer, null)` retain their signatures and behavior. Explicit
metadata still uses its own options and supports source-generated JSON in trimmed
and Native AOT applications. See [the Native AOT guide](../guides/trimming-and-native-aot.md).

## Listener selection and deployment

William approved removal of both the Microsoft backend and the inherited Mono
implementation on 2026-10-10. The engine branch now contains the original TCP
listener, protocol input, request actor, response output, endpoint/transport
lifetime and request/response/context view replacements. Complete program
acceptance is still required before shipping. This approval supersedes the earlier plan to retain Microsoft mode and
only deprecate Mono.

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

The replacement engine is the listener family on the engine branch; both old
implementations are removed. HTTP/3 public selection still uses System.Net.Quic
for its QUIC transport, with the independently authored HTTP/3 protocol engine.
That transport API is distinct from the removed System.Net.HttpListener backend.
The native MsQuic application provider is integrated as an internal candidate;
its default activation and remaining extensions/performance acceptance are still
unfinished. These source changes are not shipping acceptance. Keeping the
EmbedIO mode name does not retain the old parser's permissive behavior.

The current engine branch has these modes:

| Mode | Current development behavior | Migration action |
| --- | --- | --- |
| EmbedIO | Original HTTP/1 and HTTP/2 engine over TCP; full program acceptance remains pending. | Exercise existing routes and clients against the new engine. |
| Microsoft (v1 only) | Removed on the v2 engine branch. | Remove the mode selection and migrate backend-specific deployment configuration. |
| EmbedIOHttp3 | Opt-in HTTP/3 over UDP, without a TCP listener. | Use the .NET 10 asset, supported native QUIC, HTTPS and a certificate with its private key. |
| EmbedIOCombined | HTTP/1 and HTTP/2 over TCP alongside HTTP/3 over UDP on the same HTTPS prefixes. | Permit both transports; startup requires both bindings and has no silent TCP-only fallback. |

Library targets remain .NET Standard 2.0 and .NET 10. HTTP/3 and combined mode
require the .NET 10 asset and runtime/native QUIC support; the legacy asset cannot
provide them. Library namespaces and package identities remain preserved.
Python is used by development/testing tools, not by the shipped HTTP engine.
HTTP/2 over TLS requires the ALPN-capable .NET 10 asset; legacy cleartext HTTP/2
coverage does not establish TLS negotiation support on .NET Standard.

Follow the [HTTP/3 deployment guide](../guides/http3.md) for certificate lifetime,
key provisioning, hostname validation, native libraries and platform limits.
HTTP/3 availability does not imply automatic Alt-Svc discovery. Native MsQuic
transport, unreliable datagram and WebTransport work is still being integrated;
unit coverage or a successful native probe does not establish application-level
availability. Consult the [engine program](../project/http-engine.md) before
selecting those capabilities.

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

Neo v2 restricts final-response StatusCode assignments to 200-599. The correction
is implemented on the engine development branch and remains unreleased.
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

The HTTP/1 response-model replacement also suppresses attempted content writes
for 205 Reset Content and commits Content-Length: 0 without chunk framing. The
previous HTTP/1 model emitted those bytes, unlike HTTP/2 and HTTP/3. Applications
which used 205 with a payload should use a body-capable status such as 200 instead.
This candidate remains under validation on the development branch; no release
is implied. See [RFC 9110 section 15.3.6](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.3.6).

For HTTP/1 responses, declare Content-Length only when it matches the encoded
bytes actually sent, including any transformation, or use automatic/chunked
framing. The owner-approved output correction rejects a write exceeding that
length with ProtocolViolationException before submitting those bytes. Closing
with an incomplete declared body closes the connection; it no longer rewrites an
explicit positive length to zero or permits another response on that connection.
HEAD and bodyless representation metadata retain their separate semantics.

A failed transport submission, including cancellation during output, prevents
later body/footer bytes and connection reuse. IgnoreWriteExceptions continues to
suppress managed transport-write exceptions; it cannot restore failed framing.
Cancellation before obtaining the writer gate leaves otherwise healthy output
usable. These changes belong to the unreleased v2 output replacement.

Exception handlers now run only while the response is replaceable, that is,
before its final headers reach the wire, and they start from a response whose
Content-Length, Content-Encoding, Content-Range, ETag and chunked framing from
the failed handler have been removed. An exception thrown after a committed
write no longer invokes `OnUnhandledException` or `OnHttpException`; the engine
closes an HTTP/1 body without its final chunk or short of its declared length,
and resets an HTTP/2 or HTTP/3 stream, so the client sees an incomplete response
instead of a truncated body framed as complete. Applications that need an error
page for a failing body should produce the body before the first write; see
[deferred responses](../user-reports/deferred-responses.md). This correction is
on the engine development branch and is unreleased.

Synchronous Dispose of managed HTTP/2 and HTTP/3 response output now starts
closure without blocking a worker on network completion. Await application writes
and keep the handler alive until its work finishes; context completion awaits
closure. Use awaited tunnel completion/disposal when you need to observe errors.
Do not start detached writes and assume synchronous Dispose waits for them.
See [multiplexed disposal](migration.md#multiplexed-response-stream-disposal-unreleased).

The HTTP/3 cancellation correction preserves response output after an application
cancels its own pending body or tunnel read. Input remains unusable after that
in-flight cancellation because a frame may have been partly consumed; catch the
cancellation, answer or complete output, and finish the request. Peer reset,
connection failure and output cancellation still end the request. This correction
is integrated through [PR #254](https://github.com/WilliamSmithEdward/embedio-neo/pull/254)
on the engine development branch and remains unreleased. A token canceled before
a read begins does not abandon input.

The HTTP/1 context replacement claims request completion callbacks once, even
when infrastructure or a plug-in closes the same context repeatedly or
concurrently. Their existing last-registered-first order remains. Do not use
repeated context Close calls as an application notification mechanism; use an
explicit application callback for that purpose. Registration after completion
has begun remains invalid. This context increment is under validation and is
unreleased.

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
be restarted. See [server lifetime](../user-reports/server-lifetime.md). This
correction is on the engine development branch and is unreleased.

`WebServerOptions.UrlPrefixes`, `Modules.WithSafeNames` and `Modules.Named` now
return read-only wrappers instead of the backing `List` and `Dictionary`. Their
declared interface types are unchanged and reading or enumerating them behaves as
before. Code that cast these views to `List<T>` or `Dictionary<TKey, TValue>` gets
`InvalidCastException`, and casting to `IList<T>` or `IDictionary<TKey, TValue>`
yields a read-only collection whose mutators throw `NotSupportedException`. Use
`AddUrlPrefix` and `WithModule` before startup instead. See
[configuration locking](../user-reports/configuration-locking.md). This
correction is on the engine development branch and is unreleased.

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
Messages accepted before
a peer close are delivered before disconnection. Outgoing frame sizes can differ
from v1; clients must process WebSocket messages independently of frame boundaries.
See [WebSocket migration details](migration.md#managed-websocket-framing-unreleased).

Keep asynchronous handler lifetime and application readiness explicit. Do not
broadcast merely because a client handshake completed if your application needs
module registration first; use a readiness message as described in the
[overlap and close guide](../user-reports/websocket-overlap-and-close.md).
No finite default message limit or receive backpressure policy should be inferred
from the performance work; those decisions need their own documented contract.

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
This helper replacement adds no public API or runtime dependency.
## Concurrent stream-property access

The v2 TCP coordinator publishes one body stream and one response-output stream
per request even when first property access races. This prevents duplicate transport
owners; it does not authorize concurrent payload reads or writes without the
stream's documented synchronization. Existing WebSocket cleanup can still acquire
its harmless HTTP output flush view after the upgraded transport has closed.

## Validate an application upgrade

1. Rebuild the application and custom adapters against the selected v2 candidate.
   Verify the selected target asset, listener mode, certificate and network access.
2. Exercise normal routes, authentication, sessions, static files, compression,
   HEAD/ranges, chunked uploads, cancellations and malformed-input handling with
   the application's actual clients and reverse proxy.
3. Exercise WebSocket initialization, message limits, fragmentation and closure;
   test tunnel half-close and shutdown if those capabilities are used.
4. Run representative throughput and sustained-load tests on an otherwise idle
   host. Verify payload bytes, latency, errors and resource release after drain.
   Report the exact engine revision and environment; branch benchmarks are not
   performance guarantees for every application.
5. Recheck this guide and release notes once the default switch, native transport,
   datagrams/WebTransport and final platform acceptance are complete. Roll out only
   after the actual v2 package and its support policy are published.

## Additive HTTP datagram API

The native HTTP/3 candidate adds `HttpTunnel.Datagrams`, `HttpDatagramChannel`,
`HttpDatagram` and `HttpDatagramSendStatus`. Existing context interfaces and tunnel
constructors are unchanged. The surface exists on both current library targets;
unsupported backends return null. Public provider selection and native activation
are still under development, so this is not a released availability claim.

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
for the completion and ownership contract. This addition is unreleased.

## Malformed HTTP/1 request rejection

The HTTP/1 candidate stages transport shutdown after rejecting a malformed or
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

TLS output shutdown uses public runtime APIs when available. Installed .NET
Framework 4.8 and .NET 10 were checked with delayed TLS readers. A netstandard
host lacking public TLS shutdown can only flush and half-close TCP in this path;
it cannot promise TLS close_notify. That unavailable-capability fallback has not
been exercised on an actual runtime lacking the API. Accepted tunnels retain
their existing stricter public TLS-shutdown capability requirement. This correction
is unreleased and does not establish the cause of an isolated hosted test failure.

## Release preparation still required

Before v2 ships, reconcile every approved behavior change into this guide,
verify code examples against the final package, reconcile the listener-removal
and native-provider deployment documentation, confirm extension/platform support and complete
conformance, fuzzing, interoperability, sustained-load and exact-head CI gates.
Update README HTTP badges when the corresponding engine support is integrated
and verified. Program #181 remains open; this guide does not authorize a release.
