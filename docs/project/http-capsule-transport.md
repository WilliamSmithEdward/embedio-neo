# Capsule transport development

This is an unfinished increment of [program #181](http-engine.md), based on the
HTTP/2 integration in PR #221. No public tunnel API or advertised capsule or
native QUIC datagram capability is added by this increment.

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

Both production targets compile. Eighteen framing/lifetime cases cover wire
vectors, fragmented and nonminimal widths, maximum declared length, unknown-type
skip, truncation, partial writes, cancellation, operation ownership and output
completion. Together with negotiation and existing priority cases, 175 tests
pass on Windows. This is focused local evidence, not a protocol-support claim.

Remaining work includes application-facing generic Upgrade/extended CONNECT
handoff without breaking existing interface implementers, negotiated response
header commitment, carrier lifecycle/error translation, independent peers over
HTTP/1/2/3, flow-credit stress, performance/resource measurements and final
cross-target/platform/full-suite validation. Reliable DATAGRAM capsules and
unreliable native QUIC DATAGRAM delivery require separate capability evidence.
