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
| HTTP/2 framing and streams | RFC 9113 4, 5, 6 | h2spec (146 cases), `h2` cases, `h2-fuzz` | Gaps F4, F5, F6 |
| HTTP/2 flow control | RFC 9113 6.9 | `h2` cases, `h2-fuzz` | Conforms in targeted cases; one unexplained fuzz failure (U1) |
| HPACK | RFC 7541 | h2spec, hyper-h2 decoding every response | Conforms |
| HTTP/2 resource abuse | RFC 9113 10.5 | `h2` flood cases | CONTINUATION flood bounded; other floods unbounded by policy (O3) |
| HTTP/3 request streams and cancellation | RFC 9114 4 | `h3` cases, `h3-fuzz` | Listener stop on cancellation (F3) |
| QPACK | RFC 9204 | aioquic/ls-qpack decoding every response | Conforms |
| HTTP/3 discovery | RFC 9114 3.1.1; RFC 7838 | Not yet tested | No Alt-Svc is emitted; tracked by #182 |
| WebSocket over any version | RFC 6455, 8441, 9220 | Out of scope (#190) | Not assessed here |
| TLS and QUIC transport | RFC 9846, 9000-9002 | Exercised implicitly by every TLS and QUIC case | Delegated to platform stacks |

<!-- Results, findings and limits follow. -->
