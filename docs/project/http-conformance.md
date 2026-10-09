# HTTP conformance audit and stateful campaigns

Independent conformance work for the [modern HTTP engine program](http-engine.md)
(#181, draft #182). The engine under test is the HTTP/1.1, HTTP/2 and HTTP/3
implementation on `codex/managed-http-engine`. Managed WebSocket hardening (#190)
is out of scope here and covered separately. This document records what was
verified, against which sources and source revision, and what remains open. It is
not a claim of complete protocol conformance.

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
and the current result. "Engine" names the owner of any gap; nothing here changes
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
