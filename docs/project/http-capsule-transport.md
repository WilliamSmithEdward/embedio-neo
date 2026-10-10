# Capsule transport development

This is an unfinished increment of [program #181](http-engine.md), based on the
HTTP/2 integration in PR #221. The owner approved the additive capsule/tunnel API;
its public facade and HTTP/1/2/3 handoff are now under development.
Focused Windows validation has begun; independent peers and final platform gates remain pending. This is not an advertised capsule/native QUIC datagram capability.

## Implemented components

`HttpCapsuleTransport` borrows a stream and permits one operation per direction.
It reads type/length headers with the existing QUIC integer codec and exposes
incremental payload reads, writes and bounded-scratch skipping. Declared lengths
do not determine allocation size. The caller owns stream closure and supplies
cancellation and resource policy. Output completion validates the declared
payload length and prevents later frames; it does not close the carrier.

Partial I/O failure or cancellation makes that direction unusable. Cancellation
before I/O leaves framing intact. Caller argument/state errors do not consume
bytes. Truncated headers/values are errors rather than complete capsules.

`HttpCapsuleProtocol` parses a Boolean Item and validates selected carrier
headers/statuses. The shared structured-field reader preserves priority parsing.
These helpers are called only when an extension selects capsule semantics;
ordinary HTTP messages are not automatically interpreted as capsules.

## Specification basis

[RFC 9297](https://www.rfc-editor.org/rfc/rfc9297.html#section-3) defines capsule
framing and carrier constraints. Payload consumption must release underlying
flow credit incrementally rather than requiring the entire value to fit in a
receive window. Unknown capsule types can be skipped by an endpoint. A forwarding
intermediary has additional preservation responsibilities.

A capsule carrier excludes Content-Length, Content-Type and Transfer-Encoding.
Responses must use 101 or a permitted successful status; 204/205/206 are excluded.
Capsule-Protocol is a Boolean Item; a repeated field forming a List is ignored.
Its unknown parameters are validated then ignored. Parsing uses the existing
[RFC 9651 structured-field reader](https://www.rfc-editor.org/rfc/rfc9651.html).

## Validation and remaining work

The internal foundation before the public facade compiled for both production targets. Eighteen framing/lifetime cases cover wire
vectors, fragmented and nonminimal widths, maximum declared length, unknown-type
skip, truncation, partial writes, cancellation, operation ownership and output
completion. Together with negotiation and existing priority cases, 175 tests
passed on Windows before the public facade edits. Those results do not validate
the later public API or handoff changes. The measurement hold has now ended; the public implementation and test host build with zero warnings.

Remaining work includes application-facing generic Upgrade/extended CONNECT
handoff without breaking existing interface implementers, negotiated response
header commitment, carrier lifecycle/error translation, independent peers over
HTTP/1/2/3, flow-credit stress, performance/resource measurements and final
cross-target/platform/full-suite validation. Reliable DATAGRAM capsules and
unreliable native QUIC DATAGRAM delivery require separate capability evidence.

## Approved API work in progress

`HttpCapsuleChannel` borrows an already negotiated stream; `HttpTunnel` owns its
accepted duplex stream. The optional `IHttpTunnelContext` leaves existing context
interfaces intact. Selection and authorization belong to the application.
`CompleteOutputAsync` finishes the send direction while retaining readable peer
input. `CloseAsync` joins output completion and stream disposal on both targets;
.NET 10 additionally provides `IAsyncDisposable`. Synchronous disposal starts the
same operation without blocking a worker. Completion and disposal errors can be
observed through the asynchronous close task.

The staged multiplexed handoff uses ordinary CONNECT or a matching requested
extended CONNECT protocol. Capsule mode omits default representation/framing
headers and rejects contradictory request or response configuration. WebSocket
negotiation continues through its existing API. Draft regressions cover shared
completion, disposal failures, half-closed input, carrier headers and incomplete
output cleanup. These cases now pass in focused Windows runs; full-suite and independent peer validation remain pending.

The draft maps truncated capsule input and incomplete output to HTTP/2
PROTOCOL_ERROR or HTTP/3 H3_MESSAGE_ERROR on the selected request stream.
New raw HTTP/2 cases require no successful FIN, correct reset code, and healthy
sibling requests/PING on the same connection; they also propagate server-side
assertion failures explicitly rather than allowing the dispatcher to swallow them.

Verification of reset/error mapping, HTTP/1 lifecycle, real HTTP/2 and HTTP/3 peer
validation and cancellation/drain races remain required before acceptance. In
particular, codec error detection alone does not prove the required malformed-
message treatment under RFC 9297 section 3.3. This increment does not implement
an automatic UDP/TCP proxy or claim native unreliable datagram support.
Upgrade selection uses an original parser for
[RFC 9110 section 7.8](https://www.rfc-editor.org/rfc/rfc9110.html#section-7.8).
Names match without case, while protocol-version spelling remains exact. Invalid
field members prevent partial selection. This parser is preparation for HTTP/1
handoff; its parser cases pass on Windows.
## HTTP/1 handoff draft

The managed HTTP/1 context now stages the optional tunnel capability. Upgrade
selects an offered protocol and sends 101 with Upgrade/Connection fields;
ordinary CONNECT selects null and sends 200. Request content must have been
consumed before the connection is handed off. The response uses the existing
header/cookie serializer while omitting synthesized representation and body
framing for the negotiated carrier. Capsule mode validates forbidden fields.

`Http1TunnelStream` takes the connection lifetime while borrowing its raw/prefix-
replay stream. Bytes read with the HTTP head remain available to the tunnel.
The connection cannot restart HTTP parsing after this handoff. Output completion
serializes against writes, flushes the stream, finishes TLS output when present,
then shuts down only the socket send direction. Context completion joins the
handshake and tunnel close before running its existing completion callbacks.

The .NET 10 asset calls the public `SslStream.ShutdownAsync` API. Because the
netstandard2.0 reference assembly lacks that API, the legacy asset binds the same
public runtime method when available. It does not access private TLS fields or
add native interop. A runtime without that public method rejects a TLS handoff
before sending the handshake. Plain tunnels do not need this method. A TLS 1.3
peer is used by the staged half-close cases; older TLS closure semantics require
separate validation before broader claims.

Draft real-peer cases cover plain/TLS CONNECT and capsule Upgrade, early bytes in
the same write as the HTTP head, correct handshake fields, server send EOF before
final peer input, once-only close callbacks and a healthy subsequent client.
A separate post-handoff application-failure case requires the listener to remain
active. HTTP/1 tunnel faults close that connection; HTTP/2/3 tunnel faults abort
only the selected stream. Application failures use transport internal-error
handling rather than the malformed-capsule error code. All five real HTTP/1 cases pass on Windows, including TLS 1.3 send completion with subsequent readable peer input.
## Independent capsule peer draft

The existing `test/EmbedIO.Conformance` host has a test-only `/capsule` extension
using the public capability. It echoes opaque type-0 payloads up to 16 KiB and
streams past unknown or oversized values; it does not forward UDP or change a
production limit. The optional half-close mode finishes server output after one
echo, then continues consuming peer input. Test-host counters expose the number
and byte sum of messages consumed after server FIN, allowing the peer to prove
that input was delivered rather than merely observing a successful client write.

`drivers/capsule_campaign.py` reuses the independently implemented, pinned
hyper-h2 and aioquic peers. The draft includes an unknown capsule spanning a
196,608-byte value across underlying flow-control credit, nonminimal 8-byte
integer encodings, an empty capsule, send-FIN/input continuation, and truncated
type/length/value cases. Each case also requires a healthy sibling request on
the same connection. Reports record individual failures and return a nonzero exit
if any case fails. The H2 reset must be PROTOCOL_ERROR; the H3 reset must be
H3_MESSAGE_ERROR. This is development-only Python outside production packages.

These new peer paths have not been executed. Example commands against a fresh,
separate test host (source/core hash and host identity must be recorded alongside
the report):

```sh
python3 -I test/EmbedIO.Conformance/drivers/capsule_campaign.py h2 --port 18080 --stats-port 18080 --out h2-capsules.json
python3 -I test/EmbedIO.Conformance/drivers/capsule_campaign.py h2 --port 18443 --tls --stats-port 18080 --out h2-tls-capsules.json
python3 -I test/EmbedIO.Conformance/drivers/capsule_campaign.py h3 --port 18444 --stats-port 18080 --out h3-capsules.json
```
## First public-API validation increment

The first focused run reproduced HTTP/1 rejection of valid authority-form CONNECT.
The parser now requires an explicit nonempty, valid port and preserves the exact
destination in RawTarget; eleven malformed target cases retain strict rejection.
Routing still reconstructs the host with the listener's local endpoint, consistently
with the existing application URI model. Applications must authorize the requested
destination before accepting; this API does not connect to that destination.

The corrected focused run passed 234 Windows cases (public capsule/tunnel behavior,
Upgrade selection, malformed-capsule raw peers, existing request targets and priority).
Two additional raw HTTP/2 cases prove classic and extended CONNECT do not apply the
HTTP request Content-Length after successful handoff. Both failed with stream reset
when the old length check was restored temporarily; both pass with the transition.
The malformed-capsule cases still require stream PROTOCOL_ERROR and healthy siblings.
Full-suite, independent H2/H3 peer campaigns and final cross-platform checks remain
pending. No Linux/macOS capsule result, shipping capability or performance claim is made.