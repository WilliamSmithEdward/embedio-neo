# Modern HTTP engine program

The owner authorized a replacement managed transport on 2026-10-08, with extreme
performance as a core requirement, incremental delivery, and HTTP support through
the October 2026 standards baseline. Strict rejection of malformed and ambiguous
framing is explicitly approved; valid application interfaces remain compatible.
This document is a development plan, not a claim of complete protocol support.

## Protocol support and milestones

| Area | Required scope | Current replacement status |
| --- | --- | --- |
| HTTP semantics | RFC 9110; methods, status, authority, informational responses, headers and trailers | Existing application layer; full conformance audit pending |
| HTTP/1.1 | RFC 9112; incremental parsing, fixed/chunked bodies, pipelines, persistence, bounded input and errors | First implementation under test |
| HTTP/2 | RFC 9113; TLS/ALPN, prior knowledge, HPACK (RFC 7541), multiplexed streams, flow control, SETTINGS, GOAWAY and reset | Planned; not implemented |
| HTTP/3 | RFC 9114; QUIC, TLS 1.3, QPACK (RFC 9204), control streams, request cancellation and graceful drain | Planned; not implemented |
| WebSockets | Existing RFC 6455 plus extended CONNECT over HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220) | HTTP/1.1 existing; multiplexed transports planned |
| Extensions | Priorities (RFC 9218), HTTP datagrams/capsules (RFC 9297), discovery/Alt-Svc, current registered extensions and errata | Inventory and applicability review pending |
| Security/resource control | Framing ambiguity, input limits, slow readers/writers, cancellation, compression expansion and multiplexed-stream abuse | First framing work under test; broad abuse tests pending |

The standards inventory must be checked against the RFC Editor and IANA through
2026-10-08, including updates and errata, before claiming the requested coverage.
An optional extension or draft is tracked explicitly rather than silently treated
as mandatory, implemented, or unsupported. HTTP/2 and HTTP/3 cannot be advertised
until real independent clients and conformance tests pass.

TLS stays with platform cryptography. QUIC uses a maintained transport such as
System.Net.Quic/MsQuic; its native prerequisites and platform capability must be
detected and documented. The retained .NET Standard 2.0 and .NET 10 assets cannot
be assumed to expose identical protocol capabilities. Target removal, hidden
fallback, and a new native dependency in the legacy asset are not implied by this
plan. The Microsoft listener remains available.

## Standards inventory checkpoints

The October 2026 target includes more than protocol version negotiation. The
[IANA HTTP parameter registry](https://www.iana.org/assignments/http-parameters/)
currently lists Brotli, Zstandard, and dictionary-compressed Brotli/Zstandard
(RFC 9842) alongside existing gzip/deflate. These remain explicit content-coding
work items; the first transport increment does not implement them. Review the
HTTP field, HTTP/2, HTTP/3 and QUIC registries, relevant RFC updates and verified
errata before freezing the full conformance matrix. Record optional application
extensions separately from requirements of a conforming core server.
## Architecture and performance acceptance

Separate transport ownership, protocol framing, request dispatch and application
semantics. A connection owns input until it explicitly hands it to a body stream;
only a completed body can release bytes to the successor. HTTP/2 and HTTP/3 will
need per-stream lifecycles rather than extending HTTP/1.1's serial response model.

Eliminate redundant pipeline copies and temporary parser representations. Prefer
true asynchronous I/O and direct writes, with synchronous completion paths where
safe. Pooling requires proven ownership and protection against retained references;
do not add pooling merely to improve an allocation counter. Connection scheduling,
backpressure and response batching need independent measurements.

Performance experiments use separate load/client and server processes, a pinned
source/runtime, fixed CPU allocation, warmup and repeated sustained runs. Report
all errors, throughput, p50/p95/p99, server allocation, CPU, GC and retained memory.
Compare ordinary and pipelined requests, uploads, JSON, TLS, static files, WebSockets,
connection churn, slow peers and mixed workloads. Compare to established engines
on the same machine; local short loopback timings are not leaderboard results.
No numeric speedup is promised before reproducible measurements.

Every increment requires focused behavioral tests, both production targets,
cross-platform regression and independent wire validation. The final replacement
also needs deterministic fragmented-input tests, fuzzing, resource-exhaustion tests,
interoperability and platform app validation. Keep failing evidence. Do not weaken
checks or cache dynamic application work to improve benchmark results.

## First increment: compatibility changes under validation

The managed path gains asynchronous fixed-length input and chunked decoding,
including extensions, terminal trailers and successor-byte retention. Chunked
requests report an unknown Content-Length (-1) and HasEntityBody true. Trailers are
validated and consumed, not merged into routing or authentication headers.

Conflicting lengths, Content-Length with Transfer-Encoding, repeated transfer
encoding, unsupported transfer codings, duplicate Host, malformed field names,
control characters and non-CRLF line endings are rejected. Equal decimal repeated
Content-Length remains accepted. HTTP/1 parsing accepts versions 1.0 and 1.1 only;
future binary protocols require their own negotiation and framing paths.

Clients relying on permissive parsing must send unambiguous valid HTTP messages.
Request headers have a cumulative 32768-byte limit across fragmentation. Chunk
metadata lines are limited to 8192 bytes and trailers to 32768 bytes. Truncated or
invalid chunk bodies cannot release a connection for another request.

Public submission, a release, and the contributor reply remain separate from this
development program. No HTTP Arena acceptance or competitive ranking is claimed.

## References

- [HTTP semantics](https://www.rfc-editor.org/rfc/rfc9110.html)
- [HTTP/1.1](https://www.rfc-editor.org/rfc/rfc9112.html)
- [HTTP/2](https://www.rfc-editor.org/rfc/rfc9113.html)
- [HTTP/3](https://www.rfc-editor.org/rfc/rfc9114.html)
- [QPACK](https://www.rfc-editor.org/rfc/rfc9204.html)
- [HTTP/3 WebSockets](https://www.rfc-editor.org/rfc/rfc9220.html)
- [HTTP datagrams](https://www.rfc-editor.org/rfc/rfc9297.html)
- [.NET QUIC platform prerequisites](https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview)
