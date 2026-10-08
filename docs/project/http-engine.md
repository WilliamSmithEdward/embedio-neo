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
- [ ] Make the completed replacement engine the default managed listener and
  deprecate the Mono-derived implementation. Verify default construction, explicit
  listener selection, retained target assets and migration behavior; preserve the
  Microsoft backend as an explicit compatibility option.
- [ ] Update the README HTTP support badges when the new engine is ready, using
  validated protocol support and documented target/platform limits. Preserve the
  existing badges until their replacement claims are supported.

Release publication, HTTP Arena submission and the contributor reply are outside
this development goal unless separately authorized.

## Protocol support and milestones

| Area | Required scope | Current replacement status |
| --- | --- | --- |
| HTTP semantics | RFC 9110; methods, status, authority, informational responses, headers and trailers | Existing application layer; full conformance audit pending |
| HTTP/1.1 | RFC 9112; incremental parsing, fixed/chunked bodies, pipelines, persistence, bounded input and errors | First implementation under test |
| HTTP/2 | RFC 9113; TLS/ALPN, prior knowledge, HPACK (RFC 7541), multiplexed streams, flow control, SETTINGS, GOAWAY and reset | Implemented incrementally; listener integration under validation |
| HTTP/3 | RFC 9114; QUIC, TLS 1.3, QPACK (RFC 9204), control streams, request cancellation and graceful drain | Opt-in WebServer listener passes Windows/Ubuntu tests; discovery, extension and lifecycle completion pending |
| WebSockets | Existing RFC 6455 plus extended CONNECT over HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220) | HTTP/1.1 existing; HTTP/2 and HTTP/3 initial integrations tested; broad hardening/performance pending |
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

## Default listener transition

The owner confirmed on 2026-10-08 that the completed replacement must become the
default and the old Mono-derived managed implementation must be deprecated.
This is a final delivery requirement, not a claim that the transition is complete.
The current `WebServerOptions.Mode` defaults to `HttpListenerMode.EmbedIO`, which
constructs `Net.HttpListener`; HTTP/3 currently requires an explicit
`EmbedIOHttp3` or `EmbedIOCombined` mode. Retaining that enum default alone does not prove replacement
of the underlying implementation.

Preserve existing public names and enum values where they remain valid entry
points to the replacement. Deprecation concerns the old implementation; do not
mark `HttpListenerMode.EmbedIO` obsolete merely because its implementation changes.
Document any explicit legacy selection and its support policy before deprecation;
immediate removal is not part of this transition. Keep the Microsoft backend
available as an explicit compatibility option.

Acceptance requires real listener tests for default constructors, unconfigured
options and explicit modes, plus retained target/platform coverage. Demonstrate
that default requests reach the replacement engine and that enabled protocols
negotiate correctly, including combined TCP/QUIC hosting and hosts without QUIC.
Document capabilities, prerequisites and failure behavior; a successful HTTP/3
opt-in test is insufficient evidence for the default switch. Complete the strict
framing migration notes and README support badges against this final behavior.

## Standards inventory checkpoints

