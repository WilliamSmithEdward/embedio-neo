# HTTP conformance audit and stateful campaigns

Independent conformance work for the [modern HTTP engine program](http-engine.md)
(#181, draft #182). The engine under test is the HTTP/1.1, HTTP/2 and HTTP/3
implementation on `codex/managed-http-engine`. Managed WebSocket hardening (#190)
is out of scope here and covered separately. This document records what was
verified, against which sources and source revision, and what remains open. It is
not a claim of complete protocol conformance.

## Current integration checkpoint

The findings and baseline tables below retain the original audit revision; they
must not be read as current engine failures. A later independent campaign used
an archived d6107f8 source snapshot, with production source byte-identical to
engine e52f829. The corrected HTTP/2 peer rerun used archived 76e4f58. Both Linux
builds loaded the same core SHA-256:
`E5D0109DA72920E4A7E17705B565C737AA13B7848AD7D95A6080496E225CC57D`.
The host was Ubuntu 24.04.5 x64, SDK 10.0.401, runtime 10.0.12 and MsQuic 2.6.2,
inside a container limited to four CPUs and 6 GiB. Seed: 20261009.

| Completed campaign | Result |
| --- | --- |
| HTTP/1 cases | 44 conforming, 13 permitted policy choices; no violations/errors |
| HTTP/1 stateful | 2,000 iterations; 5,029 valid requests, 473 malformed sequences, 211 aborts; handlers drained, handles +2 |
| HTTP/2 cases, cleartext and TLS separately | 16 conforming, 6 policy observations each; no violations/errors; each includes 300 reset trials with no lost connections, 40 shrink trials and 60 paired-settings trials |
| HTTP/2 stateful with corrected peer | 300 connections, 1,245 streams, 121 client resets, no server resets; handlers drained, handles +14, retained managed growth 1,552,016 bytes |
| HTTP/3 cases | 7 conforming, 1 policy observation; includes 200 rounds of four cancelled uploads with a healthy listener afterward |
| HTTP/3 stateful, upload cancellation included | 100 connections, 676 streams, 96 cancellations, no server resets; handlers drained, handles +16, retained managed growth 1,607,688 bytes |

The original HTTP/2 runs are retained: iteration 58 exposed the pinned peer's
per-key ACK bookkeeping; iteration 76 exposed its rejection of legal empty EOF
with a negative adjusted window; iteration 178 exposed the driver's completion
phase shrinking larger credit to zero. Server-free wire regressions cover all
three corrections and still require a genuine nonzero window overrun to fail.
Overlapping SETTINGS, randomized resets, payload checks and deadlines remain.
See the [peer-model notes](../../test/EmbedIO.Conformance/README.md).

Unmodified h2spec still reports failure: 141/146 cleartext and 142/146 TLS.
Its four common failures use older closed-stream/priority expectations, assessed
against RFC 9113 and RFC 9218 in the [engine program](http-engine.md). The
[application lifecycle audit](#application-lifecycle-audit-2026-10-10) reran it
on the current engine and refines that assessment: the two closed-stream DATA
cases sit on a tension inside RFC 9113 and are not classified as permitted.
The exact plaintext invalid-preface input was captured separately: it received
HTTP/1.1 400 with Content-Length 0 and Connection: close, then clean EOF. TLS h2
received clean EOF. These observations do not relabel h2spec's aggregate as passing.
The original full campaign aggregate remains failed and its artifacts are retained.

These are focused Linux campaigns, not completion of Windows/macOS, browser,
WebSocket, long-running soak, performance or the full standards inventory.
No production source or package change was needed for these peer corrections.

## Application lifecycle audit (2026-10-10)

Application-level conformance of the behavior added most recently: informational
responses followed by a final response, response trailers and end-of-message
ordering, CONNECT and capsule tunnel cancellation, half-close and disposal,
connection survival after one HTTP/2 or HTTP/3 stream fails, graceful drain with
concurrent and committed responses, and malformed framing and stream-state
transitions. Test tooling and documentation only; no production source, workflow,
dependency, public API or default changed.

### Source and live provider

The audit branch is based on engine head `a4f7105` (#238). Campaign and focused
evidence ran on snapshots of the same audit files over engine `90e84f1`; the only
production difference between those bases is
`src/EmbedIO/Net/Internal/Http3/MsQuicNativeStream.cs`.

The live provider was confirmed from source rather than documentation.
`WebServer.CreateHttp3Listener` constructs `Http3Listener` (or
`CombinedHttpListener`), which accepts connections from System.Net.Quic
`QuicListener` and wraps them in `SystemQuicTransportConnection`. Nothing outside
their own files references `MsQuicApi.CreateListener`, the native connection and
stream types, `Http3DatagramCodec` or the WebTransport session registry. Every
HTTP/3 result below is therefore System.Net.Quic (MsQuic 2.6.2 on Linux, the
runtime-bundled MsQuic on Windows). It says nothing about the isolated native
provider, native QUIC datagrams or WebTransport, which are not integrated.

### Standards baseline

The [frozen inventory](http-standards-applicability.md#frozen-inventory) of
2026-10-10 07:25 UTC remains the RFC, errata and registry baseline. For this audit
the primary texts were retrieved again from the RFC Editor at 17:34 UTC and
stored with SHA-256 under ignored `TestResults/audit/standards` in the audit
worktree, for example RFC 9113 `a00ef91b...080506de`, RFC 9114
`6b84555c...35db34e4` and RFC 9110 `21c1cdce...7232a`; the manifest holds the full
digests. RFC 8297, 8441, 9112, 9218, 9220 and 9297 were captured the same way.
RFC 9113 has two errata
(7013 verified editorial, 9175 reported); neither touches the cases below. The
h2spec v2.6.0 sources for its stream-state cases and verifier were captured beside
them.

### Regression file

[`HttpApplicationLifecycleAuditTest`](../../test/EmbedIO.Tests/HttpApplicationLifecycleAuditTest.cs)
is owned by this audit. Every case drives a public `WebServer` through public APIs.
The peers share no code with the engine: a raw HTTP/1.1 reader written from RFC 9112,
a raw HTTP/2 frame peer with hand-written literal HPACK requests, and a raw
System.Net.Quic peer that reads HTTP/3 frame types and lengths. Server field values
are not decoded in this file; the Python campaigns below decode them independently.
Ports come from the operating system, not the shared test counter.

| Case | Requirement | Result |
| --- | --- | --- |
| HTTP/1 two 103 sections, final, chunked body, trailers, then a pipelined successor and a third request | RFC 9110 15.2; RFC 9112 7.1.2 | Pass |
| HTTP/2 frame order: interim HEADERS, final HEADERS, DATA, trailer HEADERS with END_STREAM | RFC 9113 8.1 | Pass |
| HTTP/2 client RST_STREAM after the interim section, after final headers, mid-body and before trailers (4) | RFC 9113 5.1, 5.4.2, 6.4 | Pass: no frame on the stream after the reset, application canceled, sibling, PING and listener healthy, close callback once |
| HTTP/2 stream errors on a live connection: DATA or HEADERS after END_STREAM (STREAM_CLOSED), trailers without END_STREAM, pseudo-header in trailers (PROTOCOL_ERROR) (4) | RFC 9113 5.1, 8.1 | Pass: stream reset only, sibling healthy |
| HTTP/2 connection errors: CONTINUATION without HEADERS, RST_STREAM, WINDOW_UPDATE or DATA on an idle stream (4) | RFC 9113 5.1, 6.4, 6.10 | Pass: GOAWAY PROTOCOL_ERROR, close, new connection served |
| HTTP/2 tunnel half-closed by the client keeps server output open, raw and capsule (2) | RFC 9113 8.5 | Pass |
| HTTP/2 application read timeout answers 408 before the request ends | RFC 9110 15.5.9; RFC 9113 8.1 | Pass |
| HTTP/2 drain with a committed response that reserved trailers, and a stream opened after GOAWAY | RFC 9113 6.8 | Pass: GOAWAY(1, NO_ERROR), late stream REFUSED_STREAM, admitted response completes with trailers |
| HTTP/2 drain with an open tunnel: cooperative completion, and an idle tunnel at a 2 s deadline (2) | RFC 9113 6.8 | Pass: tunnel finishes during the drain; the idle one is released at the deadline (2,011 ms), close callback once |
| HTTP/3 client abort after the interim section | RFC 9114 4.1.1, 8.1 | Pass |
| HTTP/3 drain with interim, body and trailers, then client close | RFC 9114 5.2 | Pass |
| HTTP/3 tunnel handler failure with a read in flight | RFC 9114 4.1.1 | Pass: stream aborted, sibling healthy |
| F1 public `StatusCode` setter with 1xx, HTTP/1.0 and 1.1 (2) | RFC 9110 15.2 | Explicit reproduction, fails as recorded below |
| A1 HTTP/3 application read cancellation, tunnel and request body (2) | Contract consistency | Explicit reproduction, fails as recorded below |

Discovery rises by 27 cases: 23 run in the ordinary suite and 4 `Explicit`
reproductions are reported as not executed. With base floor 4,903 the reconciled
floor is 4,930. CI and CONTRIBUTING floors are not changed here; the engine owner
reconciles them.

### Independent peer campaigns

`run-lifecycle-campaigns.sh` runs in the pinned conformance container on a
`git archive` snapshot, with a fresh server process per phase. The image was
rebuilt from the unchanged Dockerfile (local image ID `637908f6...b61ba7f8`):
SDK 10.0.401, runtime 10.0.12, MsQuic 2.6.2, Ubuntu 24.04.5, h2spec built from its
v2.6.0 tag commit (it reports itself as 2.0.0), hyper-h2 4.4.1 with hyperframe 6.1.0
and hpack 4.2.0, aioquic 1.3.0 with pylsqpack 0.3.24. The container ran with a
four-CPU quota and 6 GiB on WSL2 kernel 6.18.33.2 (`nproc` reports the host's 16).
The loaded core SHA-256 was `a22b29d5...36510fafff8` for every run.

`drivers/lifecycle_campaign.py` adds two test-only host routes: `/lifecycle`
(interim sections, a paced body and digest trailers) and `/__drain` (starts a
graceful drain of one named endpoint and reports its outcome in `/__stats`). Each
fuzz iteration opens one connection and mixes lifecycle responses, uploads,
capsule tunnels including half-close, client resets at random times, and
Content-Length mismatches, which must be stream errors. Every stream the client
kept must complete exactly, including the trailer digest, and a sibling must
succeed afterwards. Drain phases open three paced lifecycle streams and a
half-closed capsule tunnel, start a drain on another endpoint, send tunnel input
after GOAWAY and open one more stream.

| Phase, snapshot `4752c44` | Result |
| --- | --- |
| Response sections, existing driver (h1, h1 TLS, h2, h2 TLS, h3 with adapter) | Pass |
| Capsule vectors, existing driver (h2, h2 TLS, h3) | 15 of 15 pass |
| HTTP/2 cleartext fuzz, seed 20261010, 300 connections | Pass: 1,531 streams, 539 interim, 468 trailer sections, 170 malformed (all PROTOCOL_ERROR stream resets), 90 client resets, 72 half-closed tunnels; handles +14, threads +4, managed +1,672,704 bytes, active handlers 0 |
| HTTP/2 TLS fuzz, seed 20261010, 100 connections | Pass: 477 streams, 56 malformed, 48 client resets; handles +13 |
| HTTP/3 fuzz, seed 20261010, 100 connections | Pass: 491 streams, 183 interim, 165 trailer sections, 47 malformed (all H3_MESSAGE_ERROR), 14 client resets; handles +14 |
| Same three, seed 20261011 at scale 3 (900, 300 and 300 connections) | Pass: 4,488, 1,511 and 1,514 streams; handles +14, +13, +14; active handlers 0 |
| HTTP/2 TLS drain, 10 fresh servers | 10 pass: GOAWAY(7, NO_ERROR), late stream REFUSED_STREAM, every admitted stream complete with trailers, tunnel input delivered after GOAWAY, close at 0.51 to 0.54 s |
| HTTP/3 drain, 10 fresh servers | 10 pass: GOAWAY(16), late stream H3_REQUEST_REJECTED (0x10b), all complete; drain ends when the client closes |
| h2spec cleartext and TLS | 141/146 and 142/146, the same five and four cases as before (assessed below) |

The HTTP/2 client needed one counted adapter. hyper-h2 4.4.1 defines no
`(CLOSED, RECV_INFORMATIONAL_HEADERS)` transition, so a 103 the server queued
before it processed the client's RST_STREAM tears down the client connection.
RFC 9113 section 5.1 requires the resetting endpoint to minimally process and
discard such frames. The adapter decodes the block to keep HPACK state and drops
it, only for 1xx HEADERS on streams the client reset. It was used 10, 13, 36 and
33 times in the four HTTP/2 fuzz runs. The deterministic C# reset cases establish
that the server sends nothing on a stream after it has processed the reset. The
HTTP/3 peer uses the existing aioquic interim adapter documented in the
[response-section guide](../guides/response-field-sections.md); aioquic does not
surface GOAWAY, so the drain driver records the control frame without changing
its processing.

### h2spec recheck against RFC 9113

| h2spec case | RFC 9113 text | Assessment |
| --- | --- | --- |
| 3.5/2 invalid preface, cleartext only | Section 3.4: an invalid preface is a connection error, and "A GOAWAY frame ... MAY be omitted" | Permitted. The shared HTTP/1.1 and h2c port answers HTTP/1.1 400 and closes (captured earlier); h2spec fails parsing those bytes as frames. TLS passes. |
| 5.1/8 DATA after the peer's RST_STREAM; 5.1/11 DATA on a closed stream | Section 6.1: DATA on a stream not "open" or "half-closed (local)" "MUST respond with a stream error ... STREAM_CLOSED". Section 5.1, closed: an endpoint "can perform this minimal processing for all streams that are in the closed state" | Contested inside RFC 9113, not permitted outright. The engine discards the DATA and credits the connection window, which follows section 5.1; section 6.1 read literally requires RST_STREAM(STREAM_CLOSED). h2spec v2.6.0 accepts RST_STREAM or GOAWAY with STREAM_CLOSED, or closure (`VerifyStreamError`). Answering such DATA with RST_STREAM(STREAM_CLOSED) satisfies both readings. Owner decision. |
| 5.3.1/1 and 5.3.1/2 self-dependency | Section 5.3.2 deprecates RFC 7540 priority signaling; the captured RFC 9113 text has no self-dependency rule. The server advertises SETTINGS_NO_RFC7540_PRIORITIES=1 (RFC 9218 section 2.1) | Obsolete RFC 7540 section 5.3.1 expectation. The server answered the request normally. |

The previous "permitted" label on 5.1/8 and 5.1/11 rested on section 5.1 alone.
The aggregate h2spec result remains a failure, and its logs and JUnit reports are
retained.

### Findings

**F1, rechecked, unresolved (MUST).** The applicability audit's F1 still
reproduces through the public `StatusCode` setter, now alongside the supported
`IHttpResponseSections.SendInformationalAsync`. HTTP/1.0 receives `HTTP/1.0 103`
then EOF (RFC 9110 section 15.2: a server MUST NOT send 1xx to an HTTP/1.0 client).
HTTP/1.1 receives `HTTP/1.1 103` and no final response within 5 s. Identical on
Windows and Linux. The guide tells applications not to use the setter for interim
responses; the setter still accepts 100 to 199. Reproduction:
`Http1StatusSetterNeverSendsAnInformationalStatusAsTheFinalResponse`, `Explicit`,
category `AuditFinding`.

**A1, new, unresolved (contract consistency).** On HTTP/3, an application that
cancels its own pending request-body or tunnel read loses the whole stream:
`Http3RequestBody.ReadAsync` reports any `OperationCanceledException` to
`Http3QuicConnection.RequestFailed`, which calls `Abort(QuicAbortDirection.Both,
0x10c)`. The peer receives RESET_STREAM H3_REQUEST_CANCELLED, and a 408 or later
tunnel output never arrives. On HTTP/2 the same application answers 408 (tested
here), and the lifetime audit records that HTTP/1 and HTTP/2 tunnel output stays
usable after a canceled read. RFC 9114 section 4.1.1 lets a server abort a request
stream, so this is not a protocol violation. It is a divergence in the
version-independent `IHttpTunnelContext` and request-body contract, and it makes
the guide's deadline pattern abort rather than answer on HTTP/3. Reproductions:
`Http3TunnelApplicationReadCancellationLeavesOutputUsable` and
`Http3ApplicationBodyReadTimeoutCanStillAnswer`, both `Explicit`, identical on
Windows and Linux.

**Observations (permitted choices, recorded).**

- HTTP/3 drain leaves connection closure to the peer once every admitted request
  finished, bounded by the drain deadline (`Http3QuicConnection.WatchDrainAsync`).
  `DrainAsync` therefore completes when the client closes, about 0.5 s after the
  last response in the campaign, and not before. HTTP/2 closes the connection
  itself as soon as its admitted streams finish. RFC 9114 section 5.2 and RFC 9113
  section 6.8 permit both.
- Streams opened after GOAWAY are refused, not ignored: HTTP/2 REFUSED_STREAM,
  HTTP/3 H3_REQUEST_REJECTED. Both are permitted.
- After a client reset, a further application write fails with
  `ObjectDisposedException` or `OperationCanceledException`, depending on the phase.
- At a drain deadline an idle tunnel is aborted at the deadline, and `DrainAsync`
  completes without an exception.

### Fixture and driver problems found and corrected

Retained, not overwritten:

- The first focused run had four failures. Two were fixture defects: the reset
  cases waited for the handler hold and the frames in one fixed order, and the
  HTTP/2 drain case counted DATA only after GOAWAY. The HTTP/3 drain case exposed
  the drain closure policy above. The HTTP/3 read-cancellation case is A1.
- Campaign `7661d6f`: the fuzz checker demanded a server reset on malformed
  streams the client had already reset. The HTTP/2 drain driver crashed when a
  WINDOW_UPDATE met the socket the server had closed after the drain, and wrote
  no report. Snapshot `0ff65b0` then hit the hyper-h2 closed-stream limitation
  described above.
- Pinned Linux, `4752c44`: the HTTP/2 drain case failed once, because the peer
  treated a failed automatic WINDOW_UPDATE (broken pipe after the drained close)
  as the end of the connection. Thirty repetitions with reply failures recorded
  instead all passed; 9 of the 30 saw the broken pipe and still read the trailer
  section.
- Two short incremental builds ran while another agent held the benchmark lock
  (a command chained the lock attempt with `;`). The overlap is recorded with
  the evidence.

### Validation

On rebased head `094fe8f`'s audit files (snapshot `0a0d212` over `90e84f1`), the
Windows full suite at the audit's first base reported 4,924 cases: 4,915 passed and 9
skipped (5 existing platform skips and the 4 explicit reproductions). Windows 11
build 26300, SDK 10.0.401, runtime 10.0.12. All 23 ordinary cases passed in the
pinned Linux container with `EMBEDIO_REQUIRE_QUIC=1`. Both production targets
build without warnings; the suppression and null-forgiving guards and whitespace
verification pass for the changed C# files.

### Limits

- Windows and Linux only. macOS, ARM64 and browsers were not run; the HTTP/1
  tunnel and TLS 1.2 limits of the [lifetime audit](http-capsule-lifetime.md) stay.
- System.Net.Quic is the only HTTP/3 provider exercised. Native MsQuic, native
  datagrams and WebTransport are not integrated and not tested here.
- The .NET Standard 2.0 asset and the Microsoft listener backend were not run.
- Campaigns are bounded (two seeds, scale 1 and 3, 20 drains). There is no soak run,
  no throughput measurement, and no WebSocket coverage (#190).
- HTTP/1 abandonment is not detected promptly while a handler waits without
  transport activity, as the response-section guide already records.
- Evidence is under ignored `TestResults/audit` in the audit worktree: campaign
  summaries, JSON reports, server logs, h2spec JUnit, TRX files, captured standards
  and the failed attempts listed above.

## Verification date and sources

The standards baseline was captured on **2026-10-09 between 14:36:12 and
14:36:24 UTC** from primary sources by
[`capture_standards.py`](../../test/EmbedIO.Conformance/standards/capture_standards.py).
Each response is stored under ignored `TestResults/http-conformance/standards-2026-10-09`
with its URL, retrieval time and SHA-256; `manifest.json` lists them.

- RFC Editor metadata for 45 RFCs: a seed list of core, transport, extension and
  content-coding RFCs, followed recursively through `updated_by` and `obsoleted_by`.
- The RFC Editor errata API (`errata.json`, 11,736,937 bytes, SHA-256
  `9fd44408...7e59ee`), filtered to the captured RFCs: 30 RFCs have errata.
- The full RFC index, swept for RFCs published from 2023 by the httpbis, quic,
  masque, tls, webtrans, httpapi and moq working groups, so a new specification
  cannot be missed because it was absent from the seed list.
- 14 IANA registries: HTTP parameters, fields, status codes, methods, cache
  directives, upgrade tokens, Alt-Svc parameters, priority, HTTP/2, HTTP/3, QUIC,
  MASQUE, TLS extension types and WebSocket. The registry name
  `http-structured-fields` does not exist at IANA (404); structured field types are
  recorded in the fields registry.

Registry last-updated dates at capture: HTTP fields 2026-08-28, methods 2026-06-17,
parameters 2025-10-02, status codes 2025-09-15, HTTP/2 2022-08-17, HTTP/3
2023-06-13, QUIC 2026-09-28, MASQUE 2026-08-28, TLS extensions 2026-10-08,
WebSocket 2026-10-05. QUERY is registered as safe and idempotent (RFC 10008).

### Update chains

| Specification | Current state at capture |
| --- | --- |
| RFC 9110 HTTP semantics | Internet Standard; no updates |
| RFC 9112 HTTP/1.1 | Internet Standard; updated by RFC 9931 (optimistic transitions) |
| RFC 9113 HTTP/2, RFC 7541 HPACK | Proposed Standard; no updates |
| RFC 9114 HTTP/3, RFC 9204 QPACK | Proposed Standard; no updates |
| RFC 9000/9001/9002 QUIC | Proposed Standard; no updates; RFC 9369 (v2) and RFC 9368 (version negotiation) are companions |
| RFC 8446 TLS 1.3 | Obsoleted by RFC 9846 (July 2026) |
| RFC 7301 ALPN | Updated by RFC 8447, itself updated by RFC 9847 |
| RFC 6455 WebSocket | Updated by RFCs 7936, 8307 and 8441 |
| RFC 7932 Brotli | Updated by RFC 9841 (shared Brotli) |
| RFC 8878 Zstandard | Updated by RFC 9659 (window sizing) |
| RFC 9298 CONNECT-UDP | Updated by RFC 9484 and RFC 9931 |

The working-group sweep found these RFCs not reached through the seed chains.
None is a requirement for a conforming origin server; the column records why.

| RFC | Title | Applicability to the engine |
| --- | --- | --- |
| 10036 | Incremental forwarding | Intermediary behavior; an origin server may ignore the field |
| 9875 | HTTP cache groups | Optional response field; application choice |
| 9849, 9848 | TLS Encrypted Client Hello | Platform TLS stack; not exposed by `SslStream`/MsQuic server options used here |
| 9851 | TLS 1.2 feature freeze | Informs policy; HTTP/2 currently accepts TLS 1.2 or later as RFC 9113 Section 9.2 requires |
| 9954, 10024, 10015, 9963 | Hybrid and deprecated key exchange, legacy code points | Platform cryptography |
| 9368 | QUIC compatible version negotiation | MsQuic |
| 9421, 9530, 9729, 9440 | Signatures, digests, concealed auth, client certificates | Application-level extensions |
| 9457, 9745, 9652, 9727 | Problem details, deprecation, link templates, API catalog | Application-level |

IANA also lists approved but unpublished work: QUIC multipath and reliable stream
reset (`RESET_STREAM_AT`), HTTP unencoded digest, and CONNECT-UDP bind. These are
not yet RFCs and are tracked, not required.

### Errata reviewed for applicability

Only verified errata and those held for document update change requirements;
reported errata are listed when they touch a tested behavior.

| Erratum | Status | Effect on tests |
| --- | --- | --- |
| RFC 9110 7306 | Verified (technical) | `Range` permits OWS after `=`; covered by `range-ows-erratum-7306` |
| RFC 9110 7138, 7419 | Verified (technical) | Accept and charset wording; no behavior tested here |
| RFC 9114 7780 | Verified (technical) | Any endpoint (not only a client) treats GOAWAY outside the control stream as H3_FRAME_UNEXPECTED; not yet exercised |
| RFC 9114 7014 | Verified (technical) | `:path` uses `absolute-path`; covered by the engine's own path tests |
| RFC 9204 7277 | Held (technical) | Static entries 73/74 casing; the engine keeps the published table, as interoperability requires |
| RFC 9113 9175, RFC 9114 9176 | Reported | OPTIONS `*` with a query; recorded only |
| RFC 10008 9013, 9016 | Reported | Example corrections; no behavior change |
| RFC 9000 7861, 6811, 8240 | Verified | QUIC transport; MsQuic's responsibility |

## Applicability matrix

Requirement families an origin server must meet, the campaign that exercises them,
and the result at the original evidence revision. "Engine" names the owner of any gap; nothing here changes
production code.

| Area | Requirement source | Exercised by | Result at the evidence commit |
| --- | --- | --- | --- |
| HTTP/1.1 message framing | RFC 9112 2, 5, 6, 7 | `h1` cases, `h1-fuzz` | Conforms except body framing (F1, F2) |
| Request target and Host | RFC 9112 3; RFC 9110 7 | `h1` cases | Conforms; unmatched Host closes silently (O1) |
| Persistence and pipelining | RFC 9112 9 | `h1` cases, `h1-fuzz` | Conforms |
| Methods | RFC 9110 9; RFC 10008 | `h1`, `h2`, `h3` cases | Conforms (case-sensitive; QUERY served) |
| Ranges and validators | RFC 9110 13, 14 | all protocols | Conforms, including erratum 7306 |
| Content coding | RFC 9110 12.5.3 | all protocols | gzip negotiation conforms |
| Expect: 100-continue | RFC 9110 10.1.1 | `h1`, `h2` cases | Conforms |
| HTTP/2 framing and streams | RFC 9113 4, 5, 6 | h2spec (146 cases), `h2` cases, `h2-fuzz` | Defects F3, F4; stream-state gaps F6 |
| HTTP/2 flow control | RFC 9113 6.5.3, 6.9 | `h2` cases, `h2-fuzz` | Window overrun after SETTINGS ACK (F5, intermittent) |
| HPACK | RFC 7541 | h2spec, hyper-h2 decoding every response | Conforms |
| HTTP/2 resource abuse | RFC 9113 10.5 | `h2` flood cases | CONTINUATION flood bounded; other floods unbounded by policy (O3) |
| HTTP/3 request streams and cancellation | RFC 9114 4 | `h3` cases, `h3-fuzz` | Cancellation can stop the listener (F3) |
| QPACK | RFC 9204 | aioquic/ls-qpack decoding every response | Conforms |
| HTTP/3 discovery | RFC 9114 3.1.1; RFC 7838 | Not yet tested | No Alt-Svc is emitted; tracked by #182 |
| WebSocket over any version | RFC 6455, 8441, 9220 | Out of scope (#190) | Not assessed here |
| TLS and QUIC transport | RFC 9846, 9000-9002 | Exercised implicitly by every TLS and QUIC case | Delegated to platform stacks |


## Campaigns

The tooling is described in
[`test/EmbedIO.Conformance/README.md`](../../test/EmbedIO.Conformance/README.md).
Independence comes from four sources: a raw-socket HTTP/1.1 client and model
written from RFC 9112, [h2spec](https://github.com/summerwind/h2spec) v2.6.0 (146
HTTP/2 and HPACK cases), hyper-h2 4.4.1 with hyperframe 6.1.0 and hpack 4.2.0, and
aioquic 1.3.0 with pylsqpack 0.3.24 for QUIC, HTTP/3 and QPACK. All clients decode
every response, so server HPACK and QPACK output is checked continuously.

Each run is tied to a `git archive` snapshot of one commit and executes in the pinned
container on a separate server process. Every phase (HTTP/1.1, h2spec, HTTP/2 cleartext
cases, HTTP/2 TLS cases, HTTP/2 fuzz, HTTP/3 fuzz, HTTP/3 cases) starts a fresh server,
so one finding cannot hide later results. After every case a fresh connection must be
served. Stateful campaigns read `/__stats` after a forced full collection: active
handlers must return to zero, handle growth must stay within 64 and managed growth
within 32 MiB.

## Evidence

Engine source: `src/` is byte-identical at #182 heads `c0f628d` and `770f3a4`.
Campaign sources: `fdcaa69` on `codex/http-conformance-fuzzing`. Built `EmbedIO.dll`
SHA-256 `abd083305545113d39b72948295be333493c8fc645bbef12d82e6b5367369d40` (net10.0,
reported version 1.0.3). Runner: SDK 10.0.401, runtime 10.0.12, Ubuntu 24.04.5,
MsQuic 2.6.2, kernel 6.18.33.2 (WSL2), 4 CPUs and 6 GB for the container. Seed
20261009, scale 1. Raw logs, JSON reports, stats and per-phase server logs are kept
under ignored `TestResults/http-conformance/runs/evidence-fdcaa69...`.

| Campaign | Result |
| --- | --- |
| HTTP/1.1 requirement cases | 57 cases: 39 conform, 14 permitted choices recorded, 4 violations (F1, F2 x3) |
| HTTP/1.1 stateful fuzz | Pass: 2,000 iterations, 5,029 valid requests, 473 invalid, 211 aborts, 72 known F2 outcomes; handles +2, managed +0.4 MB, handlers drained |
| h2spec, prior-knowledge h2c | 138 of 146 pass (F6 accounts for 7; one is the h2c fallback, P1 below) |
| h2spec, TLS with ALPN h2 | 139 of 146 pass (F6) |
| HTTP/2 cases, cleartext | 7 conform and `settings-shrink-ordering` shows F5; then `reset-while-responding` hit F4, which escalated to F3 and stopped the listener for the remaining 13 cases |
| HTTP/2 cases, TLS | 8 conform, then F4 escalated to F3 the same way for the remaining 13 cases |
| HTTP/2 stateful fuzz | Fails on F5 at iteration 57 (earlier runs: iterations 2 and 14) |
| HTTP/3 stateful fuzz | Fails at iteration 14: F3 (fatal `QuicException` 268 after a download cancellation) |
| HTTP/3 cases | 6 conform, then F3 in the cancel storm |
| NUnit reproductions on Linux (`2c5f385`) | All 9 fail as predicted, F1 x4, F2 x3, F3, F4 |
| NUnit reproductions on Windows | F1 and F2 fail as predicted; F3 (400 cancelled uploads) and F4 (500 resets) did not reproduce |

## Findings, in priority order

Confirmed defects reproduce on an exact commit through an independent client and
have a regression in
[`HttpConformanceFindingsTest`](../../test/EmbedIO.Tests/HttpConformanceFindingsTest.cs),
marked `Explicit` with the finding ID until the engine is corrected. None of these is
fixed in this change; corrections belong to the HTTP engine owner.

Status at engine base `c25fd05`: the engine branch now carries its own
`HttpConformanceFindingsTest`, in which the F1 to F4 reproductions run as ordinary
regression tests without `Explicit`, alongside further cases for malformed-body
isolation, lower-identifier GOAWAY and HTTP/3 upload cancellation. This integration
keeps that version of the file and does not carry the `Explicit` copy from this
audit. The campaign results above were measured on `fdcaa69` and `2c5f385` and have
not been rerun against the corrected engine.

### F3, priority 1: a per-stream output failure stops the whole listener

`WebServerBase.DoHandleContextAsync` treats exceptions escaping its `finally`
(response flush, completion and `MultiplexedContext.CloseAsync`) as fatal, and
`WebServer.OnFatalException` disposes the listener. Three per-stream conditions reach
that path: `IOException` "HTTP/2 output is no longer usable" (after F4),
`InvalidOperationException` "HTTP/3 response is no longer writable" (from
`Http3QuicExchange.SendHeadersAsync` and `WriteAsync` after an upload is cancelled),
and `QuicException` 268 (H3_REQUEST_CANCELLED) after a download is cancelled. One
client's ordinary cancellation therefore stops the endpoint for every client, and
`WebServer.RunAsync` then faults with `HttpListenerException` 995. Seen in four
separate evidence phases on Linux; not reproduced on Windows in 400 cancelled uploads.
A cancelled stream's write failure needs to stay a stream outcome.

### F4, priority 1: RST_STREAM during a response can close the HTTP/2 connection without GOAWAY

`Http2Exchange` writes DATA with a token linked to the stream reset, and
`Http2FrameTransport.WriteAsync` marks its output failed on any exception,
cancellation included. A reset that lands during a shared write therefore poisons the
connection. RFC 9113 Sections 5.4.2 and 6.4 make RST_STREAM a stream error. Minimized
case `reset-while-responding`: 3 of 300 connections lost on Linux; the NUnit
reproduction lost 1 of 500 on Linux and 0 of 500 on Windows. The mechanism comes from
reading the source and agrees with the logged `IOException`; it is not yet confirmed
by instrumentation. In the evidence run the resulting exception escalated to F3.

### F1, priority 2: a Content-Length body cut short by EOF is delivered as complete

RFC 9112 Section 8. With `Content-Length: 10`, three body bytes and a client EOF, the
application reads 3 bytes, sees end of stream and answers 200. `RequestStream`
returns 0 instead of failing when the transport ends with body bytes outstanding, on
the synchronous, `byte[]` and `Memory<byte>` paths. Deterministic on Windows and
Linux. A truncated upload can be processed as if it were whole.

### F5, priority 2: DATA after a SETTINGS ACK exceeds the adjusted stream window

RFC 9113 Sections 6.5.3 and 6.9.2. Intermittent. The raw-frame case
`settings-shrink-ordering` measured a stream window of -63,000 bytes after the
server acknowledged a smaller `SETTINGS_INITIAL_WINDOW_SIZE`; hyper-h2 rejected the
same condition with FLOW_CONTROL_ERROR in three fuzz runs. Other runs of both cases
passed (40 and 60 trials). The likely mechanism, DATA credited under the old window
being written after the ACK, is inferred from frame order and not yet traced in the
source. Strict clients close such connections.

### F2, priority 3: malformed chunked bodies become 500

RFC 9112 Sections 2.2 and 7.1 recommend 400. Invalid chunk sizes, missing CRLF and
leading whitespace are detected while the application reads, surface as
`InvalidDataException` or `EndOfStreamException`, and reach the default handler as
500. The default HTML page then includes the internal message (see O4).
Deterministic. The connection still closes, so framing stays safe.

### F6, priority 3: HTTP/2 stream-state checks not enforced

h2spec 5.1/8, 9, 11, 12, 5.1.1/2 and 5.3.1/1, 2 on both transports.
`Http2StreamRegistry.Receive` silently drops HEADERS whose stream ID is not greater
than the highest seen (RFC 9113 Section 5.1.1 requires PROTOCOL_ERROR; a client
reusing an ID waits forever), drops DATA and HEADERS on closed streams instead of
STREAM_CLOSED, and ignores self-dependency because RFC 7540 priorities are disabled.
Discarded DATA is still credited back (`Http2Dispatcher`), so there is no stall.

### Unresolved and observations

- **U2**: the HTTP/3 fuzz driver can trip aioquic's own `cannot call write() after
  reset()` assertion when it cancels a stream with queued data. Driver issue; it does
  not indicate an engine defect, and it limits HTTP/3 fuzz depth until fixed.
- **O1**: a Host matching no prefix is closed without any response. RFC 9110 suggests
  404 or 421.
- **O2**: ordinary client outcomes (404, 406, 416, QUERY 400 and client cancellation)
  are logged at Error level, which hides real faults such as F3.
- **O3**: no rate limit applies to PING, SETTINGS, empty DATA or open-and-reset
  bursts (20,000, 20,000, 50,000 and 5,000 frames all accepted). Handlers did not
  run for reset streams, and memory and handles settled. CONTINUATION floods are
  bounded with ENHANCE_YOUR_CALM. Whether to add limits is a policy decision.
- **O4**: the inherited default 500 page reports the exception type and message.
- **O5**: permitted but less specific statuses: 400 rather than 414 or 431 for
  oversized targets and headers, HTTP/1.2 rejected rather than served as 1.1,
  `OPTIONS *` 404, single-line `Content-Length: 3, 3` rejected while repeated equal
  lines are accepted, bare-LF lines rejected (approved strict policy).
- **P1**: on the cleartext port an invalid HTTP/2 preface falls back to HTTP/1 and
  receives an HTTP/1 400, which h2spec reports as a failure. This is the expected
  behavior of a shared HTTP/1.1 and h2c port; the TLS case passes.

## Fixture problems found and corrected

These were defects in the test tooling, not in the engine, and are recorded so
the earlier logs are read correctly: placeholder Host values matched no prefix and
were closed (this exposed O1); an any-verb route made a lowercase `get` look served;
an abort step disposed a client still reading; an invalid zero window increment and a
GOAWAY sent through h2's state machine in two HTTP/2 cases; `git archive` produced
CRLF scripts on Windows; the first F3 reproduction disposed its cancellation sources
and cancelled before the QUIC handshake; the host rethrew a stopped listener at
shutdown; drivers failed to write reports when the final statistics request found a
stopped listener. The earliest campaign runs predate those corrections.

## Coverage not available here

- macOS and ARM64: no local runner. Desktop CI covers macOS for the existing suites,
  not these campaigns.
- Windows for HTTP/2 and HTTP/3 campaigns: only the NUnit reproductions ran, and F3
  and F4 did not reproduce. The heavy campaigns run in Linux containers by design.
- The .NET Standard 2.0 asset, the Microsoft listener backend, WebSocket (#190),
  browsers, HTTP/3 discovery (no Alt-Svc emitted) and long soak runs (only scale 1)
  are not covered by these results.
- Throughput and latency are not measured here; the separate benchmark work owns them.

## Next steps

1. Engine owner: keep per-stream output failures out of the fatal path (F3) and
   isolate stream cancellation from shared writes (F4); then remove `Explicit` from
   the F3 and F4 regressions and confirm on Linux.
2. Engine owner: fail incomplete fixed-length bodies (F1) and map body framing errors
   to 400 (F2).
3. Trace F5's frame ordering in the HTTP/2 writer, then fix it.
4. Decide on F6 and O1 to O5 as conformance or policy items.
5. Fix U2, then raise campaign scale and add Windows and macOS CI runs once the
   priority 1 findings are corrected. A CI workflow change needs coordination first.

### HTTP/1 request-head limit status checkpoint

The request-limit candidate based on accepted chunk integration `e1add82` retains
its 32,768-byte head budget, returns 414 for oversized targets and 431 for field
sections exceeding it after the request line, and keeps malformed syntax at 400.
The response is empty, non-cacheable and closes the rejected pipeline without
application dispatch. Classification scans already bounded input only on failure;
it does not add an ordinary-request target copy. See the
[migration note](../compatibility/migration.md#http1-request-head-limit-statuses-unreleased).

The unchanged-engine duplex wire reproduction failed four status cases while all
four malformed controls passed. Eighteen new unit/wire cases cover fragmentation,
exact boundary overhead, reader reset, non-cacheable replies, pipeline rejection
and healthy subsequent clients. Final focused validation passes 128 cases. The
combined Windows suite passes 4,493: 4,488 passed, five expected skips, zero failures.
Both target builds, guards, formatting, allocation budgets and the installed
Framework smoke pass. This does not claim exact Framework 4.7.2 execution.

An independent Linux campaign archived `be84c44ea96db81f20ba567d17e1cf5059b0074d`
and loaded core SHA-256
`e30785b9e39d4a9284c323015eebb9eb83738c895dd72cc70afcd06957fcb1d3`.
On the pinned four-CPU/6-GiB container it reported 47 conforms, 10 policy choices,
no violations/errors, and passed seed 20261009 with 2,000 iterations, 5,029 valid
requests, 473 invalid requests and 211 aborts. Handlers drained; handles grew by
two, managed memory by 2,084,232 bytes. Self-mode client and server share the
process, so this is neither engine-only retained-memory attribution nor soak
coverage. Logs, archive and JSON are retained under
`TestResults/head-limit-conformance-linux` in the owned worktree.

The independent model now identifies oversized-target 414 as MUST; other statuses
fail instead of being labeled a policy choice. A missing response is an observation
error, not proof of conformance. The optional 431 case is labeled MAY. Method-length
policy and broader limits/resource behavior remain separate acceptance items.
