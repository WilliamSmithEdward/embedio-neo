# EmbedIO.Quic development and testing record

This record covers the development path, test architecture, reproducible local
campaigns and remaining acceptance work for the original C# QUIC engine. It is
maintained with the implementation. The feature is pending in draft PR #506.
The ordinary HTTP/3 binding now selects EmbedIO.Quic with its own TLS capability
check and transitive runtime delivery. The earlier provider remains in internal
comparison paths until replacement acceptance and removal. The later sections
record the cutover evidence and remaining platform/application work. No package release or
public WebTransport API is implied by this record. The sections retain the implementation
sequence and its evidence; the final acceptance list describes work still outstanding.

See [the feasibility profile](quic-engine-feasibility.md) for standards references,
exact dependency pins, bounds and independent-peer setup. The implementation is
standards-based, not a formally separated clean-room project. The proposed
Linux/macOS runtime packages carry pinned OpenSSL for TLS; Windows uses
Schannel's public recordless SSPI interface. .NET cryptography supplies AES-GCM,
ChaCha20-Poly1305, AES and HKDF. Our code owns the transport and the small
QUIC-specific ChaCha header-protection mask described below.
William permits consulting MsQuic and other open-source implementations where
their licenses permit it; implementation code must be original, with no verbatim
copying from those projects.

## Development sequence

| Stage | Implementation and validation |
| --- | --- |
| Core packet and connection owners | `QuicConnection` and `QuicConnectionDispatcher` own packet/TLS/recovery state and bounded destination-ID routing in the source core. Factories supply TLS and applications; development wrappers add protocol error mapping and observations. Simple echo, multiplexed streams and H3 use the same stream owner. The library socket binding exercises the actual WebServer pipeline; an isolated ordinary-binding selection probe is recorded below. Automatic runtime delivery and public default selection remain pending. |
| Windows certificate selection | Complete ClientHello/SNI framing precedes a once-only certificate selector. A selected certificate is cloned for the native credential; the source remains application-owned. Focused tests exercise publication timing, selector errors and disposal during selection. The independent selection profile uses two non-exportable CNG key types and six aioquic cases. The actual WebServer fixture now uses the library's policy/source factory to verify SNI and live publication; Windows client-certificate integration is described below; complete platform parity remains pending. |
| Windows TLS credentials | Original managed SSPI binding, provider-neutral TLS state and non-exportable CNG RSA/ECDSA test credentials. Native credential/context/buffer ownership, bounded ClientHello parsing and once-only traffic-secret installation are exercised before production integration. The development cipher profile remains AES-128-GCM/SHA-256. |
| TLS and packet feasibility | Original wire integers, Initial secrets, packet/header protection, packet-number reconstruction, fixed CRYPTO reassembly and public OpenSSL third-party QUIC callbacks. RFC vectors and aioquic establish the first encrypted stream exchange. |
| Key generations | Current, previous and next read keys share header protection; ACK-confirmed local updates track full generations, enforce encryption/integrity usage limits and retain reordering boundaries. Independent command/packet oracles and native wire error checks complement the repeated-update peer case. |
| Receive packet history | A sliding floor and at most 128 locally generated ACK ranges retain a 4096-number window. Incoming ACK range counts are bounded by the datagram bytes rather than the local generation limit. Retired packet numbers are never processed again; bounded history can advance without a connection-wide receive packet quota. |
| Recovery and congestion | Bounded retired-packet metadata and acknowledgement barriers permit long-running recovery histories. Physical packet records retain logical delivery ownership through retransmission and late ACKs. Monotonic-time tests exercise packet/time loss, RTT, PTO probes, pacing, NewReno, application-limited ACKs and persistent congestion. |
| HTTP/3 and WebTransport bridge | Existing H3/QPACK/capsule/session code is linked into the prototype. The bridge handles SETTINGS, authorized CONNECT, exact Origin admission, bidi/uni streams, datagrams, independent sessions and reliable partial resets. quic-go provides a separate wire peer. |
| Sustained transfers | A fixed sliding receive window retains final-size history and compacts consumed H3 input. MAX_DATA/MAX_STREAM_DATA track consumption, including reset gaps after their reliable prefix. Independent cases transfer 512 KiB, with small peer windows and loss. A separate bounded endurance case transfers 8 MiB over two streams with frequent local key updates. |
| Application boundary | `StreamApplication` and its owner-bound operations separate raw streams/datagrams from the H3 adapter. Consumption returns receive credit; reliable prefixes and critical-stream rules belong to the application. The async API connects this boundary to application workers and the ordinary HTTP/3 listener; public WebTransport integration remains pending. |
| Async application ownership | `AsyncStreamApplication` and `AsyncQuicStream` provide owned read/write/open/accept/datagram/close operations, explicit cancellation and independent stream halves. A bounded signal wakes the UDP owner; FIN-ACK retirement and asynchronous disposal retain delivery ownership. Reliable stream prefixes are copied before writes, installed on the owner turn and retained through reset and flow-control delay. The console host exercises application workers on a real connection. |
| Stream lifetime | Cumulative stream credit replenishes after complete receive/application and required send-ACK retirement. Closed-ID ranges prevent reuse; at most 64 recent final sizes retain narrow conflict checks. ACK delays keep echo work within resident stream bounds. |
| Connection lifetime | Active, closing, draining and closed states use injected time. Idle expiry is silent; close replies are bounded; repeated sends or duplicate close traffic do not extend retirement. Windows ICMP reset notifications during retirement are handled explicitly. |
| Shared listener | Destination-ID demultiplexing, endpoint-bound original-ID aliases, 16 resident owners, bounded admission attempts and fixed handshake deadlines. One cancellable UDP receive loop retires owners and permits clean port rebinding. |
| Fuzz integration | All existing fuzz engines now have direct QUIC core entry points. Packet-header and ACK-range parsing are shared between the actual connection and fuzz targets. Dedicated native campaigns exercise fresh OpenSSL or Schannel contexts through authenticated Initial frames. |

These stages are a bounded transport profile. They are not completion of
full RFC conformance, production provider integration or browser support.

## Source and responsibility map

The generic assembly and namespace are **EmbedIO.Quic**. Protocol, packet, connection and
async ownership files live in `src/EmbedIO.Quic`, grouped under `Packets`,
`Cryptography`, `Recovery`, `Connections` and `Streams`. The core contains no HTTP/3
linked sources, console commands, native TLS bindings or test helpers.

Tests, peers, the OpenSSL development binding, console socket ownership and HTTP
application adapters remain under `test/EmbedIO.Quic`, whose executable assembly is
`EmbedIO.Quic.Development`. The Windows Schannel binding and its certificate-policy
factory live in `src/EmbedIO/Net/Internal/Http3/Tls` on the .NET 10 asset.
The standalone core build and host remain nonpackable. For the current production
protocol integration, the .NET 10 `EmbedIO` asset compiles the same internal engine
sources, and the unsigned development host references that library through an
explicit friend assembly. It no longer compiles copies of HTTP/3/WebTransport
codecs. This preserves existing package dependency groups and .NET Standard
compilation while exercising the actual library protocol. Default listener
selection remains unchanged outside the isolated local probe. Native TLS and final platform packaging still require
acceptance. The table names implementation files in those source groups and their
corresponding host/test files.

| Surface | Files | Test approach |
| --- | --- | --- |
| Bytes and packet boundaries | `Wire.cs`, `PacketHeader.cs`, `AckRanges.cs`, `AckRangeTests.cs`, `PacketNumberWindow.cs` | Independent integer decoding, canonical sizes, header bounds, valid token construction, full-packet incoming ACK range counts, malformed/descending ACK ranges, bounded recovery lookup and independent packet-history set models. |
| Packet protection | `PacketKeys.cs`, `KeyUpdateState.cs`, `KeyUpdateTests.cs` | RFC vectors, encrypt/decrypt identity, tamper rejection, per-key encryption limits, packet-number truncation, repeated key generations, ACK/cooldown policy models and retained header protection. |
| Receive state | `StreamReassembly.cs`, `Wire.cs` | Absolute-position dictionary oracle against the sliding ring; reversed/duplicated CRYPTO fragments; final-size, window, overlap and once-only delivery checks. |
| Recovery/congestion | `Recovery.cs`, `RecoveryRetentionTests.cs` | Deterministic algorithm cases, history compaction/late-ACK models, logical/physical accounting, duplicate ACKs, discard, loss/PTO/retransmit traces, resource bounds and independent connection ownership. |
| Async ownership and admission | `AsyncQuicStream.cs`, `AsyncStreamApplication.cs`, `AsyncStreamEcho.cs`, `AsyncStreamTests.cs` | Original error codes, borrowed-buffer release, reliable-prefix credit, pre/post-copy cancellation, sparse reads, outbound bidi/uni streams, packet/stream backpressure, unaccepted FINs, late controls, datagram bounds, close/dispose and independent-owner command oracles. |
| Generic application ownership | `StreamApplication.cs`, `StreamEchoApplication.cs`, `StreamApplicationTests.cs` | Empty stream admission, original reset codes, deferred consumption/flow credit, raw bidi/uni ACK retirement, independent owners and rejection of operations after disposal. |
| Stream counts and retirement | `StreamCountWindow.cs`, `StreamCountTests.cs`, `StreamLifecycleTests.cs` | Independent closed-ID sets, 100,000 sequential retirements, implicit stream opening, delayed/late ACKs, repeated requests, closed references, pending session association, final-size checks and buffer disposal. |
| Lifetime | `ConnectionLifetime.cs` | Deadline regressions, monotonic phases, silent draining, bounded close responses and inability to revive a closed connection. |
| H3 bridge and credit | `ConnectionStreams.cs`, `WebTransportApplication.cs` | Bounded malformed H3/datagram inputs, partial reset gaps, send-credit transitions, a healthy independent connection and exact datagram echo. |
| Connection ownership | `QuicConnection.cs`, `QuicConnectionDispatcher.cs`, `ReferenceConnectionSet.cs`, `QuicConnectionLimits.cs`, `ConnectionCoreTests.cs`, `ConnectionSetTests.cs`, `SharedListener.cs`, `verify_connections.py` | Authentication before TLS allocation, configured stream credit, rejection after disposal, sibling cleanup after a failing disposer, capacity/deadline regressions, direct managed ownership mutations and simultaneous independent clients on one UDP port. |
| Scheduled execution | `QuicExecutionPool.cs`, `QuicConnectionOwner.cs`, `ExecutionPoolTests.cs`, `ConnectionOwnerTests.cs` | Fixed worker count, one executing turn per owner, coalesced wakeups, fair requeueing, queued/in-flight input budgets, copied packet ownership, shutdown during native work and once-only cleanup. Lifecycle mutations and protected/corrupted packet inputs use the shared fuzz facade. The shared UDP development listener now uses this scheduling path. |
| TLS framing | `TlsClientHello.cs`, `TlsHelloTests.cs` | Complete handshake/vector boundaries, duplicate extensions, required transport parameters, malformed/truncated input, parameter limits and structured/raw mutations through FsCheck, deterministic mutation, SharpFuzz and AFL++. |
| Native TLS boundary | `TlsSession.cs`, `OpenSslTls.cs`, `SchannelTls.cs`, `SchannelTests.cs`, `ProbeConnection.cs`, `NativeFuzz.cs` | Fresh TLS owners, structured TLS 1.3 and malformed Initial frame/CRYPTO mutations, unauthenticated-packet isolation, credential/context/buffer cleanup and amplification accounting. Schannel tests include invalid constructor inputs, fragmented native rejection and concurrent context disposal. Native libraries themselves are not coverage-instrumented. |
| Independent transport peer | `verify.py` | aioquic positive/negative handshake, exact echo, corruption, ALPN, flow-control and packet-loss/reordering cases. |
| Independent H3 peer | `peer/main.go`, `verify_webtransport.py` | quic-go/qpack interoperability, sessions, resets, datagrams, credit, sustained transfers, key update and close/idle behavior. |

`QuicFuzzChecks`, `StreamModel`, `QuicFuzzState` and `BridgeFuzz` are test-only
oracles/adapters. Merely loading the managed fuzz facade neither opens a socket nor
loads OpenSSL. No new production dependency group or library API is introduced.

## Fuzzing engines

The eight managed target selectors are integers, packet protection, reassembly,
recovery, lifetime, WebTransport bridge, packet headers/ACKs/TLS ClientHello framing,
and scheduled connection ownership. Raw inputs are
bounded to 4096 bytes. Stateful byte traces execute at most 128 commands; checked-in
JSON traces and generated multi-connection campaigns use three actors and 48 steps.
The packet-protection selector also drives a 128-command key-generation model.
The bridge selector drives an independent 128-command stream-count model with
smaller resident limits, while the actual bridge still runs its ownership checks.
A separate recovery-history model uses up to 64 commands and smaller test-only
retention bounds to exercise compaction without large per-input workloads.
The receive-history model allows up to 256 commands so coverage-guided inputs can
exercise retirement beyond the 128-range limit.
Recovery histories and peer buffers remain bounded by the prototype's limits.

- **FsCheck:** eight additional properties use arbitrary bounded byte arrays with
  deletion shrinking and replayable seeds. Existing URL, routing and multipart
  properties remain active. Each target runs separately so selection cannot starve
  a target. Default generated inputs are at most 256 bytes.
- **Deterministic mutations:** `--quic-core seed cases` cycles through every target
  with inputs up to 512 bytes, retains a binary reproducer on failure and reports
  seed/case provenance.
- **State machines:** `quic-recovery` and `quic-lifecycle` keep three independent
  owners. Existing JSON replay, transition counts, invariant-preserving shrinking,
  per-case deadlines and cleanup rules apply. The artifact records the QUIC
  prototype assembly identity/hash for these scenarios.
- **SharpFuzz/libFuzzer:** the existing callback executes a selected QUIC target
  alongside the URL/multipart checks. Instrumentation targets a copied QUIC
  assembly, including actual parser/recovery/lifetime/bridge code and linked H3
  codecs. The harness requires instrumentation evidence, unchanged ordinary
  outputs, seed replay, managed feedback and a completed campaign.
- **AFL++:** the existing pinned, one-CPU Linux container runs the same callback.
  `afl-showmap` requires nonempty distinct QUIC maps, alongside the existing
  URL/multipart checks. Crashes, hangs, absent coverage or tool failure fail the
  campaign. The original colon-bearing filenames are preserved in artifacts.
- **Native mutations:** `fuzz-native` owns fresh TLS/connection state for each
  input. It uses no sockets, never disables authentication and feeds both validly
  protected malformed frames and corrupted packets. It does not claim complete
  TLS state-space exploration or managed coverage feedback inside native code.
  Each native campaign also exercises up to 10,000 shared-owner commands, retaining
  its seed and checking owner routing, bounded admission, amplification and retirement.

Shared-owner regressions also verify that terminal resource limits retire one
owner while a healthy neighbor continues. These host checks preserve the current
bounds; production endurance and the generic application transport boundary remain separate work.

The negative oracle self-checks verify detection of changed/missing/duplicated
stream bytes and a falsely classified authenticated packet. Existing deterministic
recovery, key-update, bridge, receive-window and lifetime regressions remain
separate checks; random testing does not replace them.

## Extended local development run

William requested deliberately extensive local fuzzing on 2026-10-20. Keep this
separate from per-push Actions budgets and shared runner capacity. Use the pinned
SDK and the same Python/OpenSSL/Go setup as the feasibility guide.

```powershell
python scripts/run-quic-development.py --openssl 'C:\path\to\OpenSSL-3.5.7\bin' --schannel
```

On Windows, `--schannel` adds native RSA/ECDSA listener, certificate-policy and
mutation checks. Omit that flag on other platforms. The defaults perform 20 seeds,
each separated by 104729, starting at 1729:

| Campaign | Default local workload |
| --- | ---: |
| QUIC FsCheck | 16,000,000 generated target cases (8 x 100,000 x 20) |
| Existing FsCheck properties | 12,000,000 cases (6 x 100,000 x 20) |
| QUIC deterministic mutations | 2,000,000 inputs |
| Recovery/lifetime state machines | 400,000 traces; 19,200,000 commanded transitions |
| Native connection/TLS mutation | 100,000 inputs when `--openssl` is supplied |
| Shared-owner native traces | 100,000 admission/retirement commands when `--openssl` is supplied |
| Windows native TLS mutations | An additional 100,000 inputs per RSA/ECDSA profile with `--schannel`, plus corresponding shared-owner traces |
| Complete Windows client-authentication handshakes | 100,000 certificate/key mutation inputs with `--schannel` (5,000 per seed), up to 16 peers at once and one continuing healthy connection |
| SharpFuzz/libFuzzer | 600 seconds |
| AFL++ | 600 seconds, one CPU and 4 GiB container limit |

These are configured workloads, not statements that a run completed. The script
stops on the first failed command or deadline and retains all earlier evidence.
It builds once and copies the ordinary test outputs into a private run directory,
so editor builds cannot modify a running managed campaign. It records source
hashes, dirty-tree state, assembly hashes, exact commands, durations and exits.
Each child command has a deadline; no background worker is silently abandoned.

All evidence lives under ignored `TestResults/quic-development/<unique-id>`.
Coverage engines retain their own fresh directories under `TestResults/sharpfuzz`
and `TestResults/afl`. Keep their source/assembly identities when comparing runs.
A source change during a long run requires validation of the changed behavior
before treating that earlier receipt as evidence for the updated implementation.

Options allow a smaller reproduction or a larger bounded local run. For example:

