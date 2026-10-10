# WebTransport session core development

This is an unfinished increment of [program #181](http-engine.md). It adds an
original internal framing and session core for WebTransport over HTTP/3 under
`src/EmbedIO/Net/Internal/WebTransport`, with unit coverage and no transport
integration. Nothing here is reachable from a listener, no setting is advertised,
no public API or dependency is added and both library targets are unchanged in
shape. It is not a WebTransport implementation and claims no interoperability.

## Specification basis

The applicable text on 2026-10-10 is
[draft-ietf-webtrans-http3-16](https://www.ietf.org/archive/id/draft-ietf-webtrans-http3-16.txt)
(6 July 2026). The datatracker lists it as an active working-group document in
WG Last Call with IESG state "I-D Exists", intended status Proposed Standard,
expiring 7 January 2027, and no RFC number. Revision history: -13 2025-07-07,
-14 2025-10-20, -15 2026-03-02, -16 2026-07-06; consensus and intended status
were set on 2026-03-06 and WG Last Call began on 2026-03-08. The companion
documents are at the same stage: draft-ietf-webtrans-overview-13 and
draft-ietf-webtrans-http2-15, both dated 2026-07-06. The three text files were
retrieved and hashed (SHA-256 `49e0a03b`, `f9e780d1` and `39c3b0a5` prefixes); the
values below were read from that text, not from a summary.

The IANA HTTP/3 parameter registries (last updated 2023-06-13) and the HTTP
Capsule Types registry (last updated 2026-08-28) contain no WebTransport entry.
Every codepoint used here is therefore a draft request, consistent with the
[standards applicability audit](http-standards-applicability.md), which recorded
WebTransport as experimental and unsupported. Draft section 7.1 says each draft
version uses a distinct `SETTINGS_WT_ENABLED` codepoint; the core carries the
-16 values and must be revised with the draft.

Values taken from the draft:

| Item | Value | Section |
| --- | --- | --- |
| `:protocol` and upgrade token | `webtransport-h3` | 3.2, 9.1 |
| `SETTINGS_WT_ENABLED` | 0x2c7cf000 | 3.1, 9.2 |
| `SETTINGS_WT_INITIAL_MAX_DATA` | 0x2b61 | 5.5, 9.2 |
| `SETTINGS_WT_INITIAL_MAX_STREAMS_UNI` / `_BIDI` | 0x2b64 / 0x2b65 | 5.5, 9.2 |
| Unidirectional stream type | 0x54 | 4.2, 9.4 |
| Bidirectional signal (`WT_STREAM`) | 0x41 | 4.3, 9.3 |
| `WT_CLOSE_SESSION` | 0x2843 | 6, 9.6 |
| `WT_DRAIN_SESSION` | 0x78ae | 4.7, 9.6 |
| `WT_MAX_DATA` / `WT_DATA_BLOCKED` | 0x190B4D3D / 0x190B4D41 | 5.6.4, 5.6.5 |
| `WT_MAX_STREAMS` bidi / uni | 0x190B4D3F / 0x190B4D40 | 5.6.2 |
| `WT_STREAMS_BLOCKED` bidi / uni | 0x190B4D43 / 0x190B4D44 | 5.6.3 |
| Prohibited `WT_MAX_STREAM_DATA` / `WT_STREAM_DATA_BLOCKED` | 0x190B4D3E / 0x190B4D42 | 5.4 |
| `WT_BUFFERED_STREAM_REJECTED` | 0x3994bd84 | 4.6, 9.5 |
| `WT_SESSION_GONE` | 0x170d7b68 | 6, 9.5 |
| `WT_FLOW_CONTROL_ERROR` | 0x045d4487 | 5.6, 9.5 |
| `WT_ALPN_ERROR`, `WT_REQUIREMENTS_NOT_MET` | 0x0817b3dd, 0x212c0d48 | 3.1, 3.3, 9.5 |
| Application error range | 0x52e4a40fa8db to 0x52e5ac983162, skipping 0x1f * N + 0x21 | 4.4, 9.5 |
| Close message limit | 1024 UTF-8 bytes | 6 |
| Stream count limit | 2^60 | 5.6.2, 5.6.3 |

Both markers are variable-length integers, so 0x41 and 0x54 occupy two bytes on
the wire (`40 41`, `40 54`). The draft's figure 11 shows the close message as
`..8192` while its text limits it to 1024 bytes; the core applies the text.

## Implemented components

`WebTransportProtocol` holds the identifiers and the pure wire helpers: session
identifier validation (a client-initiated bidirectional stream identifier, with
any other identifier on a stream or datagram a connection error `H3_ID_ERROR`),
stream-header reading that asks for more bytes rather than failing on a split
header and reports a non-0x41 signal as an ordinary request stream, server
header writers, RFC 9297 quarter-stream-identifier datagram association that
borrows the caller's packet, and the section 4.4 error-code mapping in both
directions.

`WebTransportSettings` parses the identifiers this extension depends on from a
SETTINGS payload (0x08, 0x33 and the four WT values), reports whether the
section 3.1 settings requirements are met, whether either side declared flow
control, and encodes the local non-default pairs for the connection's SETTINGS
frame. The transport parameters the draft also requires (`max_datagram_frame_size`
and `reset_stream_at`) are not visible at this layer and are supplied by the
transport as one boolean.

`WebTransportCapsuleCodec` encodes and decodes the session capsules. Close
messages are truncated at a UTF-8 character boundary when sent and rejected
when received if they exceed 1024 bytes or are not valid UTF-8; limit capsules
must consume exactly one integer, and a stream limit above 2^60 is a flow-control
error. `WebTransportCapsuleReader` drives the existing `HttpCapsuleTransport`
over a borrowed CONNECT stream, buffers only session capsules (at most 1028
bytes), skips unknown types without allocating their declared length and reports
truncation as a session error.

`WebTransportSession` is the state machine for one session: pending until the
2xx response, established, closed. It associates incoming streams and datagrams,
buffers both with bounds while pending, applies section 5 flow control when it
was negotiated (incoming stream counts and body bytes against the advertised
windows, outgoing opens and sends against the peer's, one blocked capsule per
limit value, credit advertised as data is consumed and incoming streams finish),
processes close, drain and limit capsules, and terminates by resetting every
associated stream with `WT_SESSION_GONE`. A local close queues
`WT_CLOSE_SESSION` and requires the CONNECT stream to finish; a clean peer FIN is
a zero-code close; data after a peer close is `H3_MESSAGE_ERROR`. The session
never touches a QUIC stream: it hands the transport a `WebTransportStreamHandle`
to reset and a queue of capsules to write.

`WebTransportSessionRegistry` is the per-connection map. It decides admission
(one session without negotiated flow control, otherwise a configured bound),
routes stream headers and datagrams to live sessions, buffers those that name a
session the connection has not yet seen (bounded; excess streams are reset with
`WT_BUFFERED_STREAM_REJECTED` and excess datagrams dropped), resets streams that
name a closed or non-WebTransport request stream with `WT_SESSION_GONE`, and
terminates everything on connection shutdown.

Error contract: connection-scoped violations throw the connection's existing
`Http3ProtocolException`; session-scoped violations throw
`WebTransportException`, after which `FaultErrorCode` names the code the CONNECT
stream must be reset with.

## Choices the draft leaves open

- `WT_MAX_STREAM_DATA` and `WT_STREAM_DATA_BLOCKED` are "a session error" without
  a code; the core closes the session with `WT_FLOW_CONTROL_ERROR`. They are
  ignored when flow control is not enabled, like every other flow-control capsule.
- A malformed session capsule (short close payload, non-zero drain length, extra
  limit bytes) resets the CONNECT stream with `H3_MESSAGE_ERROR`, matching the
  draft's treatment of an invalid close message.
- An initial stream-limit setting above 2^60 is `H3_SETTINGS_ERROR`; the draft
  states the bound for capsules only.
- Closed or positively classified non-WebTransport request identifiers are
  tracked in compact, exact ranges of client bidirectional stream ordinals.
  Seeing a higher request does not classify unseen lower identifiers: independent
  HTTP/3 streams can arrive out of order. The default internal range budget is
  128; excessive sparse history raises `H3_EXCESSIVE_LOAD`. Consecutive completed
  requests compact into one range, including when gaps close out of order.
  Unknown session traffic still uses the existing bounded early-data queues.
- When a buffer is full the newest arrival is discarded.
- Credit is re-advertised once half of the initial window has been released,
  before any blocked signal could be needed.
- A DATAGRAM capsule (type 0) on the CONNECT stream is skipped like any other
  unknown capsule; WebTransport datagrams travel as QUIC DATAGRAM frames.

## Validation

`WebTransportCoreTest` has 83 cases driven through reflection. They cover the
registration constants, error-code mapping (round trips, reserved-codepoint
skipping over 5000 values, out-of-range rejection), session identifier rules,
stream headers in every integer width and across splits, non-0x41 signals,
server header round trips, datagram association and malformed datagram headers,
settings requirements/duplicates/overflow/truncation/encoding, close and limit
capsule codecs including boundary lengths and invalid UTF-8, the capsule reader
over fragmented input with unknown-type skipping and oversized or truncated
payloads, session lifecycle (buffering, establishment, local and peer close,
clean FIN, abort, drain, fault termination), flow control enabled and disabled
(limit violations, blocked signals, non-increasing limits, prohibited capsules,
credit advertisement, the section 5.6.2 limit-of-three example), registry
admission, buffering bounds, gone-versus-early routing, invalid identifiers,
shutdown, handle abort-once semantics and a concurrent attach/close race.

All 83 pass on Windows with the .NET 10 asset and with the actual netstandard2.0
assembly loaded on the .NET 10.0.12 runtime (the modern asset was restored
byte-exact afterwards). Both targets build with zero warnings, the null-forgiving
guard, suppression guard and whitespace verification pass, and the full Windows
suite reports 4795 cases (4712 base plus 83), zero failures and the five existing
platform skips. This is unit evidence for an isolated
core; it proves nothing about browsers, peers or a live QUIC transport.

## Integration contract for the transport

The HTTP/3 connection owns every stream, deadline and resource policy. To attach
this core it must:

1. Parse its peer SETTINGS payload with `WebTransportSettings.Parse` beside the
   existing parser, append `EncodeLocal()` to its own SETTINGS frame when
   WebTransport is enabled, advertise `SETTINGS_H3_DATAGRAM` only when datagrams
   really work, and construct one `WebTransportSessionRegistry` with the
   negotiated transport-parameter result.
2. Call `NoteRequestStream` only after that exact request is positively classified
   as non-WebTransport or its CONNECT is rejected. Do not call it just because a
   stream arrives or header processing begins. It releases any early buffers for
   that exact identifier; other identifiers remain eligible for association.
3. On an extended CONNECT with `:protocol` equal to `ProtocolToken`, call
   `TryCreateSession`, answer `TooManySessions` with `H3_REQUEST_REJECTED` or 429,
   let the application accept or refuse (405 when the resource is not a
   WebTransport endpoint, 403 on Origin failure), and call `Establish` after
   sending the 2xx. Read the CONNECT stream with `WebTransportCapsuleReader`,
   feed `ProcessCapsule`, call `PeerFinished` on a clean end, `PeerAborted` on a
   reset, and write capsules from `TryDequeueOutgoingCapsule` in order, finishing
   the stream when `OutputMustFinish` is set. Reset the CONNECT stream with
   `FaultErrorCode` when it is set and call `SessionEnded` when the session is
   over.
4. On a unidirectional stream of type 0x54, read the session identifier with
   `TryReadSessionId`; on the first bytes of a client bidirectional stream use
   `TryReadBidirectionalHeader` before the HTTP/3 frame parser; route the result
   with `RouteIncomingStream` and a handle whose abort resets both directions.
   Report body bytes with `RecordIncomingData`/`ReleaseIncomingData` and stream
   ends with `StreamFinished`; open server streams through `TryOpenOutgoingStream`,
   the header writers and `RegisterOutgoingStream`.
5. On a QUIC datagram, call `ReadDatagramSessionId` and `RouteDatagram`.
6. Call `Shutdown` when the connection ends.

Section 4.4 requires `RESET_STREAM_AT` so a reset never loses the stream header;
`System.Net.Quic` exposes no such frame, so the native provider under
development in PR #231 is the expected home for that requirement.

## Remaining gaps

- No transport integration: no session is created by the listener, no setting is
  advertised and no `WebTransportStreamHandle` is backed by a QUIC stream.
- No `RESET_STREAM_AT`, no `max_datagram_frame_size`/`reset_stream_at` transport
  parameter check and no native datagram path; these belong to the QUIC provider.
- Draft section 7.1's requirement that, for draft versions, a server must not
  process WebTransport requests before the client's SETTINGS arrive is a
  connection-ordering rule the transport must enforce.
- The HTTP/2 mapping (draft-ietf-webtrans-http2-15) shares the flow-control
  codepoints but carries streams and datagrams in capsules; it is not implemented.
- Origin verification, 405/403/429 policy, rate limiting, keying-material
  exporters (section 4.8) and the `WT-Available-Protocols`/`WT-Protocol`
  header fields (section 3.3, 9.7) are not implemented.
- No independent peer, browser or fuzzing evidence exists; the draft may change
  before publication and every codepoint here must be re-checked against it.


## Ordering correction under review

At PR #233 head `eea1ad8`, four deterministic cases fail: request 12 is processed
first, then a stream or datagram names delayed CONNECT 0 or 8. The original
high-water inference rejects all four despite available buffering capacity.
The retained unchanged production binary SHA-256 is
`61E7349AD48829792043697891BDB7A056374C0A9A52A598FAF392D7832E4C01`.
This follows the out-of-order arrival scenario in draft section 4.6; it is a
core reproduction, not a browser or native QUIC interoperability campaign.

The isolated correction records exact unavailable IDs using bounded compact
ranges, releases early buffers when their exact request is classified as
ordinary/rejected, and prevents reuse of a closed session ID. Seven additional
cases cover four delayed-CONNECT vectors, exact-ID buffer release, range joining
and sparse-history exhaustion, and closed-session identity. The old gone-session
case now explicitly classifies request 8 instead of inferring it from request 12.
The corrected source passes all 90 focused cases on Windows with the .NET 10
asset, with the current .NET Standard 2.0 asset on a .NET 10 host, and on pinned
Linux using the same Windows-built IL. Both core targets build without warnings;
syntax, suppression and changed-source whitespace guards pass. The full Windows
suite reports 4802 cases: 4797 passed, five existing skips, zero failures, in
3m 18s. Hosted final-head checks remain required. This does not establish legacy
.NET Framework, browser or native QUIC interoperability. No existing application
advertises WebTransport support.

An initial retained-asset probe accidentally selected a stale pre-WebTransport
.NET Standard binary and failed with missing-type errors in all 90 cases. That
setup failure is retained separately; after rebuilding both actual core targets,
the current retained-asset run above passes. The modern binary is restored and
its SHA-256 verified after each retained-asset probe.
