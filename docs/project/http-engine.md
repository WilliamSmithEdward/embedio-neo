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

The HTTP/1 continue handshake is written asynchronously by the connection after
request initialization. HTTP/1.0 ignores the expectation and bodyless requests
omit the interim response; expectation-list and early-final-response completion
remain outstanding.

## Incremental HTTP/1 head reader

`Http1HeadReader` separates incremental request-head framing from connection
ownership and request semantics. It consumes through the terminating empty line,
leaves body and pipelined bytes untouched, and enforces the existing 32,768-byte
head budget before constructing complete-line strings. Fragmented lines retain
only partial-line state; failures require an explicit reset. This is one component
of the replacement, not completion of the inherited transport replacement.

Direct tests cover Latin-1 bytes, CRLF splits, invalid line endings, reset and
budget boundaries with a large unconsumed body. Three recorded random seeds
produce 6,000 insertion/deletion/replacement mutations. Each input is compared
under contiguous, one-byte, two-byte and another fragmented delivery. This
metamorphic check detects fragmentation-dependent outcomes; it is not an
independent semantics oracle or a coverage-guided fuzz campaign.

The parser/context microbenchmark measures buffered pipeline handoff, including
reflection observations, without sockets or URI finalization. The initial local
before/after allocation result is unchanged. Timing samples do not establish a
performance improvement; end-to-end comparisons remain a completion requirement.

## Whole-engine fuzzing gate

Fuzz individual codecs and parsers during implementation, then run integrated
stateful campaigns after protocol and lifecycle integration. Include HTTP/1 head
and body framing, HTTP/2 frames and HPACK, HTTP/3 streams and QPACK, WebSocket
handshakes/frames/compression, upgrades, multiplexing, disconnects, cancellation
and shutdown. Exercise resource limits, stalled peers and compression expansion.
Use independent implementations for differential evidence and resolve differences
against the applicable specification rather than assuming either implementation
is correct. Preserve reproducer inputs, seeds, source revisions, platform/runtime
versions and campaign settings; minimize defects into regression cases.

