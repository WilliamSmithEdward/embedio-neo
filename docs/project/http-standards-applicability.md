# HTTP standards applicability audit (October 2026)

Applicability audit for the [modern HTTP engine program](http-engine.md) (#181,
draft #182). It freezes the standards inventory on a stated date, maps the
requirements that reach an origin server to implementation, application API and
validation, and records gaps with minimal reproductions. It complements the
[conformance audit](http-conformance.md), which covers framing and stateful
campaigns. It is not a claim of complete conformance, and #181 stays open.

- Engine audited: `codex/managed-http-engine` at `01e785d`.
- Draft #224 (`codex/http-capsule-streaming` at `a2dc18c`, based on `01e785d`) was
  reviewed separately. Nothing below assumes it is integrated; rows that it changes
  say so.
- Status: complete with limits (see [Limits](#limits)). No production source,
  workflow, dependency or public API was changed. The tooling change adds probes
  and draft capture to the test-only conformance project.

Legend for the matrix: **V** implemented and checked by an independent peer;
**I** implemented, checked only by the engine's own tests or internal calls;
**P** partial; **M** missing; **N/A** not an origin-server requirement. "API" means
a public member an application can call; an internal method is not an API.

## Frozen inventory

Captured on **2026-10-10 between 07:25:08 and 07:25:22 UTC** by
[`capture_standards.py`](../../test/EmbedIO.Conformance/standards/capture_standards.py),
into ignored `TestResults/http-standards-applicability/standards-frozen`
(`manifest.json` SHA-256 `296de24e34b4b2b0962ec5f3243d279c56e7c02ba37088b4a5a1477f757be46c`).
Every file is stored with its URL, retrieval time and SHA-256.

- RFC Editor metadata for 48 RFCs: the previous 45-RFC seed plus RFC 7239
  (Forwarded), RFC 9209 (Proxy-Status) and RFC 6585 (429), which RFC 10036
  references. Update and obsolescence chains were followed recursively.
- RFC Editor errata: `errata.json` is byte-identical to the 2026-10-09 capture
  (SHA-256 `9fd444088360...7e59ee`, 11,736,937 bytes); 33 captured RFCs have errata.
- The full RFC index, swept for 2023+ output of httpbis, quic, masque, tls,
  webtrans, httpapi and moq.
- 14 IANA registries (all retrieved) and the datatracker state of 8 Internet-Drafts
  (new in this capture).

### Changes since the 2026-10-09 baseline

| Source | Change | Effect |
| --- | --- | --- |
| IANA HTTP fields (updated 2026-10-09) | `No-Vary-Search` registered, permanent, from `RFC-ietf-httpbis-no-vary-search-10` (approved, not yet an RFC) | Caching extension. A response field the application may emit; no engine requirement. |
| RFC Editor | No new RFC in the swept groups since RFC 10036 (August 2026) | None |
| Errata | No change | None |

### RFCs by applicability class

| Class | RFCs | Meaning for the engine |
| --- | --- | --- |
| Core origin requirements | 9110, 9112 (updated by 9931), 9113, 7541, 9114, 9204 | MUST/SHOULD requirements apply to the engine. Mapped below and in the conformance audit. |
| Engine-relevant extensions | 8441, 9220 (extended CONNECT), 9218 (priorities), 9297 (datagrams and capsules), 8297 (103), 10008 (QUERY), 9651 (structured fields, used by Priority, Capsule-Protocol, Accept-Query, Incremental), 10036 (Incremental), 6455 (updated by 7936, 8307, 8441), 7692 | Optional to implement, but once advertised or dispatched their MUSTs apply. |
| Discovery | 7838 (Alt-Svc), 9460 (HTTPS RR), 8336, 9412 (ORIGIN) | Optional; RFC 9114 3.1.1 makes Alt-Svc or equivalent the way clients find HTTP/3. |
| Intermediary and proxy | 9298 (updated by 9484, 9931), 9484, 7239, 9209, 6585, 9111 | Requirements fall on proxies, gateways and caches. An application that accepts `connect-udp`/`connect-ip` tunnels takes them on. |
| Platform-delegated | 9000-9002, 9369, 9368, 9846 (obsoletes 8446), 7301 (updated by 8447, 9847), 8470, 9849/9848, 9851, 9954, 10015, 10024, 9963, 9973 | TLS and QUIC stacks (SslStream, MsQuic). Recorded, not re-verified here. |
| Content codings | 1950-1952, 7932 (updated by 9841), 8878 (updated by 9659), 9842 | Optional codings; gzip/deflate/br status is in the conformance audit. |
| Application-level | 9530, 9421, 9729, 9440, 9457, 9745, 9652, 9727, 9875, 9512, 9532 | Fields and media types an application uses through the ordinary header API. |

Status at capture: 9110, 9111, 9112 are Internet Standards; the rest named above
are Proposed Standards except 1950-1952, 7932, 8878, 9659, 9841, 9440, 9512, 9850
and 9954 (Informational). RFC 9931 (March 2026) updates 9112 and 9298; RFC 10008
(June 2026) defines QUERY; RFC 10036 (August 2026) defines `Incremental`.

### Internet-Drafts tracked (not requirements)

| Draft | Revision, date | IESG state | Relevance |
| --- | --- | --- | --- |
| draft-ietf-webtrans-http3 | -16, 2026-07-06 | I-D Exists | WebTransport over HTTP/3 |
| draft-ietf-webtrans-http2 | -15, 2026-07-06 | I-D Exists | WebTransport over HTTP/2 |
| draft-ietf-webtrans-overview | -13, 2026-07-06 | I-D Exists | Framework |
| draft-ietf-httpbis-resumable-upload | -13, 2026-10-06 | I-D Exists | Uses 104 informational responses; 104 is a temporary IANA registration expiring 2026-11-13 |
| draft-ietf-httpbis-connect-tcp | -14, 2026-10-07 | Waiting for AD Go-Ahead | Templated TCP CONNECT; capsule types 0x08/0x09 provisional |
| draft-ietf-masque-connect-udp-listen | -16, 2026-08-31 | RFC Ed Queue | Capsule types 0x11-0x13 registered permanent |
| draft-ietf-quic-reliable-stream-reset | -11, 2026-10-06 | RFC Ed Queue | QUIC `RESET_STREAM_AT`; MsQuic concern |
| draft-ietf-httpbis-unencoded-digest | -05, 2026-08-13 | RFC Ed Queue | Application-level field |

WebTransport has no registered HTTP/3 settings or stream types: the HTTP/3 settings
and stream-type registries (last updated 2023-06-13) contain only RFC 9114, 9204,
9220, 9297 and one provisional METADATA entry.

### Registry entries that bind the audited areas

| Registry (last updated) | Entries |
| --- | --- |
| HTTP methods (2026-06-17) | QUERY, safe and idempotent (RFC 10008) |
| HTTP fields (2026-10-09) | Accept-Query (List), Capsule-Protocol (Item), Incremental (Item), Priority (Dictionary), No-Vary-Search |
| HTTP status codes (2025-09-15) | 100, 101, 102, 103; 104 temporary |
| HTTP upgrade tokens (2023-10-20) | `websocket`/`WebSocket`, `connect-udp`, `connect-ip`, `TLS`, `HTTP`; `h2c` marked obsolete by RFC 9113 |
| HTTP/2 (2022-08-17) | Settings 0x8 ENABLE_CONNECT_PROTOCOL, 0x9 NO_RFC7540_PRIORITIES; frames 0xa ALTSVC, 0xc ORIGIN, 0x10 PRIORITY_UPDATE |
| HTTP/3 (2023-06-13) | Settings 0x8 ENABLE_CONNECT_PROTOCOL, 0x33 H3_DATAGRAM; frames 0xc ORIGIN, 0xf0700-0xf0701 PRIORITY_UPDATE |
| MASQUE (2026-08-28) | HTTP capsule types 0x00 DATAGRAM (RFC 9297), 0x01-0x03 (RFC 9484), 0x08-0x09 provisional, 0x11-0x13 |

### Errata that touch the audited areas

Verified technical errata are reviewed as conformance corrections. Held-for-document-update entries are tracked for interpretation and future revision; they do not automatically amend published normative requirements.
The 2026-10-09 list in the conformance audit remains accurate; these were not in it.

| Erratum | Status | Assessment |
| --- | --- | --- |
| RFC 9204 8410 | Verified, technical | Appendix C sample encoder: Required Insert Count is index plus one. The engine computes `index + 1` (`src/EmbedIO/Net/Internal/Http3/QpackEncoder.FieldSection.cs:37`); pylsqpack decodes its output in Linux CI. Conforms. |
| RFC 9651 8869 | Verified, technical | Display String is a valid Item. Priority ignores non-integer/non-boolean values, so no effect. |
| RFC 6455 3433, 3150 | Verified, technical | Repeated `Sec-WebSocket-Extensions` allowed; corrected key test vector. WebSocket hardening (#190) owns these. |
| RFC 9113 7013, RFC 9114 7702 | Verified | Editorial (DATA not STREAM; no PADDING frame). No effect. |
| RFC 8441 5500, 8250 | Held | Editorial. No effect. |

## Requirement and evidence matrix

Line numbers are at `01e785d` unless marked `a2dc18c`. "Probe" names a case in the
[reproductions](#minimal-reproductions).

### Informational responses (RFC 9110 15.2, 10.1.1; RFC 8297)

| Requirement | Engine | API | Validation | Status |
| --- | --- | --- | --- | --- |
| 100 Continue answers `Expect: 100-continue` (MUST: 100 or final status) | Sent before dispatch on all versions: `HttpConnection.cs:376-377`; `MultiplexedContext.cs:71-76` via `HttpConnection.Http2.cs:140` and `Http3/Http3Listener.cs:297` | None: the application cannot delay, suppress or replace it | HttpClient on h1/h2/h3 (`HttpContinueNegotiationTest`); raw sockets; hyper-h2 `expect-continue` | V |
| No 1xx to HTTP/1.0 clients (MUST NOT) | Automatic 100 guarded (`HttpListenerRequest.cs:172`) | `IHttpResponse.StatusCode` accepts 100-199 with no guard (`HttpListenerResponse.cs:119-132`) | Probe `h1-app-1xx-to-http10` | **Violation, application-triggered (F1)** |
| Exactly one final response follows interim responses | Interim writer exists only for the fixed 100 on HTTP/1; HTTP/2 and HTTP/3 `SendHeadersAsync` accept 1xx internally | A 1xx `StatusCode` becomes the only response on HTTP/1, and a synthetic 200 on HTTP/2/3 | Probes `h1-app-1xx-without-final`, `h2-app-1xx-public-api` | **Violation (F1)** |
| 103 Early Hints (optional) | HTTP/2 and HTTP/3 internal only; HTTP/1 none | None | `Http2InteroperabilityTest.InformationalResponsePrecedesFinalResponse` drives internal types by reflection; the client checks only the final body | I (h2), M (h1, h3 API) |
| Unknown expectation: MAY answer 417 | Ignored | None | conformance `expect-unknown` | Policy |
| 104 Upload Resumption | None | None | None | N/A (draft, temporary code) |

### Trailers (RFC 9110 6.5, RFC 9112 7.1.2, RFC 9113 8.1, RFC 9114 4.1)

| Requirement | Engine | API | Validation | Status |
| --- | --- | --- | --- | --- |
| Request trailers parsed and validated (no framing, routing or pseudo fields) | HTTP/1 `ChunkedRequestStream.cs:222-236`; HTTP/2 `Http2RequestHeaders.cs:97-107`; HTTP/3 `Http3RequestBody.cs:65-71` | None. HTTP/1 and HTTP/2 discard; HTTP/3 stores `Http3RequestBody.Trailers`, which nothing reads | Raw socket and conformance `chunked-extensions-trailers` (h1); internal only for h2/h3 | V (h1 parse), I (h2/h3) |
| Trailers kept out of the header section unless permitted (MUST NOT merge) | Not merged | - | Probe `h1-request-trailer-visibility` | Conforms; application cannot read them (F4) |
| Response trailers (MAY) | HTTP/1 terminator is always `0\r\n\r\n` (`ResponseStream.cs:272-286`); HTTP/2 none; HTTP/3 `Http3QuicExchange.SendTrailersAsync` (`:122-136`) has no caller and no test | None | None | M (F4) |
| `TE: trailers` in HTTP/2/3 requests allowed, other TE values rejected | `Http2RequestHeaders.cs:131-133` | - | Internal unit tests | I |

### CONNECT and Upgrade (RFC 9110 7.8, 9.3.6; RFC 9931; RFC 8441; RFC 9220; RFC 9113 8.5)

| Requirement | Engine `01e785d` | Draft #224 `a2dc18c` | Validation | Status |
| --- | --- | --- | --- | --- |
| HTTP/1 Upgrade: MAY ignore; 101 only for an accepted token | WebSocket only (`HttpListenerRequest.cs:166-170`); unknown tokens ignored | Any token except `websocket` through `IHttpTunnelContext.AcceptTunnelAsync` | Probe `h1-unknown-upgrade-ignored`; #224 `HttpTunnelContextTest` (raw socket) | Policy |
| `h2c` Upgrade is obsolete (RFC 9113 3.1) | Not implemented; prior-knowledge h2c only | An application may accept `h2c` as an opaque tunnel; nothing blocks it | None | Observation (F8) |
| RFC 9931 8: close the connection when rejecting CONNECT | Authority-form CONNECT gets fixed 400 and close (`HttpConnection.cs:20, 358-373`); origin-form CONNECT >= 300 closes (`HttpListenerResponse.cs:275-278`) | Authority-form reaches the application; >= 300 closes | `Http1RejectedConnectTest`, `ModernHttpEngineTest` (raw sockets) | V |
| Rejected Upgrade: connection may continue | Kept persistent; pipelined successor served | Unchanged | `Http1RejectedConnectTest` GET cases | Policy (RFC 9931 places the risk on clients) |
| HTTP/2 SETTINGS_ENABLE_CONNECT_PROTOCOL only when extended CONNECT is supported | Sent as 1 (`Http2Connection.cs:55`) | Unchanged | Probe `h2-server-settings` (raw frames) | V |
| A 2xx response to CONNECT means a tunnel for the requested protocol | HTTP/2: any `:protocol` other than `websocket` reaches ordinary handlers as `CONNECT` (`HttpConnection.Http2.cs:114-129`); a resource answering 200 sends an ordinary body with no tunnel state. HTTP/3: non-`websocket` `:protocol` gets 501 (`Http3/Http3Listener.cs:287-288`) | HTTP/2 unchanged; HTTP/3 501 gate removed, so the same applies there (source); a 2xx CONNECT enters tunnel state (`Http2Exchange.PrepareTunnel`) | Probes `h2-extended-connect-unknown-protocol`, `h2-extended-connect-webtransport` (raw HTTP/2 frames) | **Violation (F2)** on both heads |
| Plain CONNECT over HTTP/2/3 (`:authority` only) | Validated (`Http2RequestHeaders.cs:66-68`), dispatched with path `/` | Accepted through `AcceptTunnelAsync(null)` | Probe `h2-plain-connect-to-handler` (404 from routing) | I |
| Extended CONNECT WebSocket version check | HTTP/2 and HTTP/3: 400 plus `Sec-WebSocket-Version: 13` | Unchanged | `Http2WebSocketNegotiationTest` (raw frames), `Http3WebSocketTest` (hand-written QUIC client) | I |

### HTTP Datagrams and capsules (RFC 9297)

| Requirement | Engine `01e785d` | Draft #224 `a2dc18c` | Status |
| --- | --- | --- | --- |
| HTTP/3 datagrams need SETTINGS_H3_DATAGRAM=1 from both peers | Peer value parsed (`Http3PeerSettings.cs:14, 42`), never read; server does not send it | Unchanged | Conforms by not using datagrams; M as a feature |
| Native QUIC DATAGRAM frames (RFC 9221) | Not used | Not used; the guide says so (`docs/guides/capsule-tunnels.md:115-117`) | M; depends on a System.Net.Quic datagram API (reported in the engine docs, not re-verified here) |
| Capsule Protocol framing on the stream | None | `HttpCapsuleChannel` read/write/skip with varint type/length, no length-based allocation; truncation aborts only the stream | M at `01e785d`; I and V (hyper-h2, aioquic campaign recorded in #224) at `a2dc18c` |
| `Capsule-Protocol: ?1` | None | Sent on accepted capsule tunnels; `HttpCapsuleProtocol.IsEnabled` exists but has no production caller, so the request field is never consulted | P. RFC 9297 ties capsule use to the upgrade token, so this is not a violation |
| DATAGRAM capsule (type 0x00) semantics | None | Delivered as an ordinary reliable capsule; no context or quarter-stream ID handling | Application responsibility per upgrade token |
| HTTP/2 datagrams | None | Only as DATAGRAM capsules on the stream, which is what RFC 9297 3.5 defines for HTTP/2 | P |

Reliable capsules do not demonstrate native QUIC datagrams, unreliable delivery or
WebTransport sessions.

### WebTransport (drafts)

| Item | Engine `01e785d` | Draft #224 `a2dc18c` | Status |
| --- | --- | --- | --- |
| Settings, stream type 0x54, signal 0x41, `:protocol=webtransport` sessions | None. Unknown unidirectional stream types are aborted with H3_STREAM_CREATION_ERROR (`Http3QuicConnection.cs:245-246`) and unknown frames skipped, as RFC 9114 6.2.3 and 9 require. HTTP/3 `:protocol=webtransport` gets 501; HTTP/2 dispatches it (F2) | `:protocol=webtransport` dispatched on both versions; an application could accept it only as a generic capsule tunnel | Experimental draft, not supported. Concrete experimental work remains in program181: freeze the October2026 draft/registry version and validate session/datagram interoperability before advertising support; F2 applies to the generic probe route meanwhile |

### Priorities (RFC 9218)

| Requirement | Engine | API | Validation | Status |
| --- | --- | --- | --- | --- |
| SETTINGS_NO_RFC7540_PRIORITIES sent by a server that ignores RFC 7540 priorities | Sent as 1; legacy PRIORITY frames shape-checked and ignored | - | Probe `h2-server-settings` | V |
| Priority field and PRIORITY_UPDATE parsed per structured fields; invalid parameters ignored | `HttpPriority.cs:44-79`; HTTP/2 `Http2StreamRegistry.cs:125-141`; HTTP/3 `Http3ControlStream.cs:56-73` | None; applications see the raw `priority` header only | httpwg structured-field corpus; wire tests drive internal types; no independent peer sends priority signals | I |
| Scheduling by urgency and incremental (SHOULD-level guidance, 10) | HTTP/2 orders only flow-credit admission (`Http2SendFlowControl.cs:41-48`); the frame writer is FIFO. HTTP/3 stores priority and never reads it | None | Internal | P (h2), M (h3) |
| Server-sent `Priority` response field may reprioritize (5) | Sent as an ordinary header, never read | - | None | M (optional) |

### Forwarding and incremental delivery (RFC 10036; RFC 7239; RFC 9110 7.6)

| Requirement | Engine | Status |
| --- | --- | --- |
| RFC 10036 3: an implementation whose API cannot transfer content incrementally SHOULD use `Incremental` to reduce buffering | Streaming request and response APIs exist. Opt-in whole-response buffering (`Internal/BufferingResponseStream.cs`, `ResponseSerializer.Json(true)`) and compression ignore the field. On managed HTTP/1 `FlushAsync` before the first write sends nothing (`ResponseStream.cs:50-56`), so headers cannot go out ahead of content | Conforms (the API is incremental). Early response headers on HTTP/1 are a gap (F6) |
| RFC 10036 intermediary MUSTs and 501/429 rejection | Origin, not an intermediary | N/A |
| Forwarded, Via, Max-Forwards, Proxy-Status | No processing; forwarded fields deliberately untrusted (`Security/ClientBanningModule.cs:14`) | N/A for an origin; trust is an application decision, documented |

### QUERY (RFC 10008)

| Requirement | Engine | API | Validation | Status |
| --- | --- | --- | --- | --- |
| Fail when Content-Type is missing (MUST) | 400 before dispatch for missing or unparseable Content-Type (``WebServerBase`1.cs:274-277``) | `HttpVerbs.Query`, `[Route(HttpVerbs.Query, ...)]` | Probe `h1-query-without-content-type`; raw TCP and HttpClient h1/h2/h3 tests | V |
| Inconsistent content and media type rejected with 4xx (MUST) | Only syntax is checked; consistency is the resource's | `QueryFormatPolicy.Apply` gives 400/415 and `Accept`; content checks are the application's | Probe `h1-query-empty-content` returns 200 from the echo resource | Application responsibility |
| Accept-Query is a Structured Fields List | `QueryFormatPolicy` emits quoted strings with SF parameters | Public | Unit and wire tests | V |
| Conditional and range requests as for GET | Preconditions and ranges treat QUERY as retrieval | Public helpers | `QueryPreconditionWireTest`, `QueryRangeWireTest` | V |
| Cache key includes content | No cache | - | - | N/A |

### Discovery (RFC 9114 3.1.1; RFC 7838; RFC 9460; RFC 8336, 9412)

| Item | Engine | Status |
| --- | --- | --- |
| Alt-Svc header or ALTSVC frame | Never emitted; constant only (`HttpHeaderNames.cs:104`) | M. Without it or an HTTPS record, browsers will not use HTTP/3 (documented in `docs/guides/http3.md:26`) |
| ORIGIN frame (h2, h3) | Not sent; received frames ignored as unknown | M (optional) |

## Findings

In priority order. "Owner decision" marks items whose fix changes public behavior
or an approved draft and therefore needs William's approval before implementation.

| ID | Finding | Kind | Reproduction |
| --- | --- | --- | --- |
| F1 | The public `StatusCode` setter accepts 100-199 and the engine writes it as a final head. HTTP/1.0 clients receive `HTTP/1.0 103` (RFC 9110 15.2 MUST NOT); HTTP/1.1 clients receive a 1xx and never a final response; HTTP/2 and HTTP/3 replace it with a synthetic 200 (`CompleteAsync` falls back to `RespondAsync`: `Http2Exchange.cs:110-111, 47`; `Http3QuicExchange.cs:139-140, 66`) or throw "Informational response cannot end a stream". Either reject 1xx in the setter or treat it as an interim send followed by a required final status. | MUST, application-triggered | `h1-app-1xx-to-http10`, `h1-app-1xx-without-final`, `h2-app-1xx-public-api` |
| F2 | HTTP/2 extended CONNECT with any `:protocol` other than `websocket`, including `webtransport`, is dispatched to ordinary handlers. `HttpVerbs.Any` handlers then answer 200, which tells the client a tunnel for that protocol exists. #224 keeps this on HTTP/2 and, by removing the 501 gate, extends it to HTTP/3 (source-traced). A safe default is to answer 501 (or 404) unless a tunnel-aware handler accepts the protocol through `AcceptTunnelAsync`. #224's routing of non-WebSocket extended CONNECT to applications was explicitly approved, so the default is an owner decision. | MUST semantics, security | `h2-extended-connect-unknown-protocol`, `h2-extended-connect-webtransport` |
| F3 | No public interim-response API on any version, so 103 Early Hints is unavailable to applications, and HTTP/1 has no interim writer at all. The internal HTTP/2/3 103 path is checked only through reflection. | Optional capability | `h2-app-1xx-public-api`; source |
| F4 | Trailers are not reachable by applications: request trailers are validated and dropped on HTTP/1 and HTTP/2 and stored but unread on HTTP/3; response trailers cannot be sent on any version; `Http3QuicExchange.SendTrailersAsync` is unreferenced and untested. Discarding is permitted (RFC 9110 6.5.1), so this is a capability gap, not a violation. | Capability | `h1-request-trailer-visibility`; source |
| F5 | `Expect: 100-continue` is answered before dispatch on every version, so an application cannot reject an upload (for example with 401 or 413) before the client sends the content. Conforms; recorded because applications cannot choose. | Application control | `h1-expect-app-rejects` |
| F6 | On managed HTTP/1, `FlushAsync` before the first write does not send the response head, so applications cannot send headers ahead of content (early responses, RFC 10036's bidirectional use). HTTP/2 and HTTP/3 flush headers. | Capability | Source (`ResponseStream.cs:50-56`) |
| F7 | Priority affects only HTTP/2 flow-credit admission; HTTP/3 does not schedule; there is no application API to read or set priority, and server `Priority` responses are ignored. No independent peer sends priority signals in any test. | Optional, partial | Source; test inventory |
| F8 | Draft #224 blocks only `websocket` among tunnel tokens. An application can accept `h2c` (obsolete) or registered tokens such as `connect-udp`/`connect-ip` without their RFC 9298/9484 obligations. Documenting those obligations, or rejecting `h2c`, would close the gap. `HttpCapsuleProtocol.IsEnabled` has no production caller. | API safety | Source (`a2dc18c`) |
| F9 | Discovery is absent: no Alt-Svc, ALTSVC or ORIGIN. HTTP/3 is reachable only by clients with prior knowledge. | Optional, needed for browsers | Source |
| F10 | `SETTINGS_H3_DATAGRAM` is parsed but unused, there is no native QUIC datagram path, and no WebTransport session support on either head. Correct for what is advertised; a capability limit to state wherever capsules are described. | Capability limit | Source |

Documentation drift found during the audit, for the owners of those files:
`http-conformance.md` rates QUERY "Conforms" without the content-consistency
limit and says Alt-Svc is "tracked by #182"; `http-engine.md` still lists the
extension inventory as pending (this document answers that row, but the gate stays
open until the gaps are decided); #224's `docs/project/http-capsule-transport.md`
line 140 says the new peer paths "have not been executed" while later sections
record that they passed.

## Minimal reproductions

`dotnet run --project test/EmbedIO.Conformance -c Release -- applicability --out <file>`
starts an in-process `WebServer` on loopback (managed listener, cleartext HTTP/1.1
and prior-knowledge HTTP/2) and runs the probes in
[`ApplicabilityProbes.cs`](../../test/EmbedIO.Conformance/ApplicabilityProbes.cs).
Probe routes in `ConformanceServer.cs` use only public APIs. The HTTP/1 client is
the independent `RawHttp1`; the HTTP/2 probes write frames and literal HPACK by hand
and read only static-table `:status` values; `h2-app-1xx-public-api` uses .NET
`HttpClient`. Outcome `gap` records a missing capability where the RFC permits the
observed behavior; only `violation` and `error` fail the run (exit 1).

Observed on Windows 11 (10.0.26300), SDK 10.0.401, runtime 10.0.12, 2026-10-10.
Results were identical at `01e785d` and at `a2dc18c` with the same probes applied:

| Probe | Result | Observation |
| --- | --- | --- |
| h1-app-1xx-to-http10 | violation | `HTTP/1.0 103`, then EOF; no final response |
| h1-app-1xx-without-final | violation | `HTTP/1.1 103`, then timeout; no final response |
| h1-expect-app-rejects | gap | `100` then `413`; the 100 precedes the application's decision |
| h1-request-trailer-visibility | gap | Application sees `host,trailer,transfer-encoding`; trailer consumed |
| h1-query-without-content-type | conforms | 400 |
| h1-query-empty-content | policy | 200 from the echo resource |
| h1-unknown-upgrade-ignored | policy | 200, pipelined successor 200 |
| h2-server-settings | policy | `0x3=128 0x6=32768 0x8=1 0x9=1`; no 0x33 |
| h2-extended-connect-unknown-protocol | violation | `:protocol=x-probe-unknown` to `/plain`: 200 |
| h2-extended-connect-webtransport | violation | `:protocol=webtransport` to `/plain`: 200 |
| h2-plain-connect-to-handler | conforms | 404 (routing on `/`) |
| h2-app-1xx-public-api | gap | Final 200 with empty body instead of the application's 103 |

Raw logs and JSON: ignored `TestResults/http-standards-applicability/runs`.

## Limits

- HTTP/3 behavior is source-traced, not probed: the probe has no QUIC client, and
  the HTTP/3 half of F2 at `a2dc18c` follows from the removed gate and the shared
  dispatch path. An aioquic case in `h3_campaign.py` would confirm it.
- Probes ran on Windows only, one run, no TLS, managed listener only. The Microsoft
  backend serves HTTP/1.1 through `HttpListener` and was not probed.
- No heavy campaign, browser, curl, nghttp2 or h2spec run was performed for this
  audit. Platform-delegated RFCs (TLS, QUIC) were classified, not tested.
- Requirement mapping for the core RFCs relies on the conformance audit's campaigns
  for framing; this document adds the extension and application-API layer.
- The System.Net.Quic datagram limitation is taken from the engine and #224
  documentation, not re-verified against .NET 10 API references here.
- The inventory is frozen at the capture time above. RFCs, errata and registries
  published later need a new capture.


## Integration review limits

The frozen observations above retain their exact source revisions; they are not a claim that every probe was rerun on the current engine. Capsule PR224 is now integrated as1a4def7. Program181 retains native datagram and experimental WebTransport development and validation; draft status does not silently remove those work items. F2 uses an ordinary probe resource that does not implement the requested extension: its2xx response demonstrates accidental acceptance risk with catch-all handlers. It does not establish that every application-defined protocol handler is nonconforming. Explicit application-controlled dispatch was approved; safe acceptance behavior still needs a concrete compatibility decision and protocol-specific validation.
