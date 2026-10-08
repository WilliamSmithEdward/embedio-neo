# Modern HTTP engine program

The owner authorized a replacement managed transport on 2026-10-08, with extreme
performance as a core requirement, incremental delivery, and HTTP support through
the October 2026 standards baseline. Strict rejection of malformed and ambiguous
framing is explicitly approved; valid application interfaces remain compatible.
This document is a development plan, not a claim of complete protocol support.

## Development completion criteria

On 2026-10-08 the owner explicitly requested completion of all development as an
active goal. A green first increment does not complete that goal. Track these
gates against the final source; future protocol claims require implemented and
independently tested behavior, not a codec or roadmap alone.

- [ ] Freeze the October 2026 RFC/errata/registry inventory and map mandatory core
  requirements and applicable extensions to implementations and tests. Record
  optional application extensions and platform restrictions explicitly.
- [ ] Complete HTTP/1 request/response semantics, framing, streaming, upgrades,
  bounded resource policies and compatibility/migration coverage.
- [ ] Implement HTTP/2 negotiation, HPACK, frame/state validation, multiplexed
  request lifecycles, flow control, cancellation and graceful shutdown.
- [ ] Implement HTTP/3 negotiation/discovery, QUIC integration, QPACK, control
  streams, stream lifecycle/flow control and graceful shutdown on supported hosts.
- [ ] Complete the applicable modern extension work, including WebSocket extended
  CONNECT, priorities, trailers, content-coding negotiation and datagram/capsule
  support, with explicit capability and configuration documentation.
- [ ] Pass independent clients, protocol conformance suites, deterministic
  fragmented/malformed-input tests, fuzzing and resource-abuse/lifecycle tests.
- [ ] Profile and optimize representative ordinary/pipelined/multiplexed, JSON,
  upload, TLS, static-file, WebSocket, slow-peer and mixed workloads. Publish
  reproducible baseline and established-engine comparisons, error counts,
  throughput/latency, CPU, allocations and retained-memory results. Investigate
  material regressions rather than treating isolated allocation wins as success.
- [ ] Validate retained APIs, target assets and platform applications; finish
  migration/support documentation and green checks on every final PR head.

Release publication, HTTP Arena submission and the contributor reply are outside
this development goal unless separately authorized.

## Protocol support and milestones

| Area | Required scope | Current replacement status |
| --- | --- | --- |
| HTTP semantics | RFC 9110; methods, status, authority, informational responses, headers and trailers | Existing application layer; full conformance audit pending |
| HTTP/1.1 | RFC 9112; incremental parsing, fixed/chunked bodies, pipelines, persistence, bounded input and errors | First implementation under test |
| HTTP/2 | RFC 9113; TLS/ALPN, prior knowledge, HPACK (RFC 7541), multiplexed streams, flow control, SETTINGS, GOAWAY and reset | Implemented incrementally; listener integration under validation |
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

## First-increment measurements (2026-10-08)

Baseline core: `2e8e98c`; candidate core: `561dfb4`. The same benchmark runner
exercised both assemblies in independent processes on Windows 10.0.26300,
.NET 10.0.11, AMD Ryzen 7 9800X3D (8 cores/16 logical processors). Server affinity
was logical CPUs 0-3 and client affinity 4-7. Each sample had five seconds of
warmup, 15 seconds of measurement, 16 concurrent connections and 80 plaintext GETs
per connection. Payloads and terminal EOF were validated; all eight samples
completed without errors. This developer machine was not an isolated benchmark
host; external background activity and closed-loop sampling limit conclusions.

| Pipeline | Round | Core | Requests/s | Server B/request | CPU microseconds/request | Sampled p99 ms |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | before | 180,283 | 10,841 | 20.74 | 0.2662 |
| 1 | 1 | after | 179,875 | 9,063 | 20.93 | 0.2446 |
| 1 | 2 | before | 183,395 | 10,841 | 20.73 | 0.2314 |
| 1 | 2 | after | 169,562 | 9,063 | 21.12 | 0.3185 |
| 16 | 1 | before | 224,232 | 10,932 | 16.96 | 2.4056 |
| 16 | 1 | after | 229,321 | 8,494 | 16.96 | 2.4846 |
| 16 | 2 | before | 227,056 | 10,931 | 17.00 | 2.3493 |
| 16 | 2 | after | 224,527 | 8,495 | 16.87 | 2.5102 |