```powershell
python scripts/run-quic-development.py --rounds 1 --cases 1000 --state-cases 100 --native-cases 100 --coverage-seconds 0
# Replay a saved binary through the coverage callback:
dotnet test/EmbedIO.SharpFuzz/bin/Release/net10.0/EmbedIO.SharpFuzz.dll path/to/reproducer.bin
# Replay a state trace:
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines-replay path/to/trace.json
```

Without `--openssl`, native campaigns are omitted explicitly. With
`--coverage-seconds 0`, the two coverage campaigns are omitted; this is useful when
matching coverage runs are already active, and does not mean they passed.

## Validation ledger

The earlier recovery/WebTransport checkpoint completed 15 aioquic and 13 quic-go
cases. The receive-window/lifetime increment subsequently completed 15 aioquic
and 18 quic-go cases on Windows x64, with Debug/Release builds, formatter/analyzer
checks and the mandatory whole-source gates passing. Those receipts precede the
fuzz expansion and must not be substituted for its final validation.

The extended 20-seed local campaign completed all 14,000,000 QUIC property
cases, 12,000,000 existing property cases, 2,000,000 QUIC mutations, 400,000
three-actor traces and 100,000 fresh native-context cases. The receipt retains
the frozen source and assembly identities from before the final project rename.
The final `EmbedIO.Quic` build passed separate managed properties, mutation/state
replay, all 34 independent wire cases and another 100,000 native-context cases.
Those checks include packet-space validation for connection close handling.
Debug/Release builds, changed-source formatting/diagnostics, the whole-source
quality gates, suppression checks and AnalyzerGuard passed after the final code edit.

The initial 600-second libFuzzer campaign completed 3,670,426 inputs; its managed
feedback grew throughout the run. After the final rename, another 60-second run
completed 458,339 inputs. Neither retained a crash. These figures are campaign
observations, not performance benchmarks or coverage percentages.

The first 600-second AFL++ campaign completed 318,059 inputs with zero crashes or
hangs, but its shell wrapper failed after the mounted runner script changed during
execution. Its failure evidence is retained. The runner now copies the script
inside the container before execution. The replacement 600-second campaign
passed with 326,736 inputs and zero crashes or hangs. A short end-to-end run of
the local campaign script also passed both coverage engines. Do not count the
earlier wrapper failure as a passing gate.
Native/fuzz local runs do not establish macOS or browser compatibility.

The shared-listener increment passed all 38 wire scenarios (15 aioquic, 19
quic-go and four shared-listener scenarios), another 100,000 native-context cases
and 10,000 seeded shared-owner commands. Four additional seeds passed 2,800,000
QUIC property cases, 2,400,000 existing property cases, 400,000 mutations and
80,000 three-actor traces. Fresh coverage campaigns completed 526,220 libFuzzer
inputs and 33,630 AFL++ inputs without crashes or hangs. Debug/Release builds,
full PR changed-file formatter/diagnostic checks and the mandatory whole-source
gates passed after the final C# edit. The loss relay now selects three larger
encrypted response packets, retaining exact-byte and retransmission assertions;
the earlier timing-dependent relay failure is preserved in local evidence.

The receive-history increment passed its before/after native regression with
10,000 authenticated incoming packet numbers and a checked wire ACK for the
retired duplicate. Managed tests cover 100,000 sequential numbers, sparse-range
retirement, merging, jumps and overflow. All 38 wire scenarios, 100,000 native
cases, 10,000 shared-owner commands and the final build/source gates passed.
Two additional seeds passed 1,400,000 QUIC properties, 1,200,000 existing
properties, 200,000 mutations, 40,000 state traces and 10,000 native cases.
The new sparse-history corpus passed with 400,396 libFuzzer inputs and 31,935
AFL++ inputs; neither campaign retained a crash or hang.

The recovery-retention increment first reproduced the cumulative ACK and retired-
packet history limits. Its corrected deterministic cases pass 50,000 acknowledged
sends and 5000 loss cycles, including persistent congestion after compaction and
late ACKs for evicted records. An additional 100,000 mutations passed. All 38 wire
scenarios, 100,000 native cases, 10,000 shared-owner commands and final
Debug/Release, formatter, diagnostic and whole-source gates passed. Two further
seeds passed 1,400,000 QUIC properties, 1,200,000 existing properties, 200,000
mutations, 40,000 state traces, 10,000 native cases and 10,000 shared-owner
commands. Fresh coverage runs completed 395,761 libFuzzer and 32,639 AFL++ inputs
without crashes or hangs. At that checkpoint, outbound packet count, stream-count
replenishment and broader key updates remained separate work.

The key-generation increment removes the cumulative outbound packet quota and
adds per-key encryption counts, connection-wide authentication-failure limits,
ACK-confirmed local updates and packet-number ordering across repeated generations.
The new 8 MiB peer case reproduced the old quota failure before the change. The
final matrix passes 40 wire scenarios: 15 aioquic, 21 quic-go and four shared-owner
scenarios. Both 8 MiB cases pass beyond 18,000 sent packets with dozens of local
and peer key transitions; the lossy case retransmits three dropped packets. Exact
echo, later datagrams, complete shutdown and zero final flight are required.

Deterministic checks cover 100 actual key generations, 32-bit packet-number
truncation boundaries, ACK/cooldown policy, reordered key generations and native
wire closure under smaller test-only encryption/authentication budgets. Another
100,000 mutations and the final 100,000 native cases/10,000 shared-owner commands
passed. Two new seeds passed 1,400,000 QUIC properties, 1,200,000 existing
properties, 200,000 mutations, 40,000 state traces, 10,000 native cases and 10,000
shared-owner commands. Fresh campaigns completed 329,633 libFuzzer inputs and
31,953 AFL++ inputs without crashes or hangs. All final Debug/Release,
PrettyCSharp/whitespace/diagnostic, source-quality, suppression and AnalyzerGuard
gates passed. The final runner freezes managed binaries and verifies unchanged
source hashes; the 21-case peer matrix uses those same frozen binaries. The earlier
validation pass preceded the final key-ordering changes and is retained as
intermediate evidence, not the final source gate.

The stream-lifetime increment first reproduced the lifetime stream limit after
15 data streams. Its replacement passes 128 bidi and 128 uni streams on one
session, with normal traffic and three deliberately dropped response packets.
All 42 wire cases pass (15 aioquic, 23 quic-go and four shared-listener cases).
The new cases require exact data, replenished session/transport credit, later
datagrams, at least 384 retired stream records, completed shutdown, zero retained
stream/application maps and zero final recovery flight.

Deterministic tests pass 100,000 sequential stream IDs, 2000 completed requests,
missing FIN ACKs, bounded receive capacity under response backpressure, late
controls/final sizes, pending session associations and once-only disposal. Session
ownership persists through the response ACK so closing a session still terminates
unacknowledged bidi responses; the ACK/close ordering also has a fuzz oracle.
The independent count model runs through every managed fuzz engine. Final
Debug/Release, full PR changed-file formatting/diagnostic checks, native 100,000
cases/10,000 shared-owner commands, whole-source quality, suppression checks and
AnalyzerGuard passed. Two new seeds passed 1,400,000 QUIC properties, 1,200,000
existing properties, 200,000 mutations, 40,000 state traces, 10,000 native cases
and 10,000 shared-owner commands. Fresh libFuzzer and AFL++ runs completed
332,682 and 29,994 inputs with no crashes or hangs after the final session-ownership
change. The earlier 311,268 / 32,708 campaigns are retained as intermediate evidence. No production provider switch
or cross-platform native/browser acceptance is implied by this increment.

The generic-application increment removes HTTP/3 construction and stream-ID
assumptions from the stream core. The H3 adapter explicitly owns its reliable
stream prefix and critical control streams. The raw multiplexed profile uses the
same consumption/retirement rules without HTTP headers or sessions. Its owner-bound
callbacks transfer write-buffer ownership and reject operations after disposal.
Reset notification retains the peer's original application code and waits for any
negotiated reliable prefix. Deterministic and all-engine mutation tests cover
consumption-driven credit, empty stream admission, raw uni/bidi ACK retirement and
connection isolation. Production asynchronous stream/connection integration remained
a separate acceptance item at that checkpoint; the following increment supplies
the owned API in the development host.

All 46 wire scenarios passed: 19 aioquic, 23 quic-go and four shared-listener
cases. The added raw cases pass 256 sequential bidi streams, 128 uni streams,
four groups of 16 concurrent streams with distinct payloads, and a 2 MiB transfer
with three actual retransmissions and 77 receive-credit updates. Final ACK-gated
retirement is checked after the independent peer's normal delayed-ACK timer and a
public ping. An initial check closed the peer before those final ACKs and was
corrected to observe completion; an early concurrent peer fixture reused IDs
before its first write and now reserves each ID through the public writer.
The original six-second probe deadlines remain unchanged. The new larger raw
case uses a separate 20-second peer/30-second host deadline; the first six-second
attempt remains retained.

Final Debug/Release, full PR changed-file formatting/diagnostics, mandatory
whole-source quality, suppression and AnalyzerGuard checks passed. Another
100,000 native cases and 10,000 shared-owner commands passed. A two-seed run
passed 1,400,000 QUIC properties, 1,200,000 existing properties, 200,000 mutations,
40,000 actor traces, 10,000 native cases and 10,000 shared-owner commands. Fresh
libFuzzer/AFL++ campaigns completed 256,214 / 26,044 inputs with zero retained
crashes or hangs. The final peer check uses the frozen, hash-verified assemblies
from the full validation run; the concurrent payload refinement changed only the
Python peer. These results do not supply production or non-Windows wire acceptance.

The earlier Windows Fuzz run 39497411610 failed in the existing tus killed-process
fixture when the parent opened its marker before the child closed the write
handle. Readiness now publishes by moving a closed adjacent file. Twelve repeated
killed-process recovery trials passed locally, retaining all offset, ownership,
resume and exact-byte assertions. The failed run is retained; hosted validation
of the correction is required.

The async-ownership increment uses one connection owner for transport state and
synchronized application buffers for concurrent callers. Read consumption and
explicit discard return credit; a reliable reset releases its discarded tail
without releasing an unread prefix twice. Writes copy bounded chunks before
releasing caller memory, pause under output pressure and reserve packet capacity
when later peer credit arrives. Single outstanding reads/writes, bounded
open/accept/datagram queues and unaccepted-FIN admission limit resident state.
Application wakeups share the UDP loop with its persistent recovery timer.

Managed cases verify original stream/connection error codes, cancellation before
and after write progress, explicit H3_NO_ERROR read cancellation with an intact
response half, no caller-buffer access after completion, independently opened
server bidi/uni streams, FIN-ACK disposal, late STOP_SENDING after acknowledged
FINs, shutdown wakeups and concurrent cancellation/data arrival. The generated
byte-and-retirement oracle runs through the existing property, mutation,
libFuzzer and AFL++ targets. Structured recovery traces also run the async byte
oracle, and lifecycle traces generate local/peer/owner-close sequences that fail
and release every pending accept, open, read, write and datagram operation.
The raw async host bounds application workers to 32;
its uni echo inputs alternate server uni/bidi outputs. The independent peer
finishes the unused bidi half. This API is still internal to the development
project; no production provider or public WebTransport API has changed.

The final async source passes 10,076 deterministic assertions and 51 independent
wire cases: 24 aioquic, 23 quic-go and four shared-listener cases. Five new wire
cases exercise 128 bidi streams, 128 uni inputs with server bidi/uni responses,
parallel distinct payloads, a 2 MiB transfer with loss and the original
application close code. Twenty repeated self-tests pass 2000 cancellation/data
races and isolated-process checks for unused close notifications. Notifications
are created lazily; ordinary resets discard buffered data, while reliable resets
retain only their required prefix and return each byte of credit once.

A final two-seed campaign passes 1.4 million QUIC property cases, 1.2 million
existing properties, 200,000 mutations, 40,000 recovery/lifecycle traces,
10,000 native-context mutations and 10,000 shared-owner commands. Its receipt is
`TestResults/quic-development/750b3e71069d4e14b00f5384a4c74168/receipt.json`.
Fresh libFuzzer/AFL++ campaigns complete 185,945 / 32,981 inputs without retained
crashes or hangs. Debug/Release builds, SARIF diagnostics, whole-PR changed-file
formatting/style/analyzer checks, all-source quality and suppression guards pass.
The final source hashes and all 51 wire receipts are retained under ignored
`TestResults/quic-feasibility/continuation/final-validation-async-streams-08`;
earlier intermediate receipts retain their own source hashes and failure history.

The subsequent source split preserves all 15 core files' implementation bytes
apart from checkout line endings. The independent core and development host each
pass Debug/Release diagnostics and formatting checks, and the complete split
passes all 51 wire cases, 10,076 deterministic assertions, all-source quality and
suppression guards. A fresh two-seed campaign repeats the async workload above
with source hashes covering every nested engine file and both assembly identities.
Its receipt is `TestResults/quic-development/6668588f27ce41fba75e7e3d6e409f36/receipt.json`.
Fresh libFuzzer/AFL++ campaigns complete 177,970 / 29,211 inputs with no retained
crashes or hangs. Both instrumented modules change while their ordinary build
outputs retain their original hashes. Workflow path filters include the new core.
The renamed executable is `EmbedIO.Quic.Development.dll`; earlier receipts retain
the executable identities belonging to their recorded source. Subsequent increments require their own exact-source hosted validation.

The 373e7df checkpoint's 38 hosted checks are green, including the corrected
Windows tus recovery fixture. Each subsequent increment needs its own exact-head hosted results.

CI run 39470892485 on the preceding pushed head passed on attempt 2. Its original
macOS failure occurred during checkout DNS resolution; the targeted retry passed.
This is evidence for that head, not for subsequent uncommitted changes.

The Windows TLS increment completed 153 independent wire cases: the existing 51-case
suite against OpenSSL and against each non-exportable CNG RSA/ECDSA Schannel profile.
Each profile includes aioquic, quic-go WebTransport and shared-listener cases.
The same source completed 10,000 native authenticated-Initial mutations and
10,000 connection-owner steps per provider, 100,000 managed mutations, 10,000 cases
per FsCheck property and 4,000 generated recovery/lifetime actor traces, plus corpus
replay. The native lifetime set exercised both credential kinds, invalid constructor
inputs, fragmented rejection and 64 concurrent native failures; all native credential,
context and buffer counters returned to zero. These are functional/resource checks,
not latency measurements or production/platform acceptance. Evidence is under ignored
`TestResults/quic-feasibility/continuation/tls-functional-01`.

The same increment completed a 30-second SharpFuzz/libFuzzer campaign
(92,888 executions) and a 30-second AFL++ campaign (15,921 executions),
with no retained crash or hang. Both instrument the new core ClientHello parser;
valid and duplicate-extension seeds produce distinct AFL coverage maps. Core and
host copies are instrumented independently while ordinary build-output hashes stay
unchanged. These bounded coverage runs supplement the longer historical campaigns;
they do not replace native-platform or production acceptance.

The subsequent SNI/native-boundary increment completed 159 independent wire cases:
the same three 51-case provider matrices plus six certificate-selection cases. It
also repeated the 30,000 native mutations, 30,000 owner steps, 100,000 managed
mutations, 10,000 cases per FsCheck property, 4,000 actor traces and corpus replay.
The final native self-test includes certificate selection and extension-payload
boundaries for both CNG key types. Evidence and exact source/assembly hashes are in
ignored `TestResults/quic-feasibility/continuation/sni-functional-02`. A subsequent
local-variable style correction in the selection test is covered by the final
source gate and repeated native self-test; it changes no protocol implementation.

The SNI increment completed a 30-second SharpFuzz/libFuzzer campaign (97,507
executions) and a 30-second AFL++ campaign (15,700 executions), with no retained
crashes or hangs. New valid and invalid server-name seeds produce distinct AFL
coverage maps. Both campaigns verify instrumentation of the core ClientHello parser
and preserve ordinary build outputs; native SSPI internals are outside this managed
instrumentation. Evidence is summarized under ignored
`TestResults/quic-feasibility/continuation/sni-coverage-summary.json`.

A focused Windows x64 Native AOT executable also completed the first server TLS
flight, installed handshake/application traffic keys and returned native ownership
counters to zero with both non-exportable CNG credential kinds. The executable links
the same adapter and credential fixture used by the development host. This checks
the native binding and key lifetime; it does not establish a complete AOT connection,
production listener or packaged-provider support. Source/binary hashes and logs are
under ignored `TestResults/quic-feasibility/continuation/sni-aot-probe`.

## Windows recordless TLS development profile

`SchannelTls` binds the Windows SDK SSPI message interface; it contains original
C# ownership and buffer orchestration, with no copied QUIC implementation code.
The local baseline uses the Windows 10.0.26100 SDK interface on a current Windows
host. It does not establish older Windows, mobile, browser or packaged-provider
support. OpenSSL remains the independent cross-platform development TLS adapter.

`DevelopmentTlsProvider` owns one validation credential independently of connection
owners. RSA and ECDSA P-256 fixtures use unique, disposable named software CNG keys
with export disabled; only the public certificate is written. Fixture cleanup
removes its own named key. Native credential, context and unmanaged-region counters
must return to zero after owners are disposed. Application certificates must keep
their configured storage and export policy when production integration is added.

