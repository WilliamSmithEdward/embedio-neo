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
## Listener selection and deployment

William approved removal of both the Microsoft backend and the inherited Mono
implementation on 2026-10-10. The retirement is being implemented incrementally;
the replacement engine must pass the complete platform and protocol checks before
shipping. This approval supersedes the earlier plan to retain Microsoft mode and
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

The intended v2 outcome is for the replacement engine to be the supported listener
family, with both old implementations removed. On the retirement branch, the
Microsoft factory and its adapters are removed; replacing the inherited Mono
HTTP/1 implementation remains in progress. This intermediate state is not shipping
acceptance. Keeping the EmbedIO mode name does not retain the old parser's
permissive behavior.

The current engine branch has these modes:

| Mode | Current development behavior | Migration action |
| --- | --- | --- |
| EmbedIO | Managed HTTP/1 and HTTP/2 over TCP; replacement work remains in progress. | Exercise existing routes and clients against the new engine. |
| Microsoft (v1 only) | Removed on the v2 retirement branch. | Remove the mode selection and migrate backend-specific deployment configuration. |
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

Managed WebSocket framing and UTF-8 validation are stricter. Configure an
appropriate MaxMessageSize explicitly; the managed engine now enforces it,
including HTTP/2 extended CONNECT. Oversized messages close with 1009 and invalid
text with 1007, with bounded close-handshake completion. Messages accepted before
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

## Release preparation still required

Before v2 ships, reconcile every approved behavior change into this guide,
verify code examples against the final package, finish default-listener and
Mono-deprecation documentation, confirm extension/platform support and complete
conformance, fuzzing, interoperability, sustained-load and exact-head CI gates.
Update README HTTP badges when the corresponding engine support is integrated
and verified. Program #181 remains open; this guide does not authorize a release.
