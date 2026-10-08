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
- [ ] Complete the managed WebSocket hardening and performance work in [#190](https://github.com/WilliamSmithEdward/embedio-neo/issues/190), a required sub-issue of this program.
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
| HTTP/3 | RFC 9114; QUIC, TLS 1.3, QPACK (RFC 9204), control streams, request cancellation and graceful drain | Internal QUIC connection dispatch passes initial HTTP-client/wire tests; public listener integration and lifecycle completion pending |
| WebSockets | Existing RFC 6455 plus extended CONNECT over HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220) | HTTP/1.1 existing; HTTP/2 initial integration tested locally; HTTP/3 planned |
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
### Context handoff cancellation

A deterministic regression reproduced an ObjectDisposedException when the server
assigned its cancellation token after an HTTP/2 context had closed. A peer reset
can produce this ordering between dequeue and application dispatch. Late token
assignment now keeps the request canceled without throwing into the server's
accept loop or creating another linked token source. The test also verifies that
assigning an uncancelable token cannot revive the closed request.

A real WebServer test resets an active HTTP/2 request, observes its application
cancellation and completes a subsequent healthy request. All 256 HTTP/2 cases
pass with both the modern and actual netstandard2.0 assemblies on .NET 10.0.11.
The first standard-assembly run exposed a fixture still hardcoding HTTP/2 for TLS;
the corrected fixture verifies HTTP/1.1 TLS on that target and HTTP/2 TLS on the
modern target. Both exercise cleartext HTTP/2. These results do not establish
legacy CLR or other-platform runtime coverage.

The full Windows suite passed with this production change: 2,285 successes and
five expected skips (2,290 total). A subsequently strengthened assertion verifies
that the healthy request after reset uses the same TCP client port; all 256
HTTP/2 cases passed again with both assemblies. Remote exact-head checks remain
required. The complete development goal remains active.
### HTTP/2 WebSockets and managed-engine work