The core `TlsSession` publishes managed handshake/key state, so ordinary packet
processing does not query native handles for handshake completion. The Windows
adapter bounds CRYPTO input, ClientHello vectors and output queues. Repeated SSPI
publication of the same traffic secret is idempotent and never resets packet-key
usage counters. TLS message classification selects Initial/Handshake/application
CRYPTO levels; ALPN and peer transport parameters are checked before establishment.
First-flight tests cover empty, one-byte, two-byte and maximum-size extension payloads
without transmitting native structure padding. Failed native calls do not publish
uninitialized token buffers or traffic keys.
The Windows adapter waits for a complete ClientHello before invoking its certificate
selector, snapshots the returned certificate once and releases only its own clone.
SNI vectors, duplicates and ASCII label bounds are checked before selection; original
case is retained, including compatible underscore labels and a trailing dot. Invalid
or throwing selectors refuse the handshake, and disposal during selection cannot
resurrect native credentials. A handshake's selected credential remains fixed as the
source changes; a new handshake invokes the selector again.

Malformed transport parameters use QUIC error `0x08`; closure protection follows
authenticated handshake progress. The server's first-flight parameter rejection is
readable under Initial protection. The UDP payload-size parameter is bounded to the
protocol's 1200-65527 range.
Native imports resolve through the Windows system directory. Independent-peer
receipts record provider identity and hashes of the corresponding system modules.

After the ordinary Release build, Windows development checks include:

```powershell
$hostDll = 'test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.Development.dll'
python test/EmbedIO.Quic/verify_http3.py --dotnet dotnet --assembly $hostDll --openssl schannel-rsa --output TestResults/quic-production-http3
dotnet $hostDll schannel-self-test
python test/EmbedIO.Quic/verify.py --dotnet dotnet --assembly $hostDll --openssl schannel-rsa --output TestResults/quic-schannel-rsa
python test/EmbedIO.Quic/verify_webtransport.py --dotnet dotnet --assembly $hostDll --openssl schannel-ecdsa --peer TestResults/quic-feasibility/webtransport-peer.exe --output TestResults/quic-schannel-ecdsa-h3
python test/EmbedIO.Quic/verify_connections.py --dotnet dotnet --assembly $hostDll --openssl schannel-rsa --peer TestResults/quic-feasibility/webtransport-peer.exe --output TestResults/quic-schannel-connections
python test/EmbedIO.Quic/verify_sni.py --dotnet dotnet --assembly $hostDll --output TestResults/quic-schannel-selection
dotnet $hostDll fuzz-native schannel-rsa TestResults/quic-schannel-public.pem unused-for-schannel 104729 100000 TestResults/quic-schannel-native
```

The existing Windows CI test leg is configured to reuse its already provisioned,
hash-locked aioquic peer for both credential profiles, native lifetime checks and
1,000 native mutation cases per profile plus the six certificate-selection cases.
It now also includes six client-certificate policy cases and 16 complete-handshake
certificate/key mutations in that same Windows step. This adds no job or dependency installation.
A configured step is not a completed hosted result; inspect its exact-head run.