The standalone campaign can be reproduced with:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release -- --http1-head 20261008 100000
```

It compares mutated heads against an independently written batch framing oracle,
including near-limit heads, Latin-1, arbitrary byte mutations, randomized delivery,
completion and failure states. On failure it reports the input bytes, seed,
iteration and delivery chunks, plus runtime and assembly identity. The fuzz CI
workflow runs 100,000 inputs with a run-specific seed and retains its log and
source revision. The oracle intentionally covers head framing only: it does not
validate header semantics, bodies or connection behavior. Temporary budget and
character-decoding faults were both detected when checking the harness itself.

The URL/query workflow, standalone head campaign and regression mutations do not
satisfy the whole-engine gate. Sustained integrated campaigns, coverage review, cross-platform
stress and investigation of every reproducible finding remain outstanding.

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

Managed allocation rose by roughly 5 KiB per 1 MiB response and 30-37 KiB per
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


### Managed TCP listener drain for exclusively owned endpoints

`WebServer.DrainAsync` now supports managed TCP listeners when every endpoint is
exclusively owned. Endpoint ownership is checked under registration synchronization
before admission changes; shared endpoints are rejected without stopping either
listener. Accepted connections are captured from both listener ownership and the
endpoint's pre-routing registry, preserving HTTP/2 streams whose application
routing has not yet completed. HTTP/1.1 closes after its accepted response;
HTTP/2 sends GOAWAY and completes accepted streams. Incomplete startup handshakes
are closed. Pending context consumers remain active until transport drain ends.

Concurrent calls share the first deadline, which starts at the drain request.
Deadline expiry, cancellation, explicit Stop and disposal abort remaining
transports. Cancellation callbacks are tied to the captured drain generation so
an old call cannot stop a restarted listener. New prefixes and restart are rejected
while drain is in progress. Once cleanup completes, RunAsync ends and the endpoint
can be reused. This remains transport cleanup, not forcible termination of user
callbacks: HTTP/2 propagates stream cancellation, while HTTP/1.1's existing context
token follows RunAsync cancellation.

Nineteen new cases cover HTTP/1.1 and HTTP/2 accepted/queued responses, concurrent
deadlines, five abort paths, incomplete HTTP/2 SETTINGS startup, restart,
pre-cancellation and shared-endpoint rejection without admission side effects.
The old managed-unsupported expectation is removed; Microsoft remains unsupported.
The 101-case focused set passes on Windows and pinned Linux with two logical
processors, including the actual .NET Standard asset on both .NET 10 hosts.
Both assets build without warnings; formatting, source guards and changed-source
pinned YARA checks pass. Evidence is under `TestResults/http-engine/tcp-listener-drain-*`.

Shared TCP endpoint drain and combined TCP/QUIC drain remain incomplete. TLS,
unread request bodies, slow response consumers and acceptance/cleanup races need
broader integration coverage. Default-engine replacement, WebSocket hardening,
standards closure and comparative performance work remain open; this checkpoint
does not change the engine default or release readiness.

All checks on the preceding endpoint-admission commit
`9a01f369878ecf039ac029d6d8b745b8d2b1d989` completed successfully, with only the
intentional routine-auto-merge/runtime-probe skips. This includes all desktop,
MAUI, AppContainer, compatibility, allocation, security and malware gates.
The exact-head check snapshot is retained as `endpoint-admission-ci-final.json`.
This does not establish that the separate earlier native runtime crash was fixed;
new listener-drain source still requires its own CI.

Final local listener-drain validation passed 3,163 tests with five expected skips
(3,168 total, zero failures). The rebuilt hot-path, listener-queue, cold-start and
listener allocation gates also passed. The CI discovery floor is 3,168. These
allocation budgets do not establish end-to-end performance; exact-head CI remains
required before this increment can be treated as cross-platform validated.


### Combined transport drain and canceled-response cleanup

Combined TCP/QUIC hosting now coordinates graceful drain under one shared
cancellation deadline. Both accept pumps continue moving accepted contexts while
responses finish, and either transport may finish first without stopping its
peer. Expiry/cancellation stops combined queue admission, aborts unfinished
responses, joins both pumps and clears undispatched contexts. Concurrent callers
share the first drain; pre-canceled calls have no effects. Stop, run cancellation
and disposal can interrupt it. TCP endpoint exclusivity is checked before QUIC
changes admission, so unsupported shared ownership leaves both transports live.

Thirteen new combined cases exercise HTTP/1.1, HTTP/2 and HTTP/3 response order,
concurrent calls, five abort paths, restart, pre-cancellation, shared-owner
rejection and an observed full 256-context queue. Initial focused Windows/Linux
runs passed all 24 combined-host cases. The full run then exposed two races:
run cancellation and an undispatched HTTP/2 shutdown could return a response
instead of aborting. That failed 3,181-case run is retained as
`combined-drain-full.log` (two failures).

Corrections separate abort from ordinary response finalization, close TCP sockets
before invoking protocol cancellation callbacks, stop exclusive endpoint admission
before removing routes, and avoid generating an HTTP/2 response when routing or
registration fails during shutdown. Multiplexed context close honors its combined
context cancellation token without adding a per-close linked cancellation source.
Normal HTTP/1 response Dispose keeps its finalization behavior and its atomic
once-only guard; abort uses that same guard to protect successor keep-alive work.

Four direct cancellation cases fail against the pre-correction implementation
(`combined-abort-before-final.log`), and a fifth regression protects a subsequent
HTTP/1.1 request from stale canceled-context close. The first HTTP/1 fixture used
HttpClient, whose automatic retry obscured socket closure with a timeout; its raw
TCP replacement verifies the wire directly. The failed candidate fixture run is
retained as `combined-drain-abort-focused.log` rather than claimed as a production
fix. Final focused validation passes 175 cases on Windows and pinned Linux with
QUIC required and two logical processors, plus 106 cases using the actual
.NET Standard asset on each .NET 10 host. Both assets build without warnings;
source guards and changed-source pinned YARA checks pass.

Preceding commit `9c1fa32` failed Windows CI job 113570644625 in run 37853074722:
`StopOrClientResetCancelsApplication(true)` timed out waiting for the HTTP/3 server
callback to exit after client cancellation. The complete suite still reported all
3,168 cases. The log is `tcp-listener-drain-ci-windows.log`. This is not claimed
repaired by the combined drain/response-cleanup changes; independent reset and
cancellation validation remains open, as does the earlier native listener crash.

Shared TCP endpoint drain, acceptance/cleanup race coverage, broader slow-peer and
TLS/body coverage, and exact-head platform checks remain required. The engine
default, standards closure, WebSocket hardening and comparative performance goals
are unchanged and incomplete.

Final combined-drain/abort validation passes the complete Windows suite:
3,181 passed, five expected skips, zero failures (3,186 total) in
`combined-abort-full-final.log`. The unchanged compatibility comparator passes
207 cases / 414 upstream/Neo comparisons with zero errors. Rebuilt hot-path,
listener-queue, cold-start and listener allocation gates pass; these budgets do
not establish a throughput or latency improvement. The discovery floor is 3,186.
The earlier failed full run, both direct baseline runs and the initial fixture
failure remain in the evidence. New exact-head CI is still required.

### Linked context cancellation ordering

A deterministic regression closes a multiplexed context from a later registered
parent cancellation callback, before the linked source callback executes. Four
cases (transport/server cancellation, with/without a throwing application
callback) fail before the correction; the normal-completion control passes.
Cleanup previously disposed the link and could leave an application token
uncanceled indefinitely. Close now propagates an already-requested parent
cancellation before response finalization and again before close callbacks and
link disposal. Recoverable application callback errors are logged so cleanup
continues. Normal completion remains uncanceled; no per-close CTS is allocated.

All five regressions pass after correction. The broader drain/HTTP2/HTTP3 set
passes 180 cases on Windows and on pinned Linux with QUIC required and two logical
processors (`context-cancel-focused.log`, `context-cancel-linux.log`). Both library
assets build and changed-source whitespace verification passes. The full Windows suite passes 3,186 cases with five expected skips and zero
failures (3,191 total) in 4m47s (`context-cancel-full.log`). The discovery floor
is now 3,191.

This ordering defect is proven independently; it is not yet proven to explain
Windows CI's earlier HTTP/3 client-reset timeout. Pushed head `61f7d0d` also has a
separate Windows compatibility job failure (run 37855429075, job 113578329764):
the HTTP/1 benchmark with a fully consumed 65,536-byte POST body timed out during
warmup at its existing 20-second request deadline. Its log is retained as
`context-cancel-ci-compat-windows.log`. No timeout or gate was relaxed; investigation
and exact-head validation remain outstanding.

The same pushed head's Windows desktop job 113578329715 subsequently terminated
at the whole-suite deadline and reported only 3,146 of the required 3,186 cases
(exit 7). This is a separate incomplete-suite failure, with no individual failed
case in the log; retain `context-cancel-ci-test-windows.log` and investigate the
unfinished cases. Linux and macOS desktop jobs passed on that head. No discovery
floor or suite deadline was lowered to accept the incomplete Windows run.

Downloaded Windows/Linux TRX artifacts confirm 40 case names present in the
Linux result but absent from Windows (`ci-61f7-windows-unreported.txt`), including
the exhaustive WebSocket UTF-8 cases and later server/ZIP fixtures. Windows
reported no individual failures. Its longest reported case was the repeated BCL
message-close acknowledgement test (32.3 seconds); two in-flight close tests took
15 seconds each. This distinguishes missing coverage from a passing full suite,
but does not yet establish whether budget growth or a stalled case caused exit.

The cross-platform timing comparison specifically shows approximately four extra
seconds for Windows HTTP/1 drain-abort fixtures, while their HTTP/2 counterparts
are not among the largest deltas. These HTTP/1 tests await an HttpClient GET
failure after admission closes, so automatic retry/connect behavior must be
separated from server shutdown timing before attributing the delay to the engine.
Changed-source pinned YARA scans and both source-policy guards pass locally.

The exact fully consumed POST benchmark passes 15 fresh local Windows processes
with two logical processors (`post-body-repeat-1.log` through `-15.log`). This
does not reproduce or clear the CI timeout. Benchmark failures now identify their
protocol URL, warmup/measurement/shutdown phase, workload parameters, observed
connections and server state without adding counters to the measured hot path.

A temporary timing probe in the drain fixture isolates the Windows delay:
immediate abort reaches server Stopped in 1-3ms, while awaiting the HttpClient
GET error adds about 4.07 seconds. The deadline case reaches Stopped in 152ms
and reports the client error at 4.23 seconds. Its ten-case diagnostic TRX is
retained under `drain-profile`; the temporary instrumentation was removed.
The five TCP and five combined-host HTTP/1 abort cases now use one raw TCP/TLS
connection with pinned certificate verification and assert closure/reset before
any response byte. HTTP/2 and HTTP/3 still use independent HttpClient requests.
All fifteen protocol cases pass in 1.54 seconds (`drain-wire.log`); this is a
fixture timing improvement, not a claimed engine throughput improvement.

A Linux run incorrectly shared binaries with an active Windows Coverlet run.
Its 180 tests passed but process exit failed (134) because injected coverage
tracking referenced a Windows temporary mutex path. `context-cancel-final-linux.log`
is retained as failed validation; a clean, uninstrumented rerun is required.
Do not run another host against a coverage run's instrumented output directory.

Final checkpoint validation: the complete coverage-enabled Windows suite reports
3,191 cases (3,186 passed, five expected skips, zero failures) in 4m11s, within the
unchanged five-minute limit (`context-cancel-coverage.log`, TRX and Cobertura in
`context-cancel-coverage`). Coverlet restored the library: its SHA-256 matches the
separately built performance output. Clean Linux QUIC-required validation passes
all 180 selected cases and exits zero (`context-cancel-final-linux-clean.log`).
The actual .NET Standard asset passes all 111 applicable cases on Windows and
Linux .NET 10 hosts (`context-cancel-legacy.log`, `context-cancel-legacy-linux.log`).
The compatibility comparator passes 207 cases / 414 comparisons with zero errors.
Rebuilt hot, queue, cold and listener allocation gates pass, as does the POST body
workload with the new diagnostics. Formatting, both source guards and six changed
C# files' pinned YARA scans pass. These are correctness/budget checks, not a claim
of end-to-end engine performance improvement.

Previous head 61f7d0d's CI completed with the two Windows failures above and an
Android lifecycle setup failure: the emulator download could not be unpacked
(`ZipFile unknown archive`), before the application ran. Its log is retained as
`context-cancel-ci-android.log`. Every other non-skipped job in that CI run passed.
New-head platform checks remain required; the separate POST timeout and earlier
HTTP/3 client-reset timeout are not claimed resolved by passing local runs.

### Shared HTTP/2 listener shutdown ownership

Four real-client cases reproduce sibling stream loss when Stop/Dispose of one
listener closes a shared HTTP/2 connection. Each verifies both listeners use the
same peer endpoint, then keeps the sibling response in flight while stopping the
other owner, both with an active and an already-completed owner response. Before
the correction all four fail with premature sibling response termination
(`shared-http2-stop-before-final.log`). The initial fixture also observed the
expected pending-accept error 995 as a test failure; it now handles that specific
shutdown outcome, and the corrected pre-change reproduction remains failing.

Connection ownership now retains the active exchanges for each HTTP/2 listener.
After removing an owner's routes, an endpoint that remains shared cancels only
that owner's exchanges. The sibling response and subsequent requests keep using
the original connection; the tests also reject a hidden retry. Exclusive/last-owner
endpoint shutdown still closes the transport, as does HTTP/1 shutdown. All four
regressions pass, with 192 Windows protocol/drain/two-server cases and 123 pinned
Linux TCP cases passing. Both assets build. Full, allocation and exact-head checks
for this candidate remain pending; discovery floor is 3,195.

This is a prerequisite for shared graceful drain, not its completion. Shared drain
still rejects before mutation. It needs owner-specific admission and completion
tracking without connection-wide GOAWAY, preservation of sibling streams through
deadline/cancellation, mixed exclusive/shared endpoints and concurrent owner
shutdown/restart coverage. Cancellation callbacks and active response cleanup
must not allow a shared-owner deadline to hang or abort another owner's stream.

Final shared-owner checkpoint validation passes the coverage-enabled Windows
suite: 3,190 successes, five expected skips, zero failures (3,195 total), 4m12s;
TRX and Cobertura are under `shared-http2-stop-coverage`. The actual .NET Standard
asset passes 123 selected TCP/two-server cases on each Windows/Linux .NET 10 host.
The unchanged compatibility comparator passes 207 cases / 414 comparisons with
zero errors. Rebuilt hot-path, queue, cold-start and listener allocation gates,
formatting, both source-policy guards and changed-source pinned YARA scans pass.
This does not yet measure the additional HTTP/2 owner/exchange tracking cost in a
representative end-to-end HTTP/2 benchmark; no performance gain is claimed.

Preceding pushed head `1529221efefcf418c0877de83982944be0cb3917` has all 34 checks
completed with success or intentional skips and an overall passing CI aggregate;
`shared-owner-ci-final.json` records the exact-head check snapshot. Windows full
coverage and POST compatibility checks pass on that head, but those passes do
not establish the root cause of prior intermittent failures. New shared-owner
source still needs its own exact-head CI, and the full engine remains incomplete.

### Owner-scoped drain primitives and HTTP/1 ownership transfer

The connection can now snapshot one HTTP/2 listener's accepted context completion
without connection-wide GOAWAY. Four new real-client cases cover normal finish,
Stop, Dispose and peer reset while a sibling response remains pending; subsequent
sibling requests retain the same physical connection. These primitives require
the caller to stop owner admission before the snapshot. Public shared drain is
still disabled until that lifecycle barrier and mixed-endpoint coordination are
integrated. Snapshotting alone does not establish a public graceful-drain cutoff.

Two deterministic HTTP/1 cases also reproduce a stale listener snapshot closing
or draining a connection after its keep-alive ownership transferred to a sibling
(`shared-http1-stale-before.log`, two failures). Ownership transfer now uses the
same connection lock as scoped Stop/drain decisions. A stale owner does nothing;
a current owner marks closing/draining before a subsequent transfer can occur.
Exclusive endpoint shutdown retains whole-transport behavior. All ten shared-owner
cases pass after correction (`shared-owner-primitives-focused.log`), with both
library assets building. Full/broader validation remains pending; the discovery
floor is 3,201. The initial failed evidence remains unchanged.

The expanded owner/drain/protocol selection passes 198 Windows cases and 129
pinned Linux TCP cases (`shared-owner-primitives-broad.log`,
`shared-owner-primitives-linux.log`). Changed-file formatting, both source-policy
guards and diff whitespace checks pass. Full coverage validation is pending.

### Public shared TCP drain integration candidate

The preceding owner-primitives coverage run passes 3,201 cases: 3,196 successes,
five expected skips, zero failures, 4m13s (`shared-owner-primitives-coverage.log`).
Ten added public-drain cases then fail with the old unsupported path and pass after
integration (`public-shared-drain-before.log`, `public-shared-drain-after.log`).
They exercise HTTP/1 and HTTP/2 completion, deadline, cancellation, Stop and Dispose
with active sibling responses, reject late owner requests and preserve the shared
HTTP/2 connection for sibling follow-up traffic.

Endpoint registration freezes the exclusively owned endpoint set for each drain.
Only those endpoints stop socket admission; shared endpoints remain available to
siblings. The listener's lifecycle lock excludes new shared-owner registrations
before snapshotting its accepted contexts. Previously accepted exclusive-connection
work retains its existing drain semantics. Awaiting shared HTTP/2 contexts avoids
connection-wide GOAWAY; HTTP/1 ownership decisions use the atomic handoff guard.
The previous unsupported-path tests now verify idle shared TCP/combined drain and
sibling availability. Discovery floor is 3,211. Mixed endpoint, queued-context,
combined active-traffic and cancellation race validation remain required.

Pushed head b86a174 has failed Windows/macOS CI in run 37858579174. Windows reports
`CombinedDrainCanAbortAllProtocols("dispose")` after 30 seconds with pending-accept
error 995, then whole-suite timeout (2,838 of 3,195 cases). Its cleanup exception
may mask the original failure; no cause is yet proven. macOS reports
`GracefulListenerDrainPreservesAcceptedResponse(false)` failing QUIC startup with
AddressAlreadyInUse; all 3,195 cases were reported. Logs are retained as
`shared-owner-ci-windows.log` and `shared-owner-ci-macos.log`, with Windows TRX
under `shared-owner-ci-windows`. These failures remain open and are not bypassed.

The macOS AddressAlreadyInUse stack is specifically the replacement listener's
Start after the original drain/run tasks completed (Http3ListenerDrainTest line
52), not initial fixture port selection. Investigate native QUIC resource lifetime
and drain completion; do not mask it with a new port or a startup retry.

The public shared-drain candidate passes 208 focused protocol/lifecycle cases on
Windows and pinned Linux with QUIC required and two logical processors
(`public-shared-drain-broad.log`, `public-shared-drain-linux.log`). Formatting,
both source-policy guards and diff whitespace checks pass. Full candidate coverage,
actual .NET Standard validation and the outstanding CI diagnoses remain pending.

The shared-drain candidate completed full Windows coverage with 3,211 reported
cases: 3,206 passed and five existing platform/permission skips, zero failures,
4m13s (`public-shared-drain-coverage.log`). This run preceded the test-only
combined-abort diagnostic change. That change records the failure phase, each
protocol task and admission state, and preserves the original exception when
cleanup also fails. The updated solution builds without warnings, formatting
passes, and all seven combined-abort/QUIC drain-and-rebind cases pass locally
(`shared-drain-diagnostics-build.log`, `shared-drain-diagnostics-format.log`,
`shared-drain-diagnostics-focused.log`). Neither prior CI failure is claimed
reproduced or repaired by these results; exact-head cross-platform checks remain
required.

The current candidate also passes the compatibility audit: 207 cases and 414
upstream/Neo comparisons, zero errors (`public-shared-drain-parity.log` and
`public-shared-drain-parity/`). Both source policy guards pass. These checks do
not establish complete HTTP standards conformance or whole-engine fuzz coverage.

Mixed endpoint drain coverage now exercises one owner with both an exclusive
TCP endpoint and a shared endpoint. Ten additional cases cover HTTP/1.1 and
HTTP/2 under completion, deadline, caller cancellation, Stop and Dispose. Both
owner responses must terminate appropriately while the sibling response and
subsequent traffic remain healthy; HTTP/2 sibling traffic retains its physical
connection. All 20 shared/mixed cases pass on Windows, and the broader 218-case
Linux QUIC-enabled set passes (`mixed-owner-drain-focused.log`,
`mixed-owner-drain-linux.log`). The discovery floor is now 3,221. These additions
postdate the 3,211-case full coverage run and require a new full run.

The current production candidate passes all four existing hot-path, listener
queue, cold-start and listener allocation budget sets
(`public-shared-drain-allocations.log`). These budgets are not a comparative
end-to-end HTTP/2 or HTTP/3 performance result. The actual netstandard2.0 library
passes 149 focused cases under the Linux .NET 10 host
(`mixed-owner-drain-netstandard-linux.log`); this is target-assembly validation,
not an older-runtime compatibility claim.

The same actual netstandard2.0 assembly passes all 149 focused cases under the
Windows .NET 10 host (`mixed-owner-drain-netstandard-windows.log`). The expanded
full coverage run is pending; do not treat the earlier 3,211-case run as coverage
of the ten newly added mixed-endpoint cases.

The expanded mixed-endpoint full Windows coverage run passed all 3,221 reported
cases (3,216 successes, five existing skips), in 4m31s
(`mixed-owner-drain-coverage.log`). Subsequent review found a distinct cleanup
race: dispatch removed a reset HTTP/2 response from owner tracking before
CloseAsync and OnClose callbacks finished. A new deterministic case pauses the
close callback, then starts public listener drain; it failed on premature drain
completion (`shared-owner-cleanup-before.log`). Ownership removal and context
unregistration now occur in cleanup's finally after CloseAsync/AbortAsync, so a
concurrent shared-owner drain can still await that context's completion. The
regression also verifies sibling traffic remains on the original connection.

All 31 shared-owner cases pass on Windows and the broader 219-case set passes
on Linux with QUIC required and two logical processors
(`shared-owner-cleanup-after.log`, `shared-owner-cleanup-linux.log`). The build,
formatting and both source policy guards pass. Discovery is now 3,222 cases;
the full coverage result above predates this production correction, so final
full-source validation remains required. This reproduced cleanup race is not
claimed to explain either earlier Windows/macOS CI failure. Arbitrarily blocking
application callbacks still require explicit bounded-shutdown policy evaluation.

Final cleanup-corrected Windows coverage passes 3,222 reported cases: 3,217
successes and five existing skips, zero failures, in 4m28s
(`shared-owner-cleanup-coverage.log`). The final source compatibility audit
passes 207 cases / 414 comparisons with zero errors
(`shared-owner-cleanup-parity.log`). The actual netstandard2.0 library passes
the final 31 shared-owner cases on both Windows and Linux .NET 10 hosts
(`shared-owner-cleanup-netstandard-windows.log`,
`shared-owner-cleanup-netstandard-linux.log`). Final changed cleanup sources
have no matches under the pinned YARA rules (`shared-owner-cleanup-yara.log`).
Cross-platform exact-head CI remains required, and the earlier Windows combined
Dispose and macOS QUIC same-port rebind failures remain under investigation.

A subsequent QUIC cleanup probe installs an application cancellation callback
that throws, keeps the application pending, and triggers either ordinary peer
closure or an invalid critical-stream FIN. It verifies the callback ran once,
transport shutdown does not wait for the application, late reads/writes reject,
and critical closure preserves H3_CLOSED_CRITICAL_STREAM. Both probes initially
passed on Windows; the ordinary peer-close case failed in the full 61-case
Linux direct-QUIC set with AggregateException escaping RunCoreAsync cancellation
(`quic-callback-linux.log`). This is a reproduced cancellation-boundary defect,
not evidence that the macOS same-port failure shares its cause.

QUIC transport cancellation now logs recoverable callback failures and continues
cleanup. Critical output streams are disposed through nested finally blocks
even if worker cleanup faults. All 61 direct QUIC cases pass on Linux afterward
(`quic-callback-fixed-linux.log`); the build, formatting and both source policy
guards pass. The discovery floor is 3,224; broad Windows and final full-source
validation are still pending. Initial passing probes and the failing Linux run
are retained; a native handle leak was not measured by this reproduction.

The corrected QUIC source also passes all 130 direct-transport/public-listener
cases on Windows (`quic-callback-fixed-windows.log`). Full coverage follows;
these focused results do not replace final exact-head CI.

The QUIC callback correction passes full Windows coverage with 3,224 reported
cases (3,219 successes and five existing skips), zero failures, in 4m31s
(`quic-callback-fixed-coverage.log`). The comparator passes 207 cases / 414
comparisons with zero errors (`quic-callback-fixed-parity.log`); changed-source
pinned YARA scan has no matches (`quic-callback-fixed-yara.log`).

At this checkpoint, pushed shared-drain head 3e7ddc4 has 30 successful checks,
two intentional skips, and the iOS HTTPS check still running
(`shared-drain-checks-final.json`). Windows/Linux/macOS desktop checks passed;
that does not explain the earlier b86a174 Windows combined-drain or macOS QUIC
rebind failures. Overall CI completion is not yet claimed.

### HTTP/1 CONNECT rejection and committed persistence

The RFC 9931 section 8 audit reproduced successor dispatch after six rejected
CONNECT responses (401, 403, 407, 307, 404 and 501) on the existing origin-form
application path. The wire fixture sends both requests in one write; before the
correction all six invoked the successor handler (`connect-reject-before.log`).
Two rejected WebSocket-upgrade GET controls and a lowercase `connect` method
control preserve ordinary persistence. This is a controlled legacy dispatch-path
reproduction, not complete CONNECT authority-form or tunneling validation.

HTTP/1.1 exact-uppercase CONNECT rejection now commits Connection: close. A
second controlled case showed post-flush mutation of the public Connection
header could otherwise reopen the connection (`connect-late-header-raw-before.log`).
Reuse now also checks the response's committed KeepAlive value. The first
mutation fixture used SendStringAsync, which closed the response too early;
it was replaced with raw output/flush and an assertion that mutation happened.
All 11 final new cases and the broader 114-case target/ownership/drain set pass
on Windows and Linux (`connect-reject-final-focused.log`,
`connect-reject-final-linux.log`). The full discovery floor is 3,235. Full-source
coverage, retained-target checks and exact-head CI remain required.

The prior shared-drain CI run 37861689011 completed successfully on 3e7ddc4
(`shared-drain-ci-completion.json`). Earlier platform failure causes remain
unconfirmed; the later QUIC correction 90467e6 has its own checks in progress.

CONNECT persistence full Windows coverage passes 3,235 cases: 3,230 successes
and five existing skips, zero failures, 4m29s (`connect-reject-coverage.log`).
The actual netstandard2.0 assembly passes 67 CONNECT/target cases on both
Windows and Linux .NET 10 hosts. The comparator passes 207 cases / 414
comparisons with zero errors, all four existing allocation budgets pass, and
the changed-source pinned YARA scan has no matches (`connect-reject-*` logs).

Pushed QUIC checkpoint 90467e6 failed Windows CI 37862628495 on the unchanged
five-minute suite budget, with 3,211/3,224 reported cases, 3,208 successes and
three skips, no assertion failures. Logs and TRX are retained as
`quic-callback-ci-windows.log` and `quic-callback-ci-windows-artifacts/`. All other
checks passed or intentionally skipped; the CI aggregate failed. Four new mixed
endpoint HTTP/1 abort cases take about four seconds each on Windows, similar
to the previously identified HttpClient retry delay. This is an investigation
lead; the suite limit and discovery requirement remain unchanged.

Windows CI TRX confirms approximately four seconds in each of the mixed-endpoint
HTTP/1 abort cases. Those four exclusive-endpoint clients now use the existing
Http1AbortProbe: it checks EOF/reset on the original TCP connection and fails if
any response byte arrives. Completion cases and HTTP/2 retain their independent
HttpClient checks; shared sibling responses and connection identity assertions
remain intact. All 20 shared/mixed cases pass in 1.6s locally on Windows (previous
run about 18s) and 1.9s on Linux (`mixed-drain-wire-focused.log`,
`mixed-drain-wire-linux.log`). This removes client retry delay from the fixture;
it is not an engine speedup. The full 3,235-case suite and five-minute budget
remain unchanged, and CI timeout resolution still needs exact-head evidence.

With the direct abort clients, full Windows coverage passes the same 3,235 cases
(3,230 successes, five existing skips) in 4m13s
(`mixed-drain-wire-coverage.log`). The 20 revised shared/mixed cases also pass
against the actual netstandard2.0 library on the Windows .NET 10 host
(`mixed-drain-wire-netstandard.log`). Formatting, both source policy guards and
the pinned changed-fixture YARA scan pass. These local results do not close the
Windows CI timeout investigation; the new exact head must pass its checks.

## Shared end-to-end fixture shutdown

The macOS run for `f0d2553` reached the unchanged five-minute suite limit after
reporting 3,229 of 3,258 cases, with no assertion failures among reported cases.
Its retained TRX shows tests still completing near the deadline. The shared
end-to-end fixture previously slept 500 ms before disposing each server, without
observing its run task. It now retains that task, waits for listening at setup,
cancels at teardown and requires run completion and stopped state within ten
seconds before disposing the client and server. No tests or discovery gates are
removed, and the suite deadline is unchanged. This is fixture lifecycle work,
not an engine throughput improvement or proof of the macOS failure's full cause.

An initial local Linux fixture run used a read-only assembly mount; file-serving
fixtures need writable directories beside that assembly. Their construction
failure also exposed unsafe managed cleanup from test-fixture finalizers. The
fixtures now use explicit disposal, including base cleanup; the Linux validation
copies assemblies into writable container temporary storage while keeping the
host mount read-only. All 60 affected cases pass in that environment.

The complete Windows coverage run reports 3,266 cases, 3,261 successes and five
existing skips in 3m41s, compared with 4m14s for the preceding local run. This
observed suite duration is not an engine benchmark. Repeating the deliberately
read-only directory-fixture setup now produces ordinary test failures (exit 2)
without a finalizer crash. Cross-platform exact-head CI remains required.

## Managed WebSocket close completion

The managed module waits for an internal terminal-close signal instead of polling
socket state every 500 ms. The signal is allocated only when observed and is
published after transport/resource cleanup, including when the transport-close
callback throws. Canceling one observer does not cancel the shared signal or
another observer. Repeated disposal retains once-only transport closure.

Six direct cases cover normal close, abort, disposal, blocked cleanup, late
observers, cancellation before/after registration and transport-close failure.
The retained-transport assertion failed before adding the cleanup `finally`.
The broader 55-case set passes on Windows and pinned Linux with QUIC required,
including HTTP/2 and HTTP/3 WebSockets. The actual .NET Standard asset passes 39
close/startup cases on both Windows and Linux .NET 10 hosts. The existing 64-round
close-acknowledgement tests retain all rounds and their original deadlines.
This advances #190 but does not complete its hardening or performance scope.

Full Windows coverage passes 3,272 cases (3,267 successes and five existing skips)
in 2m28s. In the retained before/after coverage TRX, the unchanged 64-round
message-originated close test took 32.69s before and 0.034s after; connected-origin
closure stayed about 0.034s. These are local regression-test timings, not a
throughput benchmark. The compatibility audit passes 207 cases / 414 comparisons,
and the rebuilt four allocation-budget groups pass. Representative WebSocket
throughput, tail latency and retained-memory campaigns remain required.

## Continue expectation integration

A shared quote-aware reader recognizes bare `100-continue` list members without
allocating substrings. The HTTP/1 listener and multiplexed dispatch use it;
HTTP/2 and HTTP/3 send informational headers before application delivery when
request framing indicates content, without ending the stream or affecting final
response state. Unknown expectations remain ignored and multiplexed CONNECT is excluded.
Twenty direct cases cover list boundaries, whitespace, case, quoted commas,
escapes and lookalikes. Expanded raw HTTP/TLS cases wait for the interim response
before sending content and then verify a subsequent request.

An independent HttpClient with a 30-second continue fallback and a 10-second
request deadline initially failed on HTTP/2 and HTTP/3 while HTTP/1.1 passed.
With the correction, two sequential requests succeed for single/repeated
expectations over HTTP/1.1 TLS, HTTP/2 TLS/prior knowledge and HTTP/3. The 78-case
focused set passes on pinned Linux with QUIC required. The legacy asset passes
74 cases on Windows with four explicit skips for TLS HTTP/2 and QUIC, while
cleartext HTTP/2 is exercised rather than skipped.

The new QUIC fixture initially selected a TCP port, exposing bind conflicts;
it now probes UDP. An IP-literal QUIC URL also failed Linux TLS negotiation before
HTTP, whereas `localhost` passes with the same exact-leaf pin and hostname check.
The IP-literal failure log is retained as `expect-linux.log`; its cause and the
engine/runtime IP-literal capability remain an unresolved validation item. The
hostname fixture does not establish that IP-literal behavior is fixed.

Final Windows coverage passes 3,306 cases (3,301 successes and five existing
skips) in 2m28s. The final Linux focused set passes all 78 cases with QUIC
required. The actual legacy asset passes 74 cases with four documented capability
skips on both Windows and Linux .NET 10 hosts. Compatibility remains 207 cases /
414 comparisons with no errors, and all four rebuilt allocation-budget groups
pass. The preceding `2c1f56e` has 32 successful checks and two intentional skips;
this increment requires fresh exact-head checks.


### QUIC credential and IP-address handshake investigation

Head 318466a completed all 3,306 macOS cases in CI 37871262043, with two
failures in the new HTTP/3 continue fixture (3,273 passed, 31 skipped). Both
failed during TLS authentication with UserCanceled, before HTTP negotiation.
The fixture now imports its generated QUIC certificate with Exportable, matching
existing QUIC fixtures; other protocol cases retain DefaultKeySet. The shared
test helper keeps its existing default. The [.NET 10.0.12 credential loader](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/Internal/MsQuicConfiguration.cs)
exports credentials to PKCS#12 for the non-Schannel backend. This is a fixture
correction; macOS confirmation on the changed head remains required. All 78
focused cases pass on Windows and isolated Linux with QUIC required.

A separate raw System.Net.Quic probe, containing no EmbedIO references, reproduces
the Linux IP-address handshake failure with .NET 10.0.12 and MsQuic 2.6.2. With
TargetHost 127.0.0.1 and a certificate containing that IP SAN, connection fails
before the server options callback or client certificate validator runs. With
TargetHost localhost, both callbacks run and connection succeeds. Windows passes
both cases. The validator retains hostname checks and pins the generated leaf,
allowing only its expected self-signed chain error. Source and logs are retained
under TestResults/http-engine/quic-ip-probe and quic-ip-probe-{linux,windows}.log.

Source inspection identifies a likely interaction, not a confirmed native trace:
[.NET omits IP literals from SNI](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicConnection.cs),
passing an empty string for an IP endpoint, while the
[MsQuic 2.6.2 OpenSSL initializer](https://github.com/microsoft/msquic/blob/v2.6.2/src/platform/tls_openssl.c)
configures reference identity and SNI from that string and returns TLS_ERROR if
those operations fail. The independent reproduction narrows this limitation to
the underlying stack; it does not establish a server-side remedy or complete
IP-address interoperability. Do not weaken certificate validation to mask it.


### HTTP/2 transport mutation campaign

The standalone fuzz runner accepts `--http2-transport <seed> <iterations>` (up to
one million inputs). It compares the production HTTP/2 transport with an
independent batch parser using big-endian integer decoding. Generated sequences
contain one to four frames with arbitrary types, flags, stream identifiers and
payload bytes; mutations insert, delete or replace bytes, truncate sequences,
and exercise the 16,384-byte receive limit. Each input runs with contiguous and
randomly fragmented delivery. The oracle checks exact frames, payload bytes,
consumed position, clean EOF versus truncation versus FRAME_SIZE_ERROR, terminal
failure behavior, and preservation of the borrowed source stream.

Windows seed 182 passed 100,000 inputs; isolated Linux seed 181 passed one million,
each with both delivery strategies. Two temporary production fault injections
(removing reserved-bit masking and rejecting the exact maximum length) were both
detected. Source was restored in a finally block, rebuilt, and the 100,000-input
Windows campaign passed again. Evidence is under TestResults/http-engine/h2-fuzz-*.
Failures print the seed, iteration, exact input bytes, read fragments, runtime
and assembly MVID. Replaying the seed and iteration range requires the same source
and runtime. The existing scheduled/PR fuzz job now also runs 100,000 HTTP/2
transport inputs and retains its log alongside the HTTP/1 framing campaign.

This is deterministic mutation/property testing of transport framing. It does not
claim coverage-guided fuzzing, frame-shape or connection-state conformance, HPACK,
HTTP/3/QPACK, WebSockets, or whole-engine resource/race validation. Those campaigns
remain required before the engine is ready to ship. No production behavior or
ordinary regression discovery count changes in this increment.


### HTTP/3 transport mutation campaign

The standalone runner now accepts `--http3-transport <seed> <iterations>` and
compares the production frame reader with an independent batch parser. Each
input is delivered contiguously and in random fragments, separately exercising
streamed reads, bounded buffering and payload skipping. Generation covers all
four QUIC integer widths, non-minimal encodings, arbitrary frame identifiers,
concatenated frames, payload mutations, truncation, the exact buffer limit and
62-bit declared lengths represented by small inputs. The oracle checks exact
headers and payload bytes, consumed position, remaining payload count, clean EOF,
H3_FRAME_ERROR versus H3_EXCESSIVE_LOAD, and terminal failure behavior. The large
length cases must fail or stream to EOF without allocating the declared length.

Windows seed 182 passed 100,000 inputs and isolated Linux seed 181 passed one
million (six delivery/consumption combinations per input). Temporary changes
rejecting the exact buffer limit and decrementing remaining bytes incorrectly
were both detected. Production source was restored in a finally block, rebuilt,
and the Windows campaign passed again. Reproducer output includes the input,
seed, iteration, consumption mode, fragments, runtime and assembly MVID. Evidence
is retained under TestResults/http-engine/h3-fuzz-*. The scheduled/PR fuzz job runs
100,000 inputs and archives the log. This adds framing coverage, not QPACK,
HTTP/3 stream-state or native QUIC fuzzing; the whole-engine campaign remains open.

The certificate correction on 62df1b8 was followed by macOS CI 37871773475:
3,306 total, 3,274 passed, 31 skipped, one failure. The failure was
GracefulListenerDrainPreservesAcceptedResponse(True), at same-endpoint QUIC
restart with AddressAlreadyInUse. This is the already tracked drain/rebind
validation gap, not a passing CI run or evidence that rebind is repaired.
The downloaded TRX confirms all eight continue-negotiation cases passed, including
both HTTP/3 cases. The full log is retained as quic-credential-macos-ci.log,
with the TRX under quic-credential-macos-artifacts.


### Independent QUIC endpoint-reuse diagnostic

The macOS same-endpoint restart failure remains open. Inspection shows listener
shutdown awaits QuicListener.DisposeAsync, accept-loop completion and tracked
connection tasks; connection tasks await native connection disposal. The
[.NET 10.0.12 listener implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicListener.cs)
waits for STOP_COMPLETE before closing the native listener and disposing queued
connections. This inspection does not prove the absence of a race elsewhere.

QuicRuntimeRebindTest now exercises the raw runtime independently of EmbedIO HTTP
classes: 32 bind/dispose/rebind cycles on the same IPv4 loopback endpoint, both
without traffic and with an authenticated accepted connection. The connected
case ceases acceptance before closing and disposing the peers, matching the
listener drain ordering. Certificates remain pinned with hostname validation;
the synthetic private key is exportable for the non-Schannel backend. Failures
retain cycle, stage, endpoint and runtime diagnostics. There are no retry loops,
sleeps, assertion relaxations or production workarounds.

Both raw cases and the two existing graceful-response/drain cases pass on Windows
and isolated Linux (.NET 10.0.12, Linux MsQuic 2.6.2). This is baseline evidence,
not a macOS reproduction or fix. The raw cases intentionally remain part of the
platform suite so macOS can distinguish a basic runtime rebind failure from the
richer HTTP/3 lifecycle. The ordinary discovery floor rises to 3,308. Logs are
retained under TestResults/http-engine/quic-rebind-*; the macOS CI result and the
full engine lifecycle investigation remain outstanding.


A native-source hypothesis is now recorded for that gap: the
[MsQuic 2.6.2 kqueue datapath](https://github.com/microsoft/msquic/blob/v2.6.2/src/platform/datapath_kqueue.c)
queues ShutdownSqe from CxPlatSocketContextUninitialize when I/O has started;
CxPlatSocketContextUninitializeComplete closes the socket file descriptor.
CxPlatSocketDelete requests this cleanup for each socket context without waiting
there for those queued shutdown events. This could explain an immediate rebind
racing native descriptor closure, but source inspection alone does not prove
that this path caused the captured macOS failure. The raw runtime tests are
intended to discriminate that hypothesis before changing production behavior.


The full Windows coverage suite with the raw rebind diagnostics passed: 3,308
total, 3,303 successes and five existing platform/environment skips, in 2m 28s.
The source builds for both library targets; analyzer/suppression guards,
formatting and the pinned YARA scan of the new fixture pass. These results do not
replace the pending macOS comparison or the final exact-head checks.


### QPACK encoder acknowledgment state

QpackEncoderFeedback introduces bounded encoder-side ownership for dynamic
response compression: registered inserts, per-stream FIFO section references,
known-received count, shared reference counts, cancellation release and bounded
section/reference admission. The caller must register inserts and sections before
publishing bytes. Rejected admission changes no ownership, allowing a stateless
fallback. The state takes its own deduplicated reference arrays so callers cannot
change eviction protection after registration. Peer protocol failures release
tracked ownership and permanently invalidate the state.

The incremental decoder-stream parser accepts fragmented/coalesced instructions,
full 62-bit stream identifiers and non-minimal integers. Following
[RFC 9204 sections 2.1.4 and 4.4](https://www.rfc-editor.org/rfc/rfc9204.html#section-4.4),
section acknowledgments release the oldest outstanding section on that stream
and may advance known-received count; cancellation releases every section on its
stream without acknowledging insertions; zero or excessive insert-count updates
and acknowledgments without an outstanding section fail with 0x202. The existing
live QUIC decoder-stream reader now uses this state and batches input rather than
reading one byte per stream read. With current stateless responses, there are no
registered inserts or sections, so only cancellation remains valid peer feedback.
Dynamic table insertion, eviction policy, encoder-stream output and dynamic field
section integration are still outstanding; this increment does not enable or
claim dynamic response compression or a measured throughput improvement.

Fifteen new focused cases cover ordering, shared references, cancellation,
fragmentation, bounds, mutation isolation and failure cleanup. The combined 184
QPACK/QUIC cases pass on Windows and isolated Linux with required native QUIC.
An initial test run failed six assertions because Assert.Throws requires an exact
exception type; corrected tests use Assert.Catch for the IOException-derived
protocol exception and additionally assert its exact 0x202 error code. Initial
and corrected logs are retained under TestResults/http-engine/qpack-feedback-*.
The ordinary discovery floor is 3,323; public APIs and asset targets are unchanged.

### Raw macOS rebind failure reproduced

CI 37872817493 on head 23eddfc reproduces AddressAlreadyInUse in the new raw
DisposedRuntimeListenerRebindsSameEndpoint(False) diagnostic, at cycle 14 during
bind on 127.0.0.1:61422, .NET 10.0.12. This case uses no EmbedIO HTTP listener,
application callbacks or client connection. All 3,308 cases were reported:
3,276 passed, 31 skipped and this one failure. The native async-cleanup hypothesis
now has an independent runtime reproduction, though no native event trace yet
proves the exact file-descriptor closure ordering. The limitation remains open;
no retries or suppressions were added. Evidence is quic-runtime-rebind-macos.log.


Encoder-feedback validation also passed the full Windows coverage suite (3,323
total, 3,318 successes, five existing skips, 2m 26s), 123 focused QPACK cases using
the actual netstandard2.0 assembly hosted on .NET 10, all four rebuilt allocation
budgets, and the compatibility audit (207 cases / 414 comparisons / zero errors).
Both library targets build without warnings; formatting, suppression/analyzer
guards and the pinned YARA scan of the new state pass. This is not evidence of
old-runtime execution or completed dynamic compression. Exact-head CI still applies.


### QPACK encoder table and insertion instructions

QpackEncoderTable now prepares dynamic insertions with absolute indices, exact
lookup, bounded storage and FIFO eviction. An entry is eligible for eviction only
when its index is below the encoder feedback's known-received count and it has no
outstanding section references. Admission first checks every eviction needed for
space; encountering a pinned entry leaves the whole table untouched. The encoder
owner must serialize table operations and section registration under one gate;
concurrent feedback only advances acknowledgment or releases references.

Insertion encodes the initial capacity update and the new field before committing
ownership. Static-name references and Huffman literals reuse the existing codec.
Instruction budget rejection does not evict, initialize capacity or consume an
absolute index; size checks precede payload-buffer growth. Sensitive names and
explicit never-indexed fields are refused, exact duplicates reuse lookup, and
entry accounting includes the required 32-byte overhead. Seventeen new cases
cover round trips, byte budgets, invalid octets, exact capacity, duplicates,
reference/acknowledgment protection and all-or-nothing eviction.

The independent QPACK tool now passes these generated instructions, fragmented
byte by byte, to pinned pylsqpack 0.3.24. It also sends a one-reference dynamic
field section and feeds the peer's fragmented acknowledgment through our encoder
feedback state. Both assets pass 100 insertions at capacities 220 and 4096,
including repeated eviction and required-count wrapping, while capacity zero
refuses insertion. Existing independent decode/cancel/stateless-response checks
remain intact. Reports now include dynamic_insertions and exact assembly hashes.
This test-only field-section construction does not mean live response integration
is complete: the encoder-stream writer, response section planner and connection
serialization remain outstanding, and live responses still use stateless QPACK.

All 140 focused QPACK cases pass on Windows, isolated Linux, and the actual
netstandard2.0 assembly hosted on .NET 10. Both targets and the independent tool
build without warnings; suppression/analyzer and formatting guards pass. The
ordinary discovery floor rises to 3,340. Evidence is retained under
TestResults/http-engine/qpack-table-* and TestResults/qpack-interop. The independent
checks do not establish old-runtime execution, complete encoder conformance or
an end-to-end performance gain.


The final Windows coverage run for the encoder-table increment passed all 3,340
reported cases: 3,335 successes and five existing skips, in 2m 26s. The preceding
feedback head c21cc5f had 32 successful and two intentionally skipped checks when
inspected; its passing macOS run does not erase the independently reproduced
raw-runtime rebind failure on 23eddfc. Required checks must run again on the table
increment. The production project has no Python dependency; the Python codec
comparison remains isolated in the non-packable test tooling.


### Dynamic QPACK field-section serialization

The C# encoder now serializes caller-selected exact dynamic matches, using the
peer's maximum table capacity for Required Insert Count wrapping and that required
count as Base. The owner supplies absolute indices and must pin the returned
unique reference set before publishing the section. Field order and repeated
fields are preserved. Static exact matches remain static; sensitive names and
explicit never-indexed fields remain literal regardless of a supplied index.
References require capacity for at least one entry, use full 62-bit counters,
and remain subject to encoded and decoded section limits and octet validation.
The stateless path shares field serialization without changing its wire format.

Twenty new cases exercise count wrapping, relative indices, repeated references,
full-width arithmetic, sensitive fields, zero/small capacity, invalid indices,
invalid octets and exact size budgets. All 160 focused QPACK cases pass on Windows,
isolated Linux, and the netstandard2.0 assembly under a .NET 10 host. The pinned
pylsqpack interop tool now uses this production field-section encoder instead of
a synthetic single-reference section, and still verifies dynamic insertion,
eviction, decoding and acknowledgments on both assets at capacities 0, 220 and
4096. Both projects build without warnings; formatting, analyzer/suppression
guards and all four rebuilt allocation budgets pass. The discovery floor is 3,360.

This completes another codec component, not live dynamic response compression.
The connection owner still needs to serialize table/section planning, publish
encoder instructions, enforce blocked-stream policy and release references on
transport failure. The live exchange still calls the stateless encoder. Evidence
is retained under TestResults/http-engine/qpack-section-* and the independent
interop reports; no throughput improvement or complete conformance is claimed.


The preceding table head 2c5b648 failed Windows CI 37874035071 in
IndependentDotNetHttp2ClientEchoesAcrossBothFlowWindows(1): a transport read
propagated IOException wrapping SocketException/OperationAborted (995) through
Http2Dispatcher.RunAsync during the fixture shutdown path. All 3,340 cases were
reported (3,336 passed, three skipped, one failure). The log is retained as
qpack-table-windows-ci.log. This is an outstanding HTTP/2 cancellation/lifecycle
investigation, not a QPACK assertion failure or a confirmed fix. Do not treat the
preceding head as green or suppress the failure merely because local runs pass.


The changed-source Windows coverage run for dynamic field-section serialization
passed: 3,360 total, 3,355 successes and five existing skips in 2m 27s. The new
source passes the pinned YARA scan. This local pass does not resolve the captured
HTTP/2 cancellation failure or the macOS raw QUIC rebind limitation; exact-head
CI and the remaining development work still apply.


### HTTP/2 aborted-read cancellation normalization

The captured Windows error 995 is now covered with a controlled transport that
returns IOException wrapping SocketException(OperationAborted) after canceling
the read's token. Four positions (before the header, mid-header, before payload,
and mid-payload) failed before correction, while seven control cases passed.
Http2FrameTransport now maps only that combination to OperationCanceledException,
retaining the exact token and original IOException as the inner exception. The
reader remains permanently failed after the interrupted operation, so a new token
cannot resume parsing at an unknown frame boundary.

An aborted socket read without token cancellation still propagates the original
IOException. Connection reset, timeout and a plain IOException also remain errors
even if cancellation races them. No broad IOException suppression or test retry
was added. The dispatcher already treats its own cancellation as normal shutdown;
this change supplies the cancellation exception it expects for this particular
transport result. It addresses the reproduced error classification, but the
original CI race still needs changed-head Windows confirmation.

All 109 frame/interoperability cases pass on Windows, isolated Linux, and the
actual netstandard2.0 assembly under a .NET 10 host. The rebuilt HTTP/2 framing
fuzzer passes 100,000 inputs with contiguous and fragmented reads. Both targets
build, all four rebuilt allocation budgets pass, and formatting plus analyzer/
suppression guards pass. Eleven new cases raise discovery to 3,371. Before/after
and validation logs are retained under TestResults/http-engine/h2-abort-*.


The final changed-source Windows coverage run passed all 3,371 reported cases:
3,366 successes and five existing skips in 2m 28s. The pinned YARA scan of the
changed transport is clean. Changed-head Windows/macOS/Linux checks remain
required; neither this local pass nor a later unchanged-source retry should be
used to erase the original failure evidence.


### Encoder admission against peer QPACK blocked-stream limits

The encoder feedback owner now requires the peer's blocked-stream limit when
registering a dynamic field section. Admission, reference pinning and the
potentially blocked stream count change atomically under the existing gate.
Multiple outstanding sections on one stream consume one blocked-stream slot.
Sections using only known-received entries can still be admitted when the limit
is zero or full. A refusal changes neither ownership nor local section budgets.

Each stream retains the highest Required Insert Count among its outstanding
sections. Section acknowledgment and Insert Count Increment advance the shared
known-received count, releasing blocked-stream slots across all affected streams
without releasing their references. Stream Cancellation releases only that
stream's references and slot; it does not acknowledge inserts. Feedback errors
clear all ownership and permanently fail the owner. The admission check uses a
counter; advancing the known frontier scans the locally bounded stream set
without allocating a temporary collection. These rules implement the ownership
requirements of RFC 9204 sections 2.1.2 and 2.1.4; they do not yet enable live
response compression.

Fourteen new cases cover limits zero/one/two, multiple sections per stream,
acknowledgment ordering, insert progress, cancellation, transactional refusal,
invalid limits and concurrent admission by 64 callers. Three seeded model tests
compare 6,000 feedback/admission steps against a separate pending-section model.
All 174 QPACK cases pass on Windows and the actual netstandard2.0 assembly under
a .NET 10 host. The pinned Linux run passes 235 QPACK/QUIC cases with QUIC required.
Both production assets also pass the pinned pylsqpack interoperability campaign
at capacities 0, 220 and 4096. Both-target builds, formatting, source guards and
the production-source YARA scan pass. Evidence is under
TestResults/http-engine/qpack-blocked-*; discovery is now 3,385 cases.

The preceding head ef509a9 passed its Windows CI test job, but macOS CI
37875045956 / job 113641584962 failed the raw runtime rebind diagnostic at cycle
23, connected=False, bind 127.0.0.1:62794, .NET 10.0.12, AddressAlreadyInUse.
The log remains qpack-blocked-parent-macos.log. This independently repeats the
known listener-disposal/rebind limitation before HTTP or client traffic; it is
not suppressed or claimed repaired. Dynamic encoder instruction transport,
connection-owned table/section serialization and live response integration are
still outstanding, along with the broader engine program.


The changed-source Windows coverage run passed: 3,385 reported cases, 3,380
successes and five existing skips in 2m 28s. No timeout, discovery or security
checks were relaxed. The independent interop host's import ordering was also
corrected after its separate formatting check reported the pre-existing order.


### Live acknowledged QPACK response encoding

HTTP/3 response headers and trailers now use a connection-owned encoder. A
nonzero peer table capacity enables a table capped locally at 4,096 bytes;
zero capacity retains stateless output. The owner serializes table lookup,
field-section creation and reference pinning under the connection gate before
considering any insertion that could evict the selected entries. Sensitive,
never-indexed and exact static-table fields are not inserted. Validation happens
before queuing insertion instructions. If local section/reference budgets are
full, the response uses stateless encoding without adding ownership.

A lazy unidirectional encoder stream carries type 2 and ordered insertion
instructions. Queued instructions are bounded to 65,536 bytes, plus one in-flight
instruction bounded by the table and queue budgets. The writer, its critical
output watcher and stream disposal participate in connection shutdown. Encoder
stream reset terminates the connection with H3_CLOSED_CRITICAL_STREAM. Final
connection disposal drops the table/queue and permanently aborts feedback
ownership, releasing references and rejecting late feedback.

This first live policy references only entries already acknowledged by the
peer. Inserts are speculative for later responses. A peer that withholds a
third outgoing unidirectional stream does not block response encoding or require
responses to wait for encoder progress. This is not the final compression or
performance policy: speculative references, admission/eviction heuristics,
transport-credit visibility and comparative performance remain open. The RFC
9204 flow-control recommendations still need their complete transport-level
audit. No throughput or allocation improvement is claimed for this increment.

Thirteen new cases cover cold/warm encoding, disabled capacity, exact instruction
budgets, pinning before eviction, sensitive data, invalid fields, final ownership
cleanup and three raw QUIC scenarios. The wire fixture independently checks the
capacity/insertion bytes for content-length: 37 and the later indexed response,
then exercises Section Acknowledgment and Stream Cancellation. Separate cases
withhold encoder-stream credit and reset the encoder stream. Its first run
failed because the fixture disposed its critical decoder stream before ending
the connection; the fixture now retains that stream until teardown. Both logs
are preserved. The final focused Linux run passes 248 QPACK/QUIC cases with QUIC
required. The actual netstandard2.0 assembly passes 184 codec cases on a .NET 10
host; QUIC transport remains unavailable in that asset.

Both assets pass the independent pylsqpack codec campaign, both targets build,
all four rebuilt allocation budgets pass, and formatting/source guards plus the
HTTP/3 production-directory YARA scan pass. Evidence is under
TestResults/http-engine/qpack-live-*. The discovery floor is now 3,398.

The preceding head 660fcd2's Security report failed on an obsolete accepted
finding, not an active security result. Downloaded CodeQL C#/Actions and Semgrep
SARIF from run 37875827618 contain zero findings and successful invocations; the
report identified the accepted ZipFileProvider cs/zipslip entry as no longer
found. That stale acceptance is removed. The unchanged report script passes on
those downloaded artifacts with zero accepted findings. The original failed
report and SARIF are retained; this recheck is not a fresh scan of the new source.
No rule or warning gate was disabled. Fresh exact-head security checks remain
required, as do the unresolved raw macOS QUIC rebind investigation and the rest
of program #181.


The final Windows coverage run for live QPACK integration passes all 3,398
reported cases: 3,393 successes and five existing skips in 2m 27s. The preceding
head 660fcd2 finished every check except the two Security report/aggregate checks
that reported the stale acceptance described above. A passing macOS test on
that head does not establish a fix for the retained native rebind failure.


### Independent response-planner interoperability campaign

The pinned pylsqpack 0.3.24 harness now exercises QpackResponseEncoder, the same
planner used by live HTTP/3 connections, at peer capacities 0, 220 and 4096. Each
capacity processes 656 response sections on each production assembly: repeated
fields, Latin-1 codec octets, sensitive/never-indexed fields, multiple sections
on one stream, byte-fragmented instructions/feedback, instruction-before-header
and header-before-instruction delivery, delayed acknowledgments, cancellation,
and a full 256-section ownership budget with stateless fallback afterward.
An entry larger than half the table is referenced while a competing insertion
is considered; independent decoding checks that the selected entry survives.

pylsqpack supplies the Section Acknowledgment bytes. Its Python interface does
not expose a standalone Insert Count Increment flush, so the driver emits those
only after the independent decoder accepts complete insertion instructions.
Selected Stream Cancellations are also driver-generated to model abandoned
sections. These limits are explicit in response_campaign.py. This is codec and
planner interoperability, not an independent end-to-end QUIC client or complete
HTTP conformance campaign. Existing incoming QPACK and insertion campaigns run
alongside it, unchanged.

Both assets pass all three capacities, with identical results. The retained
reports include assembly hashes and these representation-byte measurements:

| Peer capacity | Dynamic sections / 656 | Stateless section bytes | Encoder instruction + actual section bytes |
| --- | --- | --- | --- |
| 0 | 0 | 23,706 | 23,706 |
| 220 | 353 | 24,038 | 23,670 |
| 4096 | 603 | 29,858 | 18,732 |

Compare within a row: the adversarial eviction payload scales with capacity.
These counts exclude HTTP framing, QUIC/TLS overhead, CPU, allocation and latency;
they are not throughput benchmarks. The small-table row also shows why a single
large-table compression result cannot justify a general performance claim.

Temporarily removing reference registration caused the independent decoder to
reject the targeted stream 2052 eviction case with DecompressionFailed. The
production source was restored byte-for-byte in a finally block and the restored
campaign passes. An initial DLL hash-restoration assertion correctly detected
that rebuilding after the preceding commit changed embedded source provenance
from 660fcd2 to 630eeb5; the restored source has no production diff. Both targets
were rebuilt and independently revalidated rather than claiming the old DLL was
byte-identical. Mutation, restoration and final results remain under
TestResults/http-engine/qpack-planner-* and TestResults/qpack-interop. Formatting,
source guards and pinned YARA checks pass. Production dependencies and APIs are
unchanged; the added Python module belongs only to the non-packable test harness.

The preceding head 630eeb5's macOS run 37876745045 / job 113646973824 failed
InvalidHandshake_missing-key: the final application connection count was zero
where the fixture expected one. All 3,398 cases were reported (3,366 successes,
31 skips, one failure). This failure differs from the retained raw QUIC rebind
issue. The handshake fixture had inferred application callback completion from
client wire-handshake completion, although the module invokes that callback
independently after accepting the upgraded context. It now explicitly waits for
the first/second callback before closing each valid connection, using completion
signals and the original 15-second deadline. Invalid-request zero-callback and
400/accept-header assertions remain intact. No production handshake behavior or
retry policy changed. All 30 cases pass on Windows and isolated Linux; changed-head
macOS CI must confirm the fixture correction. The original failure log is retained.


The final Windows coverage run with the corrected fixture passes: 3,398 total,
3,393 successes and five existing skips in 2m 26s. The discovery count and timeout
are unchanged. Security report and Security passed are green on the preceding
630eeb5 head after removal of the obsolete acceptance. Its CI aggregate remains
failed because of the retained macOS handshake result; new-head checks are
required independently.


### Measured QPACK planner allocation and lookup reduction

The new C# performance mode `--qpack-response` compares stateless encoding,
zero-capacity planning and warmed 4,096-byte planning over four fixed datasets.
It compiles delegates before measurement, warms each mode for 5,000 calls and
records seven rotating rounds of 25,000 operations. JSON retains every sample,
assembly SHA-256/source revision, runtime, OS, architecture, GC mode and tiering
setting. Dynamic planning includes immediate Section Acknowledgment processing.
Field construction, the connection gate, transport, application work and
cold-table encoder instructions are outside this component measurement.

The first default-tiering capture showed substantial tier-transition differences,
so the before/final comparison uses DOTNET_TieredCompilation=0 consistently. This
is a recorded benchmark condition, not a production setting change. The initial
unbuilt command output is also retained separately; it is not benchmark evidence.
Final median nanoseconds per warmed-planner operation and allocated bytes are:

| Dataset | Windows before / after ns | Linux before / after ns | Bytes before / after |
| --- | --- | --- | --- |
| Status only | 118.5 / 89.9 | 119.2 / 88.7 | 528 / 376 |
| Static-table fields | 278.8 / 236.0 | 261.8 / 219.3 | 544 / 376 |
| Repeated dynamic fields | 1127.1 / 777.6 | 1038.3 / 765.7 | 1472 / 1064 |
| Sensitive fields | 413.2 / 369.7 | 420.0 / 365.7 | 608 / 440 |

Windows used .NET 10.0.12 x64 on Windows 10.0.26300; Linux used the pinned SDK
container on the same host with two reported processors. Both baseline and
candidate binary hosts are retained under TestResults/http-engine. The Linux
baseline assembly carries 630eeb5 provenance and the Windows baseline 4480dc0;
those commits have identical production source, verified by Git diff. Candidate
hashes identify the measured changed binaries; source revision text alone is not
a claim that an uncommitted candidate equals its parent. Results, intermediate
captures and build logs are qpack-perf-* artifacts.

The planner now allocates its index array only when it finds an acknowledged,
usable dynamic entry. A section without references uses stateless serialization
instead of constructing empty dynamic tracking objects. The privately produced
reference array transfers directly to feedback ownership; other registration
callers retain defensive copying. Already-selected entries also avoid duplicate
insertion lookups. Admission budgets, sensitivity rules, decoder feedback,
reference lifetimes and eviction ordering are unchanged.

The repeated-field case allocates 27.7% fewer bytes per operation and has lower
component times on both measured platforms. It still costs more CPU/allocation
than stateless encoding in this dataset; the tradeoff buys a smaller field
section. These measurements do not establish end-to-end throughput, tail latency,
contention or cold-start improvements, and no wall-clock CI gate was added.
Representative engine-level comparative performance remains unfinished.

All 248 focused QPACK/QUIC cases pass on Windows and pinned Linux with QUIC
required; 184 codec cases pass against the actual netstandard2.0 assembly in a
.NET 10 host. Both assets pass the expanded independent planner/codec campaign,
with unchanged encoded-byte counts. All four existing rebuilt allocation budgets,
source guards and the production/benchmark YARA scans pass. The discovery floor
remains 3,398. Reproduction commands and scope are in the performance README.


The preceding head 4480dc0 completed with 32 successful and two intentionally
skipped checks, including macOS regression coverage and all three aggregates.
This confirms the callback-synchronized fixture on that changed head; it does
not resolve the separately documented intermittent native QUIC rebind issue.
The optimization requires fresh checks on its own head before any merge.


Final changed-source Windows coverage passes: 3,398 reported cases, 3,393
successes and five existing skips in 2m 26s. No discovery, timeout, security or
allocation gate was weakened. The PR remains draft while the larger engine
program and fresh exact-head checks continue.


### Seeded response-planner state campaigns

The independent QPACK probe now appends 2,000 seeded operations per table
capacity (0, 220 and 4096) to its 656-section structured campaign. Override
`--seed` and `--iterations` on `test/EmbedIO.QpackInterop/verify.py` to replay or
extend a campaign. Each connection retains table and feedback state throughout;
operations vary fields, reuse recent field lists, delay insert credit, retain
section acknowledgments, cancel sections, and send multiple sections per stream.
Values include Latin-1 octets and sizes at/around entry-capacity boundaries.
These are codec inputs, not a claim that every generated field is a valid HTTP
application header. Synthetic stream identifiers are reused after cancellation;
this exercises codec ownership rather than a complete HTTP/3 stream lifecycle.

Pinned pylsqpack 0.3.24 decodes every emitted section and encoder instruction.
The driver compares exact decoded fields, outstanding-section counts against
its own acknowledgment queues, and known insertion counts against independently
accepted instructions. Final draining must release all pending sections and
account for every insertion. Section acknowledgments come from pylsqpack;
standalone insertion increments and cancellations remain driver-generated.
A failure reports seed, capacity, iteration, action and insertion progress;
replay uses the same assembly and probe source. JSON reports include assembly
SHA-256 and seed/iteration counts. Existing CI invocations execute the default
campaign against both target assemblies without adding production dependencies.

This is seeded stateful differential testing, not coverage-guided fuzzing or
proof of whole-engine safety. Malformed feedback, transport concurrency and
native QUIC remain separate validation surfaces. Randomized changing-header
traffic also measures encoder-stream bytes: the 100,000-operation .NET 10 run
with seed 834971 produced 327,670,735 encoder-plus-section bytes at capacity
4096 versus 322,996,894 stateless section bytes. Thus the present eager insertion
policy can increase wire traffic despite smaller field sections; admission
heuristics and end-to-end performance work remain necessary.

Validation: 100,000 random operations per capacity passed on each target
assembly (600,000 operations total), using seed 834971 for net10.0 and seed
712839 for netstandard2.0. Both assemblies ran under the installed .NET 10
runtime on Windows; this is not older-runtime validation. The preliminary
10,000-operation-per-capacity net10.0 run also passed. Evidence is retained in
`TestResults/http-engine/qpack-stateful-*` logs and JSON reports. No production
source or dependency changed in this increment. Full repository checks apply
to the eventual committed head independently of these local campaign results.


### Bound QPACK replacement admission under table pressure

Response planning still inserts eligible fields immediately when unused table
capacity can hold them. When insertion would require eviction, a candidate must
match a previously observed fingerprint in a fixed 64-slot history. This keeps
one-off fields from repeatedly replacing useful entries. The history is allocated
lazily, contains 512 bytes of fingerprint payload, retains no header strings,
and is released with the connection encoder. Deterministic fingerprints make
experiments repeatable; they are not cryptographic identifiers. Slot/fingerprint
collisions can change admission decisions, but never substitute a table field:
actual encoding and table lookup continue to compare the complete strings.
Sensitive, never-indexed and oversized fields do not enter the history. Existing
acknowledgment, pinning, instruction-queue and blocked-stream limits remain in
force. This is an internal compression choice with no public API change.

Two regressions fail against the preceding production assembly and pass with
the candidate: first-time replacement is refused until the candidate repeats,
and 200 distinct full-sized responses leave an acknowledged entry available.
The independent campaign primes targeted entries twice so it still exercises
pinning and the 256-section fallback budget under either admission policy
(659 structured sections now). Workload generation uses a separate random
source from instruction fragmentation and acknowledgment ordering. Reports
include a SHA-256 of every input section's stream, fields and delivery order;
matching hashes and stateless byte totals were verified before comparing bytes.

Matched-input Windows runs used seed 834971 with 100,000 operations per capacity:

| Table capacity | Encoder bytes before / after | Encoder + section bytes before / after |
| --- | --- | --- |
| 0 | 0 / 0 | 15,230,836 / 15,230,836 |
| 220 | 893,544 / 500,535 | 31,122,493 / 30,700,378 |
| 4096 | 5,847,933 / 2,616,003 | 321,952,826 / 319,370,605 |

The 4096-capacity result reduces insertion traffic by 55.3% and combined bytes
by 0.80%. Its stateless baseline is 317,387,901 bytes: the candidate still spends
0.62% more combined bytes on this deliberately changing workload. More adaptive
admission and end-to-end performance work remain open. These figures exclude
HTTP frame overhead, transport traffic and decoder-feedback bytes; they are
not network-throughput measurements.

The existing warm microbenchmark, run with tiered compilation disabled on
Windows .NET 10.0.12 x64, reports repeated-header medians of 781.7 ns before and
783.6 ns after, with the same 1064 allocated bytes and 8 section bytes per
operation. Other warm datasets also retain their allocation/section-byte totals.
Those timings show no material warm-path improvement; they do not measure the
new pressure-path CPU cost. All four existing allocation budgets pass unchanged.
Baseline assembly SHA-256 is
`A62ED41F1B5537EEE873B1F237D05EE0FD71E1F131703C2A6239CE38F543A824`;
candidate SHA-256 is
`45B7BDA82B4B9EEC61B88576F0653C34574294A58BD948E144741313B03754C7`.
The baseline was retained from the preceding optimization's validated candidate;
its embedded source version predates that uncommitted build, so the hash, not
that version string alone, identifies the measured binary.

All 250 focused QPACK/QUIC tests pass on Windows and Linux with required QUIC.
All 186 QPACK tests pass against the netstandard2.0 assembly under .NET 10 on
Windows. Independent decoding passes another 300,000 operations per target
assembly (net10.0 seed 834971; netstandard2.0 seed 712839). This does not establish
older-runtime support or whole-engine fuzzing completion. Evidence, including
the initial priming-assumption failure, is retained under
`TestResults/http-engine/qpack-admission-*`. Analyzer guards, formatting and the
changed production source's YARA scan pass without suppression.

The pinning check explicitly repeats the competing field while previous sections
remain outstanding, so first-sighting admission refusal cannot mask a missing
pin. Removing the table's reference-ownership eviction check temporarily causes
pylsqpack `DecompressionFailed` on stream 2060. Original source bytes were
restored in `finally`; rebuilding reproduces the candidate DLL hash above, and
the restored focused Windows/Linux and legacy-asset checks pass. The previous
committed head 820a619 finishes all 32 checks successfully with two intentional
skips. This is separate from the new candidate's forthcoming exact-head gates.

Final changed-source Windows coverage passes 3,400 cases (3,395 successes,
five existing skips) in 2m26s. The discovery floor is raised to 3,400; the
five-minute suite deadline is unchanged. The final strengthened independent
campaign passes 100,000 operations per capacity on both target assemblies,
and before/after input hashes still match at all three capacities. Full local
coverage also passed before the final pinning-test reinforcement; both logs
are retained rather than replacing the earlier evidence.


### Measure QPACK pressure-path cost and burst tradeoffs

The C# performance runner adds `--qpack-churn`, comparing stateless encoding
with the connection response planner at capacity 4096. Three preconstructed
workloads cycle through 2,048 resource values: change every call (`unique`),
repeat each value eight times (`bursts`), and combine changing values with a
stable field (`mixed`). These are synthetic workloads; `unique` revisits its
values on the next cycle and is not a claim of permanently unique traffic.
A fresh planner per round performs 4,096 warmup calls followed by 32,768 measured
calls; seven rounds alternate mode order. Three independent process pairs on
each platform alternate baseline/candidate order. The same runner and workload
hashes are used on both assemblies, with only `EmbedIO.dll` replaced. All samples
and a summary are under `TestResults/http-engine/qpack-churn-*`.

The measurements include immediate insertion credit, encoder-queue draining and
section ACK handling. Inputs, delegate compilation, reflection and JSON output
are outside timing. Both platforms use .NET 10.0.12 x64 with tiered compilation
disabled and workstation GC. Windows reports 16 processors; the pinned Linux
SDK container reports two on the same physical host. Below are medians of the
three process medians (each process median contains seven rounds):

| Workload | Windows ns before / after | Linux ns before / after | Allocated B before / after | Section + encoder B before / after |
| --- | --- | --- | --- | --- |
| unique | 806.8 / 545.2 | 776.3 / 502.3 | 848 / 418.8 | 71.67 / 37.48 |
| bursts | 513.4 / 534.6 | 500.3 / 516.8 | 827 / 775 | 13.34 / 17.51 |
| mixed | 745.8 / 752.4 | 720.2 / 724.7 | 863.4 / 863.4 | 38.50 / 38.50 |

Admission substantially improves rotating-unique cost and allocations, but
short bursts spend about 31.2% more compression bytes and modestly more CPU:
first-sighting refusal postpones useful insertion by one response. This is a
measured policy tradeoff, not a universal speedup. The mixed workload keeps its
stable oldest entry pinned during planning, so both policies retain it and emit
no replacement instructions in the measured interval; its small timing changes
do not establish a useful performance gain. Adaptive admission and table
refresh/duplication policy remain open investigations. The stateless controls
retain matching allocations and byte totals on both assemblies and platforms.
The dynamic planner remains slower than the stateless codec, even where it
reduces wire bytes.

Baseline assembly SHA-256 is
`A62ED41F1B5537EEE873B1F237D05EE0FD71E1F131703C2A6239CE38F543A824`;
current policy assembly SHA-256 is
`35A93F5A975B38677B8B901F7796F6B78B3DDAD211EBC86A5F6A9098E7DA2AEC`.
The latter is built from committed 2b87bd5; its differing hash from the prior
uncommitted candidate includes source-version metadata. No production code
changes in this measurement increment. The runner build, formatting, source
guards and new benchmark source's YARA scan pass. There is no timing gate,
transport/peer-decoder cost, retained-memory measurement or end-to-end throughput
claim here.

On 2b87bd5, macOS CI 37880644155 / job 113659314003 fails the independent raw
runtime rebind probe again: unconnected listener, cycle 12, bind stage,
127.0.0.1:54774, .NET 10.0.12, AddressAlreadyInUse (48). The suite reports 3,400
cases, 3,368 successes, 31 skips and this one failure. This uses no HTTP engine
or QPACK code, so the log does not indicate a QPACK regression; it also does not
establish the native failure's precise cause. The log is retained and no retry,
suppression or resolution claim is introduced. The PR remains draft/unmerged.


### Opt-in native QUIC rebind ordering trace

The independent .NET 10.0.12 runtime probe now accepts
`--quic-rebind 2048 TestResults/quic-rebind-probe`. It opens an unconnected
loopback QUIC listener, awaits disposal, and immediately rebinds its endpoint,
stopping at the first failure. It has no EmbedIO reference, certificate, client
traffic, retry or delay between cycles. Managed events record each bind/dispose
boundary. A 100 ms observation interval occurs only after the result, allowing
late cleanup events to be recorded without changing that result.

The CI workflow has a default-false `quic-rebind-probe` dispatch input. When
explicitly selected, the macOS test job builds a test-only C dylib and uses the
[dyld interposing section ABI](https://github.com/apple-oss-distributions/dyld/blob/main/include/mach-o/dyld-interposing.h)
to observe UDP bind/close calls in that one probe process. It records descriptor,
port, result, errno and monotonic begin/end timestamps plus managed markers on
the same native clock. Records stay in a fixed 131,072-entry memory buffer until
explicit flush; no trace-file writes occur between listener operations. Failed
binds retain their actual errno. The recorder rejects dropped/incomplete records,
untracked descriptor ranges and absent bind/close coverage. A separate local UDP
canary checks basic hook coverage. Artifact imports/hashes help verify which
native symbols/library were used. This code is neither shipped nor injected
into normal tests or application processes, and does not change the ordinary
regression, runtime library, socket options, retry policy or security settings.

MsQuic v2.6.2 resolves to commit
`819ab74f851ee168504cbc392ec32e7bed1d82e9`; the retained kqueue source was checked
against that exact revision. Its asynchronous cleanup path makes late descriptor
closure a hypothesis worth testing, but a source path is not an observed event
ordering. Native timestamps are needed to distinguish a still-open descriptor
from a kernel bind failure after descriptor closure. Instrumentation can change
scheduling; a passing trace run alone cannot invalidate the uninstrumented
failures. The native trace is an observation tool, not a workaround.

Local validation: the standalone managed probe passes 2,048 cycles on Windows
and isolated Linux .NET 10.0.12/MsQuic 2.6.2. Its existing TCP-close mode still
passes eight connections per scenario (16 total). A Linux C self-test exercises
the recorder's successful bind, address-in-use failure, errno preservation and
close path with no dropped or incomplete records; this tests the recorder, not
macOS dyld interposition. The C# build/analyzers, source guards and probe-directory
YARA scan pass. The Linux SDK image has no C compiler, so recorder validation
uses GCC 14.2.0 in the already pinned Python build image; no Python code or runtime
is added to the production engine. macOS trace execution remains pending.

The first dispatched trace run (37882226360, macOS job 113664238465) reproduces
the uninstrumented raw failure at cycle 2 on 127.0.0.1:58448. The diagnostic
itself does not run: that runner SDK does not ship `mach-o/dyld-interposing.h`.
The tracer now declares only the two interpose pointer pairs in the Mach-O
section directly, with no private SDK-header dependency; unsupported arm64e
pointer-authentication layout is rejected at compilation. The supported runner
is ordinary arm64. No native event-ordering conclusion follows from the first
run. Its failure log and artifacts remain retained. Entries in successful
native captures may appear out of timestamp order across threads; compare
recorded intervals rather than file order.


### Native trace confirms delayed socket close after managed disposal

Corrected dispatch [37882631624](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37882631624)
on commit 677b110 produces a complete trace in macOS job 113665548169. The
standalone probe fails at cycle 2 on 127.0.0.1:54888, with .NET 10.0.12 on
macOS 15.7.9 arm64. Its managed report confirms native tracing enabled and
complete. Native PID 6496 matches the managed process; all 18 events were
retained, with zero dropped/incomplete events and zero untracked descriptors.
The loader log confirms the intended tracer and relocated MsQuic 2.6.2 dylib
were loaded; the native import list includes the intercepted bind/close symbols.
The loaded MsQuic dylib hash is
`6f045def309758d760b162ab3a60239bd8afd56ff06e35cf729f5458edc8fa2e`.

The relevant timestamps below share the native monotonic clock, in nanoseconds:

| Event | Descriptor | Timestamp / interval |
| --- | --- | --- |
| Cycle 1 bind succeeds on port 54888 | 70 | 386810811000–386810863000 |
| Cycle 1 managed disposal completed | — | 386811067000 |
| Cycle 2 bind fails, errno 48 | 71 | 386811095000–386811113000 |
| Previous descriptor close succeeds | 70 | 386811265000–386811270000 |

Thus the new bind begins 28 microseconds after managed disposal and returns an
address-in-use error before closing the previous descriptor has even begun.
The prior descriptor's close starts 198 microseconds after disposal, 152
microseconds after the failed bind returns. This is direct evidence of delayed
native socket cleanup in this captured run, rather than merely a source-based
hypothesis. It does not establish a repaired implementation. The independent
ordinary regression in the same job also fails uninstrumented at cycle 26 on
127.0.0.1:60749 (3,368 successes, 31 skips, one failure / 3,400 total).

The pinned [kqueue implementation](https://github.com/microsoft/msquic/blob/819ab74f851ee168504cbc392ec32e7bed1d82e9/src/platform/datapath_kqueue.c)
drains active upcalls and removes read notifications before queueing final
context cleanup, where it closes the descriptor. The
[pinned event-queue helper](https://github.com/microsoft/msquic/blob/819ab74f851ee168504cbc392ec32e7bed1d82e9/src/inc/quic_platform_posix.h)
performs the event-removal kevent synchronously. A candidate investigation is to
release the descriptor after that rundown/removal boundary while preserving
deferred context reclamation and protection against stale events/descriptor
reuse. At the time of that capture, no native patch had been implemented, validated, shipped or substituted
for the pinned dependency yet. No retry, suppression, security relaxation or
HTTP production change is used to make this diagnostic pass.

Evidence is retained under `TestResults/http-engine/quic-trace-macos-artifact2`
and `quic-trace-macos-job2.log`. The native JSONL SHA-256 is
`aec15230393614d928aefb2f79264b0a77c4b152fc153e31e48376c27700abb7`.
The initial header-build failure remains recorded separately. Both diagnostic
runs retain their real failed status; the PR remains draft/unmerged.

### Test-only native cleanup candidate

The optional CI dispatch input `quic-cleanup-experiment` builds both an unmodified
control and a patched candidate from MsQuic commit
`819ab74f851ee168504cbc392ec32e7bed1d82e9` (2.6.2), using the same compiler,
quictls provider, pinned TLS/gtest submodules and system libcrypto. It does not
install, package or change the engine's native dependency. Ordinary CI keeps
the pinned bottle and its unsuppressed rebind regression.

The candidate patch closes and invalidates the UDP descriptor synchronously
after I/O rundown and read-event removal. It retains the existing queued
shutdown and context reclamation. This aims to release the endpoint before
returning from native deletion without freeing context referenced by previously
returned events. I/O callbacks acquire the rundown guard before accessing the
descriptor; binding deletion requires a zero binding reference count. These
source observations motivate the experiment; they do not prove all concurrent
send/event lifetime cases safe.

The experiment retains three untraced and three traced control runs. Only
the specific macOS socket error 48 is accepted as a negative-control result;
missing reports, loader mismatches and other failures stop the experiment.
It then requires five untraced and five traced candidate runs of 4,096 immediate
rebinds, native datapath tests, five repetitions of both managed raw QUIC rebind
cases (including established peers), and the full 3,400-case coverage suite.
Control datapath failures are retained as diagnostic evidence. Candidate
failures remain failures. Source/patch hashes, native build configuration,
compiler/OpenSSL identity, dylib hashes, loader logs, traces and test reports
are uploaded together.

Local preparation verifies patch application against the exact pinned source,
shell syntax and the workflow security audit. Native macOS builds and outcomes
remain pending; no cleanup repair or shipping readiness is claimed.

The first candidate dispatch (37883971270, job 113669720483, commit 2e40f00)
builds both native variants successfully. All six unmodified control processes
fail with socket error 48: untraced after 173/392/373 completed cycles and traced
after 523/397/359. Every trace reports complete capture. Both native datapath
runs report the same two failures:
`XdpMapMode_ZeroConfigUsesNormalPath` expects a nonzero feature mask, and
`XdpMapMode_InitFailsWithoutRawDatapath` expects initialization failure.
Each reports 36 cases, 19 passes, 15 skips and these two failures; several
upstream tests also print unsupported-feature messages while reporting a pass.
These are not evidence that those unsupported features were exercised.

The candidate datapath exit stopped that initial script before candidate
rebind or managed tests ran. The revised diagnostic collects all remaining
candidate results before returning failure if any candidate check fails.
It does not remove either failing test, change assertions or turn this
native result green. The first artifacts are retained under
`TestResults/http-engine/quic-cleanup-experiment-artifact`.

The next experiment also compiles an identical test-only lifetime fixture into
both native variants. It blocks the configured I/O worker inside a different
socket's receive callback, deletes an idle socket, then binds an independently
owned UDP socket to the released endpoint without retries. After releasing the
callback and draining the datapath, it checks that the replacement remains
bound. This is intended to expose both deferred endpoint release and accidental
closure of a reused descriptor. Callback waits have explicit failure deadlines,
and a scope guard releases the callback on assertion exits.

The fixture is appended to the pinned upstream test translation unit; the
control's production source remains unchanged. Its bytes/hash are retained
separately from the cleanup patch. Native compilation and control/candidate
outcomes for this new deterministic fixture remain pending.

### Native cleanup candidate results

Dispatches 37884341997 (job 113670860940, commit 113cf99) and 37884899155
(job 113672595736, commit dc12741) complete candidate validation on macOS
15.7.9 arm64 / .NET 10.0.12. Each runs five untraced and five traced processes
with 4,096 immediate rebinds: all 81,920 candidate cycles across the two runs
succeed. In both runs, all six unmodified control processes fail with socket
error 48. In the second run, their completed cycles are 124/436/5 untraced
and 48/62/200 traced.

Independent post-run trace analysis matches successful binds to descriptor
closes and managed lifecycle markers, checks event counts, process identity
and complete capture. All 40,960 traced candidate cycles show close completion
before the managed disposed marker; none show close beginning afterward.
The control traces retain violations of that ordering. Instrumentation can
affect scheduling; the untraced results and deterministic fixture provide
separate evidence.

The deterministic test compiles and fails on the unchanged control at immediate
port rebinding while another socket holds the I/O worker. It passes on the
candidate, including the replacement socket remaining bound after worker-pool
rundown. This fixture currently permits descriptor reuse; it does not separately
prove which descriptor number the replacement obtained.

Both runs pass all five repetitions of the two raw managed rebind cases.
Both full coverage suites report 3,400 cases: 3,369 successes, 31 skips and zero
failures. The native candidate datapath suite with the added fixture reports
37 cases, 20 passes, 15 skips and the same two XDP-related failures; its control
has the additional failing lifetime regression. The experiment correctly exits
with failure for the remaining native failures. No green experiment status,
unqualified native-suite pass, shipping integration or overall completion is
claimed.

Both runs produce the same native library hashes:
control `13bb173e30f621f22ed6b797c5cdbc5e1fb0dbecf100c3d14928dba340772c73`,
candidate `d9f7d872bd242ac63a5b919faecb2aa85e2c7c54a42b981bfd5c1c7c93d383c9`.
The lifetime fixture SHA-256 is
`74cd8cd82070bd444e0cd3f9fe130aab0565cbd465093442a6270a48413aa087`.
Evidence is retained in
`TestResults/http-engine/quic-cleanup-experiment-artifact2` and
`quic-cleanup-experiment-artifact3`, their corresponding job logs and
`quic-cleanup-ordering2.json` / `quic-cleanup-ordering3.json`.

Source review explains the two other failures separately: Darwin initializes
its optional feature mask to zero, contradicting the zero-config test's
assumption that every ordinary datapath advertises a feature bit. Its initializer
also ignores the XDP map configuration, whereas the second test requires
unsupported map-mode initialization to fail. Neither behavior was changed by
the socket cleanup patch. These remain recorded for separate resolution;
assertions have not been removed or weakened.

The lifetime fixture is now strengthened to identify the idle socket's
descriptor slot and verify its actual mapped loopback address and port before
deletion. After deletion it explicitly requires the replacement socket to
receive that same slot, then checks its endpoint after worker-pool rundown.
A descriptor-allocation mismatch fails the test; reuse is no longer inferred
from successful rebinding. This uses ordinary socket APIs in the isolated
native test process. Its first verified result is recorded below.

The optional cleanup experiment also prepares a separate AddressSanitizer build
using the pinned upstream `QUIC_ENABLE_ASAN` option. It repeats the deterministic
lifetime and existing UDP data cases 100 times with recorded shuffle seed
40591, while retaining the unchanged full native-suite result. Compiler
commands, CMake options, executable/library imports and loader/diagnostic logs
are collected. No sanitizer suppression or relaxation is configured.
Upstream sanitizer mode changes allocation instrumentation, including disabling
its normal pool allocator, so this supplements the ordinary build and cannot
establish production performance or replace its lifecycle checks. Instrumented
build and execution results remain pending.

### Explicit descriptor reuse validation

[Run 37886035017](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37886035017),
job `113676146594`, tested head
`51655b1377c4ffeba4de2b781ce2c433847cae8e`. The strengthened deterministic
fixture passes on the candidate: the replacement obtains the verified idle
socket descriptor, binds its former endpoint immediately, and remains bound
through worker-pool rundown. The unchanged control fails the descriptor check
(idle descriptor 10, replacement 12), immediate binding and final endpoint
check. This closes the earlier fixture's explicit descriptor-reuse evidence gap;
it does not prove all concurrent lifetime behavior.

All ten candidate probes pass 4,096 cycles each (40,960 total). Independent
inspection of the five native traces verifies 20,480 complete cycles with native
close completion before the managed disposed marker, with no incomplete events,
dropped events or untracked descriptors. All three control traces contain late
native closes and fail immediate rebinding. All five repetitions of the two
managed rebind cases pass. Full macOS coverage reports 3,400 cases: 3,369 passed,
31 skipped, zero failed.

The candidate library hash is unchanged from the two preceding controlled runs.
The strengthened fixture SHA-256 is
`1f71e152f4b5a9979a16af6c5d2a7b0fd4c4de06ea942359ef5670c292eb363e`.
Evidence is retained in `TestResults/http-engine/quic-cleanup-experiment-artifact4`,
`quic-cleanup-experiment-job4.log` and `quic-cleanup-ordering4.json`.
Across these three runs, the candidate passes 122,880 rebind cycles, including
61,440 traced cycles with the required close ordering. These are repeated
observations of the same native candidate, not independent implementations.

The native suite still reports the same two XDP-related failures in both
variants, plus the lifetime regression failure in the control. The job remains
failed. No native dependency has been shipped, no assertions were suppressed,
and sanitizer validation on head `5025bb8` remains pending.

### Unsupported Darwin map-mode initialization candidate

The optional native experiment now carries a separate
`msquic-kqueue-config.patch` alongside the endpoint-cleanup patch. The pinned
Darwin initializer ignores `XdpMapConfigCount`; the cross-platform initializer
instead treats a nonzero count as an explicit request for a raw datapath and
returns `QUIC_STATUS_NOT_SUPPORTED` when it cannot provide one. The Darwin
candidate rejects that unsupported request before allocation and clears the
output pointer. Ordinary zero-count initialization is unchanged.

The existing `XdpMapMode_InitFailsWithoutRawDatapath` test has failed in every
recorded control and cleanup-only candidate. It remains unchanged and is the
before/after regression for this correction. The sanitizer subset also includes
this case for 100 repetitions. The separate zero-config optional-feature-mask
assertion remains unchanged and failing in the recorded evidence; this candidate
does not manufacture feature bits or suppress that result.

Both patches are retained and hashed separately; the experiment records their
combined source diff and new native library hash. Prior cleanup-only measurements
must not be attributed to the combined candidate. Local patch composition,
reverse-application checks, shell syntax, suppression guard and scoped YARA scan
pass. Native compilation and execution of the combined candidate are pending.
This remains an isolated dependency experiment, not a shipped native change.

The experiment additionally compiles the same `kqueue-config-test.inc` into
control and candidate. It directly checks that an entirely zero-initialized
configuration can bind an IPv4 loopback endpoint and receive one datagram with
exact binary contents. It waits for receipt, drains socket/datapath/worker-pool
cleanup, then verifies the packet count and payload result. This checks actual
UDP behavior independently of optional feature advertisement. RAII cleanup also
runs on a failed assertion. The original feature-mask assertion is retained;
this supplemental test has not yet been compiled or executed on Darwin.
The fixture is hashed in the artifact and included in the sanitizer repetitions.

### Sanitizer configuration failure and harness correction

[Run 37886228567](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37886228567),
job `113676736861`, tests cleanup-only head `5025bb8`. Its ordinary candidate
passes all ten 4,096-cycle probes, the strengthened descriptor-reuse fixture,
five paired managed rebind repetitions and full macOS coverage (3,369 passed,
31 skipped, zero failed). Inspection of all five complete native traces verifies
20,480 closes completing before managed disposal. The candidate library hash
matches the preceding runs. The same two XDP-related native assertions fail.

The subsequent AddressSanitizer configure step fails before compilation:
upstream `CMakeLists.txt:744` calls `check_c_compiler_flag` without loading
`CheckCCompilerFlag`. No instrumented executable or sanitizer repetition ran,
and no sanitizer pass is claimed. Evidence is retained in
`TestResults/http-engine/quic-cleanup-experiment-artifact5`,
`quic-cleanup-experiment-job5.log` and `quic-cleanup-ordering5.json`.
Across four completed ordinary runs, the cleanup-only candidate passes 163,840
rebind cycles, including 81,920 traced cycles with the required close ordering.

The harness now loads the standard module through
[CMake's project include hook](https://cmake.org/cmake/help/latest/variable/CMAKE_PROJECT_INCLUDE.html)
for the instrumented build. It retains the compiler's actual flag probe and
upstream sanitizer settings. A local CMake/GCC reproduction fails with the same
unknown command without the hook and configures successfully with it; logs and
source are retained in `TestResults/http-engine/cmake-sanitizer-module-probe`.
This validates module loading only. Full Darwin instrumented compilation and
execution remain pending. The earlier queued combined-candidate runs use their
original source and cannot validate this later harness correction.

### Priority-update rejection policy audit

[RFC 9218 section 7](https://www.rfc-editor.org/rfc/rfc9218.html#section-7)
permits a parse failure in a PRIORITY_UPDATE field value to become an HTTP/2
`PROTOCOL_ERROR` or HTTP/3 `H3_GENERAL_PROTOCOL_ERROR`. The current
`Http2StreamRegistry.UpdatePriority` and `Http3ControlStream.ReadAsync` use those
errors for non-ASCII or malformed Structured Field dictionaries. This policy
is permitted; no parser change is needed merely to ignore malformed updates.
The ordinary Priority header path instead falls back to default priority through
`HttpPriority.TryParse`.

The HTTP/3 control reader also maps non-request stream identifiers to
`H3_ID_ERROR`, server-originated updates to `H3_FRAME_UNEXPECTED`, and truncated
identifiers to `H3_FRAME_ERROR`. These paths map to
`Http3ControlTest.InvalidPriorityUpdateRejectsBeforeFollowingFrame`; accepted
updates and subsequent control-frame consumption map to
`PriorityUpdatePreservesFieldAndFollowingControlFrame`. Live connection handling
rejects updates for unpromised pushes. This is a source/requirement mapping,
not new runtime validation, proof of complete scheduling, or closure of the
remaining extension conformance work.

### Initial Brotli request content coding

The .NET 10 asset now recognizes the `br` request content coding in
`OpenRequestStream` when `SupportCompressedRequests` is enabled. It delegates
streaming decompression to the runtime's `BrotliStream`; no production package
or native dependency is added. Coding names are compared case-insensitively,
as required by RFC 9110 section 8.4.1, including existing gzip/deflate/identity.
The default remains disabled. The .NET Standard 2.0 asset retains explicit
unsupported-coding rejection for Brotli, and response negotiation is unchanged.
`CompressionMethodNames.Brotli` exposes the registered coding name without
claiming WebSocket compression or response support.

The existing Brotli case now sends a valid Brotli body and verifies opt-in
decoding. Five additional cases cover uppercase Brotli, disabled Brotli,
mixed-case gzip, uppercase deflate and an unknown coding. Valid compressed payloads traverse
real HTTP and are checked after decoding. The prior request helper fails four
cases (Brotli and mixed-case existing codings); the candidate passes the focused
21-case body set. The test expectations also explicitly verify unsupported
Brotli on the actual legacy asset. Windows full coverage passes 3,405 cases (3,400 passed, five skipped, zero
failed). All 13 request-body cases pass on pinned Linux .NET 10 and on the
actual netstandard2.0 assembly hosted by Windows .NET 10, including explicit
Brotli rejection in that asset. This does not establish older-runtime execution.
All four allocation gates, source guards and formatting checks pass. Evidence
is retained under `TestResults/http-engine/brotli-request-*`.
The discovery floor is deliberately raised to 3,405.

This is an incremental content-coding integration. Brotli response/cache
variants, coding chains, Zstandard, shared dictionaries, bounded decompression,
malformed/truncated-stream policy and wider independent interoperability remain
required work. It does not complete the modern coding or resource-control gate.

### Unsupported map-mode candidate validation

[Run 37887357544](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37887357544),
job `113680246408`, tests combined-candidate head `3360e17`. The unchanged
`XdpMapMode_InitFailsWithoutRawDatapath` regression now passes on the candidate;
the control still fails it. The candidate retains the descriptor-reuse pass,
passes all ten 4,096-cycle rebind probes, and full macOS coverage reports 3,369
passes, 31 skips and zero failures. The remaining native failure is the
zero-config optional-feature-mask assertion. The sanitizer configuration still
fails before compilation at the original missing-module call; this head
precedes the harness correction.

The combined native candidate hash is
`428aa5e76f6a28330c031cfd87afa37eac4c1dfc269d571aeac11035cec5cdac`.
Evidence is retained in `TestResults/http-engine/quic-cleanup-experiment-artifact6`,
`quic-cleanup-experiment-job6.log` and `quic-cleanup-ordering6.json`.
Its measurements are separate from the earlier cleanup-only candidate.

### Zero-config functional result and Bash compatibility correction

[Run 37887537789](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37887537789),
job `113680798458`, tests head `745dcdc`. The zero-config UDP-delivery fixture
passes in both unchanged control and combined candidate. It directly receives
and verifies its datagram despite Darwin's empty optional-feature mask. The
candidate retains its unsupported-map-mode and descriptor-reuse passes and
passes all ten 4,096-cycle probes. The original feature-mask assertion remains
the sole native candidate failure. Sanitizer configuration fails at the original
missing CMake module, before instrumented compilation. Evidence is retained in
`quic-cleanup-experiment-artifact7`, its job log and `quic-cleanup-ordering7.json`.

[Run 37887939602](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37887939602),
job `113682050871`, tests head `fd79b44`. It fails before any native configuration:
Darwin's Bash 3.2 treats expansion of the empty optional-arguments array as an
unbound variable under `set -u`. The earlier local Bash syntax/CMake tests did
not exercise that shell-version behavior. The corrected harness uses one
quoted, initialized `CMAKE_PROJECT_INCLUDE` argument for both modes: empty in
ordinary builds and the module file path in the instrumented build. It does
not relax `set -u`. This run supplies no native or sanitizer execution evidence;
its log and partial artifact are retained under suffix `8`. Corrected Darwin
validation remains pending.

### Strict Brotli request completion and ownership

Eight real HTTP regressions expose permissive runtime-stream behavior: empty,
truncated and trailing-data Brotli bodies return success, while invalid data
returns an internal server error, through both byte and text helpers. All eight
fail before the correction. The .NET 10 helper now wraps the runtime
`BrotliDecoder` with local request-stream completion validation. It requires a
complete coded stream followed by the framed HTTP body's EOF, rejects extra
bytes, and maps invalid/incomplete coding to HTTP 400. It does not change the
process-wide `System.IO.Compression.UseStrictValidation` switch.

The wrapper rents input storage on the first nonempty read, validates fragmented
input through the runtime decoder and clears/returns its buffer on disposal.
A single atomic read/disposal state prevents decoder or buffer release while a
read owns them. It rejects concurrent reads, preserves cancellation tokens,
poisons an interrupted stream and closes its owned source once. Zero-length
reads do not consume input. Completing an application-level partial read is not
proof that the entire coded body has been validated; callers must read through
EOF before treating the whole request body as accepted.

All 31 focused cases pass, including the eight malformed-body/server-health
cases, valid empty streams, one-byte input fragmentation, 192 KiB random binary
content, small output reads, zero-length reads, concurrent-read rejection,
pending-read cancellation and disposal. Windows full coverage passes 3,423 cases:
3,418 passed, five skipped, zero failed. All 31 focused cases pass on pinned
Linux .NET 10. All 21 public request-body/validation cases pass on the actual
netstandard2.0 asset hosted by Windows .NET 10, where Brotli is explicitly
unsupported. Both assets build; formatting, source guards, scoped YARA and
all four allocation gates pass. Logs are retained under `brotli-strict-*`.
The discovery floor is raised to 3,423 for the 18 additional cases.
Output/resource limits, independent coding campaigns, response variants and the
other unfinished content-coding requirements remain open.

### Native sanitizer repetitions verified

[Run 37889869599](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37889869599),
job `113688135127`, tests head `d9d5533`. Its corrected harness configures and
builds the instrumented candidate. Compiler commands for `datapath_kqueue.c`
and `DataPathTest.cpp` contain `-fsanitize=address`; the native executable imports
and loads Apple's AddressSanitizer runtime and the intended instrumented MsQuic
library. Upstream's `DISABLE_CXPLAT_POOL=1` allocation mode remains enabled.

The log contains 100 iterations with 11 passing cases each, including explicit
descriptor reuse, zero-config UDP delivery, unsupported-map rejection and the
selected existing IPv4/IPv6 UDP data cases. No sanitizer diagnostic is present.
The ordinary candidate suite still fails the original optional-feature-mask
assertion, and the experiment's final exit remains failure. The script did not
record a separate sanitizer process exit code; its per-iteration results and
absence of diagnostics are direct evidence, while a separately retained exit
status is a follow-up provenance improvement. This subset does not establish
whole-engine memory safety, leak detection, or correctness of uninstrumented
system crypto libraries.

Evidence is retained in `quic-cleanup-experiment-artifact9`, its job log and
`quic-cleanup-ordering9.json`. Source configuration, compiler commands, native
imports, loader logs, XML and all repetition output are included. The native
candidate remains test-only and is not shipped.

### Zero-config test contract correction

The next experiment applies `msquic-zero-config-test.patch` identically before
building either control or candidate. It replaces the upstream test's assumption
that ordinary UDP requires a nonzero optional-feature mask with an actual socket
binding, canonical IPv4 endpoint and nonzero assigned port check. The separate
zero-config datagram fixture continues to verify exact receive contents and
cleanup. Both behaviors have already been verified with an empty feature mask.
No production capability bit, test skip or scanner suppression is introduced.

The corrected original test also owns worker/datapath/socket cleanup through a
local scope guard, including assertion failures. Its patch and applied diff are
retained and compared exactly. Earlier assertion failures remain in their
original artifacts. Native compilation and execution of this corrected test
remain pending. The genuine control failures for delayed endpoint release and
unsupported XDP configuration remain before/after regressions.

The sanitizer subset now includes the corrected zero-config test for 100
repetitions and records its actual process exit separately as
`candidate-asan.exit`. A nonzero value remains a final experiment failure.
This closes a recording gap; prior logs do not supply that separate exit value.

### Native candidate experiment passes with corrected functional contract

[Run 37891735617](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37891735617),
job `113693956600`, tests head `1a14dec`. The candidate native suite reports
38 cases: 23 passed, 15 explicit platform skips, zero failures. The corrected
zero-config binding test passes in control and candidate; the separate datagram
fixture also passes. The unchanged production control retains the unsupported
map-mode and descriptor-lifetime failures. No genuine negative control is hidden.

All ten candidate probes pass 4,096 cycles each. Independent inspection of five
complete traces verifies 20,480 native closes completing before managed disposal
and zero closes starting afterward. Full macOS coverage reports 3,423 cases:
3,392 passed, 31 skipped, zero failed. The instrumented subset reports 100
iterations of 12 passing cases; `candidate-asan.exit` and the final candidate
exit both contain zero. Instrumentation changes allocation mode and leaves
system crypto uninstrumented, so these remain scoped lifetime/datapath results.

Evidence is retained in `quic-cleanup-experiment-artifact10`, its job log and
`quic-cleanup-ordering10.json`. The native candidate library hash remains
`428aa5e76f6a28330c031cfd87afa37eac4c1dfc269d571aeac11035cec5cdac`.
This is the first entirely passing native candidate experiment; dependency
integration, wider lifecycle validation and the full engine goal remain open.

### Brotli single-byte allocation reduction

`BrotliRequestStream.ReadByte` now uses a one-byte stack buffer through the
existing validated span reader. It preserves coding, EOF, cancellation and
ownership behavior while avoiding the inherited per-call array allocation.
Two new cases verify empty input and 64 KiB fragmented binary content through
single-byte reads and repeated EOF. All 33 focused cases pass on Windows/Linux;
full Windows coverage passes 3,425 cases (3,420 passed, five skipped, zero failed).
Both assets build, source/format guards and the four allocation gates pass.

The unpackaged `--brotli-request-read` benchmark uses the same runner with the
baseline/candidate core DLL, preconstructed deterministic fixtures and compiled
constructor delegates. It validates every decoded byte and includes stream
construction, runtime decoding and disposal. Three process pairs per platform
alternate baseline/candidate ordering; each process records five alternating
single-byte/bulk mode rounds with 128 warmups and 1,024 measured streams.
Tiered compilation is disabled consistently and its previous value restored.

Median-of-process-median results for 4,096-byte single-byte reads:

| Platform | Before ns/stream | After ns/stream | Before managed B/stream | After managed B/stream |
| --- | ---: | ---: | ---: | ---: |
| Windows .NET 10.0.12 x64 | 219,886 | 209,104 | 131,281 | 176 |
| Pinned Linux .NET 10.0.12 x64 | 239,533 | 224,179 | 131,281 | 176 |

The 64-byte workload falls from 2,256 to 176 managed bytes/stream, and empty
streams from 208 to 176. Bulk-read allocations remain 176 bytes/stream on both
versions/platforms. Timing changes are component observations; bulk controls
vary modestly and native allocations, transport, application work and retained
memory are excluded. This does not establish an engine throughput improvement.

The shared runner SHA-256 is
`F4CF8D6C2F3AED8F940087D54AAE4748D0D2A4F4756BDF57322CB6AC70AB0489`.
Before-core SHA-256 is
`1C3735796D14BB1D44BBDDB51E1CFFDE9AD6F78B1FC5C9D695E9154BD710A532`;
after-core SHA-256 is
`31A4BAC4E0C149F9506E1A229BB9DB728BF195702C1963ADD5A5580ED6FA4DA6`.
Both record base source `1a14dec`; the latter includes the then-uncommitted
ReadByte override. Evidence, per-process metadata, fixture hashes, GC counts
and summaries are retained under `TestResults/http-engine/brotli-read-*`.
The discovery floor is deliberately raised to 3,425.

### Configurable decoded request-body budget

`WebServerOptionsBase.MaximumDecompressedRequestBodyBytes` and its fluent
extension configure a limit on bytes read through recognized compressed request
helpers. Null preserves existing unlimited behavior; zero accepts only an empty
decoded body, and negative values are rejected. The configured wrapper probes
EOF at an exact boundary and raises HTTP 413 for excess decoded content. It
applies to gzip/deflate on both assets and Brotli on .NET 10. Public context
interfaces and uncompressed/direct raw-input behavior are unchanged.

A lazily allocated weak context association supplies the immutable policy
without adding required public interface members, per-context fields or strong
context retention. Unconfigured default processing does not allocate that table.
The limit stream owns its source, rejects overlapping reads and retains a
limit-exceeded state. It handles byte, array, span and async read variants.

All 71 focused cases pass, including 38 new limit cases, exact/under/over limits,
UTF-8 byte accounting, valid empty compressed content, 128 KiB highly compressed
content, option validation/locking, sticky failure and server health afterward.
A temporary integration bypass fails nine over-limit HTTP cases; the restored
candidate passes them. Windows full coverage passes 3,463 cases: 3,458 passed,
five skipped, zero failed. All 71 focused cases pass on pinned Linux .NET 10.
The actual netstandard2.0 assembly passes 59 public request/limit cases on a
Windows .NET 10 host; this does not prove older-runtime execution. Both assets
build and the four allocation gates, source/format guards and scoped YARA pass.
Evidence is retained under `TestResults/http-engine/decompression-limit-*`.
The discovery minimum is raised to 3,463. The existing request guide documents
configuration, target capability, streaming acceptance and scope.

This budget covers bytes delivered through helper decoding. Native codec window
memory, wire-body limits, aggregate admission and slow-peer policies remain
separate requirements; this increment does not close the resource-control gate.

### Brotli response negotiation and static-file variants

The .NET 10 asset now negotiates `br` for response stream/text helpers and
compressible FileModule responses. The existing gzip/deflate/identity enum values
retain their numeric values, and Brotli is additive. Quality weights and explicit
client order remain authoritative; the server's wildcard tie order still puts
gzip and deflate before Brotli. Identity remains preferred when compression is
not requested and identity is acceptable. An absent Accept-Encoding remains
uncompressed. The .NET Standard 2.0 asset keeps its original coding choices and
returns 406 when only Brotli is acceptable. Explicit unsupported Brotli cache or
conversion operations throw NotSupportedException rather than mislabel bytes.

Response helpers use the BCL compressor for buffered or streaming transport.
FileCacheItem retains one additional optional Brotli byte array in the .NET 10
asset; its approximate item-overhead accounting grows accordingly. Identity,
gzip, deflate and Brotli representations convert through owned streams and have
distinct ETags. Brotli compressor disposal finalizes its output while preserving
the cache's destination stream. Variant replacement/removal adjusts section
accounting, and detached-item updates cannot increase retained section size.

Thirty-nine added cases cover eight real HTTP helper combinations across both
listeners, four static-file cache/listener combinations with cold/warm GET,
HEAD, conditional requests and variant changes, nine negotiation controls,
fourteen empty/200 KB coding conversions, and four cache-detachment/accounting
cases. The initial fixtures incorrectly expected no BOM after explicitly
selecting Encoding.UTF8, ignored existing client-order ties, and inspected
HttpClient's synthesized HEAD length after reading an empty body. Those fixture
errors are corrected; HEAD checks inspect actual header values before reading.
No production behavior or assertion was relaxed to match those assumptions.

Both library targets build with zero warnings/errors. Full Windows coverage
passes 3,502 cases (3,497 passed, five skipped, zero failed), with the discovery
minimum raised by the 39 added cases. All 115 focused new/adjacent cases pass on
pinned Linux .NET 10 and against the actual netstandard2.0 assembly hosted by
Windows .NET 10. That legacy run verifies explicit unsupported Brotli paths and
retained existing compression; it does not prove older-runtime execution.
All four allocation-budget groups, changed-file formatting, source guards and
the pinned YARA scan of production source pass. This increment adds no production
dependency. Codec CPU/native memory, compression profiles, coding chains,
Zstandard/shared dictionaries and broad comparative server performance remain
separate completion work. Required checks on the pushed head remain necessary.
### Direct HTTP/1 socket-buffer handoff

HTTP/1 connections now feed byte segments directly into Http1HeadReader instead
of copying each socket read into a MemoryStream. The head reader owns incomplete
line state. After the terminating empty line, the body stream borrows the unread
segment; after body completion, the next head reader borrows its remaining tail.
Only consumed storage may be overwritten by the next socket read. The per-request
MemoryStream, cursor field, compaction and disposal path are removed. Partial-head
limits, request semantics, TLS/HTTP2 dispatch and body-drain behavior are retained.
This replaces another inherited transport component; it is not completion of the
whole TCP listener replacement or its default/deprecation transition.

Direct parser fixtures now pass bounded segments, including nonzero offsets and
sentinel storage outside the segment. The obsolete disposed-staging-buffer
fixture now injects connection disposal before the request input getter, retaining
the existing flush/disposal assertions. Twelve added TCP/TLS cases span fixed and
chunked bodies of 8,191, 8,192 and 16,385 bytes. Each reads only 13 body bytes,
closes the response so the listener drains the rest, and checks two pipelined
requests, including an 8,200-byte header spanning another transport read.

Both targets build with zero warnings/errors. Final Windows coverage passes
3,514 cases (3,509 passed, five skipped, zero failed); all 207 selected parser,
body/pipeline/lifecycle cases pass on pinned Linux .NET 10 and against the actual
netstandard2.0 assembly on Windows .NET 10. No older-runtime execution is claimed.
All four allocation-budget groups, source/format guards and the pinned
production-source YARA scan pass.

The parser benchmark uses one compiled runner against retained baseline and
candidate binaries, three alternating process pairs and five measured rounds per
process, with tiered compilation disabled on both platforms. Context construction,
header parsing, handoff and validation are included; sockets, URI finalization,
connection construction and application work are excluded. Reflection observes
the old boxed cursor or the new boxed segment, so observation costs differ. The
measured change is 3,368 to 3,312 managed bytes/request (56 fewer), not a pure
measurement of production allocation removal. Final process-median times for
batches 1/16/64 are 1,980/1,337/1,322 to 1,980/1,301/1,301 ns on Windows, and
3,681/2,259/2,281 to 3,650/2,251/2,243 ns on Linux. Small timing changes do not
establish a throughput improvement. An earlier candidate was modestly slower
on Linux; removing repeated per-header pending-segment publication preceded the
final comparison. All samples and that initial result remain under ignored
TestResults/http-engine. Native allocations and retained connection memory are
outside this measurement.

Shared runner SHA-256: 22DB1D3A858BB472A32602B1637D313B260B73B2F0738354CA86CCDD100BC837.
Baseline core: 42B163D5057EF8419B5C28CD7A64A0A2D0E8D946839BD4F0115B66F94785E3C5.
Final candidate core: 33196B7A050D29EEB8B8632F109C4CE73139C2E88DFC34AE84E1E25D3831E1DE.

CI 37896135281 on the preceding Brotli head failed outside its added cases:
Linux reported an ObjectDisposedException from a subsequent QUIC response write
after reset handling disposed its exchange; macOS reproduced the retained
MsQuic immediate-rebind failure; the macOS upstream compatibility process timed
out receiving WebSocket data and aborted. Job logs and compatibility artifacts
are retained. These are outstanding investigations, not resolved by this input
handoff change or by a later passing rerun. Fresh exact-head checks remain required.
### Preserve QUIC output cancellation after request disposal

The Linux CI 37896135281 failure occurs when a reset cancels a request and its
owner disposes Http3QuicExchange while a detached application callback attempts
another response write. AcquireOutputAsync previously checked disposal before
cancellation, leaking ObjectDisposedException instead of the request's cancelled
operation. The owner already cancels requestStop before disposing the exchange.

Output admission now checks the caller token and exchange token while holding
the output-lifetime lock, before checking disposal or incrementing output users.
Caller cancellation retains its exact token; otherwise exchange cancellation
retains the request token. Uncancelled use after disposal still throws
ObjectDisposedException. Existing pending operations retain their semaphore user
until their finally blocks run; the last user disposes the gate. The fix adds no
linked-token source or per-write allocation and changes no legacy asset code.

Eight deterministic managed tests exercise cancellation of the caller, exchange
or both before/after disposal, uncancelled disposal, and a queued caller whose
cancellation releases its user while the final owner still holds the gate. Four
fail on the preceding production implementation and all eight pass with the
correction. The output fixture seeds lifetime fields and invokes real exchange
and body disposal; it does not require native QUIC or validate the whole exchange.
The four independent live backpressure/reset cases also pass on Windows, including
healthy sibling/subsequent streams. A broader pinned Linux .NET 10/MsQuic 2.6.2
run with QUIC required passes all 72 selected exchange/QUIC cases without skips.
Both targets build; changed-file formatting, source guards, all four allocation
budgets and the pinned production-source YARA scan pass.

The preceding direct-input head's CI 37897442361 again reports a macOS
AddressAlreadyInUse failure. That native dependency lifetime problem remains
separate and is not resolved by cancellation-order checks. The retained native
candidate evidence and its eventual supported deployment still require work.
Full Windows coverage on the final changed source passes 3,522 cases (3,517
passed, five skipped, zero failed); the discovery floor increases by eight.
Fresh exact-head platform/security checks remain necessary. No broad throughput,
whole-engine completion or native macOS cleanup resolution is claimed.
### Self-contained macOS native candidate experiment

A separate opt-in quic-self-contained-experiment dispatch now builds both native
control and candidate with QUIC_USE_SYSTEM_LIBCRYPTO=OFF. The pinned quictls
submodule supplies static crypto; its VERSION.dat and source commit are retained.
The existing Homebrew-linked experiment remains the default. Neither mode changes
ordinary CI prerequisites, installed system libraries or production NuGet assets.

Release-mode self-contained libraries must import only their own exact MsQuic
install name and macOS system libraries/frameworks; any dynamic libcrypto/libssl
or build/Homebrew dependency fails the experiment. Their exported symbols must
match MsQuic's pinned Darwin export list exactly. The candidate is copied into an
osx-arm64 runtime-shaped review artifact with MsQuic license/third-party notices,
the quictls license, the production patches and a source/hash receipt. A fresh
process must load that relocated copy and pass the existing 4,096-cycle rebind
probe. Ordinary native control/candidate comparisons, full macOS managed tests,
traced ordering and AddressSanitizer remain in the same experiment.

Shell syntax, workflow structure, source guards and pinned offline zizmor pass
locally. The Darwin build, import/export checks, relocation and runtime behavior
are pending dispatched evidence. This is a reproducible source/build recipe, not
a claim of bit-identical builds, complete crypto vulnerability review or a
production-ready native package. Supported distribution, RID coverage, package
selection/loader behavior, update policy and security/provenance remain required
before adopting it as a supported dependency. No production package includes
this artifact, and no release is published.
The pinned quictls control identifies as 3.1.7 (release date 2024-09-03), and its
commit ff36838bb69801cad56823159a036977bcbe5c75 dates to 2024-09-24. It remains an
isolation experiment, not an approved production crypto baseline. The OpenSSL
[release table](https://www.openssl-library.org/source/) lists 3.5.9 as the current
3.5 LTS release on 2026-10-09 (released 2026-09-29; support through 2030-04-08).
A separate quic-openssl-experiment selects MsQuic's existing openssl TLS backend
and pins that release's peeled commit 45e844fa2a14ec92d146bd8f5778ac130b6625fb.
It verifies VERSION.dat, builds static crypto, preserves its license and records
the backend/source in the receipt, while retaining the same native patches and
control/candidate validation. The official annotated release tag is
 d0ca66a1abe52545f14eca635c648932fcde5615; GitHub reports unknown_key for its
signature. Independent release-signature/provenance and vulnerability review
remain required before distribution; no signature verification is claimed.

The quictls isolation job 37898720873 / 113716018625 is still running at this
checkpoint. The supported-LTS experiment is a distinct dependency candidate,
not a restart of that live job or a claim that a change passed without execution.
### Native isolation result and OpenSSL release authentication

The quictls isolation job 37898720873 / 113716018625 completed successfully on
head e8b7961. The staged dylib imports only its exact MsQuic install name,
CoreFoundation, Security and libSystem, with no dynamic crypto/Homebrew/build
imports. Its only exports are MsQuicOpenVersion and MsQuicClose. The relocated
process loader identifies the staged copy, which passes 4,096 dispose/rebind
cycles; the ten build-location runs pass another 40,960. Five complete native
traces inspect 20,480 cycles, all with descriptor close completed before managed
disposal and none started afterward. The unchanged native control retains its
two failures. Candidate native tests report zero failures; platform-specific
skips remain explicit. Full macOS coverage passes 3,522 cases (3,491 passed,
31 skipped, zero failed). ASAN reports 100 successful repetitions of 12 selected
native cases and its separate exit is zero. This does not prove coverage of every
crypto allocation or whole-engine memory/concurrency behavior.

Staged dylib SHA-256:
61b7f6d8d806d0ad22cc57bf273ce18ed38774a0dc1e4ac93cc217c492373768.
This uses the old pinned quictls snapshot and proves isolation/lifetime mechanics;
it is not the approved shipping crypto baseline. The independent OpenSSL 3.5.9
candidate job 37899089793 / 113717193257 remains live at this checkpoint.

The official OpenSSL 3.5.9 release archive matches its published SHA-256
603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a.
Its detached archive signature and signed Git tag both verify locally with
signing subkey C46ED3F2CBEFDA1FDAADA44264ED7B1DCCE71CB2 under the primary
fingerprint B146647E45A7B33947AB226B2A2C87D161692D40 published by the
[authoritative OpenSSL download page](https://www.openssl-library.org/source/).
The tag object and peeled commit match the existing pins. The isolated keyring
has no Web-of-Trust owner trust configured; acceptance requires successful
cryptographic verification and the exact authoritative primary fingerprint.
GitHub's unknown_key display does not substitute for this verification.

The OpenSSL experiment now enforces that signed-tag/pinned-commit verification
before checkout/build and includes its signature status with the staged source
receipt. The keyring is job-local; no user keyring or trust database is changed.
Local shell/workflow/source/zizmor guards pass. Executing this added verification
on Darwin remains pending. OpenSSL's [3.5 release notes](https://www.openssl-library.org/news/openssl-3.5-notes/)
identify 3.5.9 as a security patch release; this version selection and signature
verification do not replace native vulnerability, binary, deployment and update
reviews. Production packages and installed prerequisites remain unchanged.
### Supported-LTS native candidate result

OpenSSL candidate job 37899089793 / 113717193257 completed successfully on head
96bf35a. Its receipt pins the MsQuic source and OpenSSL 3.5.9 commit, and the
compiled source VERSION.dat matches 3.5.9. The downloaded artifact's SHA-256
matches a717345efa0fc215e74b890831467cde9621746ac99819c743b34cc712f75941.
Its imports are limited to the exact MsQuic install name and macOS system
libraries/frameworks; only the two expected MsQuic API symbols are exported.
The relocated process loader identifies this staged library. All 4,096 relocated
and 40,960 build-location cycles pass. Five complete ordering traces inspect
20,480 closes, all completed before managed disposal with none started afterward.
Native results are 23 passing cases, 15 explicit platform skips and zero failures;
the unchanged control retains two native failures. Full macOS coverage passes
3,522 cases (3,491 passed, 31 skipped, zero failed). The selected native ASAN
campaign passes 100 repetitions of 12 cases with a separate zero exit. The
pinned full-rule YARA scan of the downloaded candidate reports no matches.

The subsequent signature-enforcing native job 37900021388 / 113720140282 is
queued at this checkpoint; the prior signed-tag verification is independently
proven locally against the same crypto commit. These results do not establish
whole-engine completion, broad crypto sanitizer coverage, supported package
selection/deployment on all RIDs, third-party TLS/QUIC interoperability,
comparative performance or release provenance. The candidate remains a review
artifact outside production packages and ordinary installed prerequisites.
### Private-package deployment validation candidate

Signature job 37900021388 / 113720140282 failed before native compilation during
public-key import: the repository-length GnuPG home exceeded Darwin's Unix socket
path limit. Its logs are retained. The job-local keyring now uses the shorter
RUNNER_TEMP/openssl-keyring path; signature verification and the exact primary
fingerprint check remain mandatory. The correction still needs Darwin execution.

The test-only EmbedIO.NativeDependencyProbe fixture builds a private local-feed
0.0.0-local package from an already validated OpenSSL candidate. It remains
outside the ordinary solution, production packages and public registries. The
package restricts its framework placeholder/dependency group to .NET 10 and
contains only osx-arm64 native assets, the unchanged notices, source evidence and
candidate receipt. The generated consumer project and per-artifact locks live
under ignored TestResults; the empty package-build project lock is committed.
The consumer pins the exact local version and reuses the independent BCL rebind
probe rather than adding a loader override.

Local SDK pack succeeds with warnings treated as errors. All three native aliases
match the candidate receipt, notices match source bytes and the portable/RID
consumer manifests select native osx-arm64 assets. A deliberately altered native
byte is rejected by the package verifier. Generated-project locked restore and
build pass with zero warnings/errors. Initial packaging errors (extensionless
notice destinations and a missing framework dependency group) are corrected;
NuGet validation is retained. The final local package hash is
fac928e62dc80aeba635f5e7be86a58d1a43b7a895e92fdc1cf93158e3d8df2d.

The native experiment now runs framework-dependent and RID-published macOS
consumers with DYLD library-search/injection overrides cleared. Each must pass
4,096 rebind cycles, load the package's own expected native path and match its
receipt hash. Locks, dependency manifests and loader evidence are retained.
Darwin execution of that deployment proof is pending. This tests one RID and
BCL library selection; all supported RIDs, full engine deployment/interoperability,
update maintenance and release readiness remain separate requirements. No core
library target, default or runtime dependency group changes.
### Successful signature and normal package-loader proof

Job 37902004523 / 113726501618 completed successfully on head edfb206. Its
Darwin GnuPG status verifies the official OpenSSL tag under the pinned primary
fingerprint; the shorter keyring resolves the earlier path-length failure.
The private package hash is
9489d87317cb77db409d89f8f64e7f8e211136d839b886d1c9082da519e56a7d,
and all native assets match candidate hash
a717345efa0fc215e74b890831467cde9621746ac99819c743b34cc712f75941.
Notices match the source. Both locked consumer restores/builds complete.

With DYLD search/injection overrides cleared, the portable .NET 10.0.12/Arm64
consumer loads libmsquic.dylib under its own runtimes/osx-arm64/native directory;
the RID-published apphost loads the copy in its publish root. Both pass all 4,096
cycles, adding 8,192 cycles of actual package-loader proof. The existing relocated
and build-location probes, native controls/tests and sanitizer campaign also pass;
full macOS coverage remains 3,522 cases (3,491 passed, 31 skipped, zero failed).
This proves one-RID normal package selection without an explicit native loader
hook; it does not establish all required distribution/deployment properties.

The native candidate used Release configuration with QUIC_BUILD_TEST=ON. The
next recipe increment builds a separate candidate-production library with tests
and sanitizers disabled, retains strict import/export checks, stages that library
for the private package consumers, and runs a second full managed macOS suite
with loader evidence identifying the tests-disabled copy. Native platform tests
and ASAN continue against their own test builds. The receipt records buildTests,
and the package verifier additionally checks the pinned MsQuic source and artifact
scope. Shell/source guards and the verifier on the prior validated package pass
locally; the new tests-disabled build/deployment/suite remain pending execution.
No production runtime asset or installed prerequisite is changed.

HTTP/1 CONNECT ownership audit identifies authority-form request initialization,
prefix/host routing, response framing and opaque transport handoff as coupled
implementation work. Successful replies must transfer buffered bytes and stop
HTTP parsing/framing, while rejected optimistic transitions retain closure under
RFC 9931. Existing multiplexed WebSocket tunnel adapters do not provide a public
classic CONNECT tunnel API. This remains concrete implementation work, not a
completed feature or a reason to mark the development goal achieved.
### Request content-coding chains

Request helpers now recognize comma-separated coding lists, validate every
nonempty token before opening decoders, and undo coding application order.
Gzip/deflate chains work on both retained assets; Brotli layers require .NET 10.
Eight compression layers bound nesting/codec allocation. Empty list members and
identity entries cause no transform; an empty field is uncompressed. Disabled
compressed-request support, unsupported tokens/parameters and excess depth
retain HTTP 400. The byte-limit wrapper is applied once after all decoding and
therefore counts final application bytes, including multibyte UTF-8, rather than
intermediate compression envelopes. Source interfaces and the opt-in default
are unchanged.

The owned chain wrapper drives its outer decoders to EOF after the final stream
ends, normalizes InvalidDataException from chain decoding to HTTP 400 and retains
sticky malformed-data failure. Top-stream disposal releases the source once.
Byte/span/memory overloads avoid inherited per-byte/per-call wrapper arrays.
Single-coding dispatch stays direct, and ASCII whitespace trimming returns the
same string when possible without allocating a params array. This does not claim
native codec allocation, intermediate expansion CPU or Gzip/Deflate internal
prefetch/validation are fully bounded. Legacy raw-deflate behavior is preserved;
standards zlib framing, response chains, Zstandard and shared dictionaries remain
conformance/development work.

139 added cases cover recognized/mixed/repeated codings, two listeners, byte/text
readers, decoded boundaries, empty bodies, disabled decoding, invalid tokens,
depth 8/9, malformed Brotli at inner/outer layers, chunked framing, all stream read
variants, source ownership, cancellation and sticky malformed-data failure.
The preceding helper fails 96 of the initial 126 HTTP cases; the candidate passes
them and the controls. Pinned Linux passes all 197 selected chain/limit/Brotli
cases. The actual netstandard2.0 assembly hosted by Windows .NET 10 passes all
185 selected public/helper cases, including explicit Brotli rejection; no older
runtime execution is claimed. Both assets build, four allocation-budget groups
and final source/format/YARA guards pass. An initially negated framework directive
was flagged by the source guard and replaced by equivalent supported if/else
structure; no check was weakened. Final changed-source coverage remains pending
at this checkpoint.

### Tests-disabled native build: evidence-capture correction

Native job 37903712236 / 113732004846 failed its additional loader-evidence gate.
The tests-disabled candidate builds and its package consumers pass, as do both
3,522-case managed suites and the selected native ASAN campaign. Its staged native
hash is a401925c352ec9358d3583e35a34972e497dc1cbda421c57df0db8c4a6fcca00.
However the redirected dotnet-test CLI stderr did not identify MsQuic, so the
experiment correctly retains a failing aggregate exit. Passing suites alone do
not prove which dependency the test process loaded.

The required check now runs inside the existing required-QUIC fixture setup. A
test-only macOS dyld image enumeration identifies the actual loaded MsQuic module,
requires one image under the expected build directory and compares its file hash
with the staged receipt. It writes process/runtime/path/hash evidence once per
process. No loader override is added by this inspection and no new test count is
claimed. The CLI-log grep is replaced by this stronger process-local check, whose
Darwin execution remains required. This does not resolve a loader mismatch by
assumption or manufacture a passing result. The test-off CMake mode and current
backend compile definitions are retained for review; private version provenance
is not represented as an official upstream binary.
Final Windows coverage on the changed source passes 3,661 cases (3,656 passed,
five skipped, zero failed in 2m27s). The discovery floor increases by the 139
added cases. The final guard-approved target conditional structure and native
inspection helper are included in that build; the optional Darwin inspection
requires its dedicated environment and remains unexecuted locally. Fresh
exact-head CI and the native deployment experiment remain required.
### Deflate envelope compatibility audit (October 9)

HTTP deflate is a zlib envelope containing DEFLATE, as specified by
[RFC 9110 section 8.4.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.4.1.2).
The existing request and response helpers use raw DEFLATE. The test-only
[content-coding probe](../../test/EmbedIO.ContentCodingProbe/README.md) preserves
an explicit format corpus and decoder observations outside production packages.
On Windows and pinned Linux with .NET 10.0.12, raw/zlib matching decoders return
identical application bytes, while the opposite decoder rejects each sample.
A valid raw stored-block body begins with 78 9C, the same prefix as a common zlib
header, yet returns 156 A bytes only with the raw decoder. Header sniffing alone
therefore cannot preserve every legacy raw body while selecting standard zlib.
The BCL zlib decoder returns complete application bytes for the tested missing
or partial checksum and trailing-byte cases; it rejects a changed checksum.
These are observed incomplete-validation behaviors, not successful HTTP strict
conformance checks. The corpus does not establish all-runtime or all-input results.

The next codec implementation must validate complete envelopes and make the
legacy raw-format migration explicit for requests, responses and cached variants.
A direct decoder swap or two-byte heuristic does not satisfy those requirements.
Current helpers and defaults remain unchanged. Probe observations and binary
inputs are retained under ignored TestResults/http-engine/content-coding-probe
and content-coding-probe-linux. Locked restore, probe execution on both platforms
and source guards pass; the ordinary test discovery floor is unchanged.

### Verified tests-disabled Darwin dependency (October 9)

[Native experiment 37907158871](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37907158871/job/113743283321)
passes on exact source 2246d4151fc0e2e0bd17b23789e767ff91fa47c5. The in-process
Darwin image evidence confirms one loaded MsQuic image from the tests-disabled
candidate build, SHA-256 a401925c352ec9358d3583e35a34972e497dc1cbda421c57df0db8c4a6fcca00,
on macOS 15.7.9 / Arm64 / .NET 10.0.12. Both full suites report 3,661 total,
3,630 passed and 31 skipped, with zero failures. This resolves the preceding
loader-evidence validation gap for this exact experiment, not by inferring a
provider from passing tests.

The private package verifies three native assets and exact notices; its hash is
fa7e68049064e61d2e0e65a0537f034eab23af0f93ec20b661b182cd763ca9e6.
Portable and published consumers each complete 4,096 rebind cycles. Independent
trace inspection confirms all 20,480 candidate traced cycles close the bound
socket before managed disposal returns; controls retain late-close/rebind failures.
The separate ASAN campaign passes 100 repetitions of 12 native cases, exit zero.
Artifacts and independent inspection are under ignored
TestResults/http-engine/quic-loader-evidence-artifact and quic-loader-ordering.json.

Ordinary CI on that same head still fails its macOS rebind test with
AddressAlreadyInUse under the stock dependency. No normal prerequisite, production
package or loader policy was changed to obtain this experimental result. Supported
native deployment, maintenance/provenance policy and final exact-head checks
remain release blockers.

### Incremental DEFLATE boundary validation

The internal DeflateFramingValidator locates the exact RFC 1951 end before any
following envelope checksum or HTTP-framed trailer bytes. It parses stored,
fixed-Huffman and dynamic-Huffman blocks with incremental bit state, validates
canonical trees/repeats/reserved symbols and rejects distances before output
history. It counts decoded bytes without retaining or generating their contents.
Stored payload bytes are counted in batches; input and output bodies are never
buffered wholesale. Tree sizes are bounded by the format. This is a framing
component, not a replacement decompressor or complete zlib/gzip envelope validator;
no existing request/response helper or default has been switched to it yet.

71 focused cases pass on Windows, pinned Linux and the actual netstandard2.0
assembly hosted by Windows .NET 10. They cover three runtime compression levels,
random/repeated data, empty bodies, multiple blocks, one-byte input boundaries,
exact separation of appended bytes, every proper prefix of selected streams,
malformed trees/repeats/lengths/distances and sticky failure. A seeded 1,000-sample
corpus compares framing counts with independently decoded runtime output. Initial
empty-body fixtures used a runtime encoder that emitted no stream; these were
corrected to the valid empty fixed block 03 00. No production rejection was relaxed.
This does not claim all-runtime conformance, checksum integrity or measured
performance. Full changed-source coverage remains pending at this checkpoint.

Final changed-source Windows coverage passes 3,732 cases: 3,727 passed, five
skipped and zero failed in 2m30s. Both library assets build without warnings.
All four existing allocation-budget groups, source/parser guards, changed-file
whitespace checks, shell syntax and pinned YARA checks pass. The discovery floor
increases by these 71 cases. Envelope integration, complete checksum validation,
codec migration and comparative codec performance remain subsequent development;
this component alone does not establish those requirements.

### Strict DEFLATE request streams

The framing component now backs DeflateRequestStream, which retains runtime
inflation and independently verifies its exact boundary and decoded length.
Single-coding and chain request helpers use the raw-format path on both assets.
Eight real-HTTP regressions fail against the preceding helper because it accepts
truncation/trailing bytes; all pass with HTTP 400 after integration. Valid raw
format selection, opt-in compression and final decoded-limit placement remain
unchanged. The empty-body fixture for raw deflate now emits the valid 03 00 stream
instead of an absent compressed stream; malformed acceptance was not restored.
Migration impact is recorded in the existing migration and request guides.

The same internal stream can strip and validate a zlib envelope on both core
targets without ZLibStream or a new production dependency. It validates CMF/FLG,
FCHECK, supported method/window, absence of unnegotiated preset dictionaries,
Adler-32 and full checksum/source completion. Huffman-distance validation enforces
the advertised window. This path is tested directly; public zlib selection,
response/cache migration and codec comparative performance remain unfinished.
Source ownership is once-only, disposal wakes pending reads while inflater
release waits for their exit, concurrent reads are rejected, cancellation retains
its token, and malformed or interrupted decoding becomes sticky. Precancelled
reads do not consume or poison the stream. No global runtime validation switch is
changed, and no whole-body compressed or decompressed buffer is added.

133 component cases and eight HTTP cases were added. All 410 selected cases pass
on Windows, pinned Linux and the actual netstandard2.0 assembly hosted by Windows
.NET 10. This includes array/span/memory/byte and async reads, fragmented input,
empty/large bodies, checksum/header/window mutations, extra spoofed checksums,
source disposal, pending-read cancellation and healthy existing request behavior.
An initial cancellation fixture required the exact base exception rather than
its legitimate TaskCanceledException subtype; it now checks the cancellation
exception family and exact token. No runtime check was weakened. Full changed-source
coverage and final gates remain pending at this checkpoint.

Final changed-source Windows coverage passes 3,873 cases: 3,868 passed, five
skipped and zero failed in 2m31s. Both core assets build without warnings. Four
existing allocation-budget groups, source/parser guards, changed-file whitespace,
shell syntax and pinned YARA checks pass. The discovery floor increases by the
141 added cases. The .NET Standard execution uses a .NET 10 host and does not
claim validation on an older runtime.

An exploratory single-process Windows component comparison is retained under
ignored TestResults/http-engine/deflate-stream-performance (source, raw rounds
and summary). Both decoders read the same 64 KiB bodies into the same 8 KiB buffer,
with 100 warmups and five rounds of 1,000 operations. It compares runtime-only
inflation against added strict framing/checksum work, rather than equivalent
validation policies. Raw low-entropy medians are about 51 us / 280 bytes versus
571 us / 4,936 bytes per operation. Raw random/stored data is about 4.36 us / 280
bytes versus 4.56 us / 576 bytes; repeated data is about 3.27 us / 280 bytes versus
10.51 us / 3,656 bytes. Zlib low-entropy is about 51 us / 312 bytes versus 594 us /
5,064 bytes. This first implementation therefore has a substantial compressed
Huffman parsing/allocation regression; it is not performance-ready. The zlib path
also adds managed checksum cost. These are exploratory component observations,
not end-to-end server measurements or a performance acceptance claim. Optimize
Huffman decoding/tree storage and checksum throughput, then repeat controlled
comparisons and final validation before the engine can meet its performance goal.

### DEFLATE validation hot-path optimization

The bounded bit reservoir now fills up to 64 bits at once, batches literal counts
and returns unused prefetched bytes at the exact final-block boundary. Buffered
stored-block bytes are consumed before direct input batches. Huffman decoding
uses compact prefix lookups with the checked tree fallback for longer codes;
dynamic tables cap their lookup width at six bits, while the shared fixed literal
tree uses nine. Dynamic tree construction references the already parsed length
slices instead of allocating copies. The .NET 10 asset uses stack scratch for
canonical counts/codes; the legacy asset retains supported array scratch. No
framing, reserved-symbol, repeat, window, checksum or completion check is removed.

Six added cases exercise every input split of stored/fixed candidate
streams, appended bytes, exact consumed counts and repeated post-completion feeds.
All 416 selected cases pass on Windows, pinned Linux and the actual netstandard2.0
asset hosted by .NET 10; full changed-source coverage remains pending here.

Frozen strict-decoder builds are compared with identical runner bytes (SHA-256
69d1592e685c7c2af187d418649932cccddeeb6641a64f449c94a97a1a76f69d), alternating
before/after order across three process pairs, five rounds of 1,000 operations,
100 warmups, tiering disabled and the same 64 KiB payloads/8 KiB output buffer.
The baseline core hash is 5fbfa531adac48dbdba8c61dc4920ed2293ee06fa56a305e9117f684e055be36;
the candidate is e084c0e71ab1e37fb790d2d7bf6fdd4b11ea499cb58e4d7878c2e24683c27cec.
Initially rebuilt runners had different hashes; the retained final pairs use the
same runner DLL for both variants. All preliminary data is retained separately.

Windows raw low-entropy median is 452 us to 250 us (4,936 to 4,456 allocated
bytes); Linux is 481 us to 270 us with the same allocation change. Raw random/
stored timing is essentially unchanged (Windows 3.07 to 3.09 us, Linux 3.28 to
3.24 us). Raw repeated timing is also essentially unchanged, while allocation
falls from 3,656 to 2,616 bytes. Zlib low-entropy improves from 475 to 270 us on
Windows and 495 to 295 us on Linux. Windows other zlib samples are essentially
unchanged; Linux random and repeated zlib samples regress from 22.6 to 28.4 us
and 26.9 to 32.7 us respectively. That counterevidence is retained as unresolved;
no universal speedup or final codec-performance acceptance is claimed. Checksums,
remaining parser cost and Linux repeatability require further investigation.
These are strict-component comparisons, not end-to-end engine benchmarks.
Artifacts are under ignored TestResults/http-engine/deflate-performance-before,
deflate-performance-after, deflate-performance-shared-summary.json and
deflate-fast-linux-performance.

Final changed-source Windows coverage passes 3,879 cases: 3,874 passed, five
skipped and zero failed in 2m30s. Both assets build without warnings. All four
existing allocation-budget groups, source/parser guards, changed-file whitespace,
shell syntax and pinned YARA scans pass. The discovery floor increases by the
six boundary cases. The retained Linux zlib timing regressions and remaining
runtime-only gap remain performance work; these passing gates do not close them.

### Bounded SIMD Adler-32 checksum accounting

The internal checksum updater processes 16 bytes at a time through portable
Vector128 widening, weighted sums and accumulation on hardware-capable .NET 10.
The weighted half sums fit ushort lanes, while 5,552-byte reduction batches bound
both uint accumulators. Remaining bytes use scalar accounting. Hardware-disabled
.NET 10 and the netstandard2.0 asset retain scalar paths without a new dependency.
The request stream stores one checksum value instead of separate accumulators;
raw decoding performs no checksum work. Framing/window/checksum validation and
source lifecycle rules remain intact.

121 added checksum cases compare against an independently written scalar
reference: known vectors, input offsets, fragmented updates, lengths around
16-byte and 5,552-byte boundaries, one-million-byte inputs, zero/all-255/random
content, maximum valid initial accumulators, a seeded 500-input corpus and invalid
array bounds. All 331 checksum/strict-codec cases also pass with runtime hardware
intrinsics disabled; the known-vector fixtures verify the opt-out took effect.
All 537 selected checksum/request cases pass on pinned Linux and the actual
netstandard2.0 asset hosted by Windows .NET 10. This does not prove execution on
older runtimes or native Arm64; exact-head platform CI remains required.

The same runner hash 69d1592e685c7c2af187d418649932cccddeeb6641a64f449c94a97a1a76f69d
compares frozen cores e084c0e71ab1e37fb790d2d7bf6fdd4b11ea499cb58e4d7878c2e24683c27cec
and 410830ab8bb3b748f09a65d8482a7da7db4207dff0233f5a61ccc54dafa93f9f using the
preceding paired-process protocol. Windows zlib random median improves from
21.8 to 10.1 us and repeated from 26.5 to 14.7 us; Linux improves from 28.3 to
11.9 us and 32.8 to 16.3 us respectively. These Linux samples are now faster than
the earlier pre-parser-optimization observations, while all historical regressions
remain recorded. Low-entropy zlib improves from 270 to 256 us on Windows and 293
to 273 us on Linux. Raw timing is essentially unchanged. Allocations decrease by
eight bytes per sampled operation. This is a component improvement, not final
performance acceptance or an end-to-end engine claim. Artifacts are under ignored
TestResults/http-engine/adler-before, adler-after, adler-windows-summary.json and
adler-linux-performance.

Final changed-source Windows coverage passes 4,000 cases: 3,995 passed, five
skipped and zero failed in 2m34s. Both assets build without warnings; all four
existing allocation-budget groups, source/parser guards, changed-file whitespace,
shell syntax and pinned source YARA scans pass. The discovery floor increases by
the 121 cases.

Separately, ordinary CI on the preceding head 61fa515 reports the known stock
Darwin rebind failure and a Linux process abort in the BCL
HttpListenerResponse.FormatHeaders / HttpResponseStream.DisposeCore /
HttpConnection.OnRead path. Linux reports only 3,234 cases before the abort, so
its discovery floor correctly fails. The stack and preceding TRX records do not
establish the triggering application's root cause. Logs and the original TRX are
retained under ignored deflate-fast-ci-linux.log and deflate-fast-ci-linux-artifact;
last completed cases concern routing and transport framing, not causal proof.
This crash requires investigation and remains a compatibility/CI blocker. No
check was weakened or rerun characterized as a fix. Fresh checksum-head checks
remain required and the full engine goal is incomplete.

### Native header-crash investigation and owned disposal correction

Exact-head checks for 39bdbabf678f8217e83f866ca112259f774d7a28 all pass, including
CI/Security/Malware aggregates; no failed/cancelled/action-required/active result
was present at inspection. The preceding Linux native formatter abort and stock
Darwin rebind failure did not recur in that run. This is passing evidence for
that head, not proof that either intermittent runtime failure is repaired.
The native macOS Arm64 TRX reports 4,000 cases, 3,969 passed and 31 skipped;
all 121 checksum cases passed. This adds native Arm64 functional evidence to the
checksum checkpoint, without claiming Arm64 comparative performance.

The [.NET 10.0.12 formatter source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.HttpListener/src/System/Net/Managed/HttpListenerResponse.Managed.cs)
uses indexed reads of mutable headers. Its
[native response-stream source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.HttpListener/src/System/Net/Managed/HttpResponseStream.Managed.cs)
already serializes native header sends with its own header lock. A test-only
Linux reflection probe under ignored TestResults/http-engine/native-header-audit
observes direct/inherited null values serializing successfully. Concurrent
clear/add mutation produces ArgumentOutOfRangeException in 200,000 formatting
operations; the synchronized control has zero errors. Repeated common-header
setting produces no errors in the selected campaign. The probe does not reproduce
the CI NullReferenceException, identify its triggering application operation or
prove a production fix. No private runtime synchronization shim is introduced.

An independently reproduced wrapper ownership defect is corrected:
SystemResponseStream previously repeated header preparation and underlying
disposal on every call, allowed disposal reentry and failed to release the owned
stream when preparation threw. Disposal now claims ownership atomically before
callbacks, attempts underlying cleanup in finally and skips header preparation
when the transport is already nonwritable. Repeat/concurrent/reentrant calls do
not prepare or close again. This prevents our wrapper from repeating these
operations; it does not establish that all native header concurrency is safe or
that the observed CI crash is resolved.

Five initial disposal cases fail on the preceding wrapper; the closed-transport
case independently fails before its guard. All six and 100 existing adapter,
cookie and binary-response cases pass on Windows, pinned Linux and the actual
netstandard2.0 core hosted by .NET 10. Final changed-source Windows coverage
passes 4,006 cases: 4,001 passed, five skipped and zero failed in 2m32s. Both
assets build without warnings; four allocation-budget groups, source/parser
guards, changed-file whitespace, shell syntax and pinned source YARA checks pass.
The discovery floor increases by six. Fresh disposal-head CI remains required;
the full engine goal and the native crash investigation remain incomplete.

### QUERY format policy and Structured Fields discovery

The additive, explicitly applied QueryFormatPolicy accepts configured HTTP media
ranges and parameter constraints. It precomputes an immutable Accept-Query
Structured Fields list and ordinary Accept media-range list, advertises discovery
at the resource where it is applied and rejects unsupported exact QUERY requests
with 415. Invalid/ambiguous actual request types yield 400. Other methods retain
their processing behavior. Wildcards are limited to type/* and */*; parameters
are constrained by name/value, with charset case-insensitive and other values
exact after HTTP quoted-string decoding. Discovery uses SF String values, including
numeric-looking parameters, ASCII-lowercase keys and correct quote/backslash
escaping. No existing handler/default is opted into this policy automatically.

The [request guide](../guides/getting-started/requests.md#advertise-and-validate-query-formats)
explains use, representability constraints and remaining application responsibilities.
This completes a format/discovery primitive, not all QUERY semantics: query content
validation, safe/idempotent handling, conditional/range/cache behavior and equivalent
resource mappings remain separate requirements.

51 new cases plus the existing QUERY cases pass locally (78 selected total),
including MIME ranges, parameter cases, configuration ownership, representability,
GET/HEAD/OPTIONS discovery, 415 fields, healthy successor requests and exact HTTP/2
and HTTP/3 responses. HttpClient normalized the first lowercase-method fixtures;
raw TCP fixtures now prove exact lowercase wire methods retain their behavior.
The policy was not weakened to accommodate client normalization. All 78 selected
cases pass on pinned Linux with QUIC required. All 47 applicable policy cases pass
against the actual netstandard2.0 assembly hosted by Windows .NET 10, excluding
HTTP/2 and HTTP/3 in that selected subset; later expanded legacy-host validation below corrects the HTTP/2 assumption. No older-runtime execution is claimed.

An independent http-sfv 0.9.9 parser with typing-extensions 4.16.0 verifies 1,000
seeded generated fields, including every printable ASCII parameter-character
category, quotes, backslashes, numeric-looking values, leading-digit media types
and wildcard items. Parsed values equal the intended source values. The test-only
peer, frozen dependency versions and records are under ignored
TestResults/http-engine/query-format-peer and query-sf-peer; Python is absent from
production. Full changed-source coverage and final checks remain pending here.

Matching now compares media-type segments directly instead of allocating type/
subtype substrings twice, and avoids creating an empty parameter collection for
parameterless requests. All syntax, ambiguity and configured-parameter checks
remain intact. In an isolated Windows paired-process comparison (three alternating
pairs, five rounds of 100,000 matches, 10,000 warmups, tiering disabled and identical
runner bytes c90ee4daf7e84896705ffb6b81ad47bfb1f73ab05e73f0c2299594de319cff80),
exact matching improves from 115 ns / 264 bytes to 75 ns / 32 bytes. Wildcard
matching is 108 to 71 ns with the same allocation reduction; parameter matching
is 192 ns / 424 bytes to 177 ns / 296 bytes, and escaped quoted parameters are
227 ns / 608 bytes to 197 ns / 448 bytes. These measure only policy matching,
not complete QUERY dispatch or server throughput. The remaining allocation
includes framework media parsing. Frozen variants and raw rounds are under
ignored TestResults/http-engine/query-policy-before, query-policy-after and
query-policy-summary.json. The exact request-guide fragment compiles and configures
a server successfully; its separate text-search demonstration is not claimed as
a complete RFC implementation. Final optimized-source coverage remains pending.

Final optimized-source Windows coverage passes 4,057 cases: 4,052 passed, five
skipped and zero failed in 2m32s. The final Linux required-QUIC set passes all 78
cases, and the actual legacy asset passes all 47 applicable cases. Both assets
build without warnings. Four allocation-budget groups, source/parser guards,
changed-file whitespace, shell syntax and pinned source YARA checks pass. The
51 added cases raise the discovery floor accordingly. No existing application
was opted into the policy; exact-head CI and the full engine requirements remain
outstanding.

### Selected-representation precondition evaluator

The additive EvaluatePreconditions request helper applies entity-tag/date
conditions to caller-supplied selected-representation metadata, with RFC ordering,
strong If-Match, weak If-None-Match, existence wildcards, quoted-comma-aware lists
and HTTP-date second precision. It returns 304/412 or null and raises 400 for
malformed entity-tag conditions; invalid conditional dates are ignored. Exact
QUERY follows retrieval validation semantics. CONNECT/OPTIONS/TRACE conditions
are ignored. Caller authorization, ordinary error/redirect decisions, validator
selection, response metadata and safe query processing remain prerequisites.
The helper is explicitly applied; existing conditional/range APIs and default
file-module behavior are unchanged. Range selection, caching, equivalent-resource
assignment and verified replay exceptions remain distinct development requirements.

94 focused cases and four wire cases pass on Windows and pinned required-QUIC
Linux. Real HTTP/1 managed/native, exact HTTP/2 and HTTP/3 tests use validators
that include query content and negotiated output: matching weak validators yield
bodyless 304, different content/negotiation yields 200, failed If-Match takes
precedence with 412 and healthy following requests succeed. The actual legacy
asset hosted by Windows .NET 10 passes 97 cases with one HTTP/3 skip, including
cleartext HTTP/2. Expanded legacy QUERY format-policy validation also passes all
49 cases. This corrects the preceding checkpoint's assumption that this asset
lacked HTTP/2; its earlier 47-case report was a filtered subset, not proof of
unsupported cleartext HTTP/2. Older-runtime and legacy TLS-ALPN execution are not
claimed by these host tests. Full changed-source coverage remains pending here.

Final changed-source Windows coverage passes 4,155 cases: 4,150 passed, five
skipped and zero failed in 2m33s. Both assets build without warnings; four existing
allocation-budget groups, source/parser guards, changed-file whitespace, shell
syntax and pinned source YARA scans pass. The 98 added cases raise the discovery
floor. These gates do not claim complete QUERY caching/ranges or overall engine
completion; fresh exact-head CI and remaining program requirements still apply.

### Selected encoded byte-range helper

The additive TryGetByteRange helper selects one GET/exact-QUERY range against the
caller's encoded representation. It clamps open/oversized ends, handles suffixes,
checks exact strong If-Range entity tags, requires explicit strong-date evidence
for date If-Range and preserves precondition-first ordering through the documented
caller flow. Invalid/unknown/multiple ranges and empty representations are ignored;
supported unsatisfiable ranges carry 416 total-length metadata. Legacy IsRangeRequest
and default file serving are unchanged. Multipart generation, automatic encoding/
range integration and the broader engine program remain unfinished.

The scanner avoids integer overflow by saturating arithmetic while preserving
normalized decimal spans for exact endpoint ordering. Thus two oversized endpoint
numbers are not incorrectly considered equal merely because both saturate. No
BigInteger, substring or framework RangeHeaderValue allocation is needed. A review
caught a weekday-prefix error: only W/ marks a weak tag, not the W in Wed. Three
date regressions accompany that correction.

66 arithmetic/validator cases and eight wire cases pass on Windows and pinned
required-QUIC Linux. The actual legacy core hosted by Windows .NET 10 passes 72
with two HTTP/3 skips, including cleartext HTTP/2. A seeded 1,000-range arithmetic
corpus checks clamping. Real managed/native HTTP/1 and exact HTTP/2/HTTP/3 cases
verify selected bytes, suffixes, 206/304/416 ordering, strong/weak validators and
gzip encoded-byte ranges. Windows native mode returns 400 without application
representation headers for beyond-Int64 Range numerals; bounded overshoot still
must return the correct clamped 206 there. The owned transports must handle the
oversized-numeral path. This platform limitation is recorded rather than describing
the helper as overriding native parsing or weakening managed expectations.
Full changed-source coverage remains pending at this checkpoint.

Final changed-source Windows coverage passes 4,229 cases: 4,224 passed, five
skipped and zero failed in 2m34s. Both assets build without warnings; four existing
allocation-budget groups, source/parser guards, changed-file whitespace, shell
syntax and pinned source YARA checks pass. The discovery floor increases by the
74 added cases. Fresh exact-head CI and the full engine program remain required;
this increment does not claim multipart generation or complete range integration.
### Scanner parsing and canceled QUIC response writes

The exact `272ab41` head failed Linux response-backpressure/reset coverage and
macOS runtime rebind coverage in [CI 37924383408](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37924383408).
The Linux exception came from `Http3QuicExchange.FrameAsync`: a detached writer
can encounter a disposed native QUIC handle after its request has been canceled.
Response DATA/framing now translates that disposal to `OperationCanceledException`
only when the request token is canceled, preserving unrelated disposal failures.
The existing four flow-control/reset cases passed ten consecutive pinned Linux
QUIC runs (40 successes, no skips), and 113 policy/QUIC cases passed on Windows.
This is a cancellation-normalization correction, not proof that every lifecycle
race is eliminated. The macOS stock-MsQuic rebinding race remains outstanding.

[Security 37924383325](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37924383325)
reported a Semgrep syntax error at the nested QueryFormatPolicy primary constructor.
It is now an ordinary constructor with the same immutable fields and behavior.
The pinned 1.178.0 scanner parsed both changed files fully with 29 source rules and
zero findings locally. No accepted finding, rule exclusion or scanner gate was
added. Local selected-file scanning does not replace the full exact-head CI scan.
Evidence is under ignored `TestResults/http-engine/reset-lifetime-*`.

The full Windows coverage run passed: 4,229 total, 4,224 successes, five expected
skips and zero failures. Both library targets built without warnings or errors;
source suppression/parser guards and changed-source whitespace verification passed.

### Bounded multipart representation streaming

The additive `SendRepresentationAsync` helper selects GET/exact-QUERY ranges after
preconditions and streams readable/seekable source content. It combines adjacent
or overlapping intervals, keeps first-request order, omits unsatisfiable members
when others succeed, and frames multiple remaining ranges as multipart/byteranges.
Declared response length includes every boundary, part header and separator.
A received-specification budget (default 16, maximum 128) bounds parser/coalescing
work; excess or malformed/unknown ranges falls back to the full representation.
No existing FileModule/range helper or automatic-compression behavior changes.

Twenty-five component cases cover framing/order/coalescing, mixed satisfiability,
range budgets, precondition precedence, HEAD/no reads/no seeks, source ownership,
blocked-output backpressure, request cancellation and premature source EOF.
Eight live cases cover managed/native HTTP/1.1, HTTP/2 and HTTP/3, identity/gzip
encoded bytes, multipart length, matching preconditions and healthy successor
requests. The selected Windows run passed all 33 with zero skips. The pinned
Semgrep source scan parsed all four added files fully: 29 rules, zero findings;
source parser/suppression and whitespace guards pass.

A positive optional Content-Length on native Windows 304 responses caused resets
and listener disposal in these live fixtures; omitting that field avoids the
failure and is permitted by HTTP semantics. The original failing run is retained
in `multipart-wire-2` through `multipart-wire-4`; the corrected run is
`multipart-wire-5`. Metadata for 412/416 no longer retains selected-source
Content-Encoding when the error response uses different content. Encoded-range
clients reassemble selected representation bytes before decoding; the fixtures
disable automatic decompression to inspect the raw encoded byte ranges.

The first local Linux attempt ran while Windows coverage temporarily instrumented
the shared binaries. Its 33 assertions passed, but the coverage tracker failed
on a Windows mutex path during Linux process exit (134). This attempt is invalid
validation, retained as `multipart-linux`; clean binaries require a separate
post-coverage run. No test or process-exit failure is accepted as success.

Final increment validation: Windows coverage passed 4,262 total / 4,257 successes /
five expected skips / zero failures. The clean post-coverage Linux run passed all
33 cases, required QUIC, zero skips and process exit zero. The actual netstandard2.0
asset passed 31 cases on the Windows .NET 10 host, with two expected HTTP/3 skips;
this does not prove compatibility on older runtime versions. Both targets built
without warnings/errors and all four existing allocation-budget groups passed.
The discovery floor is now 4,262. Broad range/cache/coding integration, native
QUIC packaging and the full engine completion program remain outstanding.

### Owned modern TCP accept loop and admission staging

The .NET 10 endpoint now uses a new owned `TcpAcceptLoop` over the platform's
ValueTask accept API. It retains Windows preallocated accept sockets independently
of runtime completion state, rearms before delivering the previous socket, cleans
up failed/stopped admission, retries transient socket errors with bounded backoff
and yields after 64 inline completions. Accepted sockets transfer only after the
synchronous admission callback returns. A tracked worker task makes shutdown
completion observable. The existing macOS IPv6 blocking-accept workaround and
.NET Standard event-based compatibility path remain in place; this does not
complete endpoint/connection replacement or the default/deprecation requirement.

Connection initialization is queued separately with execution context preserved,
after registration transfers ownership to the connection registry. This prevents
TLS authentication's work before its first await from serializing the accept
actor. Queueing failure disposes the registered connection. The modern initial
header/TLS deadline starts at construction, before queued initialization, while
reused-request deadlines retain their existing behavior and timeout values.
An internal constructor accepts a shorter initial timeout for transport-policy
validation; the retained two-argument constructor keeps the 90-second default.

Nine new cases cover queued and late sockets, failed admission/healthy successors,
pending accept cancellation, connect/stop races and plain/TLS queued initialization
that expires without any read being started. Zero-peer shutdown executes through
the first pending await before stopping, so it actually exercises an armed accept.
The worker-lifetime fixture now requires a modern actor or the macOS IPv6 worker,
and awaits whichever exists; its four previous failures were obsolete platform
expectations, preserved under `tcp-actor-full`. No shutdown assertion was removed.
The final focused set passes 101 cases on Windows and pinned Linux; the actual
netstandard2.0 asset passes 92 with nine expected modern-path skips on a .NET 10
Windows host. Older runtime compatibility is not inferred from that host.

Performance work rejected the first candidate's substantial concurrent TLS
slowdown. Rearming alone did not fix it; separating connection initialization
recovered that throughput. All preliminary logs are retained. The final comparison
uses the identical harness SHA-256
`5577de0c33d7d9ea52144b36c9182bc34121c7bde19dcfd3e570d8ac6fe27279`
with frozen `42b1af7` baseline core
`330136ebfb15ac10f827b69a6f06d4ed1df183a96108fed91be84d8db524bff2`
and candidate core
`9ee47aba00f3e1e03c49fd615570ba69c0b399ae887910c7dfdebf59b1345d5c`.
The Windows .NET 10.0.12 loopback client/server share one process, use 16 workers,
1 KiB responses, close each connection, and run three alternating pairs with six
rounds of 32 requests per worker (18 samples per variant/protocol, zero errors).

| Workload | Baseline mean requests/s | Candidate mean requests/s | Baseline/candidate mean p99 ms | Baseline/candidate median p99 ms | Baseline/candidate process bytes/request |
| --- | ---: | ---: | --- | --- | --- |
| HTTP churn | 14,710 | 14,618 | 1.76 / 1.75 | 1.52 / 1.55 | 37,604 / 37,635 |
| TLS churn | 3,977 | 3,904 | 7.17 / 8.42 | 5.89 / 5.90 | 43,263 / 43,123 |

These are limited local comparisons, not an extreme-performance or tail-parity
claim. TLS mean p99 remains higher and needs broader, longer isolated workloads.
Earlier candidate tail spikes coincided with Gen-0 collections absent from that
short baseline sample; that correlation does not prove the complete cause.
The final identical harness binary was copied into the ignored baseline build;
only its core remained frozen, and source/archive/hashes/raw samples are retained
under `TestResults/http-engine/tcp-actor-*`. Allocation measurements include both
client and server, and must not be presented as server-only allocation results.

[Multipart-head CI 37927174272](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37927174272)
passed the desktop suites but failed Windows HTTP upload verification with a
20-second client timeout (64 KiB fully consumed upload, four workers, connection
churn). It did not fail an allocation ceiling. Six local verification runs each
against the baseline and staged candidate passed, including retained stream/timer
cleanup. No original CI cause or repair is claimed from those local passes.
The failed job log is retained as `multipart-ci-windows-budget.log`.

The final changed-source Semgrep scan parsed all five relevant files completely
with 29 rules and zero findings; pinned YARA-X/Forge reported no matches. Source
suppression/parser guards pass. No scanner acceptance or check weakening was added.

Final changed-source validation passed: 4,271 Windows coverage cases, 4,266
successes, five expected skips and zero failures. Both retained library targets
build without warnings/errors. All four allocation-budget groups and final
GET/64-KiB-full/partial/unread HTTP verification workloads passed, including
retained stream/timer cleanup. The discovery floor is now 4,271. The broader
owned endpoint/connection transition, conformance/fuzz coverage and comparative
extreme-performance work remain incomplete.

### TCP accept failure ownership

The new accept actor rearms before admission, so an unrecoverable admission
exception or a throwing diagnostic listener can occur with the next accept
already armed. Two controlled tests failed on `d081ea3`: the worker propagated
its exception while the listening socket stayed open. On Unix, an accept result
could also remain unconsumed because no Windows-style preallocated socket owns
that result. This was a discovered lifecycle gap, not an observed exploit.

The actor now asks its endpoint owner to stop accepting exactly once, awaits the
armed operation on the failing branch, and disposes any socket returned by it.
Expected native abort/cancellation errors during that cleanup do not replace the
original admission/diagnostic failure. The actor never directly disposes its
borrowed listening socket. Its owner performs shutdown through the same admission
stop path used normally. Native SocketException/ObjectDisposedException retry
handling is explicitly excluded after an admission failure, so a diagnostic
exception with one of those types cannot be mistaken for an accept result.

Four regressions cover synthetic nonrecoverable admission failure (an explicitly
constructed OutOfMemoryException, not real memory exhaustion), and diagnostics
throwing I/O, disposed-object or socket exceptions. They require the original
exception, a closed listening handle, and closure/reset of accepted/pending/backlog
peers. Both initially failing cases and the corrected results remain under ignored
`TestResults/http-engine/tcp-failure-*`. All 105 focused cases pass on Windows and
pinned Linux. The actual netstandard2.0 asset passes 92 with 13 expected modern
actor skips on a Windows .NET 10 host; no older-runtime coverage is inferred.

Both targets build without warnings/errors. The pinned Semgrep source scan parses
all three changed files fully: 29 rules, zero findings. Suppression/parser guards,
changed-source whitespace checks and diff checks pass. The preceding `d081ea3`
revision completed every GitHub check successfully, including Windows upload
verification. That pass does not establish the cause of the earlier upload timeout.
Whole-engine fault injection, fuzzing and resource validation remain required.

Final Windows coverage passed: 4,275 total, 4,270 successes, five expected skips
and zero failures. All four existing allocation-budget groups and the final
64-KiB upload/retained stream-and-timer verification passed. Pinned YARA-X/Forge
reported no matches. The discovery floor is 4,275; this correction does not
complete whole-engine fault injection or the overall development goal.

### Owned accept loop in both retained assets

The retained netstandard2.0 asset now selects the same owned TCP accept actor,
using its available Task-based Socket.AcceptAsync overload. The .NET 10 asset
keeps the ValueTask overload. Both retain independently owned Windows pending
sockets, failure cleanup, admission staging with execution-context flow, bounded
inline processing and deadlines before queued initialization. The macOS IPv6
blocking-accept workaround remains. No target, public entry point, enum value,
runtime dependency group or native prerequisite changes. The inherited callback
helpers remain for existing white-box regression fixtures; they are no longer the
default accept path. Their removal and the wider owned connection/endpoint
transition remain required before claiming complete Mono deprecation.

All 105 actor/boundary/ownership/WebSocket/reset cases pass on Windows and pinned
Linux against both actual target assets, without modern-actor skips. These runs
use .NET 10.0.12 hosts; an older-runtime probe is separate evidence. A standalone
C# program compiled with the installed framework compiler loads the actual
netstandard2.0 DLL on CLR 4.0.30319.42000 / Framework Release 533509 and passes
32 fresh plain plus 32 TLS HTTP/1.1 connections. It verifies response bytes,
closes every source/client stream and stops the real listener. This does not
prove all legacy runtimes or all protocols on that runtime.

The first framework TLS harness lacked a target-framework declaration. It failed
TLS on both the frozen baseline and candidate while plain HTTP passed. Declaring
the program's actual .NET Framework 4.8 target and startup compatibility metadata
made both plain and encrypted candidate runs pass. Only the ignored harness was
retargeted; no TLS protocol policy, trust store or machine setting changed.
[Microsoft's framework TLS guidance](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls)
explains that default protocol behavior depends on the application's target, not
just the installed runtime. Original failing and corrected logs/program/config
are retained under `TestResults/http-engine/tcp-common-netfx*`.

The hosted legacy-asset comparison uses identical harness SHA-256
`638d0b7f8e117e8cb699c5d34fabac47586096f2880b3d0a912f2efdc999af66`,
frozen `a093a77` legacy core
`330fec288d98e46f28a45afe15bb18d44c65da80aad6ec9fa7d5c6c3c940709a`
and candidate legacy core
`88f54839d27bdf559474452a259971bccf31a5cb985c7be3cb7fa3f9044ab38d`.
Three alternating Windows .NET 10.0.12 pairs use 16 workers, 1 KiB responses,
per-request connections, six rounds of 32 requests per worker: 18 samples per
variant/protocol and zero request errors. Allocations include client and server.

| Workload | Baseline/candidate mean requests/s | Baseline/candidate mean p99 ms | Baseline/candidate median p99 ms | Baseline/candidate process bytes/request |
| --- | --- | --- | --- | --- |
| HTTP churn | 15,519 / 15,245 | 1.78 / 1.70 | 1.67 / 1.49 | 37,930 / 38,044 |
| TLS churn | 3,954 / 3,967 | 7.33 / 5.85 | 5.64 / 6.01 | 43,547 / 43,491 |

This limited loopback comparison supports continued development; it is not a
server-only allocation measurement, older-runtime performance measurement or
an extreme-performance claim. The Task-based path adds modest allocation in the
plain case, while TLS tail statistics remain mixed. Comprehensive load/tail
comparisons remain open. Raw samples, hashes and frozen binaries are under ignored
`TestResults/http-engine/tcp-common-*`. Both assets build without warnings/errors;
changed-source suppression/parser/format guards pass and the pinned Semgrep scan
parses all three changed files completely (29 rules, zero findings).

Final modern-asset Windows coverage passed 4,275 total / 4,270 successes / five
expected skips / zero failures. The actual netstandard2.0 candidate also passes
all four allocation-budget groups and GET/full/partial/unread 64-KiB request-body
verification, including retained stream/timer cleanup, on the .NET 10 Windows
host. Pinned YARA-X/Forge reports no matches in changed production sources.
The wider endpoint/connection replacement and final default/deprecation audit
remain incomplete.

The preceding a093a77 macOS job 113828349062 in CI 37933084461 failed
ShutdownDoesNotWaitForAnUncooperativeApplication(False,False,True) after the
30-second fixture deadline at Http3QuicShutdownTest.cs:210. The full job log is
retained as tcp-failure-ci-macos.log. This is a specific outstanding HTTP/3
shutdown validation failure; it is not attributed to TCP acceptance or claimed
repaired by this common-asset increment. Final exact-head checks remain required.

### Deterministic QUIC shutdown preparation and baseline observation

macOS job 113828349062 in CI 37933084461 timed out waiting for the stalled
application's Entered signal, before invoking transport shutdown. The old fixture
started two final responses, polled a transient output-user count and left the
client receive window at its runtime/native default. Those choices did not prove
that one DATA write was blocked while another waited for the output semaphore.
This identifies fixture weaknesses; it does not establish the precise timing or
native behavior in that failed run.

The fixture now sends one final header section, starts a bounded large DATA write
and a queued final empty DATA write, and gives the client explicit 64 KiB stream /
1 MiB connection receive windows. It requires two active writers and rejects
premature writer completion. Preparation failures fault Entered immediately and
observe output-task faults; phase/task diagnostics distinguish preparation from
shutdown failure. Existing blocked-application, late-read/write rejection,
semaphore-lifetime and callback-cleanup assertions remain. No production HTTP/3
code, receive-window default or timeout was changed. Eleven shutdown/backpressure
cases pass locally, and four shutdown variants pass 20 consecutive required-QUIC
Linux runs (80 successes, no skips). macOS exact-source validation remains required.

A separate macOS job 113837440034 in CI 37935808167 failed compatibility audit
because pinned upstream's native Unix listener completed RunAsync successfully
but prematurely, rather than faulting it with ObjectDisposedException. It still
served only the first correct DTO, had no successor response, stopped and threw
AggregateException on cancellation. Both Neo assets produced the required two
DTOs and clean cancellation. Original downloaded reports remain under
`tcp-common-ci-macos-budget-original`; local reviewed rechecks are separate.

The characterization contract now records that complete upstream alternative and
allows its distinct runError marker at exactly one reviewed difference. It never
rewrites the observed report or changes Neo's required healthy outcomes. Exact
manifest, API, payload/status and all other differences remain mandatory. New
negative checks reject unknown baseline errors, wrong DTOs, premature Neo
completion and missing Neo successors; additional checks reject mixed baseline
combinations. Both reviewed upstream markers pass the same negative guards.
The original Mac report rechecks as 143 cases / 286 comparisons with zero errors;
a fresh Windows audit passes 207 cases / 414 comparisons with zero errors.

Evidence is under ignored `TestResults/http-engine/quic-shutdown-*`. Both assets
build warning-free; changed-source parser/suppression/whitespace guards pass. The
pinned Semgrep scan parses both changed source files fully (106 rules, no findings),
and pinned YARA-X/Forge reports no matches. No scanner acceptance was added.
Whole-engine fault injection/conformance and final platform checks remain open.

The full Windows coverage rerun passed: 4,275 total, 4,270 successes, five
expected skips and zero failures. The test discovery floor is unchanged.
This fixture/characterization correction changes no production engine code.

### Retiring the callback TCP accept implementation

Both retained assets now use the owned TCP accept actor. The unused
SocketAsyncEventArgs accept implementation and its private callback helpers have
been removed from EndPointListener. This changes no public API, target,
dependency, timeout, prefix policy or production accept selection. The macOS
IPv6 blocking workaround remains; its replacement and the wider endpoint /
connection rewrite are still open.

The former backlog regression now queues all 32 or 128 peers before starting the
actual actor, admits them through the production endpoint, checks every distinct
request and admits a subsequent fresh connection. Its cleanup awaits the actor.
The obsolete fabricated event-completion cases were replaced by five late-admission
cases (one through 128 real connected sockets) that require the stopped production
endpoint to release each handle and close the peer. The actor's existing pending-
accept shutdown, independent Windows handle ownership, fatal admission and
concurrent-stop tests remain. Actor tests now fail rather than skip if either
retained asset lacks the replacement implementation.

All 100 focused accept / framing / reset / WebSocket shutdown cases pass on
Windows, pinned Linux .NET 10.0.12 and the actual netstandard2.0 core on a Windows
.NET 10 host, without skips. This is not a full older-runtime validation. Full
Windows coverage passes 4,275 total / 4,270 successes / five expected skips /
zero failures. Both assets build with no warnings or errors. Changed-source
parser, suppression, whitespace and diff checks pass; pinned Semgrep parses all
four changed source files completely (29 rules, zero findings). Logs and TRX
reports are retained under ignored TestResults/http-engine/tcp-retirement-*.
Final exact-head cross-platform checks remain required.

### Owned endpoint route snapshots

EndPointListener now delegates registration, removal, ownership checks and
lookup to EndpointRoutes. Each mutation publishes one immutable generation with
separate named, star and plus arrays. Readers borrow that complete generation
without a registration lock. Arrays are ordered by descending path length with
newer equal-length registrations first, so selection can stop at the first
eligible match. Existing named/star/plus precedence, wildcard actual-path-before-
added-slash behavior, URL decoding, ordinal path comparison, named authority
checks, duplicate rules and wrong-owner removal semantics are retained.
The added-slash match compares existing strings instead of allocating a suffix.
Public APIs, prefix defaults, targets and runtime dependency groups are unchanged.

Thirteen additional cases cover named authority/path boundaries and eight
concurrent writers publishing/removing 256 independent registrations in each
host category while preserving an anchor and rejecting wrong-owner removal.
The 157-case Windows focused set passes. The pinned-upstream Windows audit passes
207 cases / 414 comparisons / zero errors. All four existing allocation budgets
and GET/full/partial/unread 64-KiB HTTP/TLS verification pass, including retained
stream/timer cleanup.

The routing component benchmark uses identical compiled harness SHA-256
`960933a146a25eb65d1438f9826ea05f33d8657f8c42e9fa4d8ffac1a4177272`,
frozen prior legacy core
`88f54839d27bdf559474452a259971bccf31a5cb985c7be3cb7fa3f9044ab38d`
and candidate legacy core
`0400cbf3410d9de10c1df34d549e95a81b5cb9f260506124e63dd746f9bccd2e`.
Both run on Windows .NET 10.0.12 in three alternating pairs, five rounds per
pair, 100,000 lookups per round, with one, 64 and 256 routes. Last-route hits,
uniform hits and misses all verify their expected owner. Registration, reflection
and delegate compilation are outside the timed region. This measures dispatch,
not complete HTTP requests or older-runtime performance.

Initial combined-array and unsorted-partition candidates regressed larger
wildcard lookups; those samples are retained rather than discarded. Sorted
snapshots reduced allocation from 88–96 bytes to 40 bytes per lookup across the
measured cases. Default-runtime uniform named 64-route timing regressed from
304.1 to 422.6 ns, while the 256-route counterpart improved from 1,118.9 to
300.3 ns. A separate controlled comparison disables tiered compilation for both
benchmark processes only. Its median uniform-hit results (15 samples per cell)
are below; production runtime settings are unchanged.

| Routes / host category | Prior / candidate median ns |
| --- | --- |
| 1 / named | 43.8 / 38.4 |
| 64 / named | 509.1 / 132.5 |
| 256 / named | 1,871.3 / 407.5 |
| 1 / star | 35.6 / 31.4 |
| 64 / star | 224.6 / 125.4 |
| 256 / star | 747.9 / 385.7 |
| 1 / plus | 38.7 / 33.3 |
| 64 / plus | 228.4 / 128.2 |
| 256 / plus | 747.1 / 387.8 |

Controlled 256-route star misses remain close (1,399.3 / 1,411.7 ns), and miss
lookup remains linear. These results support continued development, not a
universal improvement or extreme whole-server performance claim. Raw default,
controlled and earlier candidate measurements are retained under ignored
TestResults/http-engine/endpoint-routes-*.

The first full sorted-source coverage run had one NoBufferSpaceAvailable failure
in a client's ConnectAsync, before the malformed-target test reached dispatch.
The subsequent 74-case target/routing subset passed. This does not establish the
cause or prove that broader resource behavior is repaired. The original log is
retained separately from the full-suite repeat.

Prior commit 9345596 CI also failed the known macOS native QUIC same-port rebind
probe and a Windows TLS 64-KiB full-body upload warmup with a 20-second client
timeout. Those logs are preserved as endpoint-routes-prior-*. The local final
upload verification passes; it neither reproduces nor explains the original
CI failure. Native QUIC deployment, complete connection replacement, whole-engine
fuzzing/conformance and end-to-end performance work remain open.

The unchanged final sorted-source Windows coverage repeat passes 4,288 total /
4,283 successes / five expected skips / zero failures. Final focused validation
also passes all 157 cases against the actual netstandard2.0 core on a Windows
.NET 10 host, and 155 cases on pinned Linux .NET 10.0.12 with two Windows-native
close cases intentionally skipped. Both assets build warning-free. The final
pinned Semgrep scan parses all four changed C# files completely (29 rules, zero
findings, no tool diagnostics); parser, suppression, whitespace, shell syntax and
diff guards pass. Discovery floors in ordinary CI and the native experiment are
raised to 4,288. Exact new-head macOS/CI checks remain required; no merge or release
readiness is claimed.

Pinned YARA-X / Forge also reports no matches in all four changed C# sources.

### Windows TLS upload stall investigation

Six fresh verification runs of the exact 64-KiB full-body HTTP/TLS workload pass
with DOTNET_PROCESSOR_COUNT=2. This changes the runtime's processor-count view;
it does not impose an OS CPU quota or reproduce the Windows CI machine. The
20-second client timeout and production transport code are unchanged. The
previous failed warmup remains unresolved rather than being attributed to
scheduling, socket pressure or routing without evidence.

The performance harness now captures failure-only diagnostic JSON alongside the
original exception. It samples pending admission and registered/retained
connections, accept-worker state, thread-pool counts, TLS authentication,
read-buffer allocation, context binding, remaining fixed-length body bytes and
response-finalization state. Collection samples are limited to 32 entries per
registry and 32 live connection records. A pending-registry lock attempt has a
five-millisecond bound; busy registries are reported. These cross-thread field
reads are best-effort observations, not one atomic transport snapshot. No
request/header/body data is collected and successful request loops do no new
diagnostic work. Expected reflection/race failures report unavailable metadata;
nonrecoverable exceptions are not swallowed. Production code, public APIs,
timeouts, targets and runtime dependencies are unchanged.

An ignored standalone probe exercises actual pending admission, an admitted
unread 32-byte body and completed listener cleanup on Windows and pinned Linux;
the observed state matches those phases. The rebuilt upload verification also
passes. Logs, the probe and constrained-runtime results are retained under
TestResults/http-engine/tls-stall-*. Exact new-head CI remains required. This
improves evidence for a future failure; it is not a claimed correction of the
historical TLS stall or the separate native QUIC rebind defect.

### Native deployment validation on both macOS architectures

The optional pinned-source cleanup/OpenSSL experiment now has independent
osx-arm64 and osx-x64 jobs with distinct artifacts. It derives the candidate RID
from the actual host, verifies the matrix expectation and propagates that RID
through staging, receipts, private package paths, restore and publish. The Intel
job uses GitHub's [documented macos-15-intel runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
Normal CI native prerequisites and all production package/dependency groups
remain unchanged. The private candidate remains 0.0.0-local and is not published.

Package verification now checks each actual thin 64-bit Mach-O CPU type and dylib
header as well as the exact RID asset set, SHA-256, notices, empty framework
placeholder and receipt. It rejects renaming an ARM binary/receipt as Intel.
Eight retained mutation cases use the real artifact and require rejection of
relabelled RID, wrong CPU, invalid magic, executable type, truncation, extra RID,
missing alias and altered notice. The expected-error checks run in each deployment
job before consumer restore. Local validation repacks the previously verified ARM
candidate through the parameterized project: all three native hashes/headers and
notices match, and all eight mutations are rejected. This does not establish
Intel native execution. Exact-source native matrix results remain pending until
both jobs finish successfully. Wider RID support, production deployment, update
policy and final engine conformance/performance remain open.

Evidence is under ignored TestResults/http-engine/native-dual-rid-*. The pinned
zizmor workflow audit has no findings, shell syntax checks pass and the ordinary
solution does not acquire new targets or native runtime dependencies.

The first osx-x64 experiment job 113869693050 in run 37945234080 failed before
native compilation: fetching the pinned OpenSSL tag returned Git's "shallow file
has changed since we read it" error. The original job log is preserved as
native-dual-rid-intel-first.log. This is not an Intel transport result. Source
fetches now retain each attempt and retry only that exact shallow-state error,
up to three attempts. Invalid references and other failures return immediately;
exhausted retries still fail. Automatic Git maintenance is disabled for these
fetch commands, without claiming it caused the observed failure. The immutable
source SHA, annotated tag SHA, peeled commit, signature fingerprint and crypto
version checks remain unchanged. Controlled fixture checks verify recovery on
the third attempt, immediate permanent failure and exhausted retry failure.
The existing ARM job remains a distinct run; new native validation is required
before claiming the Intel candidate works.

### Complete native candidate archive contract

A Windows repack omitted the source wildcard even though the four evidence files
were present; the original macOS-built package included them. The nuspec now
lists those files explicitly, and a fresh local pack includes all four. This
identifies a packaging discrepancy, without claiming its precise SDK/globbing
cause. The verifier requires the complete archive entry set, one NuGet core-
properties entry, exact private identity/version, the empty .NET 10 dependency
group, notice/readme metadata and unambiguous manifest structure. Unexpected
build targets or managed assemblies cannot pass the contract. Each packaged
source file must match the candidate; the two patches also match the checked-out
reviewed patches with only CRLF/LF normalization. Native architecture/hash and
license checks remain mandatory.

Seventeen mutations now cover the original eight native/notice cases plus
missing source, injected build or managed assets, changed package evidence,
a jointly altered candidate/package patch, an added dependency, duplicate
metadata and internal/external DTD declarations. All are rejected for their
expected reason on Windows and a pinned isolated Linux interpreter. The real
ARM candidate is repacked and accepted by the full contract. This does not
establish Intel native execution or prove native binary provenance from XML
metadata alone; native source/build/loaded-image checks remain separate gates.

The first local source scan identified standard-library XML parsing. It was
corrected using [defusedxml 0.7.1](https://pypi.org/project/defusedxml/0.7.1/), whose
universal wheel is pinned by SHA-256 in the fixture's requirements. DTD, entity
and external-reference processing are explicitly forbidden. The dependency is
installed only under test results for verification and is not a production or
NuGet dependency. The original finding is preserved; the final pinned Semgrep
scan fully parses both verifier files (79 rules, zero findings, no diagnostics).
Shell syntax and suppression/diff guards pass. Evidence is under ignored
TestResults/http-engine/native-complete-contract-*. Native execution results and
final exact-head checks remain required; no release or deployment is claimed.

### Intel native experiment execution budget

Run [37945962762](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37945962762)
validated the candidate's Intel native datapath suite, ten rebind probes, five
connected QUIC repeats, relocated/package consumers and 100 sanitizer repeats.
The unchanged control reproduced both expected native defects. The two full
coverage runs stopped below the mandatory 4,288-test floor: 2,192 and 2,188
results, with no completed assertion failures. Their last completed tests were
near the five-minute execution deadline; cleanup extended the process lifetime.
This remains a failed validation, not a full-suite pass or proof of a hang's cause.

The Intel experiment now gives each full coverage run 15 minutes and its complete
native-build job 75 minutes. ARM retains five-minute suites and a 45-minute job.
The selected budget is retained in the artifact. Test floors, assertions, native
image verification, source pins and sanitizer checks are unchanged. Intel stays
in scope following William's correction; a fresh run must complete every gate
before the Intel candidate can be accepted.

### Conformance finding F3: request failure isolation

The conformance helper's HTTP/3 upload-cancellation reproduction failed against
`f1bbe41` in a pinned Linux container. A monitored server observed one fatal
callback and `Listener.IsListening == false` while its public state still read
`Listening`. Earlier failure logs are retained separately. This confirms a
listener-wide consequence rather than inferring one from a closed client connection.

The candidate request boundary handles recoverable errors from multiplexed
contexts as stream failures. The existing response flush, completion callbacks
and asynchronous context close still run; a faulted context completion remains
faulted for its protocol owner. Process/resource-corruption exceptions continue
to propagate through the existing exception policy. Public APIs and library
targets do not change.

The enabled F3 regression cancels 400 uploads, checks that fatal cleanup was
never called, checks the actual listener and probes fresh client connections.
Five monitored Linux repeats passed (2,000 cancelled uploads), and the monitored
Windows regression also passed. The HTTP/2, HTTP/3, cleanup and fatal-propagation
set passed all 806 cases on each of Windows and Linux. Existing hot-path,
listener-queue, cold-start and listener-allocation budgets passed. Final full-suite
and exact-head repository gates are still required. The other eight supplied reproduction
cases remain Explicit for unresolved F1/F2/F4 work; enabling F3 is not a claim
that the full conformance campaign passes. Evidence is under ignored
`TestResults/http-engine/f3-*`.

### Conformance finding F4: shared HTTP/2 write cancellation

Two controlled writer cases fail on `2e579ec`: cancellation after a partial frame
interrupts the shared transport, and cancellation while waiting for its gate
reports a connection-wide output failure despite writing no bytes. The candidate
separates cancellation while queued from cancellation of committed I/O. DATA
uses its request token until writing starts, then the owning connection's token
for the complete frame batch. Connection shutdown and genuine partial I/O failures
still terminate unusable output.

An unstarted canceled DATA frame returns its reserved flow-control credit. The
connection credit is returned even if RST_STREAM already removed the stream's
window, allowing a blocked sibling to proceed without a peer WINDOW_UPDATE.
HPACK encoding mutates a connection-wide table, so a header block that has been
encoded is committed with connection cancellation; a reset cannot leave the
peer missing table entries referenced by the next response.

Five controlled regressions cover partial and queued reset, credit return after
stream removal, decoding successive HPACK blocks and connection cancellation.
The supplied 500-trial wire reproduction is enabled. Three Linux wire repeats
pass (1,500 trials), and the broader HTTP/2, HTTP/3, cleanup and fatal-propagation
set passes all 812 cases on Windows and Linux. Hot-path, listener-queue,
cold-start and listener-allocation budgets pass. Both library targets build;
parser/suppression guards pass. Full-suite and final exact-head checks are still
required. Settings-shrink ordering (F5), stream-state errors (F6), truncated
fixed bodies (F1) and malformed-body status handling (F2) remain separate work.
Evidence is retained under ignored `TestResults/http-engine/f4-*`.

### Budgeted Intel native experiment result

The Intel job in run
[37952457798](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37952457798)
completed both coverage suites on `f23a5a5`: each reported all 4,288 cases,
4,256 passed, 31 skipped and one failure, taking about 9.5 minutes. Increasing
the execution budget allowed full discovery/completion; it did not clear genuine
failures. Each failure was reported by
`CancellationDuringUpgradeReleasesAcceptAndAllConnectedTransports(Microsoft)`.
Later Intel triage traced it to five-second own-hostname lookups during native
listener prefix registration, exhausting that fixture's 30-second deadline;
it did not establish an upgrade-completion defect. The correction in
[37959455344](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37959455344)
reduced the lookup from 5.016 seconds to 0.008 seconds and removed that failure.
That run still failed the separate HTTP/3 cancellation test on both macOS
architectures and an HTTP.sys case on Windows, so Intel acceptance remains open.
The original job remains failed, and its logs are retained as
`TestResults/http-engine/native-intel-budgeted.log`. This is older managed source
than the F3/F4 candidates and does not validate their exact head. The ARM native
comparison on the same revision passed. Intel remains in the support scope.

### Conformance findings F1/F2: HTTP/1 body completion and status

All seven supplied F1/F2 cases fail on `998fe15`: four read paths treat a 3-of-10
byte fixed body as complete, and three malformed chunk cases return 500. The
candidate detects truncated fixed bodies in synchronous, array-async and
memory-async transport reads, retains terminal failure and preserves unknown-
length EOF. It records the actual framing exception so the request boundary
can distinguish parser failures from ordinary application data errors, including
wrappers retaining the original cause.

Uncommitted responses become generic 400 and do not reuse the connection.
Committed responses are aborted without rewriting headers or emitting a valid
chunk terminator. Seven additional cases cover both committed-body failures,
server-error preservation, wrapped framing errors and sticky failure/empty-read/
cancellation behavior across three read paths. The enabled supplied cases pass.
The Linux body, resource, fatal-policy and decompression set passes 286 cases;
Windows expanded validation also passes all 286 cases. Hot-path, listener-queue,
cold-start and listener-allocation guards pass. Both
library targets build. Full-suite and exact-head gates remain required, along
with the remaining HTTP/2 settings ordering and stream-state findings. Evidence
is retained under ignored `TestResults/http-engine/f1-f2-*`.

### F6 stream-state review against the modern baseline

The engine now distinguishes a new request (`:method`) using a closed or skipped
identifier from closed-stream trailing traffic. Two controlled cases fail on
`b4e725f` and pass with the candidate: opening a lower skipped ID and reopening
an already completed ID. The rejected request raises connection PROTOCOL_ERROR
without creating another active object. The existing 2,500-iteration registry
check retains its bounded-state assertions and now expects that error for new
requests on skipped IDs. All 65 registry/header/HPACK/dispatcher cases pass on
Windows and pinned Linux; both library targets build and source guards pass.

The legacy h2spec findings need individual classification against the modern
baseline. [RFC 9113 section 5.1](https://www.rfc-editor.org/rfc/rfc9113.html#section-5.1)
permits minimal processing and discard on fully closed streams, while requiring
STREAM_CLOSED for prohibited frames on half-closed-remote streams. Header blocks
still update HPACK state and discarded DATA still consumes connection credit.
[RFC 9218 section 2.1](https://www.rfc-editor.org/rfc/rfc9218.html#section-2.1)
permits ignoring legacy priority signals when the server advertises that policy.
Those behaviors must not be removed solely to satisfy older h2spec expectations.
The new-request identifier guard follows
[RFC 9113 section 5.1.1](https://www.rfc-editor.org/rfc/rfc9113.html#section-5.1.1).

The original h2spec failures remain recorded; no aggregate tool result has been
converted to success or its tests disabled. Fresh independent wire validation
and exact-head CI remain required. F5 settings/queued-DATA ordering is still open.
Evidence is under ignored `TestResults/http-engine/f6-*`.

### Independent lower-stream-ID wire regression and combined validation

A raw client opens HTTP/2 stream 3, waits for its response headers and then sends
an initial request on stream 1. The regression verifies GOAWAY on stream zero,
PROTOCOL_ERROR and last-stream-ID 3, then checks a new independent HTTP client
still succeeds. It passes on Windows and pinned Linux without using the engine's
frame decoder in the client. The discovery floor now includes the extra case.

The first local full combined WebSocket/engine run on `dad10ff` reports all 4,341
cases: 4,311 pass, 29 skip and one fails. The failure is the old plain-HTTP
truncated-body fixture, which expected successful EOF. Its assertion now requires
IOException for truncation, specifically EndOfStreamException on the plain
transport, while preserving partial-byte and subsequent healthy-listener checks.
The low-level application responds 400. The 63-case boundary/wire set passes on
Windows. The original failed result remains under ignored
`TestResults/frame-boundary/combined-linux-full`; a fresh combined run is required.

### HTTP/3 cancellation fixture certificate provisioning

The macOS failures of `CancelledHttp3UploadsDoNotStopTheListener` in runs
37965353933 and 37966714185 occur at the first warm-up handshake, before the
cancellation workload. The fixture imported its certificate with default key
storage flags, whereas the existing QUIC fixtures explicitly use `Exportable`.
The [.NET QUIC portable credential path](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Quic/src/System/Net/Quic/Internal/MsQuicConfiguration.cs)
exports the certificate and private key as PKCS#12. The fixture now requests
an exportable key as the other QUIC fixtures do; the 100 rounds, 400 cancelled
uploads, fatal-callback checks and fresh-client health checks are unchanged.
The focused Windows regression passes, as do formatting and analyzer guards.
The first changed-source macOS run, [37970375501](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37970375501),
passed all 4,312 cases (4,281 passed, 31 expected skips), the 27 constrained ZIP
cases and five 17-case reset-stress repetitions on `c367d25`. This supports the
fixture-provisioning diagnosis; it does not establish that the separate native
QUIC rebind failure is resolved.

### Conformance finding F5: SETTINGS and queued DATA ordering

Two controlled tests reproduce DATA sent after the SETTINGS ACK using credit
reserved under the old initial stream window. On `c367d25`, shrinking the window
to zero or 1,024 bytes still puts the reserved 16 KiB frame after the ACK.
The candidate applies SETTINGS and writes its ACK under the shared wire gate.
Queued DATA rechecks its reservation under that same gate before any bytes are
written. An invalidated reservation is returned and the response reserves fresh
credit outside the gate; body offsets and END_STREAM advance only on commitment.
The existing reset/partial-write and HPACK synchronization rules are retained.

Both reproductions now pass. An independent raw-client case checks every byte
of a 16,400-byte response after a zero-window change, 1,024-byte WINDOW_UPDATEs,
the final short DATA frame and END_STREAM, and PING while output is flow-blocked.
The Windows protocol set passes 814 cases; the later three-case ordering/body
set also passes. Linux passes all 378 HTTP/2 cases. Both targets build with no
warnings; formatting/parser/suppression checks and all four allocation budgets
pass. Test discovery floors increase by three to 4,315. Logs and TRX are retained
under ignored `TestResults/http-engine/f5-*`.

These tests cover wire commitment and resumed-response behavior. The original
independent hyper-h2 SETTINGS-shrink campaign, full suites and exact-head CI are
still required; no all-platform conformance completion or speedup is claimed.


### Independent F5 campaign and client-oracle limits

On exact engine `bbe0148`, the unmodified raw-frame SETTINGS-shrink case passes
40 trials over plaintext and 40 over TLS, with no negative post-ACK DATA debit.
The original stateful driver times out at seed 20261009 / iteration 62 because
its final restoration updates only connection credit while a live stream remains
negative after a SETTINGS shrink. Restoring stream credit is required by RFC 9113
Section 6.9.2. A separately reviewed driver removes the known-F4 connection-loss
allowance and restores both flow-control levels before requiring completion.

Two independent decoder probes identify additional hyper-h2 4.3.0 limitations:
its initial ACK consumes a subsequently queued setting override, and it rejects
a permitted unpadded zero-length END_STREAM when SETTINGS left negative stream
credit (RFC 9113 Section 6.9.1). A labeled adapted driver awaits the startup ACK
and uses a narrow, version-guarded empty-END_STREAM adapter that preserves the
negative credit. The probe verifies that nonempty overrun and idle-stream errors
still fail. Every original failed run remains retained; these results are not
represented as unmodified hyper-h2 conformance.

The adapted campaign passes 300 plaintext and 300 TLS iterations on `bbe0148`,
1,188 streams per transport, no server resets or accepted known-F4 closures and
no active handlers after settlement. Handle growth is +9 / +1 and managed growth
about 1.04 MB / 0.12 MB. The empty-END_STREAM adapter was not invoked in those
successful samples; its forced positive/negative probes pass separately.
Exact engine/driver/binary hashes, probes, original failures and successful
samples are under ignored `TestResults/http-engine/f5-*`. Longer, other-seed,
other-platform and other-client campaigns remain required.

### Outgoing HTTP/2 DATA buffer ownership and allocation

Each DATA frame previously allocated and copied its own payload, then copied it
again into the transport buffer. Outgoing DATA now borrows a bounded slice of the
caller's array for the awaited write. The transport still constructs one
contiguous pooled wire frame. Received/header/control frames keep their existing
complete payloads. Shape checks, SETTINGS commitment, canceled-queue credit
refund and response-byte accounting use the slice length. No public API, target,
production dependency or default changes.

Five new cases verify empty/end slices, a nonzero-offset slice of a parent larger
than the peer frame limit, unchanged owner bytes and cancellation credit based on
the reserved slice rather than its 1 MiB owner. All 141 affected cases pass locally.
The expanded Windows protocol/WebSocket set passes 849 cases, Linux passes all
385 HTTP/2 cases, and all four resource budgets pass. Both library targets build;
formatting/parser/suppression checks pass. Discovery floors increase by five to
4,350. Full-suite and final exact-head platform checks remain required.

A separate-process comparison uses clean `9f475d7` as control, the uncommitted
DATA-slice candidate, and installed Kestrel 10.0.12. The same #199 harness code is
used for every engine; the private runner adds `--baseline-modern` solely to
allow a modern-engine control on HTTP/2 (its default main-core restriction is
unchanged). Source patch, engine and matching harness hashes are recorded.
Three alternating rounds, 2 s warmup / 5 s measurement / 1 s idle, four connections
by four streams over TLS, disjoint server/client CPUs 0-7 / 8-15 on the same
Windows Ryzen 9800X3D development host. All 18 samples pass byte/status/framing
validation with no failures and no remaining server sockets.

| 1 MiB workload | Control allocated B/request | Candidate allocated B/request | Control CPU us/request | Candidate CPU us/request |
| --- | ---: | ---: | ---: | ---: |
| Fixed response | 1,143,800 | 93,388 | 1,894.4 | 1,666.3 |
| Streaming, flushed every 16 KiB | 1,181,201 | 130,734 | 1,971.9 | 1,721.9 |

These medians show about 92% / 89% less allocation and lower CPU for these two
workloads. Short closed-loop samples on one shared host do not establish a
universal throughput, latency or Kestrel ranking improvement. Other transports,
platforms, upload/inbound pooling and long-running memory behavior remain work.
Evidence is under ignored `TestResults/benchmark-review/data-slice-*`.

### Incoming HTTP/2 DATA leases

The connection reader now rents nonempty DATA payloads and returns them cleared
once dispatch has copied request bytes into the existing bounded body queue.
Header and control frames retain independent exact-length arrays. Logical frame
length, rather than rental capacity, governs validation, padding and flow credit.
Truncation, cancellation, invalid framing and dispatch failure return the lease;
application readers never retain the returned frame buffer. Public APIs,
receive windows, request body bounds, targets and dependencies are unchanged.

Eight regressions cover oversized pool rentals, sequential/concurrent disposal,
queued body ownership, truncated/canceled reads, invalid DATA shapes and control
frame independence. The Windows protocol/conformance/WebSocket set passes 1,149
cases and the pinned Linux HTTP/2 set passes 393, with no failures or skips. All
four resource budgets pass. The full Windows suite reports 4,358 cases, 4,353
successes and five expected local/platform skips, with zero failures. Both
production targets build with zero warnings/errors; formatting and analyzer
guards pass. Discovery floors increase to 4,358. Exact-head platform validation
remains required.

A frozen same-harness comparison uses clean `f0c4c7b` as control and its recorded
uncommitted DATA-lease patch as candidate. Three alternating upload rounds use
5 s warmup, 15 s measurement and 2 s idle, four TLS connections by four streams,
separate server/client processes on CPUs 0-7 / 8-15, Windows Ryzen 9800X3D and
.NET 10.0.12. All six samples pass byte/status/framing checks with no remaining
server sockets. Median allocation for 1 MiB uploads falls from 1,184,846 to
137,005 B/request (about 88%); median server CPU falls from 2,851.3 to 2,597.3
us/request, and every paired round has lower candidate CPU. Retained heap growth
is at most 131 KiB control / 78 KiB candidate, a settlement observation rather
than a soak result.

The preceding short comparison did not establish a CPU benefit: candidate
upload CPU was higher at its median, with substantial variation in both engines.
Longer confirmation reverses that observation, but background CPU varies from
24.5 to 76.2 seconds per sample. Neither run establishes a stable throughput or
latency improvement, a cross-platform result or a Kestrel ranking. Evidence,
source patch, engine/harness hashes and original samples remain under ignored
`TestResults/benchmark-review/inbound-*`; broader performance and soak work remain.
### Independent conformance on fd58c90

The original #197 HTTP/1.1 cases and stateful driver were run against an exact
`git archive` snapshot of `fd58c904f881b831496d8127e5562c265a7924fe`, using
unchanged helper source `56bbefe`, pinned container
`d72fcf79aaddb0fd1ea0608724b73cb698ce19ba13a60ccc25d22abcf14ea181`,
Ubuntu 24.04.5 x64 and .NET 10.0.12 with four CPUs. The built engine SHA-256 is
`8CCC2E7479DB6476364318E8114B9EA99E22F868AEF1358CB366FC077E5BC832`.

HTTP/1.1 reports 44 conforming and 13 permitted-policy cases, zero violations or
errors. The seeded stateful campaign passes 2,000 iterations / 5,029 valid
requests, 473 invalid requests and 211 aborts, with no malformed body becoming
500. Active handlers return to zero; handle growth is +2 and managed growth
357,928 bytes. This is one Linux seed, not whole-engine or cross-platform fuzz
completion.

Unmodified h2spec v2.6.0 reports 141/146 plaintext and 142/146 TLS, with no skipped
cases. All raw failures remain evidence; the tool's aggregate result is failure.

| Remaining h2spec assertion | Current-baseline assessment |
| --- | --- |
| 5.1/8 and 5.1/11: closed-stream DATA must trigger an error | RFC 9113 section 5.1 permits minimal processing/discard on all closed streams. HPACK and connection flow credit still apply. |
| 5.3.1/1 and 5.3.1/2: self-dependency must reset | The server advertises NO_RFC7540_PRIORITIES=1. RFC 9218 section 2.1 permits ignoring these deprecated priority signals. Shape validation remains required. |
| Plaintext-only 3.5/2: invalid preface reports unexpected EOF | TLS passes; plaintext protocol selection differs. Exact raw response/closure classification remains to be captured; this is not marked conforming. |

References: [closed-stream rules](https://www.rfc-editor.org/rfc/rfc9113.html#section-5.1),
[priority policy](https://www.rfc-editor.org/rfc/rfc9218.html#section-2.1) and
[preface handling](https://www.rfc-editor.org/rfc/rfc9113.html#section-3.4).
Two additional independent wire cases cover peer-reset and normally completed
streams: repeated late DATA returns exactly 32,768 bytes of connection credit
per batch, does not reset or close the connection, and a later request succeeds.
Existing direct tests also preserve HPACK updates for ignored priority fields.
Evidence is under ignored `TestResults/http-engine/conformance-fd58c90*` and
`closed-stream-*`. HTTP/3, broader seeds/clients/platforms, long-running resource
behavior and full standards coverage remain required.
Both expanded HTTP/2 sets pass 395 cases on Windows and pinned Linux. The
additional wire cases change no production code. Formatting/analyzer guards pass;
discovery floors increase by two to 4,360.

On `fd58c90`, CI 37978551488 has two concrete failures to investigate: the stock
macOS raw QUIC rebind fails at cycle 28 with error 48, and the Windows resource
job's HTTP/1 workload times out after 20 s on a fully consumed 65,536-byte upload
with four workers and a new connection per request. Compatibility itself passes
207 cases / 414 comparisons. The upload diagnostic reports Listening, no pending
thread-pool work, 56 retained connections and an empty sampled live-connection
list. This is not an allocation-budget violation or evidence of an HTTP/2 DATA
pooling defect. Its cause remains unconfirmed; the earlier load comparison also
recorded HTTP/1 upload timeouts. No budget or timeout has been relaxed, and both
original logs/artifacts remain under ignored TestResults.
### HTTP/1 upload timeout: connection-establishment evidence

The failed Windows resource job on `fd58c90` waits in HttpClient's connection-pool
path (`TaskCompletionSourceWithCancellation.WaitWithCancellationAsync`), rather
than showing an application body-read stack. The snapshot's retained objects are
not proof of live accepted sockets: its 32 sampled records are all disposed. The
cause remains unconfirmed, including whether the stalled attempt reached server
admission. The timeout and original job evidence are retained unchanged.

Five fresh Windows processes running the exact 65,536-byte/full-consumption
verification workload pass while the previously approved dotnet-trace 10.0.745401
captures System.Net.Http, System.Net.Sockets and System.Net.NameResolution events.
These successful traces do not reproduce or resolve the intermittent failure.
A subsequent untraced verification also passes. Artifacts are under ignored
`TestResults/http-engine/upload-timeout-traces` and `upload-diagnostics-*`.

Failure-only snapshots now include registered and pending connection counts plus
a nonblocking `Socket.Poll(0, SelectRead)` observation on the listening socket.
Disposed/socket-error observations are recorded locally without dropping the
remaining endpoint metadata. Counts and readiness are separate best-effort reads,
not an atomic view or proof of request acceptance. Existing sample and lock bounds
remain. No client/server timeout, successful-request loop, production source,
public API, dependency or test discovery count changes.

An isolated real-listener probe verifies a pending accepted connection (0
registered / 1 pending), then a context with a 32-byte unread body (1 registered /
0 pending, no queued accept work), followed by disposal. It passes on Windows and
the pinned Linux SDK/runtime container. The performance project builds with zero
warnings/errors; formatting, parser, suppression and whitespace checks pass.
The first Linux launcher used the directory name instead of the assembly name;
its missing-file failure is retained separately and the corrected `Probe.dll`
launcher passes. Exact-head CI remains required, and the underlying upload timeout
must still be investigated.
### HTTP/3 request-direction observation without normal cancellation throws

The two request-direction watchers previously used `Task.WaitAsync(requestToken)`.
Finishing a request cancels that token even while QUIC is still acknowledging the
response FIN, producing roughly one caught TaskCanceledException per ordinary
request. A shared, asynchronously completed stop signal now ends observation
through `Task.WhenAny`. Successful direction completion still does not cancel the
request. QUIC direction faults still cancel it; unexpected faults remain visible.
Both watchers are joined before their cancellation registration/source is disposed.
The transport completion tasks are neither canceled nor completed by the watcher.
No public API, protocol, target, default or runtime dependency changes.

An isolated watcher-only process performs 100 observer stops. The unchanged
engine throws 100 cancellation exceptions; the candidate throws zero. The first
probe filtered by a method name in the first-chance throw-site stack and falsely
reported zero on the control. Its result is retained but is not evidence; the
corrected process counts every cancellation exception with no other workload.

Five new cases cover pending/already-completed successful FIN, pending/already-
stopped observation and propagation of unexpected direction faults. The expanded
real HTTP/3/conformance set passes 444 cases on Windows and pinned Linux, including
request resets, blocked QPACK cancellation, upload cancellation, shutdown and
drain. All four resource budgets and source guards pass. Discovery floors increase
by five to 4,365. The full Windows suite reports 4,365 cases, 4,360 successes,
five expected local/platform skips and zero failures. Both targets build with
zero warnings/errors. Final exact-head CI remains required.

The frozen #199 harness compares clean `9ce5af2` with its uncommitted watcher patch,
using matching harness DLLs and recording engine/source hashes. Three alternating
rounds of HTTP/3 small responses use eight connections by 32 streams, fresh
server/client processes, CPUs 0-7 / 8-15, 2 s warmup / 5 s measurement / 1 s idle,
Windows Ryzen 9800X3D and .NET 10.0.12. All six samples validate every response,
have zero failures and leave no server sockets.

| Metric (median unless stated) | Control | Candidate |
| --- | ---: | ---: |
| Requests/s | 106,948 | 138,950 |
| Server CPU us/request | 60.4 | 53.4 |
| Allocated B/request | 16,442 | 16,282 |
| TaskCanceledException count per sample | 478,182-593,884 | 20-23 |
| Retained heap growth, maximum | 365,080 B | 186,880 B |

CPU per request is lower in every paired round (about 10-27% in this workload).
The candidate removes the exception count that scales with requests; it does not
claim zero cancellation exceptions across shutdown or other protocol paths.
Short samples on one shared host, with background CPU 5.9-15.6 s per sample, do
not establish a universal throughput/latency improvement or a Kestrel ranking.
Longer and other-platform comparisons and resource soak remain work. Evidence is
under ignored `TestResults/benchmark-review/http3-watch-*` and
`TestResults/http-engine/http3-watch-*`.
### Standards-audit bodyless response and drain-upload corrections

Owner raw-wire regressions independently reproduced two source-traced audit
findings on engine `24cb39d`. All ten initial cases failed: eight 204/304 cases
covered synchronous/asynchronous writes and explicit/unknown representation
lengths; two drain cases covered ordinary and padded in-flight DATA. The latter
failed with Http2ProtocolException("Frame received on an idle stream") after
refusing successor HEADERS, and canceled the accepted response.

The dispatcher now discards receiver-initiated streams above the drain cutoff
without entering the active registry, after existing shape/HPACK processing. DATA
still consumes and replenishes the connection window. No set of refused IDs is
retained. Two negative cases confirm that even client IDs still cause a connection
protocol error. The bodyless response policy suppresses content and final chunks,
removes prohibited informational/204 framing fields, and preserves explicit 304
representation length. HEAD behavior remains covered by the existing cases.

All twelve new cases pass, and the combined bodyless/HEAD/drain focused set reports
57 successes, zero failures. Both targets build with zero warnings/errors, and
changed-source formatting passes. Full Windows reports 4,377 cases, 4,372 successes/five expected local skips,
zero failures. Focused Linux reports 129 successes, zero failures. All four
resource budgets pass. The actual .NET Standard core (SHA-256
410B245D1813C88AD711ADEAD45E1C32135F1DA7B8F3E73593286AC9DAACE5C0)
passes all twelve new cases under the .NET 10 test host; this is not legacy
runtime or full standard-asset coverage. Exact-head CI remains pending. Discovery floors include the
twelve cases, totaling 4,377. Evidence is retained under ignored
TestResults/http-engine/audit-regression-baseline and audit-framing-*.

Other standards-audit findings remain work, including reset/idle lifetimes,
extended CONNECT handling, target parsing, HTTP/1.0 delimitation, header-overflow
isolation, range/conditional semantics and modern feature/interoperability gaps.
This increment does not establish default-engine or shipping readiness.
The first full candidate run exposed seven lifetime failures: body suppression
also suppressed a 204 empty write's header commit before force/stop/dispose. The
stream correction sends the head while discarding content; existing fixture
assertions were unchanged. The 81-case lifetime/HEAD/bodyless/drain set and the
corrected full suite pass. The original failure log remains retained.

The first direct standard-asset attempt had eleven successes and one unusable
fixed-port case: binding failed with AccessDenied and the request received 404.
The new raw-wire fixture now uses the existing free-port helper and verifies
listener startup before issuing traffic; all twelve standard cases and eight
modern bodyless cases pass afterward. An initial helper-namespace build error
was corrected and retained in the build logs. No port retry or assertion
weakening was introduced.
### HTTP/1 request-target form validation correction

Ten additional raw-wire cases cover missing origin-path slashes, query-only
requests, authority-shaped non-CONNECT targets, malformed absolute URLs, and
valid leading-slash paths containing `@` or queries. On unchanged production at
c25fd05, three of the 66 target cases failed: two leading-@ targets closed without
400, and a query-only target received 200. The other 63 cases passed. The first
attempt used an unsupported wildcard filter and executed zero tests; that log
is retained and is not counted as validation.

The listener now verifies a leading slash, OPTIONS asterisk handling, or a parsed
absolute HTTP(S) URL with an explicit scheme separator before reconstructing the
application URL. The existing host precedence, escaping, userinfo checks and raw
target preservation remain covered. All 88 focused target, URL and CONNECT
rejection cases pass. This does not implement CONNECT authority-form or tunneling;
those remain requirements for completion of the engine. Discovery floors are
4,387. Full regression and exact-head checks remain required.

Evidence: ignored TestResults/request-target-before-corrected.log and its TRX,
request-target-after.log and its TRX. An analyzer build attempted during the full
suite hit the CLI plugin file held by that suite; it must be rerun after the test
process finishes. It is not counted as a passing analyzer gate.
Final local validation: full Windows reports 4,387 cases, 4,382 passed, five
expected local skips and zero failures. The subsequent analyzer build passes
both targets with zero warnings/errors; suppression checks, analyzer guard and
changed-source formatting pass. Hot-path, cold-start, listener-queue and wire
allocation gates pass. The earlier locked-file analyzer failure is preserved.
Cross-platform and final-head GitHub checks are still required.