The owner added managed WebSocket hardening and aggressive performance work to
this active workflow, tracked in [#190](https://github.com/WilliamSmithEdward/embedio-neo/issues/190)
as a sub-issue of #181. This includes independent conformance/fuzz coverage,
lifecycle/resource checks and reproducible comparative measurements. It is a
completion requirement, not a deferred optional audit. No release is authorized.

The HTTP/2 connection now advertises SETTINGS_ENABLE_CONNECT_PROTOCOL and admits
extended CONNECT. The context accepts the websocket protocol with version 13,
sends status 200 without the HTTP/1 key/accept exchange, and preserves response
headers/cookies through the existing serializer. The existing managed WebSocket
engine operates over a duplex HTTP/2 stream; it does not own the TCP connection.
An initial BCL stream-factory approach was unavailable in the netstandard2.0 API
and was replaced without adding runtime dependencies.

Six independent .NET ClientWebSocket cases cover empty/text/binary messages,
256-KiB messages, fragmentation, orderly close and client abort. HTTP requests
during and after the WebSocket use the same client TCP port. These tests exposed
a separate adapter defect: disposing a helper's response output made the server's
final FlushAsync throw, triggering listener shutdown. Final flush is now harmless
once the response is closed; subsequent writes remain invalid. A focused regression
covers both synchronous and asynchronous final flush.

All 263 HTTP/2 tests pass with both actual library assemblies on .NET 10.0.11.
The newly advertised setting is verified by both startup-fragmentation cases.
Broader WebSocket wire validation, reset-code/resource behavior, TLS/platform
coverage, independent conformance and performance validation remain required.

The complete Windows suite passed: 2,292 successes, five expected platform skips,
2,297 total. Existing hot-path allocation checks also passed. This is not yet a
WebSocket throughput/latency comparison or completion of #190. Exact-head remote
checks and broader platform/conformance validation remain required.

### Managed WebSocket early frame validation (#190)

Sixteen header-only tests exercise rejection without supplying a masking key or
payload. Thirteen failed on the preceding source by attempting an additional
read; three existing opcode/control checks already passed. The parser now
validates masking, reserved bits and fragmentation state immediately after the
base header, then checks minimum length encoding, the reserved high length bit
and the byte-array/int representation bound before reading the masking key.
Invalid wire metadata uses 1002; unrepresentable lengths use 1009.

Eight valid masked-frame cases cover empty frames, 125/126/65535/65536-byte
boundaries, continuation and interleaved ping. All 24 new cases and six HTTP/2
WebSocket cases passed locally. The 287-case HTTP/2 plus new framing selection
also passed with the actual netstandard2.0 assembly on .NET 10.0.11. Both targets
build. This is an initial parser hardening increment, not completion of #190's
UTF-8/close checks, resource policies, fuzzing or performance work.

The full Windows suite passed with this frame-parser change: 2,316 successes,
five expected skips, 2,321 total. Existing hot-path allocation checks passed.
No throughput or latency improvement is claimed by this correctness increment.

### Managed close-frame validation (#190)

Seventeen malformed close payload cases were accepted before correction: a
one-byte status, forbidden/out-of-range codes and invalid UTF-8 reasons. The
parser now rejects one-byte close lengths at the base header, checks unmasked
status codes against RFC 6455 and the IANA registry snapshot (2026-10-08), and
validates reasons without allocating a decoded string. Protocol violations use
1002 and malformed UTF-8 uses 1007. Seventeen valid-code/Unicode cases retain
1012-1014 and application/private-use ranges; the existing empty-close case passes.

Five real HTTP/1.1 wire cases verify emitted status codes and healthy subsequent
requests. The first one-byte fixture supplied unread trailing bytes and saw a
Windows TCP reset; its corrected header-only form isolates early rejection
without assuming TCP preserves a close reply with unread input. All 326 managed
WebSocket/HTTP2 cases pass with both library assemblies on .NET 10.0.11. Public
outgoing-close argument handling and application text callback policy are not
changed by this increment. The broader #190 audit remains open.

The full Windows suite passed: 2,355 successes, five expected skips, 2,360 total.
Existing allocation checks passed. Main has since added analyzer enforcement and
CI changes (tracked origin/main 83d0b96); reconciliation and fresh exact-head
remote validation are required before this program can be considered complete.


### Mainline analyzer and API integration

Reconciled the engine branch with main `83d0b96`, including the separately
owner-approved `RawTarget` API migration, context/network banning, CI structure
and enforced analyzer/no-suppression rules. Borrowed streams and cancellation
sources now express their existing ownership explicitly; the HTTP/2 frame
transport disposes its write gate after connection I/O has joined. Recoverable
exception filters preserve cleanup without swallowing fatal runtime failures.
HTTP/2 response names use ASCII token validation and lowercase wire conversion,
avoiding Unicode case folding. Test reflection helpers use explicit null checks;
void-returning validation methods retain nullable results.

The integrated solution and standalone performance harness build with zero
warnings/errors on both library targets. Formatting, lexical and semantic
suppression guards pass. All 364 focused HTTP/1/HTTP/2/managed-WebSocket cases pass
on both the modern and actual netstandard2.0 assemblies (host .NET 10.0.11).
The full Windows suite passes with 2,469 successes and five expected skips,
2,474 total; CI's discovery floor is updated accordingly. Independent hpack 4.1.0
interoperability passes 1,200 blocks in each direction. Existing hot-path, queue
and cold-start allocation gates pass. No new throughput improvement is claimed.

The first focused integration run exposed a test-helper assertion on a legitimate
null reflection result from void trailer validation; the corrected run passes.
Local logs retain that failure. Current-source cross-platform and remote checks
remain outstanding; earlier green checks on `41ff73e` do not validate this source.
HTTP/3, broader conformance/resource policies and the complete #190 performance
and hardening scope remain development requirements.


### HTTP/3 wire primitives

The initial HTTP/3 framing layer implements the full 62-bit integer range from
[RFC 9000 section 16](https://www.rfc-editor.org/rfc/rfc9000.html#section-16),
accepting valid non-minimal encodings and emitting minimal encodings. A single
reader per stream separates frame headers from payload consumption. DATA can be
read into caller buffers and unknown frames discarded using a bounded pooled
buffer; the declared payload length is never an automatic allocation request.
Metadata buffering takes an explicit limit and rejects excess before allocation.

The framing layer distinguishes clean end at a frame boundary from truncation
within a header or payload (H3_FRAME_ERROR), following
[RFC 9114 section 7.1](https://www.rfc-editor.org/rfc/rfc9114.html#section-7.1).
It retains caller transport ownership, rejects concurrent reads, and invalidates
input after a failed/canceled in-flight read. Cancellation before entry consumes
no input. It does not yet validate frame placement, SETTINGS, QPACK instructions
or request semantics; connection/state integration will provide those checks.

All 33 wire cases pass on both library assemblies under .NET 10.0.11, including
published vectors, range boundaries, fragmentation, non-minimal encodings,
truncation, constant-storage skipping, oversize metadata and cancellation.
The solution builds with no warnings/errors; both suppression guards pass.
CI's test discovery floor becomes 2,507. This is an internal foundation, not an
HTTP/3 endpoint or QUIC interoperability claim.


### HTTP/3 peer settings and control-stream parsing

Immutable peer settings now parse QPACK capacity/blocked-stream limits, maximum
field-section size, extended CONNECT and datagram flags. Unknown identifiers are
ignored semantically but participate in duplicate and entry-budget checks.
Forbidden HTTP/2 settings, duplicate identifiers, invalid boolean values and
truncated pairs receive explicit connection error codes. Full-width peer limits
are retained without allocating tables or enabling an extension merely because
the peer advertised it.

The control reader enforces SETTINGS first and once, critical-stream closure,
forbidden frame types/directions, exact identifier payload lengths, decreasing
GOAWAY bounds and increasing MAX_PUSH_ID bounds. Unknown frames are streamed
past. It emits cancellation identifiers for the future connection push registry;
promise existence and stream uniqueness are not yet validated by this layer.
QUIC connection integration, QPACK state and 0-RTT settings handling remain open.
The behavior follows RFC 9114 sections 6.2.1 and 7.2, RFC 9204 section 5,
[RFC 9220](https://www.rfc-editor.org/rfc/rfc9220.html) and
[RFC 9297](https://www.rfc-editor.org/rfc/rfc9297.html).

All 67 HTTP/3 cases (34 new settings/control cases) pass against both target
assemblies hosted on .NET 10.0.11. The solution builds without warnings/errors and
both suppression guards pass. The discovery floor is 2,541. These local tests do
not establish QUIC interoperability or complete HTTP/3 conformance.


### QPACK field-section prefix foundation

QPACK now has a separate bounded 62-bit prefix-integer codec for counters and
stream identifiers. HPACK's existing Int32 policy is unchanged. The section-prefix
codec reconstructs wrapped Required Insert Counts and signed Delta Base using
[RFC 9204 section 4.5.1](https://www.rfc-editor.org/rfc/rfc9204.html#section-4.5.1).
It rejects truncated, impossible, negative and overflowing state with
QPACK_DECOMPRESSION_FAILED. The integer reader does not advance its offset on
failure. This supplies the state required for future blocked decoding; it does
not yet decode field lines, maintain QPACK tables or enforce blocked-stream counts.

All 97 HTTP/3 cases pass on both target assemblies on .NET 10.0.11, including 30
new QPACK cases and looped wrap-boundary checks. Both builds and suppression checks
pass; CI's discovery floor is 2,571. The full HTTP/3/QPACK integration and independent
interop remain required. The relevant RFC code-component notice is retained.


### QPACK decoder table and field representations

The decoder now includes RFC 9204's 99-entry static table, a capacity-bounded
FIFO dynamic table with monotonic absolute indices, and an incremental encoder
instruction reader. Literal/name-reference insertion, duplication and capacity
changes preserve referenced values before eviction. Incomplete instructions do
not mutate the table; malformed instructions poison it with QPACK encoder-stream
errors. A zero advertised maximum forbids encoder instructions. Pending encoded
instruction storage and decoded string allocation are bounded by local capacity.

All five field representation forms decode with Huffman support, ordered duplicate
fields and the never-indexed bit retained. A section owns a bounded copy of its
encoded payload, resolves its wrapped prefix once at arrival, and returns blocked
until its Required Insert Count exists. Table locking prevents concurrent eviction
during decoding. Missing/evicted, underflowing, overflowing or out-of-range dynamic
references fail explicitly; encoded and decoded field budgets are enforced,
including repeated indexed entries. This layer does not validate HTTP field
semantics or yet coordinate blocked-stream budgets, cancellation, acknowledgments
and QUIC flow-control credit. Those remain connection-integration requirements.

All 149 HTTP/3 cases (52 additional cases) pass against both net10.0 and the actual
netstandard2.0 assembly hosted on .NET 10.0.11. The solution builds without
warnings/errors and both suppression guards pass. Independent decoder interop
used pinned pylsqpack 0.3.24 (ls-qpack): 1,200 generated sections across capacities
0, 220 and 4,096, including 405 dynamic instruction batches, fragmented encoder
instructions, repeated fields, Huffman data and non-ASCII octets. Both assemblies
matched every expected field and its ordering. This is decoder interop, not a
claim of a complete QPACK encoder, HTTP/3 endpoint or QUIC conformance. Local
inputs/results are under ignored TestResults/http-engine/qpack-independent.json,
qpack-interop*, and qpack-sections*. The discovery floor is 2,623.
Full Windows validation of the decoder increment reported 2,623 cases: 2,618 passed
and five expected skips on an unchanged-binary rerun. The first run had one
AddressAlreadyInUse bind failure in DefaultJsonContractTest's depth case; all 14
JSON fixture cases then passed unchanged. The first failure and both recheck logs
are retained as qpack-full*, qpack-json-recheck* and qpack-full-recheck*. This does
not claim that the test port-allocation race was repaired. Pinned YARA-X/full
Forge rules reported no matches in the five new QPACK decoder source/test files.


### QPACK stateless response encoding

A stateless encoder now emits static indices, static-name literals and literal
names/values with Huffman coding when it saves bytes. It retains explicit
never-indexed markers and treats authorization, proxy-authorization, cookie and
set-cookie as sensitive. Exact/static-name dictionaries avoid rescanning the
static table for each output field. It checks decoded budgets before encoding
and encoded budgets before each field write. Required Insert Count and Base are
zero, so this output works with zero-capacity peers and never blocks on inserts.
Dynamic response-table compression remains future performance work; this is not
an extreme-performance measurement or complete connection implementation.

All 162 HTTP/3 cases pass against both target assemblies. Bidirectional interop
with pinned pylsqpack 0.3.24 matched 1,200 sections in each direction on each
assembly: ls-qpack also decoded every locally generated section with zero dynamic
capacity and zero permitted blocked streams. Solution builds and suppression
guards pass. The discovery floor is 2,636; the full 2,623-case decoder-increment
run above predates these 13 encoder cases. Fresh exact-head CI remains required.


### QPACK connection coordination and repeatable interop

The incoming-direction coordinator now enforces advertised blocked-stream counts
and local aggregate encoded-data/feedback budgets. It queues Section
Acknowledgments, Stream Cancellations and Insert Count Increments in decoder-stream
order, coalescing increments already covered by acknowledgments. Acknowledgments
for successive sections on one stream remain distinct. Cancellation releases
blocked storage without implying receipt of inserts. Encoder errors, malformed
field sections, critical-stream closure and disposal terminate the coordinator
and release pending sections; subsequent operations cannot revive it. The stream
owner must stop reading after a blocked section, serialize the decoder-stream
writer, preserve flow-control/resource accounting and prevent submissions after
stream cancellation. QUIC integration and connection scheduling remain outstanding.

All 175 HTTP/3 tests pass against both target assemblies on the local .NET 10.0.11
host. The tracked test-only `test/EmbedIO.QpackInterop` harness exercises an
independent pylsqpack 0.3.24 / ls-qpack encoder and decoder with shuffled field
section arrivals, 1-13-byte encoder-instruction fragments, cancellation, duplicates,
Huffman data and non-ASCII octets. At capacities 0/220/4096 it delivers 400/342/398
matching sections in both directions and cancels 0/58/2 sections, with 0/400/13
blocked arrivals and 0/190/400 feedback batches accepted by the independent
encoder. Both target assemblies produce those results. The harness reports its
assembly hash, Python and wrapper versions, and counts under
`TestResults/qpack-interop`; a response deadline and owned-process cleanup bound
failed probes. The wheel dependency is version/hash pinned and kept outside all
production dependency groups. Linux CI runs the probe against both assets.

To repeat it from the repository root, build both core target assemblies and
`test/EmbedIO.QpackInterop/EmbedIO.QpackInterop.csproj` in Release after locked
restores. In a Python 3.10+ virtual environment, install with
`python -m pip --isolated install --require-hashes --only-binary=:all: -r test/EmbedIO.QpackInterop/requirements.txt`.
Run `python test/EmbedIO.QpackInterop/verify.py src/EmbedIO/bin/Release/net10.0/EmbedIO.dll`
and repeat with `netstandard2.0` in the assembly path. The tool is not a server,
network interoperability test or QUIC-conformance claim.

Solution builds, standalone probe build/locked restore, complete whitespace checks
and both suppression guards pass locally. The discovery floor is 2,649. CI on
26d70a8 caught missing source-encoding markers in two earlier HTTP/3 wire files;
those markers are corrected, without disabling the formatting check. Its malware
workflow 37778299872 passed with the narrow reviewed HTTP/2 fixture acceptance;
this does not supply the absent matching GitHub alert dismissal. Fresh exact-head
checks remain necessary, and the PR stays draft.

### Mainline WebSocket and platform integration

Merged main 6961de3, retaining its asynchronous managed WebSocket close
acknowledgment completion and iOS HTTPS startup diagnostics. The combined focused
set passed 192 cases with one platform skip, and the unchanged merged-binary full
Windows suite passed 2,652 cases with five expected skips (2,657 total). Evidence
is under TestResults/http-engine/main-6961-*. Both sides' changelog entries and
interop CI steps are retained; the discovery minimum includes main's eight added
regressions. These runs precede the next request-stream reader increment.


### HTTP/3 request-stream framing and error scope

The request-stream reader now enforces initial HEADERS, streamed DATA and optional
trailing HEADERS without queuing body payloads. It pauses advancement until the
connection confirms that each encoded field section has been decoded and validated,
including QPACK blocking. It rejects DATA before headers, known frames after
trailers, control/HTTP2-reserved frames on request streams, and misplaced RFC 9218
priority updates. Unknown frames remain skippable before, between and after message
frames with bounded scratch storage. Empty DATA frames do not end the request.

Frame-placement/truncation errors retain connection scope. Missing initial headers,
Content-Length mismatches and explicit body-budget failures carry a separate
stream error with the full 62-bit request-stream identifier. Metadata is bounded
before allocation; cumulative DATA budgets are checked before reading oversized
payloads. The successful CONNECT transition permits opaque DATA and half-close
while forbidding later known non-DATA frames; the sender can commit that transition
while the independent reader awaits input. Cancellation after I/O starts poisons
that reader, while a pre-canceled call consumes nothing. The transport remains owned
by the connection. This class does not itself validate HTTP fields or expose an
HTTP/3 listener; connection/QPACK dispatch and QUIC integration remain required.

All 208 HTTP/3 tests, including 33 additional request-stream cases, pass against both
target assemblies. The current local host reports .NET 10.0.12; runtime details are
saved in TestResults/http-engine/http3-request-dotnet-info.log. Solution builds,
complete whitespace verification and both suppression guards pass. A separate
capability probe reports QuicListener.IsSupported and QuicConnection.IsSupported
as true on this host; that is capability evidence, not a connection test. The
discovery minimum is 2,690. The 2,657-case full run above predates this increment;
fresh exact-head cross-platform checks remain required.


### Initial HTTP/3 QUIC connection dispatch

The .NET 10 asset now has an internal accepted-QUIC-connection driver. It opens
server control and decoder streams, advertises bounded incoming QPACK resources,
processes peer control/encoder/decoder streams independently, and dispatches
requests while blocked field sections await encoder progress. One feedback writer
preserves instruction order. Request body reads consume their own QUIC stream
directly; there is no eagerly filled application body queue. Response writes are
serialized per stream and use the stateless QPACK encoder. Incoming trailers and
outgoing response metadata reuse the existing field validators, with HTTP/3
stream error translation. Error handling resets a malformed request before stream
disposal; framing or critical-stream failures close the connection. Worker
tracking includes asynchronous stream disposal.

Nine new loopback tests use TLS and real QUIC on Windows/.NET 10.0.12. The .NET
HTTP client requests version 3 exactly and verifies empty, concurrent 100,003-byte,
concurrent 1 MiB, and HEAD exchanges. Raw peers verify missing-method and
Content-Length errors preserve a healthy subsequent request, and forbidden frames,
closed control streams and duplicate control streams produce the expected
connection codes. Test certificates are scoped to each fixture and checked by
exact certificate hash; a PKCS#12 load is required by the tested Windows TLS
path. No firewall or system trust changes were needed. Hosts lacking QUIC and the
netstandard asset report explicit skips for these transport tests; codec/framing
coverage remains applicable to both assets.

This is an internal transport increment, not a public HTTP/3 WebServer option or
a performance result. Public negotiation/discovery, configurable resource limits,
full field-semantics audit, blocked-request reset handling, bounded application
shutdown, graceful GOAWAY/drain, dynamic response compression, priority scheduling,
extended CONNECT/WebSockets and datagrams remain required development work. The
current driver waits for application callbacks on shutdown; callbacks must honor
cancellation, and a noncooperative callback can still delay shutdown. The raw
transport types exist only in the modern target and carry platform annotations.
The nine-case wire evidence is under TestResults/http-engine/http3-quic-wire3;
the new discovery floor is 2,699. All checks on the preceding 0f9a2fb commit passed;
those checks do not validate this new increment.

The full Windows run passed 2,694 tests with five expected platform skips (2,699
total). Both core assets compile; the complete formatting check and suppression
guards pass. The pinned full YARA rules report no matches in the four new transport
source/test files. A solution build attempted while the full tests were running
hit the test process's CLI-plugin file lock; the sequential recheck passed with
zero warnings/errors after the process exited. Evidence retains both logs. The
final modern-asset HTTP/3 set passes all 217 cases; the actual netstandard asset
passes 208 framing/codec cases and explicitly skips the nine QUIC cases. The
modern test-host assembly is restored and hash-verified afterward.


### Reset-aware HTTP/3 request lifetime

Each QUIC request now has a linked cancellation lifetime and observes both
transport directions. Successful FIN is a half-close, not cancellation. A reset
or STOP_SENDING cancels that request even while its initial or trailing field
section is blocked on QPACK. Trailer decoding combines the application caller's
cancellation with the request lifetime, including callers that supply no token.
The worker releases QPACK state and queues stream cancellation once during final
cleanup; an unrelated request remains usable. Direction observers are joined
before their cancellation source is disposed.

Three deterministic wire cases first failed on e00cb8a: after confirming a section
was blocked, read-only, write-only and bidirectional peer aborts all timed out
waiting for QPACK cancellation. All pass after the change. Additional cases prove
that a normally half-closed blocked request completes after its encoder insert
arrives, and that blocked trailers are canceled even when the application uses
body reads without a cancellation token. These tests inspect pending ownership
under the connection gate to establish the precondition, then assert real wire
feedback and a healthy subsequent request. Evidence is under
TestResults/http-engine/http3-reset-before2 and http3-reset-trailers. Fourteen
QUIC cases pass locally; the discovery minimum is now 2,704. The preceding full
2,699-case Windows run does not include these five new cases. Noncooperative
application shutdown and broader lifecycle/conformance work remain outstanding.

The complete HTTP/3 set passes 222 cases on the modern asset and the 14 QUIC
cases pass a second run. The solution builds without warnings/errors; formatting,
both suppression guards and the pinned YARA scan pass. The prior checkpoint's
Linux and Windows test jobs passed in CI 37786567665; its remaining jobs and the
new source's full cross-platform gates must still complete.


### Required native QUIC coverage in desktop CI

Artifact inspection of CI 37786567665 (e00cb8a) showed nine QUIC passes on Windows
and nine explicit capability skips on each of Linux and macOS. The overall run
passed, but did not prove Unix QUIC interoperability. CI now installs MsQuic 2.6.2
on Ubuntu 24.04 from Microsoft's versioned package and on macOS 15 ARM64 from the
Homebrew bottle. Both downloads have verified, fixed SHA-256 hashes. Homebrew
handles its OpenSSL dependency; distro/Homebrew transitive crypto dependencies
remain runner-managed. Windows uses the MsQuic shipped with its pinned .NET
runtime. Setting EMBEDIO_REQUIRE_QUIC=1 makes missing capability or a legacy test
asset fail fixture setup instead of skipping; ordinary unsupported local hosts
retain an explicit skip.

The same 14 transport tests passed locally on Windows and in an isolated Ubuntu
24.04.5 container with .NET 10.0.12, MsQuic 2.6.2 and libnuma1 2.0.18-1build1.
The Linux container had a read-only root, dropped capabilities and no external
network, with loopback TLS/QUIC and a dedicated result mount. Its initial four
HTTP-client cases failed TLS setup with the IP-based server name; raw QUIC cases
already passed with localhost. The fixture now uses localhost as the HTTP/TLS
server name while connecting explicitly to IPv4 loopback, retaining the exact
certificate-hash validation. All cases then passed on both operating systems.
Logs under TestResults/http-engine/http3-required-* retain the initial TLS failure
and the corrected runs. macOS execution still requires the new CI run; installing
a prerequisite is not proof that the transport works there.

Prerequisite sources: [Microsoft QUIC platform documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview),
[Microsoft Ubuntu 24.04 packages](https://packages.microsoft.com/ubuntu/24.04/prod/pool/main/libm/libmsquic/),
and [Homebrew libmsquic metadata](https://formulae.brew.sh/api/formula/libmsquic.json).
Microsoft documents macOS support as partial; this work does not broaden that
support promise. Production packages do not gain a native runtime dependency.

The first required-capability CI attempt (37788252866) failed macOS prerequisite
setup after verifying the bottle hash: the runner's Homebrew interpreted the
local tarball path as a formula/tap instead of installing it. The revised setup
extracts that same pinned bottle into a task-private temporary directory, relocates
its dylib identifier and OpenSSL load path with install_name_tool, and reapplies an
ad-hoc signature after relocation. The loaded paths and relocated hash are recorded
as test evidence. Homebrew only supplies OpenSSL; QUIC's version and downloaded
bytes remain pinned. This setup still needs successful macOS execution.


### HTTP/3 application-independent transport shutdown

Request dispatch now runs separately from the stream owner and is awaited with
the request lifetime. Cancellation invalidates the exchange and closes the QUIC
stream without waiting for a callback that ignores that token or blocks before
returning its Task. A late application fault is observed without calling into a
disposed connection. Late body reads and response writes are rejected. Output
operations hold a counted semaphore lifetime so disposal cannot destroy the
semaphore underneath a running or queued writer. Detached callbacks count toward
a 256-callback per-connection cap until they actually finish; additional requests
receive H3_EXCESSIVE_LOAD rather than accumulating unbounded detached work.

Three controlled shutdown cases first timed out: a stalled async callback, a late
faulting async callback, and a synchronously blocked callback. All now allow the
transport to stop while application code remains blocked. A fourth case covers
an in-flight flow-controlled response plus a queued writer. A fifth sends and
resets 256 requests whose callbacks remain stalled, verifies the next request is
rejected without dispatch, then verifies callback accounting returns to zero
when those callbacks are released. This cannot terminate arbitrary application
code or prevent user cancellation registrations from blocking their invoking
thread. Server-wide admission limits, graceful drain and callback diagnostics
still need integration. The extra dispatch scheduling also needs measurement in
the performance phase; no throughput improvement is claimed.

The four-case shutdown increment passed the full Windows suite: 2,703 passed,
five expected skips, 2,708 total. Eighteen QUIC cases also passed in the isolated
Ubuntu container. The subsequent callback-cap case passed locally, bringing the
QUIC set to 19 and the discovery floor to 2,709. Logs retain the three initial
failures under http3-shutdown-before and the passing shutdown/writer/cap runs.

The revised macOS prerequisite step succeeded in CI 37789000439, but all 14 QUIC
cases failed during TLS establishment, before engine dispatch. The test key is
now imported with X509KeyStorageFlags.Exportable and its PKCS#12 export is checked
before listening. The [.NET 10.0.12 OpenSSL credential path](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/Internal/MsQuicConfiguration.cs)
exports the certificate to PKCS#12. This is a candidate fixture correction and
requires fresh macOS confirmation; neither macOS transport success nor complete
platform conformance is claimed yet.

Final focused validation passes all 227 HTTP/3 cases on Windows and all 19 direct
QUIC cases in the isolated Ubuntu container, including the callback cap and the
exportable test-certificate setup. Complete formatting, suppression guards and
the pinned full YARA scan pass. This does not substitute for fresh full-source
CI or the still-unconfirmed macOS TLS correction.


### Confirmed desktop QUIC execution and bounded graceful drain

CI 37790277013 artifacts for cb2e6d5 confirm all 19 direct QUIC cases passed,
without skips, on Windows 2025, Ubuntu 24.04 and macOS 15 ARM64. The exportable
synthetic certificate correction resolved the observed macOS TLS setup failure.
The full platform suites reported 2,709 cases each: Windows 2,706 passed/3 skipped,
Linux 2,680 passed/29 skipped, macOS 2,678 passed/31 skipped, with zero failures.
The skipped cases are outside the required QUIC set. Downloaded TRX evidence is
under TestResults/http-engine/ci-cb2e6d5. This supersedes the earlier missing-native
and TLS-failure status, while retaining their provenance above.

The internal connection driver now accepts a separate graceful-drain signal and
deadline through RunWithDrainAsync. It freezes request admission, sends GOAWAY with
an exclusive upper bound for requests that may have reached application code,
rejects newly arriving requests with H3_REQUEST_REJECTED, and lets already-admitted
requests finish. Unprocessed streams, including incomplete or QPACK-blocked
headers, are explicitly rejected even when their IDs are below that bound. Critical
control and QPACK streams remain active during drain. Completion or the deadline
closes with H3_NO_ERROR; the independent abort token can interrupt the drain.
If the entire client request-ID space is exhausted, no GOAWAY is needed, as allowed
by [RFC 9114 section 5.2](https://www.rfc-editor.org/rfc/rfc9114.html#section-5.2).

An initial lower-ID test revealed that QUIC can surface an implicit stream before
its header bytes arrive. Admission is therefore recorded after valid initial
fields, separately from transport stream acceptance. Six real-wire cases cover
successful completion, drain timeout with a stalled application, explicit abort,
a delayed lower-ID stream, confirmed QPACK-blocked headers and an idle connection.
They inspect GOAWAY's cutoff, retryable rejection, preserved response bytes and
normal connection closure. All 233 HTTP/3 cases pass locally on Windows; all 25
QUIC cases pass in the isolated Ubuntu container. Solution build, complete format
verification, suppression guards and the pinned full YARA rules pass. Logs are
under TestResults/http-engine/http3-drain-*; the initial lower-ID failure is retained.
The discovery floor is 2,715. This is internal connection support; listener-wide
drain/configuration, discovery, application adapters and other extension/performance
acceptance criteria remain incomplete. Fresh exact-head CI is still required.