The Windows helper retains a bounded `schannel-contract-probe` report on every run,
including a native lifetime-check failure. Its 144 first-flight cases vary RSA/ECDSA credentials,
short/long ALPN, SNI presence, transport-parameter lengths, X25519/P-256 shares and
fragmented ClientHello delivery. X25519 cases separately exercise the original
basepoint and the public Alice key from [RFC 7748 section 6.1](https://www.rfc-editor.org/rfc/rfc7748.html#section-6.1).
The helper also retains the OS version, configured TLS groups and legacy FIPS setting
as read-only host evidence. Missing policy evidence fails validation. Native lifetime
fixtures use P-256; the matrix separately requires enabled X25519 to succeed and
disabled X25519 to reject without installing keys or retaining resources. A configured
unsupported group is exercised as a rejection case, not skipped. No machine TLS policy
is changed. P-256 must be enabled for this acceptance fixture. The helper chooses
`schannel-contract-probe enabled` or `schannel-contract-probe disabled` from the
captured X25519 policy; a wrong expectation is itself a failing result.
The native step precedes the ordinary regression suite within the existing Windows
job; all existing gates, assertions and time allowances remain enforced.
A failed lifetime check remains fatal. The matrix checks native first-flight
key installation and cleanup; it is not a completed TLS handshake or peer result.

The adjacent shared HTTP lifecycle campaign also observes pending client requests
when intentionally stopping listeners. Linux .NET 10.0.13 can report a direct
`SocketError.NotConnected` from HttpClient endpoint discovery during that shutdown.
Cleanup recognizes this specific outcome after checking handler cancellation and
closure. Other direct socket errors, expired cleanup deadlines and normal response
assertions still fail. The extended state-machine self-test exercises these boundaries.
Both captured traces are retained in the ordinary state corpus.
The correction passed 40 captured-trace replays and 10,000 lifecycle cases on Linux
.NET 10.0.13. Final argument-order and equivalent collection/style cleanups passed
all cleanup and negative-oracle checks, both corpus traces and 200 Windows lifecycle
cases. All 96 native diagnostic cases and existing native/core self-tests passed
locally. Hosted run 39528086800 passed all 48 P-256 cases and failed all 48 original
X25519 cases, independently of ALPN, SNI, credential and fragmentation. The expanded
144-case diagnostic passed locally. Run 39530212076 then confirmed the hosted image
configures only NistP384/NistP256 with legacy FIPS disabled: both X25519 variants
failed all 96 cases, while all 48 P-256 cases passed. The fixture now follows that
observed group policy and keeps explicit rejection checks. Complete hosted acceptance
of the corrected fixture remains pending; local first-flight success does not
establish hosted interoperability.

Use fresh output directories. Both `schannel-rsa` and `schannel-ecdsa` are validation
selectors; no production listener setting selects them. The unused key-path argument
keeps the existing development command shape; Schannel uses its non-exportable
credential. Native mutation receipts now distinguish constructed TLS owners from actual native
context starts. Inputs mix raw frames, malformed CRYPTO data and structured TLS 1.3
ClientHello messages; forged packets remain isolated. OpenSSL allocates its SSL
context at construction, while Schannel starts its context after complete-hello
selection, so the start counts are not directly comparable between providers.

The Windows profile currently restricts negotiation to
TLS_AES_128_GCM_SHA256. The supplied-selector development profile is implemented on Windows; production
SNI/certificate-source policy integration, client certificates, credential reload,
resumption and other cipher suites remain acceptance work. Native TLS operations
also need production worker/lifetime integration before moving into the UDP owner.

## Source connection-owner integration

The core now contains the packet engine and bounded connection registry. Their
injected TLS and application factories keep native bindings, HTTP error mapping
and development success counters in the host. `ProbeConnection` and `ConnectionSet`
are thin development wrappers; they no longer duplicate packet or registry logic.

Every established development connection uses `ConnectionStreams`, including the
simple echo profile. Its former special stream-zero echo path is removed. The
shared default profile advertises 16 incoming bidi and 16 uni streams, 64 KiB per
stream, 256 KiB connection receive credit and a ten-second idle timeout.
`QuicConnectionLimits` supplies validated internal stream counts and idle timeout;
the focused HTTP/3 configuration test advertises 128 bidi streams, 16 uni streams
and a 60-second idle timeout and checks the actual stream-limit close frame. This
is an internal configuration seam; production listener defaults are unchanged.

Shutdown releases connection keys even when application or TLS disposal throws.
Registry disposal visits every owner, removes aliases and reports ordinary cleanup
failures together after completing sibling cleanup. Direct managed mutations use
a deterministic test TLS substitute with public test keys to exercise authenticated
packet framing, flow limits, connection admission and retirement. The native and
independent-peer suites remain responsible for actual TLS interoperability.
Both coverage-guided scripts instrument the source packet and registry owners;
AFL++ also checks distinct coverage for normal and malformed owner seeds.

Async stream admission now uses the same validated stream-window capacities as the
packet owner. The production 128 bidirectional / 16 unidirectional profile permits
144 pending inbound accepts and 208 retained stream owners, including the existing
32 locally opened streams of each direction. The 16/16 development profile retains
its 32-accept / 96-owner bounds. Unaccepted terminal streams retain their credit;
accept cancellation does not consume an item; transport limit errors and local-open
backpressure still enforce each direction separately. Regression cases exercise
full mixed-direction occupancy, local overlap, retirement, replenishment and owner
cleanup. Managed property/mutation/state-machine and coverage-guided paths include
configured-capacity cases.
The increment passed 72 raw/async independent-peer cases across OpenSSL and both
Schannel credential profiles, 100,000 managed mutations, 10,000 cases per property,
4,000 generated actor traces and corpus replays. Separate 60-second libFuzzer/AFL++
campaigns completed 127,662 / 27,729 executions with no retained crashes or
hangs. Source quality, Debug/Release diagnostics and changed-file formatting gates
passed; native Windows hosted interoperability remains a separate open check.

The connection-owner promotion passed the same 159 independent wire cases across
OpenSSL, Schannel RSA and Schannel ECDSA, including six SNI selection cases. It also
passed 30,000 native mutations, 30,000 shared-owner commands, 100,000 managed
mutations, 10,000 cases per FsCheck property, 4,000 actor traces and their stored
replays. The two 60-second coverage campaigns completed 161,506 libFuzzer and
30,322 AFL++ executions with no retained crashes or hangs; ordinary assemblies
remained uninstrumented. These bounded runs exercise the current development
profile and do not establish production load, browser or platform acceptance.

### Receive-page ownership

The source receive window rents storage for occupied pages and clears it on return.
Empty streams preserve their offsets and final-size state without reserving payload
storage. Consumption releases empty pages; stream retirement and connection shutdown
release remaining pages. Buffered overlap, final-size, reliable-prefix and sliding
window rules are unchanged. The generic stream model now includes page-boundary and
non-power-of-two window traces, shared by all existing QUIC fuzz engines.

Dedicated regressions exercise dirty pool input, partial consumption, wraparound,
retired duplicates, overlap rejection, empty FIN, sparse disposal and repeated disposal.
The empty-owner allocation regression fails with the eager-window implementation.
The focused suite passed 72 independent peer cases, 10,000 FsCheck cases per
property, 100,000 managed mutations and 4,000 generated recovery/lifetime traces.
The final Windows fixture passed 54 peer/certificate-selection cases, 2,000 native
mutations and 144 first-flight cases. Supplying the wrong disabled-X25519 expectation
on the enabled local machine failed exactly its 96 X25519 cases. Separate script
checks retain failure for missing policy, failed lifetime checks and failed matrices.

Fresh 60-second campaigns completed 198,310 libFuzzer and 25,027 AFL++ executions,
with no retained crashes or hangs. Both instrument the source receive window, and
the page-boundary/minimal corpus entries replay through their ordinary harnesses.
Debug/Release diagnostics, PrettyCSharp, source-quality and analyzer guards passed.

An isolated Windows source-loop comparison reduced allocation for 144 empty owners
from 18,892,912 to 11,632 bytes; the regression gate requires less than 64 KiB.
The loop favored paged storage for 64-byte and larger blocks, while one-byte loops
cost tens of nanoseconds more. These are source-level observations, not completed
HTTP cold/warm/tail or whole-listener memory acceptance.

### Discarded receive payload

Read abort and reliable reset release unused receive payload before stream
retirement. A one-way retention limit lets a reliable prefix shrink while its
missing bytes arrive. Discard clears partial-page payload and returns empty pages;
late suffix data does not rent storage again. Offset, extent, final-size and
receive-window checks still apply to all incoming frames. Bytes already delivered
to the application retain separate consumption ownership, returning credit once.

Direct regressions cover partial-page clearing, pool return ownership, wraparound
at power-of-two and other window sizes, a missing/shrinking reliable prefix,
late consumption, duplicate frames, final-size history and repeated disposal.
The dictionary model now includes discard commands; a dedicated suffix-discard
seed also reaches the shared coverage-guided harness.

The discard increment passed 72 independent peer cases, 10,000 FsCheck cases per
property, 100,000 managed mutations and 4,000 generated recovery/lifetime traces.
Fresh 60-second campaigns completed 178,301 libFuzzer and 29,203 AFL++ executions
without retained crashes or hangs. Debug/Release diagnostics, PrettyCSharp,
source-quality and analyzer guards passed. An isolated warm receive-loop comparison
against the preceding paged implementation observed unchanged allocation per
operation and no slowdown in the tested 1/64/1200/65536-byte blocks; complete HTTP
and listener latency acceptance remains outstanding.

### Scheduled connection ownership

`QuicExecutionPool` bounds active execution registrations and worker tasks. Each
registration has at most one queued or executing turn. Wakeups during a turn
coalesce into one later turn, queued behind other ready owners. Closing prevents
new work; cleanup runs after the active turn, and pool shutdown awaits every owner
before ending its workers. Ordinary execution and cleanup failures remain visible.

`QuicConnectionOwner` copies admitted UDP payloads and counts both queued and
executing packets against its internal mailbox bounds. Packet, timer, application
and disposal operations share the same registration, so native TLS state stays
owned by one execution context. Closing stops later turns and suppresses output
when the current turn observes closure; disposal waits for any publication already
in progress and for native cleanup. The caller supplies a monotonic clock.
The shared UDP host now drives this component through `QuicConnectionDispatcher`.

Local execution/packet ownership regressions, 10,000 FsCheck cases per property,
100,000 managed mutations, 4,000 recovery/lifecycle traces and 72 existing
independent-peer cases passed. Two concurrent first-flight cases passed with each
of OpenSSL, Schannel RSA and Schannel ECDSA, followed by packet-key cleanup and
Schannel native-resource checks. This is first-flight evidence for scheduled
owners. The scheduled shared-socket checks described below extend this evidence.

The eighth managed target and lifecycle traces exercise worker/owner cleanup;
both coverage runners instrument these source types. Fresh 60-second campaigns
completed 148,194 libFuzzer and 25,737 AFL++ executions without retained crashes
or hangs. Ordinary build hashes remained unchanged. AFL reported 40.04% map
stability with concurrent scheduling, so the execution counts do not imply
deterministic edge coverage. Debug/Release diagnostics, changed-file formatting,
source-quality and suppression guards passed.

### Scheduled socket dispatch

`QuicConnectionDispatcher` owns bounded pending/active admission and destination-ID
routing for the shared UDP host. The socket loop drives receive, timer and output
operations while the worker pool runs native TLS and per-connection processing.
Original destination aliases are scoped to copied peer endpoints; issued IDs are
registered before their first packet is sent. A different peer cannot use an
existing issued ID until path validation is implemented. Handshake deadlines
include queue wait and are rechecked before native execution and publication.
Capacity remains reserved through native cleanup, even after expiry requests close.

The output channel has global packet/byte bounds and per-owner bounds. Those
counts include an active asynchronous send. Exhaustion drops UDP output for
transport recovery without creating send tasks or blocking native workers.
Only one socket-loop send runs at a time; each turn yields back to receive/timer
processing. Shutdown waits for the active send and all native work, drains queued
output, then reports unexpected owner/cleanup failures. The caller owns socket
cancellation and must let an active send finish before disposing its socket.

The earlier synchronous registry now lives only in `ReferenceConnectionSet.cs` in
the development host for deterministic packet/native mutation checks. It is no
longer part of the source core or the live UDP routing path. Both fuzz runners
instrument the source dispatcher and the separate test reference.

Regressions pass for a blocked TLS call with a healthy sibling, admission held
through cleanup, queued handshake expiry without a timer tick, send backpressure
including borrowed output, send failure, copied peer ports, foreign-peer issued
IDs, invalid Initial authentication, observable native cleanup failures and
callback timeouts distinct from handshake expiry. The ordinary CI self-test also
runs the execution-pool, connection-owner and dispatcher regressions.

All 84 independent-peer cases passed across OpenSSL and Schannel RSA/ECDSA,
including twelve shared-listener scenarios: concurrent noise/loss, connection
waves and port reuse, concurrent WebTransport and cancellation of a live owner.
The final core also passed 100,000 managed mutations, 10,000 cases per property,
4,000 generated actor traces and six scheduled native first flights. The eighth
managed selector and lifecycle traces exercise dispatcher admission, routing,
output bounds and final resource ownership. A dedicated binary seed retains
mixed routing, timer, send and invalid-packet commands.

Fresh 60-second campaigns completed 77,413 libFuzzer and 24,528 AFL++ executions
with no retained crashes or hangs. AFL reported 5.16% map stability with concurrent
scheduling; its edge percentage is not reliable coverage evidence. These runs
exercise bounded failure/resource assertions, with deterministic regressions and
independent peers providing separate behavioral evidence. Production HTTP/3
integration remains separate from this shared development host.

### Scheduled asynchronous application commands

The dispatcher supplies its connection factory with a coalescing wake callback.
`AsyncStreamApplication` can invoke that callback when an application queues a
stream open, write, FIN, abort, datagram or connection close. Its existing command
readiness channel remains available to standalone consumers. Both notifications
represent the same bounded pending work; the connection worker processes it under
the existing serialized owner, including when a wake overlaps packet processing.
Late callbacks after retirement cannot create new work or revive disposed TLS.

The ordinary self-test covers outbound bidirectional and unidirectional streams
opening, writing, finishing and closing without another packet or timer tick.
The shared UDP host has a `serve-many-async` mode that owns and awaits each async
echo application through listener shutdown. Independent-peer coverage includes
eight concurrent 128-KiB uploads on this mode, with byte-for-byte response checks.
Clients keep the connection open until the server closes after acknowledged
write completion; merely reading EOF and closing immediately does not prove that
the server observed the final acknowledgement. Shutdown awaits every application
task even if connection-owner cleanup reports an error.
The eighth managed target also varies stream direction and payload in bounded
scheduled application traces; a dedicated binary corpus seed selects this path.
The async integration passed 87 independent-peer cases across OpenSSL and
Schannel RSA/ECDSA, 100,000 managed mutations, 10,000 cases per property, 4,000
actor traces and six scheduled native first flights. The new traces also ran in
60-second campaigns with 68,143 libFuzzer and 24,746 AFL++ executions and no
retained crashes or hangs. AFL's 5.04% map stability has the same concurrent
coverage limitation described above.

These are development transport integrations; the production HTTP/3 listener,
controllers and authentication path still require integration and acceptance.

## Actual library HTTP/3 transport integration

The scheduled owner now backs `EmbedIOQuicTransportConnection` and
`EmbedIOQuicTransportStream`; the existing HTTP/3 protocol consumes them through
an owned transport entry point. Closing retains the owner until closing/draining
finishes. HTTP/3 recognizes the engine's stream and connection failures with the
same request/critical-stream decisions used for the existing providers. The
native datagram adapter retains its existing ownership and status behavior; the
engine adapter returns owned payloads without an extra payload copy.

The development shared UDP host can run this actual protocol with
`serve-many-production-h3`. Its independent aioquic peer performs seven requests
per client: a health request, a 128 KiB echo, an unfinished upload that receives a
complete 408 response, and four concurrent subsequent requests. It checks the
local read timeout's `STOP_SENDING` code `H3_NO_ERROR`, normal certificate
verification, response bytes and final connection retirement. This host still
uses development TLS/UDP configuration and a direct exchange callback; it does
not validate production listener configuration, controllers or authentication.

Deterministic and mutation tests cover local/request/raw-stream cancellation,
borrowed-buffer release, completion detaching the caller token, failure
classification and bounded datagram status/ownership. The ordinary self-test
runs these regressions. FsCheck, byte mutation, libFuzzer and AFL++ use the shared
transport oracle. Coverage scripts instrument the actual library once, including
the engine and HTTP/3 adapters, and verify that ordinary assemblies stay intact.

Final-source checks passed 90 independent-peer cases across OpenSSL and Schannel
RSA/ECDSA, including 84 requests through the actual HTTP/3 protocol. Managed
validation passed 100,000 mutations, 10,000 cases per property, 4,000 generated
actor traces and six scheduled native first flights. The existing HTTP/3 and
request-abort suite reported 690 successes and two skips out of 692 cases; a
subsequent configured independent-peer/datagram run passed all 59 cases, covering
the peer-related skip. The remaining skipped certificate-export fallback case
is not applicable to Windows' native certificate-context loading.

Both library targets, Debug/Release core/host/fuzz builds, PrettyCSharp, diagnostic
and source-quality gates passed. All six packages passed content checks, and the
compatibility audit completed 142 cases / 284 upstream/Neo comparisons with zero
errors. Updated coverage instrumentation completed 68,043 libFuzzer and 24,279
AFL++ executions with no retained crashes or hangs. AFL's 4.91% map stability
retains the concurrent coverage limitation described above. These observations
cover this development integration; they do not establish final provider,
platform, browser, controller or authentication acceptance.

## Actual WebServer listener integration

`EmbedIOQuicListenerBinding` now owns the UDP socket and scheduled connection
pool behind the existing HTTP/3 listener interfaces. Stopping acceptance leaves
the socket available for accepted requests and GOAWAY; final disposal joins I/O,
native owners and pending peers. The HTTP/3 session stops accepting before it
joins connections and releases bindings. Existing providers retain their public
selection and settings.

Scheduled factories receive copied local and remote endpoints, including IPv6
scope identifiers. Routing retains its own copies; changing request metadata
cannot change the destination-ID map or later request metadata. Issued IDs stay
bound to the admitted local/remote tuple. The library binding uses the listener's
512 pending/256 active admission envelope, 128/16 incoming stream profile and a
ten-second handshake deadline. It disables the development dispatcher's optional
per-second admission quota while retaining the aggregate resource limits.

The `serve-webserver` host constructs the real WebServer, HTTP/3 listener, bearer
module and WebApi controller using the internal per-instance binding factory.
`verify_listener.py` drives four simultaneous certificate-verified aioquic clients
through route argument binding, missing/invalid bearer credentials, denied and
accepted authorization policies, 128 KiB uploads, complete 408 timeout responses,
stream-local request abort and healthy sibling requests. It mutates request
endpoint objects and checks that later requests retain their original addresses.
A separate accepted request completes a 128 KiB response after listener acceptance
has stopped, followed by shutdown and a bind to the same UDP port. These checks
passed on the OpenSSL and non-exportable Windows RSA/ECDSA development profiles.

Deterministic regressions cover deep endpoint copying, local-destination routing,
continued existing-owner traffic after admission stops, optional admission-window
limits, canceled accepts, repeated concurrent stop/dispose and port ownership.
Dispatcher fuzz mutations now vary local destinations and acceptance state while
checking bounded output and final owner cleanup.

Seven additional failure-injection cases cover accepted-worker cleanup, synchronous
and asynchronous sibling-binding failures, context-reader completion, restart after
a failed drain, throwing cancellation callbacks and propagation of critical errors.
The listener collects ordinary failures while completing teardown, preserves a single
failure's type and stack, and reports multiple failures together. The dispatcher
mutation target also exercises both synchronous and asynchronous binding failures.
The teardown update passed all seven regressions, another 100,000 managed mutations,
10,000 cases per property, all three actual-WebServer TLS profiles and the same
750-case existing suite (749 successes, one inapplicable skip). Package and
compatibility checks passed again. Its instrumented campaigns completed 56,208
libFuzzer and 25,084 AFL++ executions with no retained crashes or
hangs. AFL reported 6.17% map stability; the concurrent coverage limitation
still applies. The extended development command
includes the actual WebServer peer fixture when an OpenSSL path is supplied.

Validation repeated the 90 existing independent-peer cases and six scheduled
native first flights, alongside 12 actual-controller clients, 12 request-abort
cases and three active-response drain/rebind cycles. Managed checks passed
100,000 mutations, 10,000 cases per property and 4,000 generated actor traces.
The final listener source passed another 100,000 mutations and all three TLS
profiles, including exact controller invocation counts after authorization.
Existing HTTP/3, request-abort and native-datagram coverage reported 749 successes,
zero failures and one Windows-inapplicable certificate-export skip out of 750.

Final Debug/Release builds, SARIF/diagnostic checks, PrettyCSharp, all-source
quality checks, suppression checks and AnalyzerGuard passed. All six packages
passed content checks, and the compatibility audit again reported 142 cases /
284 upstream/Neo comparisons with zero errors.

The initial listener campaigns completed 71,651 libFuzzer executions in
61 seconds and 25,342 AFL++ executions in 60 seconds, with no retained
crashes or hangs. AFL's 5.83% map stability reflects concurrent scheduling;
its edge percentage is not reliable coverage evidence.

The host rejects TLS policy settings outside its development profile. These tests
establish the integration path; supported-platform packaging, full certificate
policy and public WebTransport acceptance still precede production selection.

## Library Windows TLS and certificate policy

The original Schannel binding now compiles in the .NET 10 library instead of the
development executable. The main library enables unsafe compilation only for
that target to support its bounded native buffers and generated Windows SDK
interop. The Windows actual-WebServer fixture uses the library's certificate-source
and policy factory. Its ordinary handshake path does not collect development
status, algorithm or SNI diagnostic strings. Public provider selection and existing
runtime dependency groups are unchanged.

The factory captures its fallback certificate when a handshake is constructed.
After complete ClientHello framing, the configured selector receives the original
bounded SNI spelling once. A null result uses that captured fallback; the selected
certificate is cloned for the native credential without exporting its key. The
application retains ownership of source certificates according to the existing
`ServerCertificateSource` contract. Policies lock when the factory is created.
The internal profile requires TLS 1.3. Its Windows client-certificate policy integration
is described below; full cipher and supported-platform parity remain required before
production provider selection changes.

`ProductionTlsTests` covers fragmented selection, publication before and after
handshake construction, fallback timing, policy locking, explicit unsupported
configuration, ordinary callback rejection, critical callback propagation and
absence of ordinary diagnostic strings. It verifies that the native TLS class
comes from the actual library assembly. Existing native lifetime tests still
exercise both non-exportable key types and concurrent context cleanup.

`verify_tls_listener.py` drives the actual HTTP/3/WebServer/controller path with
strict aioquic certificate verification. Six HTTP requests verify initial and
published fallback credentials, selected SNI credentials and healthy existing
connections. Each records the verified leaf's SHA-256 fingerprint. Three negative
handshakes require the certificate-validation error for obsolete trust, wrong SNI
trust and an unknown hostname. Missing/denied bearer credentials cannot publish a
certificate; the authorized controller publishes once. An accepted large response
then completes during drain, and the UDP port can be rebound. The fixture passed
with non-exportable Windows RSA/ECDSA credentials; it does not establish other
platforms. Client-certificate validation has its separate fixture below.

The final Windows TLS increment passed another 100,000 managed mutations,
10,000 cases per property, 5,000 native mutation inputs per non-exportable key
type, all three actual-WebServer TLS profiles and the certificate-policy fixture.
The wider promotion checks also passed 90 independent-peer cases, 4,000 generated
actor traces and six scheduled native first flights. The existing HTTP/3,
request-abort and native-datagram suite again reported 749 successes, zero failures
and one Windows-inapplicable certificate-export skip out of 750 cases. All six
packages passed content checks; the compatibility audit again completed 142 cases /
284 upstream/Neo comparisons with zero errors.

Updated instrumented campaigns completed 55,662 libFuzzer and 24,941 AFL++
executions with no retained crashes or hangs. AFL reported 6.30% map stability;
the concurrent-coverage limitation still applies, and its edge percentage is not
used as a coverage claim. The updated `--schannel` development driver also passed
an end-to-end one-seed check with 100 cases per property, 20 traces per state
scenario and 50 native inputs per key profile. This checks the command's wiring;
the larger native campaign counts above come from the separate full increment run.

## Windows client-certificate policies

The library TLS policy factory now requests a client certificate for `Allow` and
`Require`, using the Windows SDK's mutual-authentication request and disabling
Windows account mapping. After native handshake verification, it queries the
peer certificate and presented intermediates. The chain builder applies client
usage, dates and the configured revocation mode. Presented roots enter only the
extra-certificate collection; they do not become trust anchors. The existing
`ServerTlsPolicy` validator receives the chain and errors and makes the final
application decision. Missing certificates follow `Allow`/`Require` without
calling a certificate callback. Ordinary callback exceptions refuse the handshake;
critical runtime exceptions retain their propagation behavior.

TLS completion remains false until validation succeeds, so the stream application
and HTTP/3 controller cannot start earlier. The accepted peer publishes an independent
public certificate through the existing request API. Request and validation-callback
certificate copies remain usable after native connection cleanup. The chain remains
borrowed for the validation call. Tests keep bounded samples, verify them after shutdown,
then release their test-owned references. Native query contexts, store snapshots,
server credentials, TLS contexts and buffers retain explicit cleanup.

`verify_client_certificate.py` covers all three modes, default trust decisions,
application pinning, custom in-memory test roots, date/client-usage checks, missing
intermediates and validation callbacks. A loopback-only CRL fixture uses fresh URL
identities and checks a healthy and revoked chain with online validation; disabling
revocation makes no CRL requests. It does not modify operating-system trust stores.
The matrix also checks the existing bearer policy, retained certificate objects,
healthy sibling connections, active-response drain and port rebind.

`fuzz_client_certificate.py` drives complete encrypted TLS handshakes with original,
truncated, extended, modified and arbitrary certificate encodings and distinct key
proofs. The oracle requires the pinned identity for every completed controller
request and keeps a separate healthy connection active throughout the campaign.
It uses at most 16 mutation peers, retains an unexpected input with its seed/index
and ephemeral fixture, and attempts normal host shutdown before a kill fallback.
Native implementation code is not coverage-instrumented. The existing managed
properties, mutation targets, actor traces, SharpFuzz and AFL++ continue to cover
transport/parser/ownership behavior separately.

A local 5,000-input complete-handshake campaign passed with seed 104729, 1,429
accepted pinned identities, 3,571 expected TLS rejections and 1,123 successful
healthy-sibling requests. The run completed in 68.988 seconds on the Windows
validation host. This is fuzz workload evidence, not a latency benchmark. The
23-case wire matrix passed again on final source, including retained request and
validation-callback certificates after shutdown. A second 5,000-input campaign
with seed 1729 passed with 1,429 accepted pinned identities and 1,117 successful
healthy-sibling requests. Synthetic replay controls reproduced both an accepted
identity and a mismatched-key rejection using the retained DER/key fixture.

The final increment also passed 100,000 managed mutations, 10,000 cases per
property, both 5,000-input native first-flight profiles, all three actual-WebServer
TLS profiles, and the 750-case existing HTTP/3/request-abort/datagram suite
(749 successes, zero failures and one Windows-inapplicable certificate-export
skip). All six packages passed content checks; the compatibility audit completed
142 cases / 284 comparisons without errors. Updated instrumented campaigns
completed 58,599 libFuzzer and 25,070 AFL++ executions without retained crashes or
hangs. AFL reported 6.04% map stability, so its edge percentage remains unsuitable
as a coverage claim.

The extended `--schannel` command runs the full policy matrix and adds one native
authentication campaign per seed, using `--native-cases` for its input count.
The bounded Windows CI profile selects six policy cases, including online CRLs,
and 16 mutation inputs. Configured hosted steps require an exact-head passing run.
For retained input replay, use a fresh output directory:

```powershell
python test/EmbedIO.Quic/fuzz_client_certificate.py --dotnet dotnet `
  --assembly <harness>/EmbedIO.Quic.Development.dll `
  --replay <evidence>/failure-<index>.json --output TestResults/auth-replay
```

The retained DER and generated test-key fixture are required for exact input replay;
the seed alone does not reproduce cryptographically generated certificates. Keep
these local evidence directories ignored. Cross-platform client certificates,
supported-platform TLS parity and final production provider selection remain acceptance work.

## TLS 1.3 cipher profiles

The packet core implements TLS_AES_128_GCM_SHA256, TLS_AES_256_GCM_SHA384 and
TLS_CHACHA20_POLY1305_SHA256. Initial protection remains AES-128-GCM/SHA-256
regardless of the negotiated handshake cipher. AES-256 uses SHA-384 and 48-byte
traffic secrets; the other profiles use SHA-256 and 32-byte secrets. Key updates
retain the negotiated profile and original header-protection key. Payload AEADs
use .NET cryptography. The ChaCha header mask is an original single-block
implementation of the operations specified by
[RFC 9001 section 5.4.4](https://www.rfc-editor.org/rfc/rfc9001.html#section-5.4.4)
and [RFC 8439](https://www.rfc-editor.org/rfc/rfc8439.html).

The library Schannel profile negotiates both AES-GCM suites using Windows cipher
policy. It validates native secret metadata and buffer lengths before installing
keys, and rejects profile changes between directions or handshake levels. It does
not advertise ChaCha through the Windows native binding. The development OpenSSL
binding uses its public negotiated-cipher API for all three profiles. This is
cipher interoperability evidence, not completed cross-platform certificate,
packaging, browser or production-provider acceptance.

[RFC 9001 section 6.6](https://www.rfc-editor.org/rfc/rfc9001.html#section-6.6)
requires different integrity bounds. Failed authentications accumulate across all
packet keys and generations: AES-GCM is capped at 2^52 and ChaCha20-Poly1305 at
2^36. Reaching the applicable bound stops packet processing with
AEAD_LIMIT_REACHED. The current internal per-key encryption budget remains a
conservative 2^23 for every profile, and normal ACK-confirmed update initiation
remains at 2^20 packets. A configured lower test budget survives key updates.

`CipherSuiteTests` covers the RFC 9001 Appendix A.5 complete ChaCha packet and
header mask, the RFC 8439 block vector, secret sizes, native metadata/truncation,
key-update budgets and authentication bounds. Repeated-generation tests exercise
all three suites. The shared protection mutation target selects among all three,
so the managed mutation, FsCheck, libFuzzer and AFL++ entry points exercise the
new profiles without adding production dependencies.

`verify_cipher_suites.py` compares complete protected packets, updated packets and
random ChaCha header samples against the pinned independent aioquic engine.
Forced-cipher listener checks cover OpenSSL with all three suites and Schannel
RSA/ECDSA with both AES suites. Each actual WebServer fixture retains controller,
bearer authorization, upload cancellation, sibling isolation, response drain and
port-rebind assertions. Run the complete local matrix with:

```powershell
python test/EmbedIO.Quic/verify_cipher_suites.py --dotnet dotnet `
  --assembly test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.Development.dll `
  --openssl <OpenSSL-directory> --schannel --output TestResults/quic-ciphers
```

The local development driver includes this matrix; the existing Windows Schannel
CI step includes a bounded two-credential AES matrix. Certificate-mutation failures
now retain a qlog alongside their DER, test-key fixture and receipt for investigation.

The final cipher source passed 384 independent packet/key-update vectors and all
seven forced-cipher actual-WebServer profiles, plus 100,000 shared-core mutations
and 10,000 cases per property. Debug/Release compiler reports, PrettyCSharp,
formatter/analyzer checks and suppression guards pass for the independent core,
development host, Fuzz and SharpFuzz projects. Evidence is retained under ignored
`TestResults/quic-feasibility/continuation/cipher-functional-03` and
`cipher-quality-04`. The same final source passed both 5,000-input Schannel
first-flight campaigns, the full 23-case client-certificate matrix and 5,000 complete
authentication mutations: 1,429 accepted pinned identities, 3,571 expected rejections
and 1,225 successful healthy-sibling requests. The extended driver passed with
both OpenSSL and Schannel enabled. The 750-case existing HTTP/3/request-abort/native
datagram set passed with 749 successes and one Windows-inapplicable certificate-export
skip. All six packages and 142 compatibility cases / 284 comparisons passed.

Final coverage campaigns completed 52,569 libFuzzer executions and 24,976 AFL++
executions with no retained crashes or hangs. AFL's 5.25% map stability under
concurrent scheduling limits edge-percentage claims.
Receipts and corpus/failure directories remain ignored. Full replacement acceptance
remains below.

A five-process-per-size Windows packet microbenchmark compared AES-128 protection
and unprotection with `e4667e7`. Each process warmed for three seconds before
4,096 measured batches of 32 pairs. Median-of-process results, before / after:

| Payload bytes | Median pair ns | P95 batch pair ns | P99 batch pair ns | Allocated bytes/pair, rounded |
| --- | ---: | ---: | ---: | ---: |
| 128 | 687.5 / 681.25 | 771.875 / 700 | 1075 / 912.5 | 696 / 696 |
| 1200 | 925 / 931.25 | 1021.875 / 1031.25 | 1412.5 / 1340.625 | 3912 / 3912 |

The fresh-process first pair, including packet-key creation, rose from 8.087 to
8.812 ms for 128 bytes and 8.055 to 8.913 ms for 1200 bytes. This captures primitive
initialization/JIT cost on a shared development machine. It is not an endpoint,
handshake or production tail-latency measurement. An earlier short-warmup sample
had inconsistent tails; both sets remain retained under `cipher-latency-01` and
`cipher-latency-02`. Full cold/warm/tail listener acceptance remains required.

## Independent HTTP/3 peer request lifetime

The independent Python HTTP/3 peer retains a terminal connection event. New
requests fail immediately with that event's error after closure; pending requests
receive the same error. When the pinned reference engine authenticates a peer close
and enters draining, the helper notifies HTTP waiters immediately. It leaves the
engine's drain timer and normal connection cleanup intact. A single stream reset
leaves sibling and later requests usable.

Four deterministic regressions use the real QUIC/H3 objects with UDP transmission
disabled, including a complete encrypted in-memory handshake and a six-second drain.
A separate full native-handshake run with the same slow reference drain completed
all 16 inputs: five accepted identities, 11 expected rejections and 97 successful
healthy-sibling requests. The five-second HTTP response deadline and existing
case deadlines remain unchanged. The existing Windows Schannel step runs the
regressions through the pinned peer interpreter:

```powershell
python -m unittest discover -s test/EmbedIO.Quic -p test_http_peer.py
```

## Remaining production acceptance work

1. Complete application configuration, certificate lifetime and platform acceptance
   for the new internal socket binding. The real WebServer path now has initial
   controller/authentication/cancellation/drain evidence, plus Windows SNI and live
   certificate-publication checks through the actual policy factory. Complete
   client-certificate/revocation behavior on the other supported platforms, broaden
   SNI/reload and non-exportable-key support, native cipher-policy parity and
   bounded native TLS worker scheduling. The listener uses bounded worker ownership;
   the offline actor model is not a listener load test.
2. Validate longer transfer/loss/endurance patterns and expose sustainable stream
   lifecycle through the production application transport boundary. The development
   host already replenishes stream counts and retires stream/application buffers.
   Receive and recovery histories retire within fixed bounds; packet encryption
   enforces per-key AES-GCM limits and ACK-confirmed local/peer key generations.
3. Complete the remaining transport/TLS profile, including Retry, post-handshake
   TLS, resumption and any intentionally supported 0-RTT policy. Extend acceptance
   of the implemented error classification, reset and path handling across peers
   and platforms. Features outside the supported profile must fail explicitly.
4. Validate the now-integrated ordinary provider selection, actual TLS capability
   checks and transitive native runtime delivery across supported platforms.
   Finish public WebTransport API/controller integration and broaden the actual
   WebServer auth/cancellation/disposal checks across those configurations.
5. Independent peers on Windows/Linux/macOS, browser compatibility, dependency
   packaging, and startup/warm/tail-latency measurements. Windows embedded hosting
   leads; meaningful warm regressions are not accepted for cold-start gains.
6. Exact-source final build/diagnostic/security/package gates and full existing
   HTTP/1-3 regressions before merging the production replacement.

## MsQuic retirement and issue closure

William directed full removal of MsQuic, including indirect `System.Net.Quic`
usage, and removal of its obsolete quarantines/workarounds after replacement.
Keep historical evidence intact. Preserve transport-independent lifecycle and
restart assertions by exercising the new provider rather than deleting coverage.

The retirement inventory includes native API/binding/transport/datagram classes,
provider selection, native library packaging/provisioning pins, MsQuic cleanup and
credential workarounds, bind retries, raw-runtime quarantine classifiers and
counts, diagnostic dispatches, retirement flags and provider-specific guides.
Review the existing `ci.yml`, `diagnostics.yml`, cleanup scripts,
[QUIC lifetime guide](quic-lifetime.md) and [engine record](http-engine.md) together.
New-provider failures must never inherit an old MsQuic quarantine.

| Tracking issue | Closure condition |
| --- | --- |
| [#202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202), lifetime/workaround retirement | Production replacement and verified removal of obsolete bind retry/quarantine/runtime workarounds, with owned restart/lifetime coverage passing. |
| [#278](https://github.com/WilliamSmithEdward/embedio-neo/issues/278), send-buffer retention/endurance | Verify bounded managed/native memory, handles and retained state after realistic traffic, quiescence and repeated replacement on the new provider. |
| [#398](https://github.com/WilliamSmithEdward/embedio-neo/issues/398), Windows credentials | Recheck combined TCP+QUIC and first-handshake behavior on the replacement; close when the reported runtime path is removed and the behavior is validated. |
| [#399](https://github.com/WilliamSmithEdward/embedio-neo/issues/399), macOS accept/lifetime | Recheck the acceptance/parent-child lifetime case on macOS with the new provider before closure. |
| [#303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303), reliable reset | Close after current-wire reset is integrated and validated in the actual supported provider, beyond this development host. |
| [#414](https://github.com/WilliamSmithEdward/embedio-neo/issues/414), WebTransport API | Close after production session/stream/datagram application integration and its tests; removing MsQuic alone does not deliver the API. |

The broader [HTTP engine program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181)
also references MsQuic. Update its QUIC work items at retirement; retain unrelated
actionable protocol/performance work.

After MsQuic is fully removed, review every open EmbedIO-Neo issue, including
reports outside this inventory. Confirm resolution against current source and
relevant validation, close resolved issues, and record concrete remaining work or
specific validation gaps on issues kept open. Keep existing outcome replies
current and avoid duplicate notifications. No issue is closed by this document
or by the development host alone.

## Portable TLS policy boundary

The internal portable adapter uses OpenSSL 3.5.9 only for TLS through a small
original polling ABI. Owned native queues carry CRYPTO output, packet-key secrets
and peer parameters; managed code owns SafeHandle lifetime, bounded reassembly,
packet keys and application certificate policy. Key material and exported server
credentials are cleared after transfer. Borrowed native peer certificates become
independent DER copies before managed validation. The common factory captures
fallback certificates per handshake and preserves existing SNI, certificate
publication, trust, revocation and callback behavior. Windows still uses Schannel.

Initial Linux x64 / glibc 2.39 validation of the development build passed 24 wire
cases, six SNI cases, live certificate publication, 24 mutual-TLS policy cases,
96 cipher vectors and all three forced-cipher WebServer profiles. A deterministic
5,000-input encrypted certificate/key campaign passed with 715 accepted pinned
identities, 4,285 rejections and 1,227 healthy concurrent requests; drain and rebind
also passed. A retained input replay passed. A minimal consumer referencing the
actual EmbedIO assembly and common TLS factory passed 128 KiB echo/ping with each
of the three cipher suites under both JIT and NativeAOT, with zero retained native
contexts or queued blocks. Wrapper-instrumented ASan/UBSan/LeakSanitizer checked
3,072 ownership lifecycles, including bounded queues and premature certificate
queries. The initial vendor archives were not compiler-instrumented; this is not
whole-vendor sanitizer coverage or an endurance/performance acceptance.

The reproducible native build and license/receipt output are documented in
[`native/EmbedIO.Quic.Tls`](../../native/EmbedIO.Quic.Tls/README.md). The new build
configuration additionally disables automatic system configuration and dynamic
module loading; its exact binaries require separate repeat validation. Portable
CI is configured on Linux and macOS with independent peers, SNI/publication,
mutual TLS and bounded authentication mutations. This paragraph records configured
scope, not a completed hosted run. Native RID packaging, older libc/musl, other
architectures, remaining TLS/transport behavior and full memory/endurance/latency
acceptance remain open. Public provider selection and dependencies are unchanged;
production MsQuic removal and the subsequent issue audit are still pending.

The restricted Linux build repeat passed the same 24 wire cases, six SNI cases,
live publication, 24 client-certificate cases, 96 vectors and three forced-cipher
WebServer profiles. Its 5,000-input authentication campaign passed with 715
accepted pinned identities, 4,285 rejections and 1,223 healthy concurrent requests,
plus drain/rebind and the retained input replay. Six fresh common-factory JIT and
NativeAOT wire cases also passed with zero retained native contexts/buffers. The
Windows Schannel CI script passed after the test-loader rename. Build-tool archive
regressions cover ordinary file contents/permissions and refusal of traversal,
foreign roots, links and special files. These are Linux x64/glibc 2.39 and Windows
local results; hosted macOS validation and distributable native assets are pending.

## Outgoing chains and portable revocation fixtures

Private native ABI 2 adds ordered DER issuer installation after the selected
leaf's private credential. The managed adapter uses the runtime's normal issuer
discovery, retains a partial chain's final issuer, omits a complete chain's trust
anchor and releases the copies it creates. Client-certificate validation remains
separate. ABI 1 fails preflight before readiness.

Five local Linux profiles passed against both a BCL TCP control and the actual
QUIC factory: complete, partial, two intermediates, direct-root and self-signed.
The peer trusts only the configured root and checks exact wire issuer hashes/order
and root omission. Native contexts/buffers return to zero. This fixture modifies
only public CA entries in an explicitly disposable Linux user profile, removes its
entries, never trusts a root globally, and is outside ordinary application startup.
Generated chain certificates include subject and issuer key identifiers. The TCP
control explicitly enables `VERIFY_X509_STRICT`, so older local peer interpreters
apply the same strict certificate checks as newer hosted interpreters.
The native ownership run passed 6,144 lifecycles, including 32-issuer capacity,
malformed DER, credential parsing and retirement. Wrapper ASan/UBSan/leak checking
used uninstrumented vendor archives; it does not establish whole-vendor coverage.

The independent revocation fixture now supplies signed OCSP responses alongside
CRLs, checks requested certificate IDs, echoes nonces and bounds HTTP GET/POST.
Four responder/transport regressions passed; the existing 23 Windows and 24 Linux
policy cases passed without changing their expectations. Local ABI 2 validation
also passed the wire/SNI/publication/cipher matrices, 5,000 authentication mutations
and six common-factory JIT/NativeAOT wire cases. The hosted macOS 26 ARM64
[portable job in CI 39638190853](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/39638190853/job/119354520165)
passed 24 wire cases, six SNI cases, live publication, all 24 certificate policies
including online good/revoked status, all three ciphers and 256 native-auth inputs
with 96 healthy concurrent requests. Linux in the same run exposed missing issuer
key identifiers in the generated chain fixture's TCP control. The corrected
fixture passed all five local chain profiles with strict validation enabled;
the original hosted failure is retained and a changed-source CI repeat is required.
This is not acceptance of all native RIDs or final dependency packaging.

## TLS completion time and portable runtime assets

A Windows hosted revoked-client check timed out after policy had correctly
refused the identity. A controlled local responder delay reproduced the missing
wire rejection: initial TLS work could outlive a closing interval measured from
packet arrival. The serialized owner now supplies its monotonic clock to the
connection; initial TLS completion refreshes time before close/recovery work.
Established packet processing avoids this additional clock read. The existing
revoked-client fixture delays its local revocation responses by 600 ms and still
requires the exact CRYPTO_ERROR, no controller invocation, cleanup and rebind.
The full 23-case Windows policy suite and execution/dispatcher checks passed
locally. Changed-source hosted and remaining platform results remain required.

The Linux x64 asset is built using a digest-pinned manylinux2014 compiler image
with the exact signed Perl-core package required for configuration. Its Python,
Perl and compiler are build tools, not runtime dependencies. The final local
artifact's highest required glibc symbol version is 2.17; dependencies are libc,
libpthread and libdl. This is below the .NET 10 portable glibc 2.27 baseline and
does not add support for the image's retired host distribution. See the
[.NET platform requirements](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
and [manylinux toolchain documentation](https://github.com/pypa/manylinux).

The audited artifact passed 24 wire cases, all three cipher profiles/96 vectors,
24 client-certificate policies, five strict issuer-chain profiles, six SNI cases,
live publication and 5,000 full-handshake mutations. The latter accepted 715
pinned identities, refused 4,285 inputs and completed 1,237 healthy concurrent
requests with cleanup/drain/rebind. These native-binary runs precede the separate
clock-path change above; its combined Linux repeat is recorded separately.
The ordinary and compiler-instrumented/native-vendor coverage limitations still
apply.

`audit.py` checks native architecture/RID, exact versioned exports, dependency
closure and ELF stack/RELRO/immediate-binding properties without loading the
library. A requested glibc distribution RID fails if symbols require a version
above 2.27; the earlier development binary requires 2.38 and is rejected. macOS
builds target 15.0 and verify their Mach-O deployment/dependency metadata. Musl
identity alone cannot establish its minimum compatible runtime. Eight native
build/audit test methods cover incompatible architectures, libc/RID mismatches,
foreign dependencies/exports, missing protections and macOS deployment metadata.
The portable CI artifacts now retain binaries, licenses, audit records and build
receipts. Other RIDs, actual NuGet runtime-asset consumers, signing/AOT/browser
acceptance and the provider transition remain outstanding.

## Native package assembly and consumers

The native runtime project is outside the ordinary solution and builds an
unpublished `EmbedIO-Neo.Quic.Native` package. Its six runtime paths cover Linux
glibc/musl and macOS, each x64/ARM64. It ships the private TLS library, required
OpenSSL license, binary audits, build receipts and a complete inventory manifest;
managed engine code remains in the modern core assembly. Core dependency groups
and ordinary six-package output are unchanged in this development increment.
The future dependency must remain modern-only to preserve legacy Framework
AnyCPU behavior; [NuGet documents this native-package caveat](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages).

Staging checks exact source/archive identity, platform headers, audit/receipt
consistency, licenses and bounded file inventories. The source comparison accepts
only current bytes or CRLF-to-LF normalization. Package validation compares every
native/provenance/notice path and byte with those staged inputs, checks the package
identity/license and records both preview package hashes and their source commit.
These consistency records are not build attestations. Explicit single-RID flags
support local development; default staging/packaging/consumer CI requires all six
RIDs and never publishes the previews.

The new reusable workflow uses native hardware for each RID, immutable compiler
and musl SDK image digests, exact signed Python packages and two concurrent jobs
per matrix. After assembly, every RID must pass package-only portable, RID-specific
and Native AOT consumers using a fresh mapped NuGet cache. The test applications
cannot copy a locally built native library in package mode. The focused AOT
consumer uses generated controller bindings and source-generated JSON, exercising
normal WebServer HTTP/3 dispatch, bearer policy, 128 KiB upload/rejection,
request-abort cancellation, healthy subsequent requests and owned cleanup with
all three ciphers. The full reflection-based development harness separately keeps
its wire, cipher, SNI, publication, certificate-policy, mutation and chain suites.

Local Alpine 3.24 x64 validation used a musllinux-built asset with only the musl
libc dependency. The actual library/development host passed 24 wire cases, all
24 certificate-policy cases and 96 cipher vectors across the three forced-cipher
WebServer profiles. The initial local package passed 24 portable wire cases and
six RID-specific certificate-policy cases. All six focused package-only JIT/AOT
cipher cases passed, including authorization, limits, abort and zero owned native
resources. The expanded package driver then passed all nine portable/RID/AOT
cipher cases and the full protocol/policy/mutation/issuer suites. These are local
single-RID preview results; they do not establish all six RIDs, the oldest musl
baseline, hardened macOS loading/signing, browser interoperability, whole-vendor
instrumentation or endurance/latency acceptance.

The earlier macOS 26 authentication campaign in run 39644578782 timed out in two unchanged
hosted attempts on different inputs. Both retained peer traces show only outgoing
Initial retransmissions and no received server flight; they do not implicate the
later mutated certificate payload. Other wire/cipher/SNI/publication/policy checks
in those jobs passed. No peer deadline, concurrency or rejection oracle was changed.
The development host now reports post-disposal dispatcher counters and the latest
rejected-owner exception, bounded to a 4 KiB diagnostic string. Formatting happens
only when the retired listener is inspected. This is additional failure evidence,
not a claimed correction of the unexplained macOS timeout. Exact-source hosted
validation remains required before acceptance and provider retirement.


## Peer connection-ID retirement and current platform acceptance

The connection now applies authenticated `NEW_CONNECTION_ID` frames.
[RFC 9000 retirement watermarks](https://www.rfc-editor.org/rfc/rfc9000.html#section-5.1.2) select a live destination
before the next response; `RETIRE_CONNECTION_ID` uses existing ACK/loss recovery.
The helper owns copies of peer IDs and reset tokens, bounds active IDs to the
advertised two and pending retirements to four, drops acknowledged references,
and represents retired sequence history with a watermark. A 10,000-rotation model
checks that state stays bounded. Invalid framing, conflicting active identities
and exceeded limits retain their distinct transport error codes. Reset tokens
are retained for active-ID consistency only; stateless reset is not implemented
by this change.

A controlled encrypted reference peer removed the original ID from its accepted
set. Before correction, replies used that retired ID and the request failed while
a separate connection remained healthy. After correction, independent wire checks
passed eight Windows Schannel cases and ten Linux portable cases: both AES suites,
Linux ChaCha20, 8/20-byte peer IDs, 16 rotations per positive case, exact retirement
frames, invalid-frame closes, healthy siblings and drain/rebind. A packet model
also verifies retirement retransmission after loss. Local CID issuance/retirement,
path migration, Retry and stateless reset remain separate work.

The helper joins the existing core model, state-machine and lifecycle fuzz paths,
so all existing managed, FsCheck, libFuzzer and AFL++ entry points reach it. The
local candidate passed 100,000 managed mutations with seed 1729 and 10,000 FsCheck
cases per property. Short instrumented campaigns completed 44,022 libFuzzer and
24,929 AFL++ executions with no reported crashes or hangs. AFL++ stability was
4.74%; its reported bitmap is not a coverage percentage or proof of deterministic
state coverage. Linux also repeated all 24 wire cases and a 5,000-handshake
certificate campaign with seed 104729: 715 pinned identities accepted, 4,285 inputs
refused, 1,272 healthy sibling requests and completed cleanup/drain/rebind. Native
vendor coverage and sustained endurance/latency acceptance remain outstanding.

CI run 39653100127, PR head `097a3045` and synthetic merge `52c88c77`, passed all
six native builds and combined-package assembly. The Linux x64, Linux ARM64 and
macOS ARM64 package-only consumer suites passed, including the nine focused
portable/RID/AOT cipher profiles and full protocol/policy/certificate campaigns.
The macOS authentication timeout did not recur in that run; its earlier no-flight
cause remains unproven. Both musl jobs failed at Git's dubious-ownership check,
before their consumer tests. Running Docker as the checkout owner's UID/GID,
with disposable writable HOME/CLI directories, preserves the check. A local
red/green reproduction then passed the complete 26-step musl x64 driver against
the exact six-RID feed and synthetic source commit; ARM64 and changed-source
hosted validation are still required.

Intel macOS failed during peer dependency restore, before wire testing.
[Cryptography 49 removed x86_64 macOS wheels](https://cryptography.io/en/latest/changelog/#v49-0-0);
the pinned 50.0.2 has none. `build_peer_wheel.py` prepares an unpublished test
wheel from its SHA-256-pinned source archive using the reviewed static OpenSSL
3.5.9 SDK, Rust 1.99.1, the unchanged vendor Cargo lock and hash-locked Python
build tools. It checks archive paths, wheel metadata/RECORD, retained vendor and
OpenSSL licenses, architecture, shared dependency closure and actual backend
version. The consumer requires matching workflow source, builder source hashes,
wheel hash and the exact native build receipt from its package. It installs the
specific local hashed wheel first, then the unchanged full peer dependency lock.
No fallback version, unpinned source resolution or skipped Intel wire suite is
introduced. This is test infrastructure, not an upstream-supported Intel wheel
or a production package. Actual Intel hosted validation remains required.


The source-wheel workflow was also exercised locally on Linux with the actual
reviewed manylinux-built SDK. The wheel reported cryptography 50.0.2/OpenSSL 3.5.9,
retained all three vendor licenses plus OpenSSL's license, and had no dynamic
OpenSSL dependency. After installing it into a fresh peer environment and
restoring the unchanged full hash lock, the final managed source passed its
10,076-assertion self-test, ten CID wire cases, all 24 existing wire cases, all
24 certificate-policy cases and 256 authentication mutations (37 pinned identities
accepted, 219 inputs refused, 67 healthy sibling requests). All 22 native
build/audit/package/wheel test methods passed on Linux without skips. This is a
Linux source-build/integration proof, not Intel macOS acceptance. An initial local
Ubuntu-native SDK attempt was correctly refused by the existing glibc baseline
audit; the final proof used the reviewed manylinux builder.


## Received stateless resets and macOS peer dependency metadata

The engine now recognizes received stateless resets for recently used,
non-retired peer connection IDs. It checks a datagram's trailing token after its
first packet cannot be associated or authenticated; a valid authenticated packet
retains precedence. Unused, retired, corrupted and undersized tokens do not reset
the connection. Recognition enters silent draining, cancels active application
work and suppresses later close/recovery responses. The draining interval is not
extended by repeated resets. An exhausted AEAD authentication budget still permits
a valid reset to stop an already-closing connection.

A randomized reset header need not carry the issued server ID. The dispatcher
therefore indexes endpoint-bound keyed token transforms and routes a recognized
candidate to its serialized owner for the final constant-time comparison.
Snapshots change only when the selected eligible token changes. Shared endpoint
groups preserve authentication by the header's owner before forwarding an
unrecognized candidate once through the token owner's bounded mailbox. Expired
header aliases still allow association with a live token owner. The ordinary
single-connection path avoids per-packet token hashing. Routes and
groups remain bounded by admitted owners, retire during cleanup, and their counts
are available only in post-disposal development diagnostics. The per-listener
transform key is cleared after its workers stop.

A controlled Windows wire test initially left an active controller request
running after a valid reset. The receiver correction cancels that request while
an independent sibling remains usable, then completes drain and rebind. Ten
Windows Schannel cases passed across both AES profiles and five header forms,
including minimum-size, known/unknown-ID, clear-fixed-bit and long-header resets.
Each case also rejects five invalid or ineligible reset candidates. Deterministic
model/dispatcher checks cover token ownership, stable metadata, eligibility,
valid-packet precedence across owners, shared endpoints, expired aliases, wrong
source/local addresses, silent draining, authentication-budget closing and complete index cleanup. The helper
also joins the existing core, lifecycle and state-machine mutation entry points.
The local candidate passed 100,000 managed mutations, 10,000 FsCheck cases per
property and 1,000 traces each for recovery and lifecycle with seed 1729.
The final Windows source repeated all ten reset cases and its model/dispatcher
suites. Linux repeated all 15 reset cases, ten peer-ID cases, 24 existing wire
cases and 24 certificate-policy cases with the same-version source-built peer;
all 26 native build/audit/package/wheel test methods passed without skips. The
final mutation, property and trace campaigns also passed after the cross-owner
routing correction. Guided-fuzz results and hosted platform acceptance are
recorded separately; received resets do not implement server reset emission,
server-issued ID rotation, Retry or migration.

In CI run 39662188674, all six native TLS SDK builds/audits completed, including
Intel macOS, where the pinned cryptography 50.0.2 wheel also compiled with Rust
1.99.1 for Python 3.14. Its dependency audit then failed and the downstream package
consumer matrix did not run. The failing audit did not retain its dependency
listing, so the exact rejected row is unavailable. The updated helper preserves
both dependency and install-name output before validation. It uses `otool -D` to
identify the module's own install name, which `otool -L` lists before actual
imports; [Apple documents this distinction](https://developer.apple.com/forums/thread/736719).
Only that exact leading identity row is removed. Actual external, repeated,
malformed, path-traversing and dynamic OpenSSL loads still fail. Regression tests
cover these distinctions and preservation of failed-audit evidence. The expanded
native build/audit/package/wheel suite passed all 26 methods locally on Windows
with its existing symlink-only skip. The subsequent hosted run and retained dependency inventory are recorded below;
source compilation alone does not establish platform acceptance.


## Server-issued IDs and emitted resets

The next engine increment supplies two active 16-byte server connection IDs.
The initial token is an authenticated server transport parameter; a spare and
replacement IDs travel in NEW_CONNECTION_ID frames through existing recovery.
Each ID has a monotonically increasing sequence. Retirement checks the highest
sequence actually sent and rejects retirement of the packet's own destination.
Duplicate retired sequences do not grow retained history. Retired announcement
deliveries stop retransmitting, while lost active announcements retain the same
ID/token. Active state and immutable published alias lists remain bounded at two.

The socket owner installs all active aliases from immutable worker snapshots,
removes retired aliases, and updates routing before sending related datagrams.
The original handshake ID remains stable metadata for admission accounting. It
does not remain an active routing alias after retirement. The fixed ID width is
16 bytes in this unreleased engine profile; peer-issued destination IDs still
support their independently negotiated lengths. IDs are random 128-bit values;
a listener-owned HMAC secret derives their 16-byte reset tokens. Connection
cleanup borrows this capability and listener disposal clears the secret after
all workers stop. Listener-secret persistence across restarts is not implemented.

For unknown eligible short headers, reset output is shorter than its trigger,
uses an unpredictable prefix and the derived token, and never exceeds 43 bytes.
Triggers of at most 43 bytes receive one byte less; packets too small for the
fixed ID and protected sample suffix are discarded. Known IDs and mismatched
endpoint associations do not cause stateless responses. A global 64-attempt
allowance per 1,000 milliseconds and a bounded control share of the existing
output queue limit this work. Copied destination endpoints and normal queue
accounting preserve output ownership and teardown.

The preserved previous host passed health requests but failed the new initial
ID/token assertion. The Windows candidate then passed both AES profiles, each
with 32 server-ID rotations, two independently verified retired-ID reset outputs,
71 healthy controller requests, drain and rebind. Active-ID invalid ciphertext
did not produce stateless output. Sibling requests continued through rotation
and retired-token responses. The model suite passed 10,000 retirements, immutable
snapshot/token ownership, sent-sequence bounds, destination prohibition, lost
announcement retransmission and duplicate retirement. Dispatcher controls passed
rate/output limits, endpoint copying, ordinary handshake progress behind a reset
burst and complete alias/output cleanup. A truncated RETIRE_CONNECTION_ID sequence produces FRAME_ENCODING_ERROR (7),
while an unissued sequence or the carrying destination produces
PROTOCOL_VIOLATION (10). The malformed-sequence regression failed with the previous
error code before the parser correction.

Windows also passed all 24 existing wire cases, eight peer-ID cases, ten received
reset cases and 24 certificate-policy cases. Linux passed the new rotation/reset
suite on all three cipher profiles, plus 15 received-reset, ten peer-ID, 24 wire
and 24 certificate-policy cases. All 26 native tooling test methods passed on
Linux without skips. Model/dispatcher checks also passed after the malformed-frame
correction. These are local development results; final exact-source and hosted
acceptance remain separate.

With seed 1729, the increment passed 100,000 managed mutations, 10,000 FsCheck
cases per property and 1,000 traces each for recovery and lifecycle. Two-minute
instrumented runs completed 82,504 libFuzzer executions and 51,866 AFL++ executions
with no saved crashes or hangs. AFL++ reported only 2.21% coverage stability;
its execution count does not establish stable feedback or exhaustive coverage.
The old short-header mutation fixture was updated to construct the engine's
16-byte ID width after a retained seed exposed its obsolete eight-byte assumption.
No production parser check or mutation invariant was removed.

For the earlier pushed source `61fcc167`, run 39671061660 passed all six native
builds and the Intel macOS peer wheel's metadata audit. Its retained inventory
identifies `@rpath/cryptography.hazmat.bindings._rust.abi3.so` as the module's own
install name; actual imports are `/usr/lib/libiconv.2.dylib` and
`/usr/lib/libSystem.B.dylib`. The combined six-RID package and all six complete package-consumer suites then
passed, including Intel macOS and both musl architectures. These hosted results
apply to `61fcc167`, before the server-issued-ID increment. The ordinary Android
MAUI job failed while preparing an emulator ZIP, before starting the application;
the unchanged failed-job retry passed on attempt two. The first failure and
retry provenance are retained.


## Standards capture controls

The development-only capture helper uses the same reviewed, hash-locked
`defusedxml` 0.7.1 as the source-quality environment. Its separate requirements
file supports an isolated manual capture environment. Requests and redirects
permit only the enumerated RFC Editor, IANA and IETF Datatracker HTTPS origins.
A response or RFC index may contain at most 64 MiB, the RFC update graph at most
512 documents, and index parsing rejects DTD/entity markup. The existing
120-second socket timeout remains; it is not a whole-campaign deadline.

Eleven offline controls cover origin/redirect refusal, exact byte boundaries,
response cleanup, malformed/oversized XML, namespace preservation, graph limits,
parser pin consistency and failure without a fabricated manifest. CI runs them
in its existing source-quality environment. Live bounded fetches returned RFC
9000 metadata and the QUIC registry successfully; a retained 13,732,913-byte RFC
index parsed 9,848 entries and contained RFC 9000. The earlier partial smoke had
incorrectly assumed more than 10,000 entries; the corrected assertion checks the
known document rather than guessing a count. These checks do not redate the
separate frozen standards inventory. See [HTTP conformance](http-conformance.md)
for capture commands.


## Path-control response semantics

The next same-path increment makes each `PATH_CHALLENGE` produce one
`PATH_RESPONSE` with the exact eight-byte payload. The datagram is expanded to
1200 bytes unless the unvalidated-address amplification budget requires a
smaller response. Loss/PTO handling does not retransmit this response; a fresh
challenge can elicit a fresh response. Malformed challenge or response data
produces FRAME_ENCODING_ERROR (7). A complete unmatched response retains
PROTOCOL_VIOLATION (10), which RFC 9000 section 19.18 explicitly permits.

Nineteen of twenty deterministic cases failed on unchanged production source;
the unmatched-response policy already passed. The candidate passes the twenty
cases, including padding at the amplification boundary and response loss after
acknowledging earlier deliveries. The fixture derives the application's actual
sent packet-number range and uses distinct incoming numbers. Earlier fixture
attempts that acknowledged the wrong packet space or reused incoming packet
numbers are retained separately and are not production failure evidence.

The locked independent peer appends an original challenge frame through its
packet builder, retains its cryptography and first-response validator, and
withholds later outgoing datagrams for two seconds. The preserved previous host
repeated the same 38-byte response seven times without a new challenge. The
candidate passed both Windows AES profiles: each peer observed one 1200-byte
response, withheld seven subsequent datagrams, completed five healthy controller
requests across both connections, then drained and rebound. The first peer-field
collision and missing-probe fixture attempts are retained separately.

`PathControlTests.Mutate` joins the existing core packet, recovery and lifecycle
entry points, so managed mutation, FsCheck, trace and instrumented fuzz campaigns
exercise exact/malformed path-control data. The independent script joins the
Schannel, development and package-consumer drivers. These checks validate current
path-control responses; endpoint migration, server challenge initiation and NAT
rebinding still require their own state, routing and wire acceptance.


Linux repeated the twenty model cases and complete dispatcher suite, all three
path-control cipher profiles, three local-ID profiles, 15 received-reset cases
and ten peer-ID cases. Windows repeated its model/dispatcher and both path/CID
profiles. Both systems passed seed 1729 campaigns with 100,000 managed mutations,
10,000 FsCheck cases per property and 1,000 recovery and lifecycle traces each.
The new two-minute instrumented runs completed 71,157 libFuzzer executions and
49,619 AFL++ executions without saved crashes or hangs. AFL++ reported 2.42%
coverage stability; reliable feedback stability remains acceptance work, and
these counts do not establish exhaustive coverage.

An initial Windows instrumented run stopped before fuzzing because a concurrent
compiler rebuild removed the ordinary DLL while its hash was being checked.
The compiler/guided build phases were then sequenced and both campaigns passed.
Separately, hosted static analysis caught two null-forgiving operators in the
CID fixture; explicit missing-response failures replace them without a new
suppression. The final local validation includes both suppression guards.
The same hosted run's ARM64 builder stopped at a CentOS repository HTTP 403
before TLS compilation. Its log is retained; no package pin or verification
check was relaxed. A local execution probe of that exact ARM64 base image could
not run because this Docker host lacks ARM64 emulation. Hosted ARM64 acceptance
remains necessary.


## Address-aware path validation and NAT rebinding

The next unmerged increment carries owned peer/local endpoint metadata through
the serialized connection mailbox. An authenticated fresh packet can introduce a
new peer path only after handshake confirmation; a packet from a different local
endpoint is ignored. The highest received packet number governs whether a
non-probing packet selects the active route. Each queued datagram retains its
route, and output for a subsequently retired route is discarded.

At most three active paths and three failed-endpoint cooldown entries are retained.
Each path has its own received/sent byte accounting and three-times amplification
budget until address validation. A source change probes both the new and previous
active paths. Challenges use fresh eight-byte random values, at most three attempts
per phase, no faster than a PTO with the initial-path floor, and a bounded lifetime
even when transmission is blocked. Only actually transmitted challenges can be
answered. A short probe proves the address but starts a new full-size proof phase;
it does not establish a 1200-byte path. A matching response can arrive on another
path. Failed candidates return to the last validated route, with a bounded
cooldown that prevents immediate oscillation. Known late responses cannot revive
retired routes. The completed/abandoned challenge history is bounded at 24 values.

A path retains its selected peer CID. A new source using the same incoming CID
can continue the associated outbound CID for NAT rebinding; an unrelated path
requires an unused peer CID. Every used, non-retired peer reset token is retained,
with bounded immutable token/path publication and endpoint-index cleanup. IP
changes reset congestion and RTT training by recovery epoch: old-path ACK/loss
still completes or retries reliable deliveries without modifying new-path flight,
RTT, congestion growth or loss history. Port-only rebinding retains the existing
congestion controller. The HTTP request/controller endpoint snapshot remains the
original connection identity, as in the existing binding.

`PathValidationTests` covers admission, ordering, per-path budget, unsent/nonmatching
proofs, small/full proof phases, retry/fallback, stale responses, 500 candidate
changes, peer CID/token selection and recovery epochs. Its engine case sends a
new-path proof back through the original path. Its bounded mutation helper joins
the core, recovery and lifecycle fuzz entry points.

The independent `verify_path_validation.py` relay changes only the client's UDP
source port. The preserved previous host completed two initial requests, then
received no response on its rebound socket and timed out waiting for the same
connection's ping. Both local Windows AES profiles now complete three successive
source changes, full-size probes, a blackholed candidate within its amplification
budget, fallback, ten controller requests across two connections, drain and socket
rebinding. This is controlled same-IP NAT evidence; broader IP/CID combinations,
browser/WebTransport and platform/package acceptance remain separate work. The
script joins the Schannel, development and six package-consumer drivers.

The received-reset test driver also retains already captured stdout/stderr when
its final host assertion fails. A controlled failing exit after the real reset and
drain sequence reproduced the old double-read losing both streams and verified
that the corrected single-read retains them. This diagnostic correction does not
establish the cause of the separately retained hosted Windows cleanup failure.


Linux also passed the complete model/dispatcher suite, all three same-IP
rebinding/fallback cipher profiles, three local-CID profiles and fifteen received
reset profiles using the verified unchanged native SDK and same-version peer
wheel. These local SDK checks are separate from exact-source package-consumer CI.
The Windows repeat additionally verified unchanged request endpoint snapshots,
401/403/200 authorization and cancellation of a blocked controller request opened
before rebinding. Final source diagnostics, pinned scan, broader fuzzing and
cross-platform package acceptance still apply to the completed increment.

The subsequent CID increment retires obsolete routes after full proof and sends
reliable peer-ID retirements through the validated route. Destination reservations
are separate from reset-token eligibility, preventing two unsent unrelated paths
from selecting one spare ID. Local retirement keeps a compact watermark with
bounded still-active lower IDs; delayed lower frames cannot revive removed IDs.
The model includes 1,000 higher-ID retirements while one lower path stays active.
These transitions also join the existing peer-ID mutation entry point.

A controlled simultaneous source-port/CID test stalled on its second migration
before this increment: the new socket received no server bytes. Both Windows AES
profiles now pass three such changes, full-size probes, the controller/auth/abort
checks, bounded blackhole/fallback and cleanup. The maintained relay has separate
same-IP NAT and simultaneous source-port/IP/CID modes. The IP change uses
127.0.0.2 on its third move. Changing the relay socket before requesting the peer
CID change is essential because the peer transmits immediately on that call.

The prior source snapshot also completed 100,000 managed mutations, 10,000 cases
per FsCheck property and 1,000 recovery and lifecycle traces on Windows and Linux.
Its two-minute guided runs completed 91,946 libFuzzer and 51,668 AFL++ executions
without saved crashes or hangs. AFL++ reported only 1.41% feedback stability;
coverage growth is not reliable acceptance evidence until that is corrected.
These earlier campaign counts do not validate the subsequent CID edits.

Reordered NEW_CONNECTION_ID frames can retire the last still-active lower ID while
the supplied replacement was already retired locally. Empty pools publish no reset
token, block output using retired IDs and resume when a fresh replacement arrives.
Regression cases also cover the local retirement budget and preservation of a
usable close destination. Stateful CID mutation traces include
reservation, transmission, local retirement, acknowledgment and replenishment.

The combined wire snapshot passed four Windows and six Linux NAT/CID-IP cipher
profiles. Each verifies ten controller requests across two independent connections,
original endpoint identity, 401/403/200 authorization, cancellation of a request
opened before migration, full-size probes, bounded blackhole/fallback and complete
drain/rebind cleanup. Linux reused a verified unchanged native SDK and same-version
peer wheel; this does not substitute for exact-source package-consumer CI.
The model suite now has sixteen path-validation groups, including packet-level
empty-ID-pool recovery and invalidation of queued probes for retired routes.
Connection-close cases also verify the response route and destination ID when
an unvalidated current path lacks send allowance or a usable peer CID.

### Connection-close fallback validation

A packet-level close case failed before the fallback correction when the new
path could not send 1,200 bytes, although the prior validated path could.
The corrected path selection also covers a new path with enough allowance but
no usable peer ID. Both cases assert one encrypted close response on the prior
validated route with its original destination ID; bounded mutation inputs vary
the new-path packet size. The path-validation model now passes all 16 groups.

On that final local source, Windows passed four independent NAT and simultaneous
IP/CID-change profiles using its two AES cipher suites; Linux passed six profiles
including ChaCha20-Poly1305. Both passed the local-ID and reset profiles. Each platform passed 100,000
managed mutations, 10,000 cases per FsCheck property, and 1,000 recovery and
lifecycle traces. Windows completed 85,728 libFuzzer and 51,460 AFL++
executions in separate two-minute campaigns with no saved crash or hang.
AFL++ feedback stability was 1.51%, so that campaign is crash evidence rather
than reliable coverage-growth evidence. The final source-quality, compiler,
format and pinned scan gates passed. Browser, package and production-provider
acceptance remain separate work.

The macOS package-consumer source-IP/CID case binds 127.0.0.2. The hosted
runner configures this as a temporary loopback /32 alias, verifies that a
socket can bind it, and removes the alias when the validation step ends. This
keeps the actual IP-change case enabled without changing production networking.

## Ordinary HTTP/3 binding and paced scheduling

An isolated local selection probe makes the ordinary HTTP/3 binding create
`EmbedIOQuicListenerBinding` through the existing WebServer modes. Its selection
patch is retained separately until automatic native TLS runtime delivery and
supported-platform capability detection are wired through the ordinary build
and CI paths. The committed default and internal native comparison path still
use the old providers. Selecting the new binding alone would not complete the
replacement or preserve Unix package consumers.

A local selection probe reported 574 passes and two failures among 576 HTTP/3
cases. One failure came from best-effort request cleanup aborting a stream after
its connection had already closed. A direct model regression reproduced this for
local and remote closure with pending reads and writes. The adapter now treats
that terminal abort as complete while preserving the original pending-operation
failure, checking arguments and queuing no more frames. Both variants join the
existing production-transport mutation target.

The other probe failure was the three-concurrent-1-MiB HTTP echo case reaching
its client deadline. With the cleanup correction it passed in 12.63 seconds in
one local run, still close to the 15-second client limit. The inter-packet pacer
discarded elapsed scheduler credit after every packet, allowing only one data
packet at each wake. Recovery now retains a leaky pacing budget at the existing
1.25-times-window/RTT rate, capped at ten maximum-size packets and independently
limited by the congestion window. ACK-only traffic retains its existing unpaced
behavior. Congestion/RTT transitions refill using the previous rate; idle time
cannot grow the burst beyond the initial window and an address change starts
with a fresh path budget.

Deterministic checks cover the initial window, a pacing-limited burst below the
congestion limit, partial/full rate refill, credit consumption, PTO exemption,
idle-burst bounds and a fresh path. Bounded mutation inputs vary packet size and
RTT across idle and path transitions in the recovery, lifecycle and property
targets; trace coverage records the pacing case only when it actually runs.
The larger allowed batch also exposed a fixture that had skipped the model
Handshake packet. That fixture now authenticates a Handshake packet before
claiming a validated fallback, without changing the production validation rules.

All six focused combined-stop/upload cases passed with the bounded pacer. The
same three-concurrent-1-MiB case took 0.79 seconds in that run; these are controlled
test elapsed times, not a latency benchmark or a cross-platform speedup claim.
An initial full Windows run reported 7,600 cases with seven failures at explicit
old-provider identity assertions, 7,572 passes and 21 existing opt-in/platform
skips. The updated assertion requires the replacement provider before checking
HTTP behavior; all 21 focused body-limit cases then passed. The final local
selection probe subsequently passed all 7,600 Windows cases: 7,579 passed, 21
existing opt-in/platform skips and zero failures in 7m 11s. This is Windows
WebServer regression evidence, not automatic Unix runtime delivery or a claim
that the remaining MsQuic startup dependency has been removed.

The Linux snapshot passed the model/dispatcher suites, six independent
NAT/IP/CID profiles across the three cipher suites, local-ID and reset cases,
100,000 managed mutations, 10,000 FsCheck cases per property and 1,000 recovery
and lifecycle traces. Each trace campaign actually exercised the bounded pacing
case 335 times. These use the verified unchanged native TLS SDK and pinned peer;
ordinary Unix package delivery and final replacement acceptance remain separate.

On the finalized implementation increment, Windows also passed its four
independent NAT/IP/CID cases, local-ID, path-control and reset suites. Managed
mutations, FsCheck properties and recovery/lifecycle traces passed with the same
100,000/10,000-per-property/1,000-per-scenario counts as Linux. Two-minute guided
campaigns completed 81,115 libFuzzer and 51,231 AFL++ executions without a saved
crash or hang. AFL++ stability was 1.18%; dependable coverage feedback remains
outstanding. Both core targets and the development host had zero unsuppressed
nonhidden compiler diagnostics, changed-source formatting/analyzers passed,
model/dispatcher suites passed and the pinned full source scan passed.

Formatting the local probe's body-limit test with pinned PrettyCSharp 1.1.10 required
repairing its angle-bracket layout at three named-tuple generic constructions to
the Roslyn whitespace form. That fixture edit is retained with the isolated
selection patch, rather than the current implementation increment. No
compiler/analyzer rule or file was excluded.


## Transitive native runtime delivery

The unmerged .NET 10 core references the owned, asset-only
`EmbedIO-Neo.Quic.Native` project/package. A normal project reference emits its
NuGet dependency without using a managed wrapper assembly. The .NET Standard
asset and its dependency group remain unchanged. Explicit native package
references were removed from both package-only consumers; their validation now
requires generated locks to show the exact-version native dependency as
transitive from the core, before checking the selected native bytes.

`quic-native.yml` owns the existing six-RID build matrix and its two-job parallel
limit. Ordinary CI shares its same-run artifacts with Unix regression tests,
package assembly and the portable consumer workflow. Standalone portable runs
still build their inputs. The shared `quic-runtime` action stages and verifies all
six artifacts before packing; CI code builds and Publish input assembly now verify
seven packages. Documentation-only CI retains its existing six managed-package
content check. No release or dry-run dispatch was performed for this increment.

The source-build test hook is shared by the ordinary NUnit project and the QUIC
development host. CI first checks the artifact source/hashes and executing RID,
then enables that explicit copy hook. Package-mode builds continue to reject
manual native copies. Runtime delivery does not itself switch the public listener
or remove the previous provider.

Local Windows assembly of all seven packages passed exact native payload,
inventory and notice checks using unchanged, source-matched six-RID build inputs
from CI 39692993875. The native tooling suite passed 27 cases with one Windows
symlink-privilege skip (28 total). Solution restore and 21 available affected
project restores passed in locked mode; the only lock changes add the owned
runtime project to modern source-reference graphs. The six mobile/platform lock
graphs await their matching CI workloads. Offline pinned workflow analysis passed.
A clean Linux x64 snapshot then passed the complete package-consumer driver:
all nine portable/RID/AOT cipher/application profiles and the package-backed
host's wire, cipher-vector, connection-ID, reset, path, SNI/publication,
certificate-policy and issuer-chain suites (31 steps). All 256 deterministic
certificate-mutation handshakes completed with 37 pinned-identity acceptances,
219 expected rejections and 61 healthy sibling requests; cleanup/drain/rebind
passed. The first local attempt stopped at an overly strict RID-lock assertion:
RID groups contain differences from the base graph. The verifier now validates
the combined graph, with negative coverage for direct and wrong-version RID
entries; assertions on the transitive package and selected bytes remain enforced.

Three local negative gates confirmed missing native packages, missing source-build
libraries and package-mode manual copying are rejected. Final source acceptance
uses the prescribed source-quality and guard scripts. These local checks do not
establish hosted workflow or supported-platform acceptance; final-head platform
runs remain required.


## Public HTTP/3 construction and TLS capability

The unmerged ordinary HTTP/3 and combined listener now select EmbedIO.Quic.
`WebServer` checks the actual 64-bit desktop TLS path before listening: Windows
Schannel TLS 1.3 requires Windows 11 / Server 2022 or later, and Unix checks the
private ABI/version. Missing libraries receive a platform-support exception with
the loading error retained; an incompatible ABI/version remains fatal. No fallback
to another provider is attempted on this ordinary path. Internal comparison paths
remain until replacement acceptance is complete.

The package-only application now uses the public WebServer constructor. All three
publish modes also run in fresh processes with their private TLS library removed;
public construction must reject that state before announcing readiness. The old
binding accepted that missing-runtime control, reproduced in a retained local
baseline. The corrected source passed all 34 Linux x64 package-driver steps using
a fresh source-matched, explicitly incomplete one-RID preview. Before that run,
MsQuic libraries were removed only inside the disposable container and an independent
BCL capability probe confirmed QUIC support was false. All nine ordinary
portable/RID/AOT cipher/application profiles, three missing-runtime controls and
the full package-backed host suite passed. This is not six-RID acceptance.

Windows ordinary construction passed the complete suite with 7,579 successes and
21 existing opt-in/platform skips (7,600 total, zero failures). The private native
build's macOS deployment target was reduced from 15.0 to 14.0 to avoid adding a
newer loading requirement; artifact audits now reject a newer minimum. A fresh
Linux x64 build passed the unchanged ABI/export/dependency and glibc-2.17 checks,
and native tooling passed 27 cases with one Windows symlink-privilege skip. Real
oldest-OS execution, particularly downlevel macOS, remains separate validation.

AFL++ CI 39699515019 failed before fuzzing because its isolated workspace omitted
the new shared test target. The workspace now includes that target and the owned
native project's build/lock metadata, and workflow path filters include those
inputs. A local one-minute campaign passed build, instrumentation/map checks and
25,122 executions with no saved crash or hang. Feedback stability was 3.32%; the
existing coverage-reliability limitation remains unresolved. No coverage or failure
gate was relaxed.

The same source checkpoint's Android HTTPS smoke exceeded its ten-second in-app
HTTP client deadline after listener startup. Its failure/logcat evidence is
retained; no cause correction or successful replacement run is claimed.
Current-head platform and final source gates remain required before acceptance.


The retained Windows native certificate campaign also exposed a client-fixture
lifetime issue: a successful TLS/identity operation was charged for the client's
subsequent protocol-close waiting period. Certificate exchanges now create their
UDP context, wait for TLS and check identity inside the unchanged eight-second
operation budget; context teardown runs outside that budget but remains within
the unchanged overall campaign deadline. TLS acceptance/rejection codes,
concurrency and identity assertions remain unchanged. Five isolated async cases
cover slow close, stalled handshake/request, rejection and bounded cleanup. All
13 peer-helper cases passed locally. This change affects test orchestration.

With that helper, the unchanged 256-case campaign passed on Windows Schannel:
73 pinned-identity acceptances, 183 expected rejections and 50 healthy sibling
requests in 3.697 seconds. Linux's package-backed public host passed 37 pinned
acceptances, 219 expected rejections and 63 healthy siblings in 13.013 seconds.
Both completed cleanup/drain/rebind. These are local campaign durations, not
latency benchmarks or proof of the hosted Windows 2025 outcome. The original
failure and first helper attempt that omitted aioquic's explicit Initial
transmission remain retained; the helper now starts that transmission before
waiting for handshake completion.

CI's existing package-build artifact now feeds a supplemental reusable malware
scan on code changes. It must contain exactly seven packages, including the owned
native runtime, and both scanners inspect their packed and unpacked contents.
Publish sets the same seven-package requirement. The standalone source/managed
scan retains its six-package input and required check names. The native binaries
are reused from the one same-run build; no second native build is introduced.
This workflow change still requires actual final-head execution.

The final affected Windows body-limit selection passed all 45 cases after pinned
formatting and diagnostic cleanup, with no skipped cases. The test file retains
its assertions while grouping independent checks, using collection constraints
and modern declaration/collection syntax. Both core targets, the development
host and changed regression source had zero unsuppressed nonhidden compiler
diagnostics; changed-file formatter/style/analyzer checks and model/dispatcher
suites passed. Six local package-count controls checked ordinary six-package and
native seven-package acceptance plus missing, wrong and extra package rejection,
including rejection of the seven-package input by the old guard. Those synthetic
inputs validate the guard only; they are not binary malware-scan evidence.


## Source-built Native AOT runtime delivery

At source checkpoint `8400dbc8`, CI 39704632250's Linux Native AOT job published
successfully but failed when public HTTP/3 construction could not load
`libembedio_quic_tls.so`. Its source-reference fixture had not imported the shared
native TLS copy target. This is distinct from package-only AOT runtime delivery,
which already passed through NuGet selection.

The fixture now imports that explicit test-only target. CI orders the AOT job
after its native build and verifies/reuses the same Linux artifact. A standalone
AOT invocation instead builds and audits the pinned TLS source for its executing
host. Neither path changes a production package or allows a package-mode manual
copy. The fixture's independent BCL HTTP/3 client retains its own pinned MsQuic
prerequisite, including the Windows AOT client DLL copy.

A clean Linux source snapshot restored in locked mode, published with Native AOT
and warnings treated as errors, verified the copied library byte for byte, then
passed all 40 existing fixture checks under runtime 10.0.13 with dynamic code
disabled. These include exact HTTP/1.1, HTTP/2 and HTTP/3, generated controllers,
authorization/JWT, per-request DI, streaming, cancellation, sessions, WebSockets,
shutdown, caching, idempotency and resumable uploads. This local source fixture
used the unchanged source-matched Linux asset; it is not new platform-matrix or
standalone workflow execution evidence. Two local negative targets also rejected
a missing library and manual copying in package mode. The standalone branch's
exact native build command also completed on a local Linux host, including its
pinned source/archive verification and binary ABI/export/dependency audit. That
locally compiled binary is a host build, not a new distribution asset. The
original hosted failure is retained; actual final-head CI and complete standalone
workflow execution remain required.


## Client WebTransport SETTINGS requirements

Server-side admission follows the role-specific requirements in
[draft 16 section 3.1](https://datatracker.ietf.org/doc/draft-ietf-webtrans-http3/#section-3.1).
A client requires its WebTransport and HTTP datagram SETTINGS plus the required
QUIC transport parameters. It does not need to advertise extended CONNECT;
the server continues to advertise that capability. Boolean-value parsing,
datagram requirements and reliable-reset negotiation remain enforced.

A new model group covers eight SETTINGS vectors, three malformed boolean values
and 32 combinations of peer
settings, local enablement and transport prerequisites. It failed on the earlier
core's extra client requirement, then passed after the role distinction. A
separate direct-admission probe likewise changed two legal client cases from
NotNegotiated to Accepted while retaining rejection of missing required settings.
All 91 existing NUnit WebTransport core cases passed without skips.

The pinned independent quic-go peer now omits the client extended-CONNECT
advertisement while still requiring the server's advertisement. All 23 existing
Windows Schannel bridge profiles passed, including bidirectional/unidirectional
streams, datagrams, reliable reset, authorization/Origin rejection, separate
sessions, loss, credit, key updates and repeated stream retirement. These use the
development bridge; they do not establish a public application API or browser
acceptance. Those remain required production integration work.


## Source-reference validation host TLS

The Unix real-HTTP/3 state-machine job at source checkpoints `8400dbc8` and
`01db12dd` failed before replay when its source-reference host could not load
`libembedio_quic_tls.so`. The fuzz, conformance and load-benchmark projects now
import the same explicit test-only native-copy target as the development and
NUnit hosts. Their Unix fuzz/manual latency workflows prepare the pinned host
TLS source with a shared build/audit action before building the managed host.
Standalone AOT uses that same action; ordinary CI AOT continues to reuse its
verified same-run native artifact. The action creates one local host build in
an existing job, with no additional native matrix or relaxed campaign deadlines.
The independent BCL client retains its separate pinned MsQuic prerequisite.

A clean local Linux snapshot executed the action's exact Bash body, verified
its pinned archive/source and ABI/export/dependency audit, and compared the
copied fuzz-host library byte for byte. All four saved cached/streamed HTTP/3
response traces replayed successfully. Seed 1729 then passed 32 real HTTP/3
connection-state cases and 32 response-state cases with the existing oracles.
The conformance and load-benchmark projects also restored in locked mode,
built without warnings/errors and received the exact verified library. Those
builds are not latency campaign or complete standalone-workflow acceptance.

The original hosted failures remain retained. At `01db12dd`, Windows and Linux
Native AOT jobs passed, together with all six native builds, package assembly
and the supplemental seven-package malware scan. Desktop suite and final-head
workflow acceptance remain separate requirements. The prior Linux concurrent
three-by-1-MiB upload transport failure and macOS route/socket failures remain
under investigation. A local Linux probe completed 100 fresh public servers
and 300 exact 1-MiB echo uploads without reproducing that transport error;
that result does not establish a cause or correction for the hosted failure.


## Shared UDP destination errors

A controlled local Linux diagnostic enabled the kernel's UDP error queue on an
actual public listener socket. A send to a disposed loopback UDP endpoint then
reported asynchronous ConnectionRefused on receive, ended the listener and caused
a subsequent healthy HTTP/3 request to fail. Linux documents that an unconnected
UDP socket can report an asynchronous error from a previous datagram. The
application's shared socket must keep that path error separate from listener
lifetime.

Receive, send and shutdown observation now recognize ConnectionReset,
ConnectionRefused, HostUnreachable, HostDown and NetworkUnreachable as datagram
path failures. Recovery/idle handling remains per connection; these errors do
not prove a peer's QUIC state. An internal counter is included in the existing
closed-listener diagnostics. Other socket errors and incomplete sends retain
their previous failure handling; production does not enable the Linux error queue.

The new ordinary-listener Linux regression waits for three real ICMP errors to
be observed and verifies both existing-client and fresh-client requests after
each one. A separate regression disposes the actual UDP socket and requires the
listener to stop, retaining the fatal resource-failure boundary. Both passed in
three local Linux runs (six cases, no skips). Windows passed the disposal case;
the Linux-only ICMP fixture was the one expected platform skip.

These local checks do not establish the cause of every hosted macOS failure.
At source `01db12dd`, CI 39706770816 again reported ten macOS suite failures,
including unreachable-route cases, and the same concurrent 1-MiB upload transport
error on both Windows and Linux. All suites discovered 7,600 cases. The original
logs remain retained; final-source desktop CI must establish each correction.


## Retaining the upload failure's underlying exception

The repeated hosted upload protocol error has not yet reproduced locally. A
complete disposable Linux diagnostic run reported 7,602 cases: 7,576 successes,
25 skips and one failure. All three upload cases passed. The single failure was
the existing metadata test correctly rejecting temporary console tracing in the
instrumented core. That temporary build was isolated from tracked source and
is not a successful production acceptance run; CI's independent Python peer
environment was also not provisioned there.

A second disposable Linux build used the ordinary `dotnet test --project`
command, four logical processors and 80 repetitions of each existing upload
case. All 240 iterations across the three reported cases passed in 80.835
seconds, preserving the original sizes, concurrency and assertions. These
results do not establish a cause, a fix or latency benchmark acceptance.

The ordinary test fixture now observes first-chance InvalidDataException and
the engine's transport/key-update exceptions whose stacks identify EmbedIO.Quic.
Collection is selected only for the three concurrent-upload cases. It retains at
most 16 records, each limited to 4,096 characters, and prints them only when that
case fails. Rethrows can produce multiple notifications for one exception. No
production source, wire error, payload/key logging, dependency or test deadline
changes. Fixture completion detaches the process-level observer; per-case
selection clears the prior records. Zero records are reported when no matching
exception was observed rather than inventing a server-side cause. Recoverable
observation failures are reported explicitly and cannot replace the original
test exception.

Two regression cases exercise the actual packet-number parser's exception,
record limits, unrelated-exception filtering, per-case reset and disposal.
The initial seven-case affected selection passed on Linux without skips and
on Windows with six successes and the one existing Linux-only fixture skip.
A disposable controlled-failure run also verified the full NUnit reporting path:
one intentional upload case remained failed with its original assertion and
actual parser stack retained, while the two ordinary upload cases passed.
An initial control assumed one notification per exception; fresh reflection
invocation produced two, so the control verifies bounded retained observations
without depending on CLR rethrow notification counts. The original records remain
retained. Hosted exact-source validation and the actual upload correction remain
outstanding.

## Incoming ACK range interoperability

RFC 9000's ACK Range Count is a variable-length field. The local packet-history
sender limits its own generated ACKs to 128 ranges, but that limit does not govern
what another endpoint may send. A controlled 265-byte wire frame with 129
additional, descending ACK ranges failed at the former incoming count limit;
the same probe's 127 and 128 additional ranges passed. Larger valid examples
with 255 and 511 additional ranges failed for the same reason.

Incoming ACK parsing now bounds the count by bytes available for at least two
varints per additional range, and by the datagram size. It rejects impossible
counts relative to the largest acknowledged number before building the range
list. The parser maps malformed ranges to QUIC FRAME_ENCODING_ERROR and an ACK
for an unsent packet to PROTOCOL_VIOLATION. Recovery searches descending ranges
by index after one ordering check; direct internal callers with unordered lists
retain the previous membership behavior. The one-range path remains direct.

The new deterministic cases cover counts from zero through 32,700, exact
descending packet numbers, truncation, underflow, error codes and cursor
consumption. A 4,096-sent-packet model uses an instrumented 32,701-range list
to assert a bound on range reads and the resulting delivery ownership, without
a wall-clock threshold. The existing packet/header fuzz target now also builds
structured valid ACKs, including counts above 128, from each selected input
and checks their independently calculated ranges. The initial model self-test
passed on Windows; final source, platform and performance acceptance remain
outstanding.

At source `40152437`, before this ACK change, hosted CI passed the Windows and
Linux regression suites, static analysis, all six native builds, seven-package
assembly and malware checks, and the real HTTP/3 state-machine fuzz job. Fuzz,
Security, Malware, AFL and libFuzzer workflows all completed successfully. The
macOS suite reported 7,604 cases with 7,570 successes, 24 skips and ten
unreachable-route failures, so the CI aggregate remained red. This is still a
draft PR; the hosted outcome does not establish the cause of those macOS errors
or resolve the earlier upload error.

## Public WebTransport transport prerequisites

The ordinary HTTP/3 listener's connection factory previously received QUIC's
negotiated maximum DATAGRAM frame size and reliable-reset capability, but passed
only the async stream application to the HTTP/3 adapter. That binding and the
development production-H3 host now retain both values with the application.
The provider-neutral HTTP/3 transport reports them as zero/false for providers
that cannot establish them. Session admission must use the negotiated values,
not infer transport support merely from a DATAGRAM send method.

The async stream owner now accepts a copied reliable prefix of 1–64 bytes
before writes begin. It installs the prefix on the serialized connection-owner
turn before any queued write abort. A duplicate reservation, a late reservation
after a queued or committed write, or an oversized prefix is rejected. Closing
the connection releases an uninstalled prefix. The owned HTTP/3 stream adapter
exposes this operation; providers without the capability reject it.

The model regression opens real owned streams, aborts before the first write and
checks that the exact copied two-byte session header is transmitted before
`RESET_STREAM_AT` with matching reliable/final sizes. A separate case grants
one byte of connection credit at a time and verifies that reset waits until the
whole header is sent. The QUIC self-test passed locally; five ordinary Windows
HTTP/3 listener cases passed without skips with the feature handoff. This is a
transport prerequisite only. Public WebTransport sessions, exact Origin/auth
admission, incoming and outgoing data-stream routing, capsules, drain semantics
and browser validation remain required.

The HTTP/3 control-stream parser now has an explicit WebTransport SETTINGS mode.
In that mode it validates the draft's enable flag, datagram flag and stream-count
limits from the same bounded payload as ordinary HTTP/3 SETTINGS. When the mode is
off, unknown extension values keep their existing HTTP/3 treatment; a malformed
WebTransport boolean is not rejected by an ordinary HTTP/3 listener. A fragmented
control-stream regression verifies both modes and the negotiated flags. Public
WebTransport configuration and session use remain pending.

The HTTP/3 connection's new internal opt-in mode checks the negotiated QUIC
datagram and reliable-reset features before startup, appends the draft's enable
and initial-flow SETTINGS pairs to its normal datagram-capable server SETTINGS,
and retains the client's validated WebTransport settings from the control stream.
The ordinary non-opt-in wire bytes are verified unchanged for both datagram and
non-datagram connections. A fragmented control-stream regression parses the
new server bytes and checks the advertised limits; a missing datagram profile is
rejected. Both focused cases passed locally. No production listener enables this
mode yet, so this is not public WebTransport availability or browser acceptance.

The QUIC bridge fuzz target now also constructs opt-in server SETTINGS from
input-derived flow and stream limits, checks the encoded frame length, parses it
through the ordinary HTTP/3 and WebTransport readers, and verifies the round-trip
limits and required flags. Selected inputs are additionally fed directly to the
WebTransport SETTINGS reader to exercise malformed and duplicate pairs. The local
QUIC self-test passed with 10,076 assertions, and a 10,000-case seeded core
campaign passed across eight targets. These local results do not establish public
WebTransport interoperability or replace final hosted fuzz acceptance.

## Concurrent-upload loss-burst queue pressure

On source `9f226d48`, CI `39715967428` failed the three-concurrent-1-MiB
HTTP/3 upload case on Windows and Ubuntu. Both TRX artifacts include the engine's
first-chance `InvalidDataException: Outbound queue budget exceeded` in
`QuicConnection.QueueRepeat`; the clients then report QUIC transport error 10.
The exact artifacts are saved under ignored
`TestResults/quic-feasibility/continuation/webtransport-public-01/ci-9f`.
Seven macOS tests failed with route, timeout or rebind errors in the same run;
that evidence does not establish their cause.

The owner now keeps a 512-delivery ready window and a separate bounded backlog
for recovery bursts, capped at the recovery packet bound plus one ready window.
New stream frames wait while that backlog exists. ACK-only deliveries and PTO
probes retain an urgent path with its existing 128-entry bound. On each owner
turn, acknowledged or discarded backlog entries are dropped and live entries
are promoted as space becomes available. Connection close and disposal release
both queues. A deterministic model ACKs the highest of 1,024 sent packets to
trigger the recovery callback past the former cap, then checks backlog retirement
and probe progress. The core fuzz target mutates the burst size and late-ACK
boundary. The local QUIC self-test, a 2,000-case seeded core campaign and all
three real Windows concurrent-upload variants passed. Hosted Windows/Ubuntu
rechecks on the corrected head remain required.

An additional exact-head Windows check repeated all three real concurrent-upload
variants eight times on `38507d79` (24 cases total). Every run passed with
`EMBEDIO_REQUIRE_QUIC=1` and no skipped case; its TRX files and per-run output
are under ignored `TestResults/quic-feasibility/continuation/webtransport-public-01/upload-repeat-head385`.
This local repetition does not replace the hosted Windows/Ubuntu recheck.

Hosted CI `39719366609` on `38507d79` passed the Linux regression suite,
including the concurrent uploads. Windows finished 7,590 successful cases,
14 skips and one failure: a stalled HTTP/3 body read received
`H3_REQUEST_CANCELLED` instead of the complete 408 response. The exact H2/H3
cases then passed 40/40 focused local repeats without reproducing that reset.
macOS finished 7,568 successful cases, 24 skips and 13 loopback QUIC connection
failures reporting `HostUnreachable` across unrelated tests. Their cause is not
established. The exact logs and macOS TRX are retained under ignored
`TestResults/quic-feasibility/continuation/webtransport-public-01/ci-head385`.

The HTTP/3 fixture now records at most four accept-loop error messages of
4,096 characters during each test and reports them only when that test fails.
This test-only observer does not capture request payloads or keys and does not
change listener behavior. Its focused observer case passed, as did the eleven
selected WebSocket and concurrent-upload cases. New-head hosted evidence is
still required before concluding either platform failure is resolved.

Hosted CI `39722515237` on `a0eeda69` passed the Windows regression suite but
failed Linux and macOS. Linux completed 7,588 successes, one failure and 17
not-executed/skipped cases out of 7,606. The sole failure was a concurrent
HTTP/3 listener stop: a late STREAM frame reached `AsyncStreamApplication.GetStream`
after connection closure and raised `QuicConnectionFailure` during shutdown.
The local async-stream regression reproduced that exception before the correction;
the owner now discards inbound stream events after closure without creating new
owners, while outbound operations keep reporting the original connection failure.
The QUIC self-test passed 10,076 assertions locally after the correction. This
has not yet passed hosted CI.

macOS completed 7,578 successes, four failures and 24 not-executed/skipped cases
out of 7,606. Two cases reported `HostUnreachable`, one drain case timed out,
and a stalled HTTP/3 body-read case saw `H3_REQUEST_CANCELLED` instead of a
complete 408 response. The bounded accept-loop diagnostic was not present in
that hosted run; it is committed for the next head. The exact TRX files and Linux
job log are retained under ignored
`TestResults/quic-feasibility/continuation/webtransport-public-01/ci-heada0-*`.
The macOS causes and intermittent 408 reset remain unresolved; neither a local
focused pass nor the async-stream correction establishes their resolution.

## Open-issue resolution baseline

The open-issue inventory on 2026-10-21 contains eight issues. This is a
pre-removal baseline, not a resolution claim. Recheck the issue descriptions,
current source and final acceptance evidence after the old QUIC provider is
removed; close only issues whose specific concern is then verified resolved.

| Issue | Current disposition to verify |
| --- | --- |
| [#414](https://github.com/WilliamSmithEdward/embedio-neo/issues/414) | Public WebTransport session API, browser streams/datagrams and drain behavior are still unfinished. |
| [#401](https://github.com/WilliamSmithEdward/embedio-neo/issues/401) | Intermittent macOS package-audit exit 137 is independent of the QUIC provider and needs its own evidence. |
| [#399](https://github.com/WilliamSmithEdward/embedio-neo/issues/399) | Native MsQuic listener acceptance failure: check that the old campaign is retired and its replacement has equivalent coverage. |
| [#398](https://github.com/WilliamSmithEdward/embedio-neo/issues/398) | Windows MsQuic/Schannel credential failure and its exact CI quarantine: remove and verify the quarantine only after replacement coverage. |
| [#303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303) | Current-wire reliable reset: verify independent peer and browser exchange through the owned engine. |
| [#278](https://github.com/WilliamSmithEdward/embedio-neo/issues/278) | Provider memory retention/endurance: compare the replacement under sustained load and preserve any distinct actionable finding. |
| [#202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202) | Prior QUIC lifetime workarounds: remove each obsolete workaround and confirm equivalent resource cleanup. |
| [#181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181) | Broader modern HTTP-engine program; evaluate all remaining acceptance items separately from the provider replacement. |