The October 2026 target includes more than protocol version negotiation. The
[IANA HTTP parameter registry](https://www.iana.org/assignments/http-parameters/)
currently lists Brotli, Zstandard, and dictionary-compressed Brotli/Zstandard
(RFC 9842) alongside existing gzip/deflate. These remain explicit content-coding
work items; the first transport increment does not implement them. Review the
HTTP field, HTTP/2, HTTP/3 and QUIC registries, relevant RFC updates and verified
errata before freezing the full conformance matrix. Record optional application
extensions separately from requirements of a conforming core server.

### October 8 registry and update-chain audit

The first captured inventory comprises eight IANA XML registries (HTTP/2,
HTTP/3, HTTP parameters, fields, status codes, methods, QUIC and WebSocket), with
retrieval timestamps and SHA-256 hashes in local evidence
`TestResults/http-engine/standards-2026-10-08/registries.json`. RFC Editor JSON
metadata for 31 starting specifications is in `rfc-metadata.json`; follow-up
metadata files capture discovered updates. This is an audit checkpoint, not a
frozen complete inventory. Recursively finish update/obsolescence relationships,
verified errata, normative dependencies and requirement-to-test mappings.

| Newly identified requirement | Current evidence and required follow-up |
| --- | --- |
| [RFC 10008: QUERY](https://www.rfc-editor.org/rfc/rfc10008.html) | IANA registers QUERY as safe and idempotent. Dedicated Query routing and request content now have initial tests across the testing asset and HTTP/1, HTTP/2 and HTTP/3. Required media-type validation, Accept-Query discovery policy and resource-level semantics remain to complete. An enum alone does not implement the specification. |
| [RFC 9931: optimistic transitions](https://www.rfc-editor.org/rfc/rfc9931.html#section-8) | Updates RFC 9112 and RFC 9298. Audit HTTP/1.1 rejected CONNECT closure and ensure subsequent bytes cannot become another request. Cover rejection/authentication paths with real pipelined input. Do not apply this requirement indiscriminately to every rejected Upgrade or to HTTP/2/3. No vulnerability reproduction is claimed by this inventory. |
| [RFC 9846: TLS 1.3](https://www.rfc-editor.org/rfc/rfc9846.json) | Official metadata dates publication to July 2026 and lists RFC 8446 as obsoleted. Inspect the full replacement and map relevant requirements to supported platform TLS implementations and configuration. Metadata alone does not establish runtime compliance; cryptography remains delegated to maintained platform providers. |
| [RFC 9659: Zstandard windows](https://www.rfc-editor.org/rfc/rfc9659.html) | Include this update to RFC 8878 when implementing zstd negotiation and bounded decompression. |
| [RFC 9841: shared Brotli](https://www.rfc-editor.org/rfc/rfc9841.html) | Include this RFC 7932 update in dictionary-compression work alongside RFC 9842. |
| WebSocket and ALPN update chains | RFC 6455 metadata lists RFCs 7936, 8307 and 8441; RFC 7301 lists RFC 8447, whose metadata also lists RFC 9847. Review applicability and later updates before freezing the baseline. |

The method audit also found case-insensitive enum parsing in `SystemHttpRequest`
and a separate parser in the testing asset. Any method integration must review
all three paths, preserve existing enum numeric values and verify exact wire
method handling. Application handlers remain responsible for the safety and
idempotence of their query operations; the framework cannot infer that from a
method name. Broader standards and performance completion gates remain open.

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


### Shared application context for HTTP/2 and HTTP/3

The internal MultiplexedContext, MultiplexedRequest and MultiplexedResponse now
adapt either transport through IMultiplexedExchange. HTTP/2's listener dispatch
uses these shared adapters; HTTP/3 can use the same application-facing request,
response and context contracts without copying their cookie/header/callback logic.
ProtocolVersion remains 2.0 for HTTP/2 and is 3.0 for HTTP/3. HTTP/3 has no initial
END_STREAM bit: a declared zero-length body is known empty, while an absent length
is exposed as unknown (-1) and potentially present. The body reader determines
EOF from FIN. This does not buffer or pre-read the request body.

Nine real-QUIC/.NET HttpClient cases exercise metadata, actual endpoints, query
values, cookies, fixed and unknown-length uploads, empty input, HEAD, 204/205/304,
response-field filtering, close callbacks, final flush and late cancellation-token
assignment. An application setting KeepAlive=false requests connection drain after
its response completes. The internal default for that drain is 30 seconds; the
explicit connection-drain overload retains its supplied deadline. These adapters
are not yet a public HTTP/3 listener option or proof of complete WebServer/module
integration. HTTP/3 extended CONNECT remains disabled pending that work.

Broader testing exposed an idle shutdown race on Windows and Ubuntu: completing
WriteAsync on the control stream does not prove the peer received GOAWAY, and
immediate connection closure could discard it. After completing admitted workers,
the driver now keeps critical streams alive until the peer closes or the original
drain deadline expires. This supersedes the earlier immediate-close-on-completion
behavior. The seven drain scenarios retain strict GOAWAY/cutoff/rejection checks,
exercise cooperative peer closure, and separately verify the idle deadline. A
finite deadline still cannot guarantee delivery to an unreachable peer; timeout
and explicit abort remain bounded termination paths. See
[RFC 9114 section 5.2](https://www.rfc-editor.org/rfc/rfc9114.html#section-5.2).

All 506 focused HTTP/2 and HTTP/3 cases pass on Windows; all 35 direct QUIC cases
pass in the isolated Ubuntu 24.04/.NET 10.0.12/MsQuic 2.6.2 container. The initial
four HTTP/2 fixture failures were fixed by including public methods in their
fixed-target reflection lookup after the exchange implemented the internal
interface; the production wire assertions remain unchanged. Initial idle-shutdown
failures and subsequent deadline/peer-close validation are retained under
TestResults/http-engine/http3-adapter-*. CI's discovery floor is now 2,728.

The previous-head CI 37791977129 failed one macOS native-listener case,
PrefixPathSelectsRequestsWithoutRewritingModulePaths(Microsoft), with a response
connection reset. Windows and Ubuntu desktop jobs passed. An unchanged-source
failed-job rerun was requested; no native-listener repair is claimed here. This
failure remains part of the validation provenance, and new-head CI is required.

The first full Windows run then exposed two cooperative-close races: a normal
connection-wide QuicException could reach the critical-output observer before the
accept loop and be misclassified as H3_CLOSED_CRITICAL_STREAM. The control-write,
feedback-write and output-completion paths now distinguish connection termination
from a reset confined to a critical stream. Three additional cases each repeat
five cooperative closures (idle, completed response and QPACK-blocked sibling).
All 38 direct QUIC cases pass in Ubuntu after this correction. Against the actual
netstandard2.0 assembly, all 471 applicable HTTP/2/HTTP/3 tests pass; its 38 direct
QUIC cases are explicitly skipped. The initial full-run failures remain recorded.

After the connection-termination correction, the rebuilt full Windows suite passed
2,723 cases with five expected skips (2,728 total, zero failures). A subsequent
fixture refinement gives the empty-body case a distinct empty response and asserts
Content-Length: 0; all nine adapter cases pass again. Solution builds are warning
free, format verification and both suppression guards pass, and the pinned full
YARA scan has no matches in the transport/shared-adapter files. New-head CI and
complete listener integration remain outstanding; no performance claim is made.


### Opt-in HTTP/3 WebServer listener

HttpListenerMode.EmbedIOHttp3 now selects an internal QUIC listener through the
public WebServer options. It binds HTTPS UDP endpoints, adapts exchanges through
the shared application context and runs the ordinary module/session pipeline.
It requires the modern asset/native support and a private-key certificate. Its
configuration, sample and current limits are in the [HTTP/3 guide](../guides/http3.md).
TCP defaults and existing listener modes are unchanged.

Listener startup binds all endpoints transactionally, including both localhost
loopbacks where enabled, before starting accept loops. Per-endpoint prefix maps
prevent authority on another configured port from crossing the receiving binding.
A bounded context channel applies backpressure, connection admission is capped,
and stop/dispose cancel transport work and pending accepts. Repeated direct
listener restart and canceled accept waiters are exercised with real requests.

The initial candidate had two reproduced defects: authority for a different
configured port incorrectly reached application code, and one failed peer TLS
handshake terminated the accept loop. Tests failed with 200 instead of 404 and
listener error 995 respectively. Routing now uses the actual endpoint's prefixes;
peer authentication/connection-handshake errors are scoped to that peer. The
BCL's [QuicListener implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicListener.cs)
confirms handshake errors are propagated through AcceptConnectionAsync.

All 15 listener cases pass on Windows and isolated Ubuntu 24.04/.NET 10.0.12 with
MsQuic 2.6.2. Coverage includes concurrent empty/100,003-byte/1 MiB uploads, sessions,
path and endpoint isolation, client reset, server stop, accept cancellation,
three restart cycles with traffic, partial startup rollback, rejected peer TLS,
and invalid prefix/certificate configuration. The initial IP-literal Ubuntu run
failed six TLS/client cases; DNS-named localhost targets pass. This is retained
as a documented runtime/platform limitation, not described as an IP-TLS fix.
Evidence is under TestResults/http-engine/http3-listener-*; the discovery floor is
2,743. Full-suite/new-head CI and the remaining engine goals still apply.

All checks on preceding adapter head 5134f15 passed, including CI 37794987741,
Security 37794986717 and Malware 37794986798. The prior macOS native-prefix reset
in CI 37791977129 passed on unchanged attempt 2; the original failure is retained,
and no cause or repair of that native case is claimed.

The first full Windows listener run reported one failure outside HTTP/3:
PermanentBansWorkLiveAndAcrossRealServerRestarts(EmbedIO) failed at File.Replace
with "Unable to remove the file to be replaced" (2,737 passed/5 skipped, 2,743 total).
Both listener variants passed the unchanged focused recheck. The full unchanged
rerun is retained separately; no persistence fix or environmental cause is claimed.
The complete guide server compiles with zero warnings/errors, and a separate
probe using the actual netstandard assembly confirms the explicit unsupported-mode
exception. Formatting, suppression guards and the pinned YARA scan pass.

The unchanged full Windows rerun passed 2,738 cases with five expected skips
(2,743 total, zero failures). All new listener cases passed in both full runs.
This establishes the local checkpoint while retaining the initial persistence
failure and separate retry provenance; fresh exact-head CI is still required.

### HTTP/3 WebSocket negotiation checkpoint

The QUIC driver now advertises SETTINGS_ENABLE_CONNECT_PROTOCOL=1 and accepts
validated extended CONNECT headers. The public listener rejects unsupported
protocols with 501 and invalid WebSocket versions with 400/version 13. Existing
shared context and managed framing adapters carry accepted tunnels. Six real-wire
cases failed at the missing setting before implementation; four rejection cases
also failed before listener validation, including bad versions returning 500.
All ten now pass, covering empty/text/binary/fragmented messages, cookies,
subprotocols, orderly QUIC FIN, abort, and sibling HTTP requests.

The 69 focused HTTP/2 WebSocket, HTTP/3 driver and listener cases pass on Windows.
All 25 public listener cases pass on isolated Ubuntu 24.04, runtime 10.0.12 and
MsQuic 2.6.2. The fixture uses BCL WebSocket framing over a literal-encoded QUIC
HTTP/3 request; response QPACK decoding is shared with production and is not an
independent decoder claim. The ordinary .NET ClientWebSocket cannot negotiate
HTTP/3 in that runtime. Evidence is under TestResults/http-engine/http3-ws-*;
the discovery floor is 2,753. Full-suite and exact-head CI remain required.

The preceding public-listener head 7a9cb6e passed CI 37797945219, Security
37797944618, Malware 37797944578 and the fuzz workflow. This includes all three
desktop platforms and required MAUI jobs; the new WebSocket increment requires
its own exact-head checks.

The complete Windows suite for the WebSocket increment passed 2,748 cases with
five expected skips (2,753 total, zero failures). Both targets build without
warnings, formatting and analyzer guards pass, and the pinned YARA scan reports
no matches in the new wire fixture. No performance improvement is claimed.

### Managed WebSocket message UTF-8 validation (#190)

Thirteen independently encoded malformed text-message cases reached the echo
callback on the preceding source instead of closing with 1007. Eleven valid
text/binary/fragmented cases already passed. The frame reader now validates
complete text directly and carries a strict BCL decoder across partial text
frames, flushing at the final fragment. Control and binary payloads do not change
text state. Fragment conversion borrows a bounded character buffer and clears it
on return; no decoded message string is retained by validation.

An initial GetCharCount-based attempt did not advance decoder state and failed
six valid fragmentation cases; that evidence remains under ws-text-after. The
corrected streaming Convert implementation passes all 118 initial focused cases.
Two further wire cases prove rejection before a malformed non-final message
finishes; four large text cases cover both unfragmented and fragmented HTTP/2
and HTTP/3 tunnels. All 61 text/tunnel/listener cases pass on Windows. Against the
actual netstandard2.0 core assembly, all 97 applicable managed framing/close/text
and HTTP/2 WebSocket cases pass on runtime 10.0.12. Evidence is under
TestResults/http-engine/ws-text-*. The discovery floor is 2,783. Migration guidance
records the malformed-text behavior change; performance costs and full-source
checks remain to be measured and validated.

All 124 focused cases pass on isolated Ubuntu 24.04 with runtime 10.0.12 and
MsQuic 2.6.2. The pinned YARA scan has no matches in the changed parser, new wire
fixture or benchmark harness. The initial scanner command incorrectly supplied
multiple target paths as rule inputs; its syntax errors are retained separately
from the corrected one-target-per-invocation scan. Preceding WebSocket head
2b6bbbc passed CI 37800390714, Security 37800389784 and Malware 37800389812.

The initial decoder implementation passed the complete Windows suite (2,778
successes/five expected skips, 2,783 total). A measured refinement then added the
.NET 10 Utf8.IsValid complete-chunk path and reuses a strict decoder when code
points span frames. Binary/control frames do not inherit decoder state. A later
fragmented text message and arbitrary binary message on the same connection now
exercise reuse explicitly. All 124 focused cases pass; the actual netstandard
asset passes all 97 applicable cases again. The full refined-source run remains
separate evidence.

#### UTF-8 reader cost experiment, 2026-10-08

The new `--websocket-read` harness validates every decoded byte and complete wire
consumption. AMD Ryzen 7 9800X3D (8 cores/16 logical), Windows 10.0.26300 x64,
.NET 10.0.12, Release, one process at a time, no full-suite test run concurrent
with measurements. Three alternating baseline/decoder/refined process runs,
five timed samples per workload after 1,000 warmup messages; median of 15 samples.
`DOTNET_TieredCompilation=0` avoids tier transitions observed in the first run.
The initial tiered results are retained but are not used for these comparisons.
No affinity or clock-frequency control was imposed; this is local diagnostic
evidence, not a portable percentage guarantee.

The final experiment verifies an identical runner binary for all three versions
(SHA-256 38FC9B67BD85A8220E5DF1BE6D542948539B154DCDFED23BBF40807D436B196A);
only the core DLL is replaced. An earlier comparison used a rebuilt candidate
runner with a different hash; those results are retained but superseded here.
Baseline core is built from 2b6bbbc.
The intermediate decoder and refined cores are identified by SHA-256 below.
UTF-8 payloads repeat U+00E9; the 16-byte/16-frame case deliberately splits each
code point. MemoryStream parsing includes masking, reflection and content checks;
network, message reassembly, callback work and sending are excluded.

| Text bytes / frames | Unchecked baseline ns/message | Initial decoder ns/message | Refined ns/message | Refined allocated B/message |
| --- | ---: | ---: | ---: | ---: |
| 16 / 1 | 311.3 | 320.8 | 325.9 | 624.0 |
| 16 / 16 | 2,884.1 | 4,076.6 | 4,099.6 | 8,704.1 |
| 1,024 / 1 | 663.4 | 775.8 | 773.0 | 1,736.0 |
| 1,024 / 16 | 3,466.6 | 4,396.4 | 3,640.8 | 10,752.1 |
| 65,536 / 1 | 30,088.0 | 35,888.0 | 36,268.2 | 132,016.8 |
| 65,536 / 16 | 31,380.2 | 47,131.0 | 38,117.4 | 76,928.8 |

Refined steady-state allocations equal the unchecked baseline for all twelve
text/binary workloads; the initial decoder added 56 or 112 bytes per fragmented
text message in these measurements. The refinement reduces the measured large
fragmented-text cost relative to the first correct implementation, but does not
make validation free or prove end-to-end speedups. Binary medians stay within
about 2% of baseline in the final comparison; small differences are not treated
as improvements. Decoder allocation on first use and connection retention are
outside these warmed steady-state allocation figures.

Core hashes: baseline `A82D3DD7685C0FCFA912030A53C86D4D424021217A60DFB3D63D4B7170FC2337`;
initial decoder `E38B35FEA660AC93686C43F938B5108699E9F62AD4D4D6C2C0B03022CE1B2FF0`;
refined `412D5A6312BDFE6EC5EAB5EEE0C9B2725212164341FBC584D5EBB02C66959367`.
Raw JSON, stderr and earlier experiments remain under
TestResults/http-engine/ws-text-identical-*, ws-text-comparison-* and ws-text-perf-*.
Reproduce with the same runner against each core and `DOTNET_TieredCompilation=0`;
see the performance README. Broad WebSocket throughput/tail latency, retained
memory, overload and cross-platform performance remain required by #190.

The final refined Windows suite passed 2,778 cases with five expected skips
(2,783 total, zero failures). Linux focused tests, both-target builds, actual
netstandard tests, formatting/analyzer guards and the final YARA scan also pass.
Fresh exact-head CI remains required; #190 and the full engine goal remain open.

### Managed HTTP/1 opening handshake validation (#190)

Twenty-three of 30 real-wire cases failed before this increment. The managed
accept path upgraded malformed nonces, wrong method/version or missing upgrade
tokens, while missing/unsupported WebSocket versions returned 500. Repeated
nonce/version fields silently replaced earlier values. A separate valid repeated
subprotocol case lost the client's first supported choice.

The accept path now validates request metadata and the 24-character base64 shape
representing a 16-byte nonce without decoding an extra byte array. Invalid
handshakes throw HTTP 400 before constructing the upgrade response or connecting
the application; version errors also advertise 13. HTTP parsing retains repeated
singleton fields for rejection at the handshake boundary, and combines list-valued
Upgrade/subprotocol fields in order. No per-request duplicate flag is added.

An initial parser-level duplicate rejection closed the connection without an HTTP
response; moving validation to the handshake boundary fixes that wire result.
The HTTP/1.0 rejection fixture was corrected to expect its legitimate HTTP/1.0
response version. Both attempts are retained. All 30 final handshake cases pass;
the preceding 87-case handshake/H2/H3/cookie selection also passes. Each handshake
case verifies subsequent healthy HTTP and BCL WebSocket traffic. Migration notes
record the stricter malformed-request behavior. Evidence is under
TestResults/http-engine/ws-handshake-*. Discovery floor: 2,813. Full-source,
cross-platform and target checks remain required; this is not complete handshake
or extension conformance.

### Native Unix upgrade cleanup regression

The d97f60b CI run 37803210662 failed on macOS 15 ARM64: cancellation of the
native listener threw ObjectDisposedException from HttpResponseStream.InternalWrite
through HttpListener.Stop, followed by a pending-server timeout. Its unchanged
attempt 2 passed; this does not resolve the observed defect.

The .NET 10.0.12 Unix runtime leaves its internal SentHeaders property false after
WriteWebSocketHandshakeHeadersAsync. Response cleanup therefore attempts another
HTTP response on the upgraded transport. A deterministic Linux wire regression
confirmed a second HTTP/1.1 101 response on shutdown while application connection
initialization was held pending. This is a separate controlled reproduction of
the unwanted write, not a claim to deterministically reproduce the macOS race.

The candidate records the completed native handshake through that runtime's
internal boolean property immediately after successful AcceptWebSocketAsync,
before exposing the socket to application callbacks. Windows is unchanged.
The property shape is checked; unrecognized runtime shapes retain their existing
native behavior, so this mitigation is not promised for all runtimes. No errors
are swallowed and IgnoreWriteExceptions remains unchanged. This uses a narrow
private-runtime compatibility shim; HttpListenerMode.EmbedIO does not require it.
Cancellation overlapping an upgrade still needs explicit stress coverage; this
change is not a general native-listener shutdown guarantee.

The Linux wire regression failed before the candidate and passed afterward;
all 44 selected native-shutdown/message-callback cases passed on Linux. The
original cancellation regression now repeats 32 times per listener mode.
Final source, Windows/macOS, netstandard and scanner checks remain required.
Evidence: TestResults/http-engine/native-stop-*. Discovery floor: 2,814.

The preceding handshake increment passed the full Windows suite (2,808 passed,
five expected skips), 154 Linux focused cases and 127 actual netstandard-asset
cases before this native cleanup candidate was added.

The first Windows fixture run exposed HTTP.sys's abortive ConnectionReset on
listener stop. The wire test now accepts only that specific Windows transport
termination while still asserting that all bytes preceding it are empty; Unix
EOF behavior remains checked. The initial logs are retained. Linux repeated
cancellation and all 44 selected cases pass. Both-target builds and analyzer
guards pass; the pinned YARA scan of all seven changed C# files has no matches.

### Fragmented UTF-8 validation cost and upgrade cancellation follow-up

All checks on ef04f66 passed, including CI 37806790138, Security 37806789636,
Malware 37806789642 and Fuzz 37806789641. This includes the original macOS
cancellation regression and the Unix duplicate-handshake wire regression. The
combined local Windows suite passed 2,809 cases with five expected skips.

A new stress fixture performs 32 listener lifetimes per backend, racing four
partially sent upgrade requests with stop and rebinding the same endpoint.
Some rounds await one completed application connection, while others race the
final request bytes immediately or after a scheduling yield. Every client task
and the server accept loop must finish; client-side resets are permitted only
for specified socket termination errors, while timeout cancellation still fails.
This supplements the deterministic wire test; it does not prove every schedule.

Incoming text validation now keeps a small byte-range state following
[RFC 3629 section 4](https://datatracker.ietf.org/doc/html/rfc3629#section-4),
without a retained Decoder or per-fragment rented/cleared character buffer.
Complete spans retain runtime validation; a split leading scalar and potentially
incomplete trailing scalar use the byte state. Binary/control messages do not
advance that state, and new text messages reset it. Invalid input still closes
with 1007 before application dispatch.

Four independent-oracle tests cover all 1,112,064 Unicode scalars fragmented byte
by byte, every one/two-byte sequence at each split, every single-byte mutation
of boundary scalar encodings (also surrounded by substantial ASCII spans), and
20,000 seeded longer valid/random inputs. Strict UTF8Encoding.Decoder.Convert
is the malformed/prefix oracle; Rune supplies valid scalar encodings. Existing
real TCP and HTTP/2/HTTP/3 WebSocket tests remain required.

The first scalar-only candidate improved tiny fragmented text but regressed
large netstandard text substantially (65,536-byte single frames rose from
36.9 to 71.0 us in that experiment). It was not retained. Complete-span runtime
validation and bounded partial-scalar handling corrected the regression. Earlier
ws-utf8-stream-*, ws-utf8-final-* and ws-utf8-verified-* experiments remain
available. The 500-iteration large-message samples produced a conflicting
65,538-byte single-text result (44.9 to 61.9 us for the modern asset), so the
benchmark now records GC counts and uses 5,000 iterations for every sample.
The longer comparison shows 53.9 to 53.4 us for that row. Both sides have
3,123 generation-2 collections across its 75,000 measured messages; this
workload is GC-sensitive, but that does not prove the cause of the earlier
variation. No earlier sample is discarded from the evidence directory.

The authoritative comparison below is ws-utf8-long-*: final core builds,
byte-identical runner A3546DD59EBA351A113B06B64FF61C94C20239DF4077A67626C18C2F0E89DA46,
three alternating processes per target/version and five measured samples each.
Baseline core source is ef04f66. The target directive was expressed as the
explicit NETSTANDARD2_0 condition after the regex guard misread a negated
preprocessor condition as nullable suppression; no checker was weakened.

Environment: AMD Ryzen 7 9800X3D (8 cores/16 logical), Windows 10.0.26300,
.NET 10.0.12, x64, DOTNET_TieredCompilation=0. Both assets run on that runtime;
this is not a measurement on a legacy runtime. No concurrent test/benchmark was
intentionally launched; CPU affinity/frequency and desktop background activity
were not isolated. Every row is the median of 15 samples. The maximum absolute
median allocation difference is 0.0816 B/message; no allocation improvement is
claimed. This measures in-memory parsing/unmasking and validation with identical
reflection/content checks, not end-to-end performance.

| Payload bytes | Frames | Type | .NET 10 before / after (us) | netstandard before / after (us) |
| --- | --- | --- | --- | --- |
| 16 | 1 | binary | 0.332 / 0.319 | 0.339 / 0.311 |
| 16 | 1 | text | 0.355 / 0.328 | 0.339 / 0.328 |
| 16 | 16 | binary | 3.104 / 3.001 | 3.137 / 3.027 |
| 16 | 16 | text | 4.223 / 3.166 | 4.179 / 3.042 |
| 1,024 | 1 | binary | 0.685 / 0.671 | 0.686 / 0.668 |
| 1,024 | 1 | text | 0.785 / 0.786 | 0.797 / 0.792 |
| 1,024 | 16 | binary | 3.620 / 3.517 | 3.500 / 3.520 |
| 1,024 | 16 | text | 3.885 / 3.828 | 4.388 / 3.847 |
| 65,536 | 1 | binary | 30.290 / 30.283 | 30.359 / 30.148 |
| 65,536 | 1 | text | 36.573 / 36.890 | 36.575 / 36.134 |
| 65,536 | 16 | binary | 32.284 / 32.427 | 31.519 / 31.486 |
| 65,536 | 16 | text | 38.128 / 38.657 | 47.481 / 38.787 |
| 65,538 | 1 | binary | 54.504 / 54.235 | 54.392 / 53.453 |
| 65,538 | 1 | text | 53.911 / 53.431 | 53.596 / 54.009 |
| 65,538 | 16 | binary | 32.713 / 32.467 | 32.586 / 32.725 |
| 65,538 | 16 | text | 46.213 / 39.239 | 49.607 / 39.698 |

Core hashes:

- modern before: CE32BF9D5D48A24396555FF7477E9A78C5083CC11F5F7012A061990BCC72C860
- modern after: 0345A257DE8BC2B267C71B9CC59AD4D701B54B180E1C9735C8F09B31FD07914B
- legacy before: 0FC20306BB313C126F2E837C29E73F92A5A0D4D4D5F6A1990654786C3D3177A9
- legacy after: CE903BDB5D45D60AC4AD0ADFEEE199BB3F53077F39940399AFF0C6A1923A0597

Small changes in the control rows are not claimed as improvements. The full
network, application, tail-latency and retained-memory objectives remain open.
Discovery floor: 2,820. The final Windows suite passes 2,815 cases with five
expected skips (2,820 total). The modern Linux selection passes 111 cases and
the actual netstandard asset passes 76 cases on both Windows and Linux. Both
library targets and the benchmark build with zero warnings/errors; formatting,
analyzer guards, the allocation budget and the five-file pinned YARA scan pass.
Fresh exact-head CI remains required. The full engine goal and #190 remain open.

The large-frame benchmark also exposes the shared reader's geometric
MemoryStream growth beyond a requested non-power-of-two length, followed by a
final copy. That observation led to the bounded-growth increment below.

### Bounded frame-buffer growth and native accept cancellation

The shared byte reader grew MemoryStream geometrically beyond a non-power-of-two
requested length, then copied again to return the exact result. Two allocation
regressions measured 328,136 bytes for a 65,538-byte result and 655,840 bytes for a
131,074-byte result before correction. Growth now occurs only after a successful
read, remains geometric, and is capped at the requested result length. Complete
reads can return their owned backing buffer; partial reads still return only
received bytes. The fixed scratch-buffer policy is unchanged. Ten focused cases
cover allocation budgets, short reads, independent result ownership, immediate
EOF and a pending first read with an advertised int.MaxValue length. The latter
two verify bounded initial allocation rather than eagerly reserving the payload.

The benchmark retains the exact runner from the preceding UTF-8 experiment:
A3546DD59EBA351A113B06B64FF61C94C20239DF4077A67626C18C2F0E89DA46.
Baseline source is da9d61b. Final samples include the separate cancellation fix
below; the earlier frame-growth-* samples are retained. Authoritative data is
frame-growth-final-*: three alternating processes per target/version, five
5,000-message samples per process, with runtime/OS/architecture, hashes and GC
counts recorded. The same AMD Ryzen 7 9800X3D / Windows 10.0.26300 /
.NET 10.0.12 x64 host uses DOTNET_TieredCompilation=0, without fixed CPU affinity
or frequency. No other test/benchmark was intentionally run concurrently.
Both assets execute on .NET 10; this is not an older-runtime measurement.

Every timing below is the median of 15 samples. All workloads are shown; small
control-row variations are not claimed as improvements. The byte counts include
frame parsing/reflection/content checks. Network I/O, reassembly, application
callbacks, retained memory and tail latency remain separate objectives.

| Payload bytes | Frames | Type | .NET 10 before / after (us) | netstandard before / after (us) | .NET 10 bytes/message before / after |
| --- | --- | --- | --- | --- | --- |
| 16 | 1 | binary | 0.330 / 0.318 | 0.313 / 0.311 | 624.0 / 624.0 |
| 16 | 1 | text | 0.337 / 0.327 | 0.330 / 0.328 | 624.0 / 624.0 |
| 16 | 16 | binary | 3.162 / 3.041 | 2.948 / 3.045 | 8704.1 / 8704.1 |
| 16 | 16 | text | 3.350 / 3.136 | 2.997 / 3.047 | 8704.1 / 8704.1 |
| 1,024 | 1 | binary | 0.696 / 0.709 | 0.668 / 0.668 | 1736.0 / 1736.0 |
| 1,024 | 1 | text | 0.795 / 0.802 | 0.786 / 0.788 | 1736.0 / 1736.0 |
| 1,024 | 16 | binary | 3.694 / 3.558 | 3.466 / 3.491 | 10752.1 / 10752.1 |
| 1,024 | 16 | text | 3.805 / 3.839 | 3.808 / 3.841 | 10752.1 / 10752.1 |
| 65,536 | 1 | binary | 30.328 / 30.410 | 30.148 / 30.457 | 132018.7 / 132018.7 |
| 65,536 | 1 | text | 36.483 / 36.675 | 36.248 / 36.467 | 132018.7 / 132018.7 |
| 65,536 | 16 | binary | 31.579 / 32.770 | 31.208 / 32.264 | 76928.7 / 76928.7 |
| 65,536 | 16 | text | 38.944 / 38.737 | 38.096 / 38.869 | 76928.7 / 76928.7 |
| 65,538 | 1 | binary | 54.864 / 31.998 | 53.919 / 32.013 | 328755.2 / 197588.4 |
| 65,538 | 1 | text | 54.568 / 38.157 | 52.816 / 38.291 | 328755.2 / 197588.4 |
| 65,538 | 16 | binary | 33.280 / 32.748 | 32.186 / 32.464 | 109984.9 / 93552.7 |
| 65,538 | 16 | text | 41.346 / 39.584 | 39.045 / 39.233 | 109984.9 / 93552.7 |

Core hashes:

- modern before: 5A22FD8970E354C8444B7B021CE2E8CC3446D116509B5F7D5820A1C0BFEA16D4
- modern after: F471FB98E6B0575C421C5731BB0F3D7CB78E093D7D0CC7B8488DB989049BAF1E
- legacy before: 0C2EC3CB3CC525C48B4082F1FC7EEAB42CC39502A759076AADD34A019332D783
- legacy after: 9045E9FA34B98894A82E9919FFB29AFE5B7A61E6CC31688EED713E2947C54500

The modern single-frame binary row records 3,126 / 3 generation-2 collections before / after across 75,000 measured messages.
The modern single-frame text row records 3,123 / 0 generation-2 collections before / after across 75,000 measured messages.
The legacy single-frame binary row records 3,126 / 3 generation-2 collections before / after across 75,000 measured messages.
The legacy single-frame text row records 3,123 / 0 generation-2 collections before / after across 75,000 measured messages.

CI on the preceding da9d61b head (37810710607 attempt 1) also found a native
macOS race in the new upgrade-cancellation stress fixture: a closed native socket
was dereferenced while SystemHttpContext captured request endpoints. The native
adapter previously translated cancellation only around GetContextAsync, leaving
construction outside that boundary. It now checks cancellation before accept and
before construction, and covers construction with the same cancellation-aware
exception translation. Recoverable errors are translated only when the supplied
token is canceled; uncanceled and fatal errors still propagate. A separate
pre-canceled-accept regression timed out before correction and now passes without
stopping the listener. The macOS stress failure remains the cross-platform
regression to verify; a local pass does not prove that schedule repaired.

The same CI run's Android lifecycle job failed while unpacking the downloaded
emulator ZIP, before application execution. Its job-only unchanged-source rerun
passed on attempt 2. The old macOS failure remains visible; no retry success is
being used to substitute for the cancellation correction. Logs are retained as
da9-android-failed.log and da9-ci-failed.log.

The final combined-source Windows run passed 2,826 cases with five expected
skips (2,831 total). The focused Linux selection passed all 185 modern cases;
the actual netstandard asset passed all 150 selected cases on Windows and Linux.
Both core targets built without warnings or errors. Formatting, suppression and
C# parser guards, existing allocation budgets and the pinned YARA scan of all
four changed C# files passed. Logs use the growth-acquisition prefix under the
ignored evidence directory. Fresh exact-head CI, particularly the reported
macOS cancellation schedule, was subsequently verified in CI 37813837559
on 1da822edf0929d78ebd419a67b3e15876215e8c4. The macOS regression,
interoperability and reset stress steps passed; every reported PR check on that
head was passing or intentionally skipped. Discovery floor: 2,831. No release or contributor reply is included; the full
engine program and #190 remain open.


## HTTP/3 priority-update ingestion

The extension audit checked the [IANA HTTP/3 registry](https://www.iana.org/assignments/http3-parameters/)
and [RFC 9218 section 7.2](https://www.rfc-editor.org/rfc/rfc9218.html#section-7.2).
The control reader previously skipped both PRIORITY_UPDATE frame types. It now
returns their identifiers and ASCII field values, rejects server-originated
updates and non-request stream identifiers, and bounds buffered metadata at
16 KiB before allocation. Empty field values are retained for default resets.
The connection rejects push updates because it has not promised any push IDs.
The request reader already rejects these control-only frames on request streams.

Thirteen control-reader regressions failed before the increment and pass after
it, including fragmented input, full-width identifiers, truncation, non-ASCII
fields, oversized declared length and exact successor-frame preservation.
Four raw QUIC cases check connection-scoped error codes for invalid request IDs,
unpromised push IDs, oversized frames and updates on request streams. All 155
focused modern cases passed on Windows and Linux, and all 113 framing cases
passed with the actual netstandard asset on both hosts. Both targets built
without warnings/errors; formatting, analyzer guards and the pinned YARA scan
passed. The full Windows run passed 2,843 cases with five expected skips
(2,848 total). CI 37815009316 subsequently passed on
5c2d1ff67a6f2bc2ec2b5ac6bfc6713f54b5e537, including macOS. Every reported
PR check on that head was passing or intentionally skipped. Evidence uses the
h3-priority prefix under TestResults/http-engine.

This is ingestion and wire validation, not completed priority scheduling.
Structured Field parsing, effective-priority state, bounded early-update storage,
HTTP/2 equivalents and scheduling measurements remain required. The transport's
live QUIC stream limit is not exposed here; request-ID kind is checked, but the
RFC's recommended stream-limit rejection is still pending. ORIGIN advertisement
(RFC 8336/9412), datagrams/capsules and the rest of the standards inventory remain
open. No support badge changes are justified by this increment.


## Structured priority field parser

The next priority increment parses the complete Dictionary required by
[RFC 9218](https://www.rfc-editor.org/rfc/rfc9218.html#section-4), including
[RFC 9651](https://www.rfc-editor.org/rfc/rfc9651.html) dates and display strings.
Urgency defaults to 3 and incremental to false. Last duplicate members replace
previous members; unsupported types/ranges are ignored. Unknown members and
parameters still undergo syntax validation, and a malformed dictionary cannot
retain partially parsed priority values. Input is bounded to 16 KiB, lists do
not recurse, and unknown dictionaries/lists are not materialized. Display-string
UTF-8 validation allocates only the individual encoded string's bounded length.

All 432 Dictionary records from httpwg/structured-field-tests revision
00462dd7938b43bf596cb2af6a373d9c928a6cbe passed a standalone comparison. The same
records are embedded in the test assembly with source attribution and the
upstream license, and their syntax results plus effective priority projections
pass in the regression fixture. Another 64 cases exercise priority-specific
semantics, malformed fields, numeric limits, Unicode, escapes and input bounds.
Two control-frame cases and a raw QUIC case cover malformed dictionary errors.
The control event exposes both the raw value and parsed parameters. Scheduling
integration, bounded priority state and HTTP/2 signaling are still pending.

Both assets build without warnings/errors. The final focused Linux set passed
223 cases; the actual netstandard asset passed 180 on Windows and Linux. The
formatting/analyzer guards and pinned YARA scan of changed C# and fixture files
passed. The combined Windows run passed 2,911 cases with five expected skips
(2,916 total; priority-parser-full log). Fresh exact-head CI remains required.
Discovery floor: 2,916. No complete scheduling/performance claim is
made, and the public support badges remain unchanged.


## Bounded HTTP/3 priority state

HTTP/3 connections now retain the latest priority update across request arrival
and header decoding. An update overrides the Priority header even when it arrives
first; later updates replace both parameters, including an empty dictionary's
default reset. Active exchanges expose an atomic urgency/incremental snapshot.
Malformed header dictionaries fall back to defaults without invalidating the
request; malformed PRIORITY_UPDATE dictionaries retain the preceding increment's
connection error behavior.

Active state follows the existing 256-worker limit and is removed when the stream
worker ends. Each connection retains at most 256 pending targets and 256 recently
closed IDs. Pending eviction removes the least recently updated target without
evicting active state; this is an explicit local policy for advisory updates.
Recent closed-stream updates are ignored. IDs older than that bounded history can
occupy pending-cache space, but cannot reopen a QUIC stream and cannot grow the
cache beyond its bound. Disposal clears all three collections.

Six state tests cover precedence, default/reset handling, latest-update eviction,
10,000-stream churn, active capacity and atomic concurrent reads. A real QUIC case
sends an update before request HEADERS, verifies that it overrides the independently
encoded header, applies late updates and a default reset, then verifies release,
ignored late closed-stream updates and a healthy successor. All 50 initial
state/QUIC focused cases passed on Windows. The broader Linux selection passed
230 cases, and the actual netstandard asset passed 186 on both Windows and
Linux. Both targets build without warnings/errors; formatting, analyzer guards
and the pinned YARA scan passed. The full Windows suite passed 2,918 cases
with five expected skips (2,923 total; priority-state-full log). Fresh exact-head
CI remains required.

This state is prepared for scheduling; it does not yet change DATA transmission
order. HTTP/2 signaling and a scheduler that keeps flow-blocked streams from
stalling unrelated streams remain required, followed by mixed-workload performance
measurements. No complete priority or extreme-performance claim is made.


## HTTP/2 priority updates and stream ownership

HTTP/2 PRIORITY_UPDATE now validates connection scope, the four-byte target,
client request IDs and the same structured priority dictionary used by HTTP/3.
The reserved target bit and undefined frame flags are ignored. Stream state
exposes an atomic priority snapshot; a stored early update overrides request
headers, later updates replace it, and locally finished responses ignore updates.
The implementation does not promise server push, so even-numbered push targets
are rejected as idle. Closed request targets are discarded using the existing
stream high-water mark.

Active streams plus idle prioritized targets cannot exceed the advertised stream
limit. Repeated updates reuse an existing slot. Opening a higher request removes
priority state for lower implicitly closed idle IDs; abort clears pending state.
An update that exceeds the shared budget fails the connection. An unprioritized
request that cannot fit is refused without evicting active or future-target state.

Twelve registry cases and six real TCP/HTTP2 cases cover the new behavior; all
61 focused registry/interoperability cases passed on Windows. The expanded
126-case selection passed on both Linux assets and the actual netstandard asset
on Windows. Both targets built without warnings/errors; formatting, analyzer
guards and the pinned YARA scan passed. The full Windows suite passed 2,936
cases with five expected skips (2,941 total; h2-priority-full log). Fresh exact-head
CI remains required. DATA output
scheduling and explicit legacy-priority settings negotiation remain unfinished.

Transport investigation also confirmed that native QUIC stream priority is a
.NET 11 addition ([runtime proposal](https://github.com/dotnet/runtime/issues/90281),
[preview release notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview5/libraries.md)).
This program retains .NET 10; it must not describe a managed write-admission policy
as control over native QUIC packet scheduling. A scheduler must allow a flow-blocked
stream's asynchronous write to remain pending while healthy streams progress.


## Windows accept completion ownership during shutdown

CI 37818002036 failed the managed WebSocket upgrade cancellation stress on
Windows; an unchanged local repeat reproduced it. Temporary connection tracing
showed the stuck peer had never become an HttpConnection. Two deterministic
real-socket cases then showed that failed accept completions discarded their
AcceptSocket without disposal; the successful completion control already passed.
Disposing only that property was insufficient: a later stress run still failed.

The Windows runtime can clear AcceptSocket when completion's accept-context
update races listener disposal ([runtime completion source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L833-L855)).
The endpoint now supplies and retains its Windows accept socket independently
until completion. Both inline and asynchronous completions transfer successful
ownership or dispose failed sockets, including when the runtime clears the
property. Terminal rearm failure also releases its event arguments. Unix keeps
its existing socket allocation path. No private runtime access is used.

Five real-socket cases cover aborted/reset completions with and without the
runtime-cleared property and a successful completion after endpoint stop. The
36-case shutdown/lifetime/endpoint selection passed on Windows and Linux for
both the modern and actual netstandard assets (hosted on .NET 10). One hundred
unchanged cancellation stress repeats passed: 12,800 client connections per
listener mode. Both assets built without warnings/errors; formatting, analyzer
guards, the pinned YARA scan and existing allocation budgets passed. The combined local Windows suite passed 2,948 cases with five expected skips
(2,953 total, including the then-local scheduler). Exact-head CI 37821402117
and all checks passed on 759db37. Temporary production
tracing has been removed. This checkpoint makes no new protocol-support or
performance claim; the original failing CI remains recorded.


## HTTP/2 priority-aware flow-credit scheduling

DATA reservations now use one explicitly owned waiter per stream. Only waiters
with positive stream credit enter an indexed priority heap; connection-credit
updates grant the most urgent writable request first. At equal urgency,
non-incremental responses follow stream order and incremental responses follow
reservation arrival order. Priority changes and SETTINGS/WINDOW_UPDATE changes
update eligibility before grants. A flow-blocked urgent stream cannot stall a
writable lower-priority peer. Canceling an ungranted waiter removes it without
spending credit; close/abort faults pending writers. Cancellation registrations
are disposed outside the flow lock, including synchronous completion races.

Eight focused cases cover priority order, reprioritization, blocked-stream
independence, 256 cancellation/grant races and seeded 128-stream ordering models
with cancellation, arbitrary removal and window changes. A real TCP case queues
two writers at exhausted connection credit and verifies the high-priority DATA
frame arrives first after a one-byte WINDOW_UPDATE. The 81-case focused selection
passes on Windows and Linux with both production assets hosted on .NET 10. The
final full Windows run passed 2,950 cases with five expected skips (2,955 total;
h2-scheduler-final-full log). Both targets build without warnings/errors;
formatting, analyzer guards, pinned YARA scans and existing allocation budgets
pass. Fresh exact-head CI remains required.

`python scripts/compare_http2_flow.py` reproduces the component comparison against
759db37: baseline and candidate share one runner with only the baseline class
identifier renamed. Three stable-JIT processes each run five alternating samples;
source/binary hashes, raw samples and summaries remain under TestResults. The
initial scan scheduler regressed the 128-writer batch from 44.9 to 88.4 us. A tree
reduced that to 53.0 us; the final indexed heap removed per-waiter tree/list nodes.
The final direct comparison measured 44.4 versus 44.5 us for 128 writers, while
a later reproduction measured 51.4 versus 45.1 us. No robust latency improvement
is claimed for that larger batch. Allocation consistently fell from about 80,379
to 58,435 bytes per batch (27%). The eight-writer comparison measured 3.87 versus
3.20 us and 5,978 versus 4,490 bytes; writable reserve/replenish cycles measured
52.0 versus 46.5 ns with unchanged 72 B/cycle. These include bookkeeping and
worker allocation, use no network, and are not end-to-end throughput/tail-latency
claims. The rejected implementations and raw measurements are retained locally.

This schedules flow-credit admission, not the final transport write queue or
QUIC packets. HTTP/2 legacy-priority negotiation, HTTP/3 output scheduling,
policy/fairness validation and mixed-workload end-to-end performance remain open.


## HTTP/2 extensible priority settings

The initial server SETTINGS now advertises NO_RFC7540_PRIORITIES=1 alongside
stream/header bounds and extended CONNECT. The peer parser validates Boolean
values before applying any settings and freezes the initial effective value.
Omission means zero; equal repeats remain valid. Initial duplicates apply in
wire order, while a later value change fails the connection with PROTOCOL_ERROR.
Deprecated PRIORITY and HEADERS dependency/weight semantics are ignored, including
self-dependency, while their framing checks and HPACK synchronization remain.
This follows [RFC 9218 section 2.1](https://www.rfc-editor.org/rfc/rfc9218.html#section-2.1);
[the migration guide](../compatibility/migration.md#http2-extensible-priority-settings-unreleased)
records the change from the development engine's previous unknown-setting behavior.

Fifteen cases were added, including independent TCP startup/settings and GOAWAY
checks. The pre-change focused selection failed 15 cases; the final expanded
135-case selection passed on Windows and Linux with both target assets hosted
on .NET 10. The full Windows suite passed 2,965 cases with five expected skips
(2,970 total; h2-priority-settings-full log). Both targets build without warnings
or errors; formatting and analyzer guards pass. Exact-head CI remains required.

The preceding dc7daab malware run flagged a casing heuristic in the performance
README. Exact committed bytes, the complete rule and command examples were
reviewed; the finding is restricted to a canonical product name and Markdown
language tag. Full-bundle scanning reproduces it, while the isolated rule does
not. The source/rules were not rewritten to avoid detection. The narrow review
is in `.github/security/performance-readme-yara-review.md`; scanner failures,
other paths/rules and stale acceptances remain fatal. No corresponding GitHub
code-scanning alert exists to dismiss, so the owner's matching-alert dismissal
requirement remains a pre-merge limitation. A fresh complete scan must pass.

Transport-queue scheduling, HTTP/3 write admission/native transport capabilities,
configuration, frozen standards inventory and end-to-end performance/conformance
work remain required; this checkpoint does not complete the engine program.


## HTTP/3 response backpressure and bounded DATA writes

The d40d839 checkpoint passed every applicable PR check, including macOS,
Windows/Linux regression suites, MAUI, security, malware and fuzzing. The engine
program remains incomplete and unreleased.

The response adapter now submits DATA in fragments of at most 256 KiB. QUIC can
complete a single large write after buffering it, so asynchronous completion
alone does not establish backpressure from an unread peer. The adapter retains
the caller buffer and per-stream output gate until all fragments finish or the
transport fails; only the final fragment can carry FIN. This bounds each DATA
submission, not total native transport memory, connection bandwidth or retained
application buffers. Headers and critical control streams are unchanged.

Four real QUIC cases advertise a 64 KiB receive window, leave an 8 MiB response
unread and require a sibling response to finish while the large writer remains
pending. Both repeated application writes and one large application write are
covered. Draining validates exact body bytes; reset terminates the pending writer;
both paths require another healthy request on the same connection. The expanded
pre-change set failed one of four cases, confirming that a single large write can
complete depending on transport buffering/timing. An additional 262,145-byte
HttpClient response checks a short final fragment. All 49 QUIC cases pass on
Windows and Linux (pinned MsQuic 2.6.2). The final full Windows suite passed 2,970 cases with five expected skips
(2,975 total; h3-backpressure-selected-full log). Both targets build without
warnings/errors and formatting/analyzer guards pass. Fresh exact-head CI remains
required.
The earlier 2,974-case full Windows pass applies to the rejected 16 KiB candidate,
not the selected 256 KiB implementation. Initial fixture failures and a build
attempt blocked by the running test apphost remain in local evidence; they are
not reported as production failures or clean-build passes.

Run `python scripts/compare_http3_writes.py` to reproduce the comparison. It builds
identical current core snapshots with only the baseline exchange taken from
commit d40d839, hashes sources/binaries and records SDK information. The default
output is `TestResults/http3-write-comparison`; `--output` must stay under
TestResults. Native QUIC support is required. Each of three alternating process
pairs runs three samples per size/concurrency with tiered compilation disabled.
TLS setup and warmup are excluded, exact body checking is included. Client and
server share one process; CPU and managed allocations cover both, while native
allocation is not counted. Per-sample p50/p95/p99 values and raw results are saved;
these short loopback samples are not production tail-latency guarantees.

The 16 KiB trial reduced sequential throughput by 27% for 1 MiB and 34% for 8 MiB
responses, with increased CPU, and was rejected. A 64 KiB trial recovered much
of that cost. The selected 256 KiB comparison (`h3-write-comparison-selected`)
measured these medians over nine samples per cell on local Windows/.NET 10:

| Response / concurrency | Baseline MiB/s | Candidate MiB/s | Baseline CPU ms/response | Candidate CPU ms/response |
| --- | ---: | ---: | ---: | ---: |
| 1 MiB / 1 | 354.84 | 447.85 | 5.86 | 5.86 |
| 1 MiB / 8 | 427.69 | 544.35 | 5.86 | 5.37 |
| 8 MiB / 1 | 386.43 | 439.85 | 44.92 | 42.48 |
| 8 MiB / 8 | 428.19 | 458.76 | 49.32 | 47.85 |

Managed allocation rose by roughly 5 KiB per 1 MiB response and 30Ã¢â‚¬â€œ37 KiB per
8 MiB response, under 0.3% of combined client/server allocation in those cases.
Small-response results varied: sequential 128-byte throughput fell from 0.92 to
0.73 MiB/s in the selected run, while concurrency eight rose from 4.78 to 5.41.
No universal speedup or resolved small-response performance claim is made.
The shorter exploratory samples were inadequate for fine CPU comparisons;
the checked-in runner uses longer small-response batches. All rejected trial
sources and raw results are retained under TestResults/http-engine.

A queue that immediately starts every ready asynchronous write would only order
submission calls; it would not establish relative bandwidth under congestion.
Do not present bounded writes as completed RFC 9218 scheduling. The .NET 10
transport still needs a maintained strategy for stream priority and datagrams;
no private native handle workaround was introduced. The public priority property
appears in [.NET 11 preview 5](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview5/libraries.md),
which is not evidence of .NET 10 availability or validated scheduling. Native
retention measurements, small-response investigation, mixed-workload performance,
transport scheduling and the wider completion checklist remain required.

The new comparison runner triggered the same pinned runtime-compilation heuristic
previously reviewed for the HTTP/2 fixture. The complete rule and fixed reflection
calls were reviewed; canonical hashes and a narrow rule/path acceptance are in
`.github/security/http3-benchmark-yara-review.md`. Source/rules were not rewritten
to evade detection. No matching GitHub alert exists to dismiss, leaving the
owner's pre-merge requirement outstanding; fresh CI scans remain necessary.


## Explicit HTTP/3 listener drain

`WebServer.DrainAsync(timeout, cancellationToken)` adds an opt-in shutdown path
for the modern HTTP/3 listener without changing `IHttpListener` or the existing
immediate RunAsync-cancellation, Stop and disposal paths. Other modes explicitly
throw NotSupportedException. The method exists in both target assets; it does
not add HTTP/3 transport support to the legacy asset.

A session separates accept cancellation, connection abort and drain signals.
One shared shutdown task closes every listening endpoint, sends GOAWAY through
the existing connection driver, waits for accepted response ownership and closes
remaining connections at the first caller's deadline. Caller cancellation aborts
remaining transport work; cancellation is reported after cleanup. Concurrent
calls share the first deadline. The request loop recognizes an intentional drain
completion without converting it into a fatal listener error. Applications still
need to honor context cancellation, and the host disposes the server after its
run task completes to release module/session resources. The HTTP/3 guide documents
the public contract and explicitly preserves the one-run WebServer lifecycle.

Sixteen new cases cover accepted responses, concurrent drain requests, deadline,
caller cancellation, RunAsync cancellation, direct Stop/disposal, idle listeners,
endpoint reuse, invalid timers, pre-cancellation and unsupported modes. Two
independent raw QUIC connections observe GOAWAY, then receive H3_REQUEST_REJECTED
for later streams while their accepted responses finish. A fresh client also
fails to enter the listener during drain. All 92 focused listener/QUIC/API cases
pass on Windows and pinned Linux MsQuic 2.6.2; all six portable API cases pass
with the actual netstandard asset on both hosts, using .NET 10 as the test host.
A wire regression failed before correction when an unused external timeout
replaced the existing application-triggered close policy. Application-triggered
drains now retain their 30-second deadline; the listener separately bounds its
own drain through the abort token. The earlier 2,990-case full Windows run passed
before that correction. Both targets build cleanly and formatting/analyzer checks
pass. The final-source full Windows suite passed 2,986 cases with five expected
skips (2,991 total; h3-drain-policy-full log). Pinned YARA scans of all changed
files found no matches. Fresh exact-head CI remains required.

Every applicable check on the preceding add4e0f checkpoint passed. This increment
does not complete other transports' graceful shutdown, combined hosting,
discovery, priority scheduling, datagrams, the frozen standards inventory or the
engine program's remaining performance/conformance work.

### HTTP/1.1 CONNECT rejection audit

Three real TCP cases now cover authority-form CONNECT with DNS, IPv4 and bracketed
IPv6 targets, followed in the same write by a complete HTTP request. Current
source closes with no response and dispatches neither request; a fresh healthy
connection reaches the same pending accept and completes successfully. All 41
ModernHttpEngineTest cases passed on Windows and pinned Linux. The first fixture run incorrectly
expected a 400 response (three failures); the observed empty response is recorded
explicitly rather than claimed as a useful protocol-level rejection.

This establishes connection isolation for the current unsupported authority-form
path only. It does not implement CONNECT tunneling or prove application-level
rejection/authentication handling. Proper authority parsing and explicit rejection
responses remain development requirements, with RFC 9931 connection closure to be
retained when those paths are implemented. Evidence: local ignored
`connect-audit-test.log` and `connect-audit-focused.log` under
`TestResults/http-engine`. No production behavior changed in this checkpoint.

The final Windows suite passed: 2,994 total, 2,989 passed and five expected skips
(`connect-audit-full.log`). Both-target build, whitespace validation, suppression
and syntax guards passed. The pinned YARA scan found no matches in the changed
files. Cross-platform CI on the committed head remains required.

### QUERY routing increment

`HttpVerbs.Query` is appended without changing existing enum values. The managed
parser, shared multiplexed request adapter and testing asset recognize exact
QUERY; the native adapter retains existing method behavior while requiring exact
case for the new method. Nine tests cover route/body delivery, POST/lowercase
non-matches, an application-provided Accept-Query field and real HTTP/1 (both
backends), HTTP/2 and HTTP/3 requests. All nine passed on Windows and pinned Linux with QUIC required. The initial
in-process QUERY route failed before implementation. A client-based wire fixture
normalized method case; raw TCP replaced it and reproduced the native adapter's
case-insensitive mapping before correction.

This is routing support, not complete RFC 10008 support. Handlers must preserve
safe/idempotent behavior and validate content against their supported media types.
Automatic missing Content-Type rejection, discovery policy, conditional/range
semantics and guidance remain pending. No automatic caching or query language is
introduced. Evidence is under local `TestResults/http-engine/query-*.log`.

Final QUERY validation: 3,003 Windows cases, 2,998 passed and five expected skips
(`query-full.log`). The actual .NET Standard core/testing assets passed eight
applicable cases on Windows and Linux, hosted on .NET 10; this is not validation
on every legacy runtime. Both targets build, formatting and repository source
guards pass, and pinned YARA reports no changed-file matches. Exact-head CI is
still required before integration.

### QUERY missing media-type validation

The shared server dispatch boundary now rejects exact QUERY requests whose
Content-Type is absent, empty or whitespace-only, through the existing HTTP
exception path before modules execute. Three in-process cases failed before the
change. Nineteen focused cases now pass on Windows, including real HTTP/1 managed
and Microsoft listeners and exact HTTP/2 and HTTP/3 responses. POST and lowercase
custom methods retain their behavior. This supersedes the previous checkpoint's
pending missing-header validation item. Content/type consistency, resource-specific
supported formats, discovery and remaining QUERY semantics still need completion.

Evidence: local `query-media-before.log` (three reproduced failures),
`query-media-wire.log` (19 passes) and both-target build logs. The change adds ten
test cases; the full discovery minimum is 3,013. The final Windows suite passed
with 3,008 successes and five expected skips (`query-media-full.log`). All 19
focused cases passed on pinned Linux with QUIC required, and all 17 applicable
cases passed against the actual .NET Standard asset on Windows/Linux .NET 10
hosts. Both-target build, formatting, source guards and changed-file pinned YARA
scans passed. Exact-head repository CI remains required.

### QUERY Content-Type syntax validation

The shared dispatch check now uses the existing framework media-type parser to
reject malformed QUERY Content-Type values with 400. Four real HTTP/1 wire cases
failed before this correction across managed and Microsoft backends. In-process
cases already passed because the testing client's typed headers expose invalid
values as missing; this was insufficient evidence for wire behavior. Valid vendor
media types and quoted parameter values remain accepted. Resource-specific media
type support and content consistency remain the handler's responsibility.

All 29 focused QUERY cases passed on Windows after correction. Evidence is local
`query-syntax-wire-before.log` and `query-syntax-focused.log`. Ten additional cases
raise discovery to 3,023. Final Windows validation passed: 3,018 successes and
five expected skips (`query-syntax-full.log`). All 29 focused cases passed on
pinned Linux with QUIC required; the actual .NET Standard asset passed 27 applicable
cases on Windows and Linux .NET 10 hosts. Both-target build, formatting, repository
source guards and changed-file pinned YARA scans passed. Exact-head CI remains
required. QUERY result preconditions/ranges must be evaluated against the selected
query result, not by treating the method as a static-file GET.

### Bounded HTTP/1 parser rejection responses

The managed parser now sends a fixed 400 response with zero content and explicit
connection closure on parsing/initialization failure. The request timer stays
active through the asynchronous write, and a finally block forces closure even
when writing fails. No parser diagnostics are reflected. Successful dispatch and
prefix-routing rejection retain their paths. The earlier CONNECT audit's silent
close is superseded by this response; CONNECT tunneling remains unimplemented.

Thirteen strengthened raw-wire cases failed before implementation. All 44 focused
HTTP/1 engine cases now pass on Windows, including three new certificate-pinned
TLS CONNECT rejection cases and healthy fresh connections. Ambiguous framing
cases also include an optimistic successor that must not dispatch. Evidence is
local `h1-rejection-before.log` and `h1-rejection-tls.log`. Final full and
cross-platform validation completed. The first full run found two existing
negative-length fixtures that required silent EOF; both now verify the complete
400 response and closure. The repeated full Windows suite passed: 3,026 total,
3,021 successes and five expected skips (`h1-rejection-full-final.log`). All 46
focused cases passed on Windows; the 44 engine cases and two updated boundary
cases also passed on Linux and with the actual .NET Standard asset on both hosts
under .NET 10. Build, formatting, source guards and pinned changed-file YARA scans
passed. Exact-head repository checks remain required.

The RFC Editor errata feed was captured during this increment under local
`standards-2026-10-08/errata.json`, with URL, timestamp and SHA-256 recorded in
`errata-source.json`. The inventoried RFC selection contains 146 records: 44
verified, 35 held for document update, 29 reported and 38 rejected. These categories
must not be conflated. Mapping the verified corrections to code/tests remains
open, including HTTP/3 path grammar (7014), GOAWAY error scope (7780), QPACK dynamic
index counting (8410) and Structured Field display strings (8869). TLS-provider
requirements and obsoleted specifications require separate applicability review.

### Verified multiplexed-protocol errata mapping

The following mappings use the captured RFC Editor feed; they do not complete
the full October 2026 inventory or all 44 verified errata.

| Erratum | Implementation and evidence |
| --- | --- |
| [9114 / 7014](https://www.rfc-editor.org/errata/eid7014) | The shared request-header parser accepts HTTP absolute-path. Three real HTTP/3 cases preserve `//`, `///items` and `//items?filter=one` in RawTarget without changing the authority: `Http3ListenerTest.LeadingEmptyPathSegmentsReachApplication`. |
| [9114 / 7780](https://www.rfc-editor.org/errata/eid7780) | Existing `Http3WireTest.ControlAndReservedFramesAreConnectionErrorsOnRequestStreams(7)` verifies GOAWAY on a request stream raises connection-level H3_FRAME_UNEXPECTED. This test covers the server receive direction corrected by the erratum. |
| [9651 / 8869](https://www.rfc-editor.org/errata/eid8869) | The priority parser recognizes display strings, including the existing escaped-byte fixture in `HttpPriorityTest`; the corrected item-category list does not require a production change here. |
| [9204 / 8410](https://www.rfc-editor.org/errata/eid8410) | Dynamic response encoding remains pending. `QpackEncoder` currently emits zero Required Insert Count and Base and uses static/literal fields. The dynamic encoder must derive Required Insert Count from the highest referenced absolute index plus one, including dynamic-name references; no completed implementation is claimed. |

All 79 selected path/control/priority cases passed on Windows and pinned Linux
with QUIC required (`h3-errata-focused.log`, `h3-errata-linux.log`). Three new
real-wire cases raise discovery to 3,029. This increment changes tests and audit
documentation, not production behavior. The preceding production increment's full
Windows suite passed with 3,026 cases; new exact-head full CI remains required.

### HTTP/1 request-target syntax increment

Four initial raw-wire cases exposed rejected OPTIONS asterisk-form and accepted
literal fragments/backslashes. A second set reproduced three silently repaired
percent escapes. The parser now preserves RawTarget `*` while using the root URI
for valid OPTIONS dispatch, and rejects these malformed target characters before
URI normalization. Valid encoded hash/percent characters remain supported. All 67
focused target/URL-compatibility/HTTP1 cases passed on Windows. Twelve new cases
raise discovery to 3,041. Evidence: local `h1-target-before.log`,
`h1-target-percent-before.log` and `h1-target-final-focused.log`.

This does not finish request-target conformance. Absolute-form authority precedence,
authority-form CONNECT, server-wide OPTIONS policy across prefix registrations,
and the complete target grammar still require work. Existing transport/local-port
URL behavior is retained in this increment. All 67 focused cases also passed on
pinned Linux and against the actual .NET Standard library asset on Windows and
Linux (.NET 10 hosts). The full Windows suite passed: 3,041 total, 3,036 passed,
five expected skips, zero failures (`h1-target-full.log`). Both-target build,
formatting, source guards and the pinned YARA scan passed. Exact-head CI, including
macOS and mobile hosts, remains required.

### HTTP/1 Host authority normalization boundary

Eleven new raw TCP cases cover non-digit ports, multiple port separators, userinfo,
path/query/fragment delimiters and valid numeric/empty ports. Eight failed before
validation was added (`h1-host-before.log`). All 78 focused request-target, URL
compatibility and managed-engine cases pass after correction. Validation occurs
before stripping the supplied port, preserving the established local-port URL
contract. RFC 3986 section 3.2 permits an empty port; this case remains accepted.
This increment does not complete absolute-form authority precedence or the wider
authority/URI grammar audit. All 78 focused cases also passed on pinned Linux and
against the actual .NET Standard asset on both hosts under .NET 10. The full
Windows suite passed: 3,052 total, 3,047 passed and five expected skips
(`h1-host-full.log`). Both-target build, formatting, source guards and changed-file
YARA scans passed. Exact-head CI remains required. Cross-protocol empty-port
handling was identified for the following shared-validator increment.

References: [HTTP/1 Host rejection](https://www.rfc-editor.org/rfc/rfc9112.html#section-3.2)
and [URI port syntax](https://www.rfc-editor.org/rfc/rfc3986.html#section-3.2.3).

### Shared HTTP/2 and HTTP/3 empty authority ports

Ten new parser cases reproduce rejection of valid empty ports for ordinary and
extended CONNECT requests. The shared authority validator now permits these while
retaining the nonempty explicit-port requirement for classic CONNECT. Coverage
includes HTTP/HTTPS defaults, DNS/bracketed IPv6 authorities, Host fallback and
Host equivalence to the default numeric port. One older invalid-authority case
was corrected, giving a net discovery increase of nine (3,061 total).

All 141 selected parser, HTTP/2 wire and HTTP/3 listener cases passed on Windows
with no skips (`empty-port-focused.log` plus `empty-port-h2-wire.log`); all ten new cases failed before the
correction (`empty-port-before.log`). These new cases directly exercise the shared
parser; the selected wire tests are existing integration coverage, not new
empty-port wire reproductions. The initial filter used a source filename rather
than its partial-class name and omitted the 45 HTTP/2 wire cases; the corrected
filter ran those cases separately on Windows and included all 141 on pinned Linux
with QUIC required. The actual .NET Standard asset passed 100 applicable cases on
both hosts under .NET 10. Full Windows regression passed: 3,061 total, 3,056
passed, five expected skips and zero failures (`empty-port-full.log`). Both-target
build, formatting and source guards passed. The YARA scan reported only the
previously accepted request-header fixture heuristic, re-reviewed in
`.github/security/http2-fixture-yara-review.md`.

Prior-head CI run 37840025467 failed macOS compatibility while the unchanged
published upstream fixture awaited a WebSocket receive (ten-second cancellation),
and Android smoke failed extracting the downloaded emulator archive. Logs and
upstream artifacts are retained locally under `ci-8e9f870-*`. Neither is claimed
fixed by the authority change; unchanged job reruns were requested. Exact-head
checks remain mandatory. GitHub rejected both rerun requests while the parent
run was still active; no retry success is claimed.

### Multiplexed path/query grammar

Twelve raw HTTP/2 cases exercise malformed percent escapes and excluded URI
characters, preserved encoded percent, the permitted punctuation set and leading
`//`. Nine malformed cases dispatched before correction; the revised fixture
reports unexpected application DATA directly (`path-grammar-before-data.log`),
with the earlier timeout-based failures retained (`path-grammar-before.log`).
The shared HTTP/2/3 parser now validates RFC 3986 path/query characters and percent
escapes. Each wire case verifies stream-local rejection, dispatch count and a
healthy subsequent stream. The scan allocates no per-character strings.

All 153 selected parser and integration cases passed on Windows and pinned Linux
with QUIC required on Linux. The actual .NET Standard asset passed 112 applicable
cases on each host under .NET 10. A candidate restore initially preserved an older
source timestamp, allowing incremental build to reuse baseline binaries; the
failed focused run and interrupted full run are retained. After invalidating that
timestamp, the rebuilt candidate passed (`path-grammar-focused-final.log`,
`path-grammar-linux.log`, `path-grammar-legacy*.log`). The rebuilt full Windows
suite passed: 3,073 total, 3,068 passed and five expected skips
(`path-grammar-full-final.log`). Both-target build, formatting, source guards
and changed-file pinned YARA scans passed. Exact-head CI remains required.
This is new HTTP/2 wire coverage;
HTTP/3 uses the same parser but new malformed-path QUIC wire cases remain pending.
HTTP/1 origin/absolute-target grammar reconciliation also remains pending.

Path grammar reference: [RFC 3986 sections 3.3 and 3.4](https://www.rfc-editor.org/rfc/rfc3986.html#section-3.3).

### Raw QUIC malformed-path coverage

Nine independently encoded QPACK requests cover the malformed paths added to
HTTP/2 wire coverage. Each asserts a QUIC stream abort with H3_MESSAGE_ERROR
(0x10e), then sends a healthy request on the same connection and reads its
response. This closes the malformed-path QUIC wire coverage gap from the prior
increment without changing production code. All 59 selected QUIC cases passed on
Windows and pinned Linux with QUIC required and no skips
(`path-quic-focused.log`, `path-quic-linux.log`). Both-target build, formatting,
source guards and changed-file pinned YARA scans passed. Test discovery rises to 3,082. The preceding production increment
passed the full Windows suite with 3,073 cases; no new full local run is claimed
for this test-only increment. Exact-head CI remains required.

### HTTP/1 path/query grammar consistency

Six malformed character cases in both origin-form and absolute-form reproduced
acceptance before correction (`h1-grammar-before.log`, twelve failures). Four
valid encoded/punctuation cases also run over real TCP. HTTP/1 now locates the raw
path/query component and invokes the shared grammar validator before constructing
a URI, avoiding normalization hiding malformed syntax. The validator accepts a
start offset without creating a substring. Existing transport/local-port URL
semantics remain intact; authority precedence and the full absolute-target audit
remain open. All 94 focused cases passed locally (`h1-grammar-focused.log`).
All 94 focused cases also passed on pinned Linux and against the actual .NET
Standard library asset on Windows/Linux under .NET 10. The full Windows suite
passed: 3,098 total, 3,093 passed, five expected skips and zero failures
(`h1-grammar-full.log`). Both-target build, formatting, source guards and
changed-file pinned YARA scans passed. Exact-head CI remains required.
Discovery rises to 3,098.

### HTTP/1 absolute-form host precedence

Three raw TCP cases with conflicting but syntactically valid Host headers failed
before correction (`absolute-host-before.log`). URL construction and prefix
routing now use the absolute target host, while preserving the original header.
A fourth case confirms that a matching local Host cannot redirect an unregistered
absolute target into the listener. All 98 focused cases pass locally. The original
Host syntax checks and transport/local-port URL contract remain intact.

This implements the host precedence rule in
[RFC 9112 section 3.2.2](https://www.rfc-editor.org/rfc/rfc9112.html#section-3.2.2).
It does not complete absolute-URI scheme/port semantics, userinfo handling,
non-HTTP target forms or the entire authority grammar. Those items remain open.
All 98 focused cases also passed on pinned Linux and against the actual .NET
Standard asset on both hosts under .NET 10. The full Windows suite passed with
3,102 total, 3,097 passed and five expected skips (`absolute-host-full.log`).
Both-target build, formatting, source guards and changed-file pinned YARA scans
passed. Exact-head CI remains required. Discovery rises to 3,102.

CI on preceding head d8869a0 exposed a parity-fixture request containing raw `[]`
in its query. The fixture now sends `%5B%5D`, preserving the decoded array-key
coverage and unchanged expected results. Malformed character rejection remains
covered by separate wire regressions; no comparator contract was relaxed.
Initial three-platform failures in run 37843224071 are retained under
`ci-d8869a0-*`. The corrected Windows audit passed 207 cases / 414 upstream/Neo
comparisons with zero errors (`absolute-host-parity.log`), including the unchanged
comparator contract and negative checks. Unix parity and all exact-head CI checks
remain required.

### HTTP/1 absolute-target userinfo rejection

Five raw TCP cases reproduced accepted userinfo in absolute targets, including
HTTP/HTTPS scheme text, a colon-bearing component, empty userinfo and an escaped
at-sign within userinfo (`userinfo-before.log`). The authority scan now rejects
the raw delimiter before URI parsing, so empty userinfo cannot disappear during
normalization. Two valid path/query at-sign cases remain accepted. All 105 focused
cases passed locally (`userinfo-focused.log`).

This follows [RFC 9110 section 4.2.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-4.2.4)
and the approved strict malformed-input policy. Seven new cases raise discovery
to 3,109. All 105 focused cases also passed on pinned Linux and against the
actual .NET Standard asset on both hosts under .NET 10. The full Windows suite
passed: 3,109 total, 3,104 passed and five expected skips (`userinfo-full.log`).
Both-target build, formatting, source guards and changed-file pinned YARA scans
passed. Exact-head CI remains required. Scheme/port semantics and remaining
target-form handling remain open.

### HTTP/1 Host port range

Three raw TCP cases reproduced acceptance of ports above 65535, including integer
overflow-sized values (`host-port-before.log`). Host validation now bounds numeric
ports before stripping them. The accumulator is checked on each digit, so it
cannot overflow; leading zeros, empty ports and the 0/65535 boundaries remain
accepted. Six new cases raise discovery to 3,115. All 111 focused cases passed
locally (`host-port-focused.log`) and on pinned Linux, plus the actual .NET
Standard asset on Windows/Linux under .NET 10. Full Windows validation passed:
3,115 total, 3,110 passed, five expected skips and zero failures
(`host-port-full.log`). Both-target build, formatting, source guards and changed-file
pinned YARA scans passed. Exact-head CI remains required.
This does not change the existing local-port URL contract or finish absolute-target
scheme/port semantics.


### Combined TCP/QUIC hosting: transport boundary validation (2026-10-08)

Four independent-client cases now bind the existing TCP and QUIC listeners to
one HTTPS authority and port. Each case sends 16 requests per transport
concurrently, checks the negotiated protocol and response owner, stops either
listener, and uses a fresh client connection to verify that the surviving
listener still accepts requests. Both HTTP/1.1 and HTTP/2 are paired with HTTP/3.
All four cases passed on Windows and the pinned Linux SDK/MsQuic container,
with QUIC required on Linux and no skipped cases. Both library targets built;
changed-file formatting and the source guards passed. Evidence is under
`TestResults/http-engine/shared-port-*`. The discovery floor is now 3,119;
this increment did not rerun the full suite and does not claim that total passed.

This establishes transport coexistence, not a combined listener implementation.
The source inspection identifies the following integration requirements:

- The TCP listener owns queued contexts and connection shutdown. HTTP/3 dispatch
  waits for `MultiplexedContext.Completion`, and exchange cancellation closes
  contexts that remain queued. The shared host must preserve these owners rather
  than flush an unhandled response as a substitute for aborting it.
- Each transport must retain its pending accept across delivery from the other
  transport. Creating two accepts per call and discarding the loser can consume
  a context that is never dispatched. A canceled caller must not orphan either
  pending accept. Admission and ownership must be bounded and tested with both
  accepts completing simultaneously.
- Startup must publish the combined session only after all selected bindings
  succeed. Failure must roll back only resources owned by that session. Tests
  must occupy TCP and UDP separately and verify rollback and later restart.
- HTTP/3 already supports graceful draining; the current TCP listener does not.
  A combined drain cannot silently translate TCP draining into immediate Stop.
  TCP connection admission, HTTP/1 keep-alive completion and HTTP/2 GOAWAY must
  participate in one deadline before the combined host advertises drain support.
- Lifecycle tests must cover stop, disposal, restart, individual accept failure,
  peer cancellation, mixed-protocol load and resource recovery. The new tests
  above cover independent transport shutdown only.

Default selection and legacy deprecation remain incomplete. Combining the
existing transports alone would not establish the promised replacement of the
Mono-derived HTTP/1 implementation. Protocol discovery, optional QUIC policy,
platform behavior and measured shared-dispatch overhead also remain open.


### Combined listener implementation (2026-10-08)

`HttpListenerMode.EmbedIOCombined` now hosts the existing TCP engine and QUIC
engine behind one `WebServer` on the same HTTPS prefixes. Both bindings are
required, with transactional startup and fresh transport instances on restart.
The mode requires the .NET 10 asset, native QUIC and a private-key certificate.
It does not silently downgrade when QUIC is unavailable. Existing enum values
and default selection remain unchanged.

One accept pump per transport writes into a bounded 256-context channel. A pump
may retain one further context while waiting for channel capacity. Consumer
cancellation leaves both pumps running; stop cancels them, aborts both transport
owners, waits for the pumps and discards canceled queued contexts. It does not
close undispatched responses as successful empty replies. Unexpected recoverable
accept failure initiates shutdown of both transports. Further deterministic
failure-injection, full-queue saturation, competing-prefix ownership and
concurrent lifecycle stress remain required for this integration.

Eleven new cases passed on Windows and the pinned Linux SDK/MsQuic container:
all three negotiated protocol versions through one server, 288 concurrent mixed
requests without missing responses, canceled-consumer recovery on TCP and QUIC,
pending-accept stop/disposal, restart, occupied TCP/UDP startup rollback, and
aborting undispatched HTTP/1.1, HTTP/2 and HTTP/3 responses. The first Linux
rollback run used IP-literal HTTPS URLs and timed out after restart; changing the
fixture to `localhost` addresses the already documented client limitation, not a
new transport fix. That failed log is retained as `combined-linux-ip-literal.log`.
The final focused logs are `combined-focused.log` and `combined-linux.log` under
`TestResults/http-engine`.

Both target assets build without warnings, changed-source formatting and the
source guards pass, and the pinned YARA scan reports no matches for the new
listener and fixture. Combined mode currently rejects graceful drain because
TCP drain is unfinished. Discovery, measured dispatch overhead, default
replacement and legacy deprecation remain open; this combined host still uses
the existing TCP implementation. The HTTP/3 guide documents these restrictions.

The final full Windows suite passed 3,125 tests with five expected skips
(3,130 total, zero failures) in `combined-full.log`. The CI discovery floor
is 3,130. macOS and final-head CI validation remain pending.


### HTTP/2 external drain primitive (2026-10-08)

The HTTP/2 dispatcher now exposes its existing GOAWAY operation internally for
future listener coordination. Concurrent callers receive the same task while
GOAWAY is pending. After sending it, an empty exchange set cancels the read loop,
so an idle connection does not wait indefinitely for a client request or close.
Dispatcher cleanup also observes any pending drain write before disposing its
cancellation source. The existing response-triggered drain uses this same path.
The returned task represents GOAWAY completion, not completion of all accepted
application responses; the dispatcher run task remains the connection lifetime.

Five raw TCP cases check idle close, concurrent calls, an explicitly gated pending
GOAWAY write, the last accepted stream identifier, preserved accepted output and
REFUSED_STREAM for a later request. The two idle cases failed before the change;
the gated concurrent-write case separately failed against the prior dispatcher.
An initial corrected-production run exposed unread SETTINGS acknowledgment bytes
in the idle fixture, which could turn its socket close into a reset. The fixture
now completes a PING round trip before testing idle shutdown. Before/failure logs
are retained under `TestResults/http-engine/h2-drain-*`.

All 62 HTTP/2 interoperability cases passed on Windows and pinned Linux, both
with the .NET 10 library and with the actual .NET Standard library asset loaded
by the .NET 10 test host. This is not old-runtime validation. TCP listener-wide
and combined drain remain unfinished: endpoint admission and routing ownership,
HTTP/1 keep-alive completion, shared deadlines and connection completion tracking
must still be connected and tested.

A full Windows run exposed socket bind failures in the newly introduced combined
fixtures. Their UDP-only ephemeral port selection did not check TCP availability;
the rollback fixture also selected only the deliberately occupied transport.
The updated helper reserves TCP and UDP on both localhost address families while
choosing a candidate, then releases those probes before the actual test. Only
candidate selection retries; listener startup and assertions are not retried.
The OS-level gap between probe release and bind still exists. No production bind
failure is silently retried or hidden.

CI on the preceding combined-host commit `f6f69b4` passed Windows but failed the
Ubuntu suite in run 37847220707, job 113550991817, with an unhandled runtime
`System.Net.HttpListenerResponse.FormatHeaders` NullReferenceException during
`System.Net.HttpConnection.OnRead` cleanup. The log does not identify the active
test. It is retained as `combined-ci-linux-failed.log`; no repair or successful
retry is claimed. This separate native-backend failure remains to investigate.


The downloaded CI TRX artifacts provide additional evidence: both Ubuntu and
macOS failed `CombinedListenerDispatchesEveryProtocol(96)` at the existing
15-second client deadline. This mixed-load failure is unresolved despite focused
local Windows/Linux passes; neither its concurrency nor its deadline has been
weakened. Ubuntu subsequently crashed in the native runtime, while macOS ended
with only 2,775 of the required 3,130 cases reported. The artifacts are retained
under `combined-ci-linux-artifact` and `combined-ci-macos-artifact`. The latest
completed test entries do not establish which operation triggered the Ubuntu
background exception. Coverage/constrained-runner reproduction and per-protocol
diagnostics are the next investigation for the mixed-load timeout.


The initial all-transport probe still failed during a second full run: successive
TCP port-0 allocations can land inside long UDP exclusion ranges. A separate
Windows socket probe reproduced 60 exclusive UDP bind failures in 64 distinct
TCP-assigned candidates; `netsh` confirmed UDP exclusion ranges overlapping the
observed ports. Evidence is in `port-exclusion-observation.txt` and
`udp-port-exclusions.txt`. The fixture now samples candidates from 10000-29999
and checks both transports/address families before using one. The second failed
full run remains `h2-drain-full-final.log`; no test result is overwritten and no
network settings were changed.


### Asynchronous short responses under mixed-protocol load (2026-10-08)

The preceding CI mixed-load timeout reproduced locally with two logical
processors in the pinned Linux container. The command enabled Coverlet, but its
summary was N/A in the copied Windows-build container; this is constrained-host
reproduction, not equivalent CI coverage evidence. The 96-requests-per-protocol
case exceeded the unchanged 15-second client deadline. Changing only context
closure to await asynchronous response completion still failed. Adding an
explicit asynchronous writer flush made both mixed-load cases pass in 1.101
seconds total in that run. These are fixture timings, not throughput benchmarks
or a claimed general speedup. Logs preserve the failing baseline, failed
close-only candidate and passing flush candidate as `combined-coverage-linux.log`,
`combined-coverage-async-close-linux.log` and
`combined-coverage-async-flush-linux.log`.

`SendStringAsync` previously buffered short text until synchronous writer
disposal, which could block the calling worker on multiplexed network I/O.
A deterministic gated-header-write case now verifies that the helper returns a
pending task while the network write remains blocked; the prior helper fails
that case (`short-string-before.log`). The helper now explicitly flushes
asynchronously on both assets and disposes the writer asynchronously on .NET 10.
The multiplexed output stream supplies asynchronous disposal for that modern
path. The server pipeline and HTTP/2/HTTP/3 adapter cleanup also await context
closure, including response completion and close callbacks. The synchronous
public close API remains available, with once-only callback behavior retained.
Legacy-target disposal remains synchronous because that target lacks the modern
asynchronous disposal API; other response helpers and compressed legacy paths
still require performance review.

All 119 selected HTTP/2 interoperability and HTTP/3 listener cases passed on
Windows and on pinned Linux with `DOTNET_PROCESSOR_COUNT=2`, QUIC required and no
skips. All 63 HTTP/2 interoperability cases passed with the actual .NET Standard
library on Windows and Linux .NET 10 hosts. The unchanged compatibility audit
passed 207 cases / 414 upstream-versus-Neo comparisons with zero errors. Both
assets build without warnings. These results do not clear the separate native
Linux background exception or prove macOS CI recovery.

The final combined-source Windows suite passed 3,131 tests with five expected
skips (3,136 total, zero failures) in `async-context-full.log`. Formatting and
both source guards passed; the pinned YARA scan reported no matches for all
changed C# files. The discovery floor is 3,136. Final-head cross-platform CI
remains required.


### TCP connection drain checkpoint (2026-10-08)

The managed TCP connection now has an internal drain operation with lazy
completion signaling. It waits for transport resource cleanup, shares the
pending completion between callers and records cleanup failures. Ordinary
connections do not allocate a completion source unless drain is requested.
HTTP/1 retains its accepted response, suppresses keep-alive restart and emits
`Connection: close` when drain precedes response headers. Admission checks under
the connection lock prevent a request from being newly queued after an idle
connection starts draining. Already-buffered pipelined successors are not
started. HTTP/2 borrows the live dispatcher's drain operation and waits for
connection closure after its accepted streams finish. An incomplete protocol
handshake has no accepted work to preserve and can be closed immediately.

Seven new plaintext TCP cases cover HTTP/1 drain before/after headers with and
without a pipelined successor, idle keep-alive drain, and HTTP/2 accepted output
or explicit listener abort. An initial empty-response fixture omitted the flush
normally performed by WebServer and failed waiting for its response; the fixture
now flushes headers before closing. That failed log remains `tcp-drain-focused.log`.
The corrected cases and broader selected tests pass: 126 cases on Windows and
pinned Linux with two logical processors, plus 70 cases with the actual
.NET Standard library on Windows/Linux .NET 10 hosts. Both assets build without
warnings; formatting, source guards and changed-file pinned YARA scanning pass.
Logs are under `TestResults/http-engine/tcp-drain-*`.

This is a connection primitive, not listener-wide or combined drain support.
Endpoint admission/routing ownership still needs coordination: disposing the
current endpoint aborts its unregistered connections, and HTTP/2 connections can
remain in that endpoint set after routing. The host must detach those ownership
paths correctly before removing the last prefix. Shared endpoints, TLS,
incomplete/unread bodies, slow peers, common deadlines and stop/drain races need
integration tests before advertising TCP or combined graceful drain. Existing
WebServer graceful drain support remains HTTP/3-only.

CI for preceding commit `186c38f` passed Windows, Ubuntu and macOS test jobs in
run 37849674374. This confirms that run of the mixed-load correction, not a repair
of the earlier native runtime crash. Android lifecycle setup failed while
extracting an invalid emulator ZIP; the Mac Catalyst HTTPS app did not become
ready and its artifact contains only the package lock. Those failed-job logs
are retained as `async-context-ci-android.log` and
`async-context-ci-maccatalyst.log`. No successful retry or cause of the app
startup failure is claimed; final-head platform validation remains required.

The final full Windows suite passed 3,138 tests with five expected skips
(3,143 total, zero failures) in `tcp-drain-full.log`. Hot-path, listener-queue,
cold-start and listener allocation gates passed against the rebuilt current
library. These budgets do not establish end-to-end performance or combined
drain completion. The CI discovery floor is 3,143.


### Endpoint admission boundary before listener-wide drain

Endpoint admission can now stop independently of accepted transport disposal.
The endpoint manager checks ownership under its registration lock and stops only
sockets whose exact-host and wildcard routes all belong to the draining listener.
Shared sockets remain available to sibling listeners. Routes and accepted HTTP/1.1
and HTTP/2 transports remain alive until their owners drain or abort them. The
Windows asynchronous accept and macOS blocking accept paths both observe the
admission-stop state; sockets caught before registration are discarded.

A stopped endpoint rejects new prefix registration until its original owner
releases it, preventing successful registration on a closed listening socket.
Seven regression cases cover accepted HTTP/1.1 and HTTP/2 responses, explicit
abort, repeat admission stop, sibling routing before and after the first listener
stops, and failed registration followed by successful replacement after cleanup.
The focused set passed 77 cases on pinned Linux with two logical processors and
77 cases against the actual .NET Standard asset on Windows and Linux .NET 10
hosts. Both assets build without warnings; changed-file formatting, source guards
and pinned YARA scanning pass. Evidence is under
`TestResults/http-engine/endpoint-admission-*`.

This admission primitive is not yet connected to public listener-wide drain.
Shared-endpoint request admission, accepted HTTP/2 streams awaiting routing,
incomplete handshakes, deadline ownership and combined-host coordination remain
open. Public WebServer graceful drain is still HTTP/3-only; the new engine is not
yet the default.

The full Windows suite passed 3,145 tests with five expected skips (3,150 total,
zero failures). The rebuilt cold-start allocation gate also passed. The CI
discovery floor is now 3,150. Final-head CI and macOS admission-path validation
remain required; these local results do not establish completed host draining.