Allocation reductions repeat across both pairs: approximately 16% for ordinary
requests and 22% for pipeline16. Ordinary throughput includes a slower candidate
run; pipelined throughput is mixed. These measurements do not establish a general
throughput improvement or attainment of the program's extreme-performance goal.
Latency samples start at batch send and exclude connection establishment. CPU
figures cover the server process only; detailed GC counts remain in raw output.

Earlier attempts failed from client socket exhaustion and are excluded. The
client was corrected to await server EOF after the explicit final close. A valid
intermediate run then exposed excess 8 KB request-buffer allocation; sizing
promoted storage to actual input produced the results above. No OS network limit
was changed and no failed workload was retried inside a measured sample.

The separate parser microbenchmark reduced pipeline64 allocation from
10,868.88 to 3,368 B/request. Header serialization allocation for 1 KB and 16 KB
values fell from 7,904/98,584 to 1,168/16,528 bytes. Those narrower results exclude
sockets and application work. The initial direct-header serializer had a CPU
regression; a bounded stack buffer improved it, with some workload tradeoffs
remaining. See the [benchmark procedure](../../test/EmbedIO.Performance/README.md#separate-process-engine-comparison).

Raw local experiments are retained under ignored `TestResults/http-engine`;
`separate-process-sized` contains the eight samples above, including assembly
hashes. These are development results, not HTTP Arena submissions.

## References

- [HTTP semantics](https://www.rfc-editor.org/rfc/rfc9110.html)
- [HTTP/1.1](https://www.rfc-editor.org/rfc/rfc9112.html)
- [HTTP/2](https://www.rfc-editor.org/rfc/rfc9113.html)
- [HTTP/3](https://www.rfc-editor.org/rfc/rfc9114.html)
- [QPACK](https://www.rfc-editor.org/rfc/rfc9204.html)
- [HTTP/3 WebSockets](https://www.rfc-editor.org/rfc/rfc9220.html)
- [HTTP datagrams](https://www.rfc-editor.org/rfc/rfc9297.html)
- [.NET QUIC platform prerequisites](https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview)

## HTTP/2 compression development checkpoint

The bounded prefix-integer and Huffman codecs and HPACK decoder are implemented
in isolation. Forty-nine focused cases pass against the modern and netstandard2.0
assets on .NET 10.0.11. They cover published request sequences, all Huffman octets,
malformed padding/EOS, dynamic eviction, acknowledged table-size reductions,
never-indexed flags, truncation and decoded-header limits. Normative wire tables
are attributed in source and the distributed LICENSE.

This is not HTTP/2 listener support. The encoder, broader independent compression
vectors, frame/stream state machines, negotiation and actual client integration
remain to be implemented and tested. New source requires fresh full/remote checks;
the first increment's green result does not certify these changes.


The encoder now supports dynamic indexing and Huffman selection, retains the
smallest pending table-size reduction, and never indexes credentials or cookies.
Fifty-eight focused compression cases pass. A separate deterministic probe checks
1,200 independently generated blocks in each direction against Python hpack 4.1.0,
including table changes, arbitrary octets and sensitive headers. The dependency
is test-only, wheel-hash pinned in `test/EmbedIO.Fuzz/hpack-requirements.txt`; Linux
CI runs the probe without adding any production dependency. The initial probe
build failed from missing namespace imports; the corrected run passed. This
evidence covers compression interoperability, not HTTP/2 transport or conformance.

To reproduce, create an isolated Python virtual environment and install that
requirements file with `pip --isolated install --require-hashes --only-binary=:all:`.
Run `hpack_interop.py generate unused <input.jsonl>`, the .NET fuzz project with
`--hpack-corpus <input.jsonl> <output.jsonl>`, then
`hpack_interop.py verify <input.jsonl> <output.jsonl>`. Both files retain all blocks
in connection order, and any mismatch fails the process. Logs and corpora belong
under ignored TestResults. HTTP/2 frame/stream integration is the next increment.

The asynchronous frame transport now enforces negotiated receive bounds before
allocating payload storage, preserves unknown extension frames, and serializes
write batches. Thirty focused frame tests cover fragmentation, cancellation,
malformed shapes, stream/connection error scope and concurrent output. Eighty-eight
compression/framing cases pass in total. Connection preface, SETTINGS handling,
continuation assembly, stream state/flow control and application dispatch are still
pending; these isolated components do not yet provide HTTP/2 service.

Server connection startup now exchanges the preface and initial SETTINGS over a
real TCP stream, acknowledges settings/PING and records GOAWAY. Fourteen focused
cases pass, including fragmented startup, invalid initial frames, settings bounds,
ordered window adjustments and capability withdrawal. The preceding framing
checkpoint passed the full Windows 2,122-case suite (2,117 successes/five skips).
Header-block assembly, stream state/flow control and application dispatch remain
in development; no HTTP/2 endpoint is advertised yet.


Header assembly and outbound credit checkpoint (2026-10-08): all incoming frames
now pass an uninterrupted CONTINUATION guard. Header blocks have encoded byte and
fragment-count bounds, strip priority/padding metadata, and preserve connection
compression state even when a self-dependent stream must be rejected. Fourteen
new cases exercise fragmentation, ordering, resource bounds and malformed HPACK.

The outbound flow coordinator reserves connection and stream credit atomically,
permits negative stream windows after SETTINGS reductions, checks overflow with
connection/stream error scope, and releases blocked writers on cancellation or
closure. Eleven new cases include simultaneous reservations across 64 streams.
Incoming SETTINGS now update this coordinator; live DATA dispatch is still pending.
Wakeup signals are allocated only when a writer actually blocks. No throughput or
allocation improvement is claimed for these components before end-to-end measurement.

All 127 focused HTTP/2 cases pass with both the modern library and the actual
netstandard2.0 library loaded on .NET 10.0.11. This does not establish compatibility
with a legacy CLR. Both library targets build. Stream state, inbound flow control,
request semantics and application dispatch remain necessary before enabling HTTP/2;
HTTP/3 and remaining program acceptance criteria remain open.

The initial header/flow full Windows run passed 2,156 cases with five expected
skips (2,161 total). Final review added explicit header-state disposal and cleanup
of failed connection startup, and strengthened real TCP SETTINGS-to-credit checks.
All 127 focused cases pass again on both assemblies after those changes; a fresh
full run and remote final-source checks remain required before merge.

The final header/flow checkpoint (`7e9182f`) also passed its full Windows rerun:
2,156 successes and five expected skips. The next increment adds consumption-driven
inbound credit, batched updates, padded-DATA accounting and reset recovery, plus
bounded client-stream tracking. Half-closed streams count toward concurrency;
closed streams retain no per-stream objects. Already closed streams use the minimal
processing/discard policy permitted by RFC 9113 section 5.1: the future dispatcher
must still decode their headers and count their DATA against connection credit.

Nine receive-flow and eleven lifecycle cases bring focused HTTP/2 coverage to 147
passing cases on both assemblies (.NET 10.0.11 runtime). Both library targets build.
An initial fixture build used an obsolete NUnit delegate type; correcting it to
Action resolved the compile error without changing assertions. The registry and
receive coordinator are owned by connection cleanup but are not yet wired into a
body-delivery/application loop. No end-to-end HTTP/2 performance or conformance
claim is made. Next work is request semantics/body delivery and stream dispatch.

Request admission now validates decoded headers before registering an application
stream: pseudo-header order/uniqueness, required request metadata, CONNECT and
negotiated extended CONNECT rules, connection-specific fields, authority/Host
normalization, content lengths and trailer framing. Split Cookie fields are joined
with semicolon-space for the application contract. Extended CONNECT remains
unadvertised and disabled in stream admission until its transport integration exists.

The new asynchronous request-body stream enforces declared length and a 65,535-byte
unread-content bound, coalesces small DATA fragments into cleared pooled chunks,
returns consumption credit through its owner callback, and handles cancellation,
reset and disposal without retaining payload buffers. One stress case delivers
65,535 one-byte slices of a 16-KiB producer buffer while retaining only sixteen
4-KiB chunks; this establishes a bounded-storage invariant, not a throughput result.
Forty-six semantic cases and eight body cases bring focused HTTP/2 tests to 201,
passing with both library assemblies on .NET 10.0.11. Both targets build. The body
stream still needs connection frame dispatch and application-context integration;
HTTP/2 remains unadvertised. Full final-source/remote validation remains required.

The internal connection dispatcher now joins request admission, body delivery,
stream resets, batched receive-credit updates and concurrent application callbacks.
Response headers are compressed in serialized wire order, including SETTINGS table
changes; DATA writes reserve both stream and connection credit. Output failures
terminate dispatch so an ambiguous write cannot leave HPACK state in use.

Eight real TCP interoperability cases pass against the independent .NET HttpClient
HTTP/2 implementation with exact version selection: empty and 1/65,535/65,536/262,144
byte echoes, 24 concurrent uploads/responses on one accepted connection, cancellation
of a blocked stream followed by healthy requests, and large response header blocks.
The initial run had one fixture teardown failure (client disposal raced server read,
Windows socket error 10053); canceling/joining the server before client disposal
corrected the fixture. Original failure evidence is retained. All 209 focused cases
pass with both assemblies on .NET 10.0.11; both targets build. The previous request
checkpoint passed the full Windows suite: 2,230 successes/five skips, 2,235 total.

This internal service is still separate from the public EmbedIO listener. Next work
includes raw-wire error/reset/flow-control conformance, full response semantics,
the IHttpContext adapter, protocol negotiation and platform validation. The new
exchange helper is not a completed public response implementation. Performance
has not yet been measured end-to-end for HTTP/2, and no HTTP/2 capability is advertised.

Direct-wire validation now covers zero-window PING progress and resumption, short/
long request bodies, padding excluded from Content-Length, malformed header names,
stream-scoped invalid WINDOW_UPDATE and connection-level GOAWAY. A deterministic
callback test reproduced cancellation being published before stream reset state;
release now marks the stream reset before invoking cancellation callbacks.

Outbound response validation runs before HPACK mutation, rejects invalid status/
field framing, handles informational responses, suppresses DATA for HEAD/no-content
semantics, and validates declared lengths. Completion can finish a response whose
headers were already sent. Four additional real-client cases cover HEAD metadata,
103 Early Hints, 204 and streamed completion. Invalid application response data uses
InvalidDataException; the initial unit assertions incorrectly expected IOException,
and were corrected to the exact exception after standardizing shared validation.

All 242 focused cases pass with both assemblies on .NET 10.0.11; both targets build.
The prior dispatcher commit2458daa passed the full Windows suite:2,238 successes,
five skips,2,243 total. Public context/response adaptation (including automatic Date,
cookies, encoding and callback semantics), negotiation, broader conformance and
HTTP/2 performance measurement are still pending. The goal remains incomplete.

HTTP/2 now has internal implementations of the existing IHttpRequest,
IHttpResponse and IHttpContextImpl interfaces. Real client tests exercise the
public string-response helper, query/cookie parsing, charset metadata, independent
Set-Cookie fields, automatic Date/custom Server, HEAD/no-content writes, close
callbacks and concurrent write/close completion. Shared cookie serialization is
retained. Response shutdown disposes its synchronization only after queued
operations finish, and repeated closes await the same completion.

An unknown-length upload test reproduced a metadata race: application dispatch
could observe remote END_STREAM before constructing the request, incorrectly
clearing HasEntityBody. That property now uses the initial header framing state.
KeepAlive=false maps to GOAWAY and graceful draining; existing uploads finish and
new streams above the advertised last stream receive REFUSED_STREAM. Two wire
cases verify this behavior. Nine new cases bring focused coverage to 251 passes
on both assemblies under .NET10.0.11. Both targets build; initial missing diagnostics
import/unsupported netstandard HttpVersion constant errors were corrected.

The adapters have not yet been connected to EndPointListener/HttpListener routing
or TLS negotiation. HTTP/2 extended CONNECT/WebSocket adaptation remains explicitly
unadvertised until implemented. Those integration steps, platform validation and
performance measurements remain part of the active program.

### Managed listener integration

The managed listener now routes HTTP/2 streams through its existing public context
queue and normal WebServer module pipeline. Cleartext prior knowledge works on
both library targets. The net10.0 build also negotiates h2 through TLS ALPN,
retaining HTTP/1.1 fallback. The netstandard2.0 build retains its existing TLS
handshake API and does not advertise TLS HTTP/2. The Microsoft listener is unchanged.

Connections remain owned by the endpoint and each routed listener so stop/disposal
closes their transport and cancels active stream contexts. Prefix detection replays
already-read bytes into HTTP/2 and retains ordinary POST/PATCH parsing on mismatch.
Real WebServer tests pass twelve concurrent uploads larger than the initial
flow-control window over both cleartext and HTTPS, then verify HTTP/1.1 fallback.
A shutdown test verifies cancellation reaches an application awaiting its context
token. The 301-case HTTP/2, HTTPS and listener lifecycle selection passes locally.

This supersedes the earlier unconnected-adapter status. Extended CONNECT/WebSockets,
additional conformance/resource limits, cross-platform negotiation validation,
HTTP/2 performance measurements and HTTP/3 remain outstanding program work.

The complete Windows suite for this listener increment passed: 2,283 successes,
five expected platform skips, 2,288 total. Both library targets build. This is local
validation; exact-head remote checks and other platforms remain required.

