# EmbedIO.Quic development and testing record

This record covers the development path, test architecture, reproducible local
campaigns and remaining acceptance work for the original C# QUIC engine. It is
maintained with the implementation, which [PR #506](https://github.com/WilliamSmithEdward/embedio-neo/pull/506) added; 2.17.0
is the first release to contain it. The ordinary HTTP/3 binding selects EmbedIO.Quic with
its own TLS capability check and transitive runtime delivery, the earlier providers are
removed, and servers can opt in to public WebTransport sessions. The sections keep the
implementation sequence and its evidence in order, so an earlier section describes the
source as it was then. [Remaining production acceptance work](#remaining-production-acceptance-work)
and the [open-issue audit](#open-issue-audit) give the current state.

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
readable under Initial protection. The UDP payload-size parameter must be at least 1200,
the only bound RFC 9000 sets (see [Received frame error codes](#received-frame-error-codes)).
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

Validated before the merge, each recorded in its own section:

- On all six runtime identifiers in CI, the package consumers: portable, RID-specific
  and Native AOT publishing, three cipher profiles, missing-runtime controls,
  generated controllers, authorization, limits and abort, and the wire, cipher,
  connection-ID, stateless-reset, path, SNI, publication, client-certificate and
  authentication-fuzz suites. The same set passed once on macOS 14
  ([Oldest macOS](#oldest-macos)).
- The independent quic-go peer's 15 public WebTransport modes, in CI on Linux and
  macOS; draft-02 browser sessions from Chromium on Windows and Linux and from Firefox
  on Linux.
- 30-minute endurance runs on Windows and Linux ([Endurance](#endurance)).

Still open after the merge, each with its tracking issue:

1. Resolved: Retry is an option, checked against the .NET client and the quic-go peer;
   0-RTT and other post-handshake TLS messages stay outside the supported profile, and
   Unix TLS keeps needing an exportable key
   ([Retry and the supported TLS profile](#retry-and-the-supported-tls-profile)).
   [#520](https://github.com/WilliamSmithEdward/embedio-neo/issues/520). Session
   resumption came later ([TLS session resumption](#tls-session-resumption),
   [#550](https://github.com/WilliamSmithEdward/embedio-neo/issues/550)).
2. Resolved: past its bounds an HTTP/3 endpoint refuses new connections before any TLS
   work, and a connection that can serve no more requests frees its listener slot
   without waiting for its closing or draining period
   ([Bounded TLS work under load](#bounded-tls-work-under-load)).
   [#521](https://github.com/WilliamSmithEdward/embedio-neo/issues/521).
3. Resolved: cold, warm and tail latency are measured on Windows and Linux beside
   HTTP/2 over TLS and Kestrel, and ReadyToRun takes most of the JIT time out of the
   first request with no warm cost
   ([Startup, warm and tail latency](#startup-warm-and-tail-latency)).
   [#522](https://github.com/WilliamSmithEdward/embedio-neo/issues/522).
4. Resolved: runs of eight hours on Windows and Linux, three hours before them, and 30
   minutes on macOS ([Endurance](#endurance)). Live native memory on Linux held flat;
   the memory glibc keeps after the server frees it and the handles on Windows grew
   early and slowed to a fraction of that rate
   ([#552](https://github.com/WilliamSmithEdward/embedio-neo/issues/552)). The runs'
   one client error came from a race in QUIC stream retirement, since fixed
   ([Stream retirement race](#stream-retirement-race),
   [#555](https://github.com/WilliamSmithEdward/embedio-neo/issues/555)).
   [#523](https://github.com/WilliamSmithEdward/embedio-neo/issues/523).
5. Resolved: ad-hoc signed hardened apps, AOT and JIT, run in CI
   ([Hardened-runtime macOS apps](#hardened-runtime-macos-apps)), as do the oldest
   glibc and musl that .NET 10 supports
   ([Oldest glibc and musl](#oldest-glibc-and-musl)). Developer ID signing is not
   validated; the project has no Developer ID.
   [#524](https://github.com/WilliamSmithEdward/embedio-neo/issues/524).
6. Resolved: Safari 27, Chromium and Firefox on macOS pass
   ([macOS browsers and draft-14](#macos-browsers-and-draft-14)). Alt-Svc discovery is
   in 2.18.0: `EmbedIOCombined` advertises HTTP/3, and a
   .NET client moves to HTTP/3 after its first response
   ([Combined TCP and QUIC hosting](../guides/http3.md#alt-svc)).
   [#537](https://github.com/WilliamSmithEdward/embedio-neo/issues/537).

The list this replaced, kept for its history:

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
   Run the public WebTransport sessions (see "Public WebTransport sessions" below)
   against the independent peer in hosted CI, and broaden the actual WebServer
   auth/cancellation/disposal checks across those configurations.
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

The providers and their workarounds were removed on 2026-10-22; see
[retiring the earlier HTTP/3 providers](#retiring-the-earlier-http3-providers).

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

## macOS datagram destinations

Hosted CI `39725851625` on `07670812` passed the Windows and Linux regression
suites, so the late-STREAM correction above now has a passing Linux run. macOS
reported 7,607 cases: 7,577 passed, six failed and 24 were not executed. All six
failed about 1.2 seconds into the test with `HostUnreachable`, and every one
carried the new accept-loop record: `ArgumentNullException` for `address` from
the `IPEndPoint` constructor in `EmbedIOQuicListenerBinding.RunAsync`. The
listener built each datagram's local endpoint from
`packet.PacketInformation.Address`, which was null. The receive loop failed, the
accept loop stopped the HTTP/3 listener, `RunAsync` completed normally, and the
client's packets met a closed port. This one mechanism matches the scattered
`HostUnreachable` failures of the earlier macOS runs.

The .NET 10.0.13 source explains both ways the address goes missing.
`Socket.ReceiveMessageFromAsync` enables destination reporting on its first call
(`SetReceivingPacketInformation`), after the socket is bound; macOS attaches the
destination when a datagram arrives, so a client datagram that beats the first
receive has none. `SocketPal.Unix.SetReceivingDualModeIPv4PacketInformation` is
a no-op on macOS because it cannot report IPv4 destinations on a dual-mode
socket, so a wildcard prefix would lose every IPv4 destination.

The binding now enables destination reporting for its address family before
binding, plus IPv4 reporting on a dual-mode socket where the platform accepts it.
A datagram that still has no destination is attributed to the bound address,
which is the wildcard for a dual-mode socket; it no longer stops the listener.
`DatagramsWithoutAReportedDestinationUseTheBoundEndpoint` checks that resolution
directly, and `WildcardHttp3ListenerServesIpv4Clients` serves two fresh IPv4
connections through a wildcard (dual-mode) listener and requires it to stay up.
The wildcard case reaches the macOS dual-mode path on every run, not only when a
datagram races the first receive.

Locally on Windows, the core and test projects built without warnings, the 20
affected HTTP/3 cases passed, and the full suite reported 7,608 cases: 7,586
passed, 22 skipped, none failed. The wildcard case was excluded from that local
run because binding `[::]` on the desktop can raise a firewall prompt; hosted CI
runs it on all three platforms. Evidence is under ignored
`TestResults/quic-feasibility/continuation/macos-destinations-01`.

Hosted CI `39730144714` on `f2b190b0` removed the scattered macOS
`HostUnreachable` failures: macOS reported 7,609 cases with two failures. One was
the new wildcard case. Its accept-loop record showed a second macOS limit:
`Socket.ReceiveMessageFromAsync` throws `PlatformNotSupportedException` for any
dual-mode IPv6 socket there (`SocketPal.Unix.CheckDualModePacketInfoSupport` in
.NET 10.0.13), so an EmbedIO.Quic listener on a wildcard prefix stopped at its
first receive on macOS. The binding now receives on such a socket with
`ReceiveFromAsync`, which reports no destination, and attributes every datagram to
the bound wildcard address. Windows, Linux and specific-address bindings keep
`ReceiveMessageFromAsync`. The other macOS failure was the quarantined raw-runtime
rebind case of the earlier provider, which fails the job only alongside another
failure.

### Request input ended by the server

The same run failed `StalledMultiplexedBodyReadAnswers408(3)` on Linux, as earlier
runs had on Windows and macOS: the client received `RESET_STREAM` with
`H3_REQUEST_CANCELLED` instead of the 408 response. When a handler's read deadline
expires, `ReadTimeoutStream` abandons the pending body read and the handler answers
408. When the request ends, the request worker stops reading with
`STOP_SENDING`/`H3_NO_ERROR`, which fails that abandoned read. `Http3RequestBody`
reported the failure to its owner as a request failure, and the owner reset both
directions with 0x10c, after the 408 and its FIN had been sent. A client that had
not yet read the response saw the reset. A read failure that reports only this
side's own read abort now ends the input without resetting the stream.
`LocalReadAbortEndsAnAbandonedBodyReadWithoutResettingTheResponse` drives the real
request reader and body over an in-memory engine stream: on the unchanged source the
response direction was reset with 268 (0x10c); with the correction it stays open.

### Listener disposal racing the first receive

The same run failed the Windows Schannel stateless-reset check in one of ten
cases, during host cleanup. That host binds a second listener and disposes it at
once to check that the port can be rebound. Disposal cancels the first receive
while .NET may still be starting it; .NET then completes it at once with
`SocketException(OperationAborted)` rather than as canceled (`Socket.Tasks.cs`
reports a synchronous error without translating it), and the listener reported a
shutdown failure. `ListenerBindingDisposedAtOnceStopsCleanly` creates and disposes
bindings concurrently. It failed in 2 of 10 local runs against the `07670812`
binding and 1 of 5 against `f2b190b0`. During a stop, an operation completed with
`OperationAborted` is now treated as canceled; outside a stop it stays fatal, as
`Http3DisposedDatagramSocketRemainsFatal` requires. After the correction the stress
passed 30 of 30 local runs.

Locally on Windows, the full suite with these corrections (wildcard case excluded)
reported 7,610 cases: 7,588 passed, 22 skipped, none failed. Hosted acceptance of
the three corrections is pending.

Hosted CI `39734248524` on `8cffe1d0` passed the Windows and Linux regression
suites. macOS failed two cases: the quarantined raw-runtime rebind case of the
earlier provider, and `CombinedDrainAbortsAFullUndispatchedQueue(False)`, whose
100-millisecond drain did not finish within the test's 5-second bound. Two earlier
macOS runs failed the same way. The case takes about 4.5 seconds on Windows as
well, but a local timing probe put only 0.15 to 0.43 seconds of that in the drain;
the rest is the test waiting for its 288 requests to fail. When the bound is
missed, the test now lists the internal tasks still pending, by field path, and
how long the drain really takes (waiting at most 25 more seconds), then fails as
before. The cause on macOS is not yet known.

CI `39738902765` on `87af9981` failed the same case on macOS with that report. At
5 seconds the QUIC side was still waiting for one connection task, the listener
loops and its own shutdown; the drain finished after 5.13 seconds. Every HTTP/3
connection closes with its own 5-second deadline, which also covers the QUIC
closing period of three probe timeouts, so a closing period of about 5 seconds on
that runner would explain the timing; this is not yet confirmed. The report now
also lists each QUIC connection's phase, closing deadline, RTT estimate and closing
period. A local run that forced the report showed one active connection with a
12 ms smoothed RTT and a 282 ms closing period, and a drain of 0.41 seconds.

### HTTP/3 fixtures on the EmbedIO.Quic binding

Eight test files ran the HTTP/3 connection layer over a server-side
System.Net.Quic listener. They now accept connections through the production
EmbedIO.Quic listener binding, through the shared `EngineQuicHost` test helper, so
their protocol assertions exercise the provider that ships; the BCL `QuicConnection`
and `HttpClient` remain the independent clients. The backpressure reset cases
accepted only the BCL's `QuicException` for a writer ended by `STOP_SENDING`; they
now accept any `IOException`, which the stream failures of both providers are. The
227 cases of the affected classes pass locally.

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

## Retiring the earlier HTTP/3 providers

On 2026-10-22 the source dropped both earlier HTTP/3 server providers: the
`System.Net.Quic` binding and the internal native MsQuic binding with its API,
listener, connection, stream, datagram and transport classes. The internal
`WebServer(WebServerOptions, bool nativeHttp3)` comparison constructor and the
`native` switches of `Http3Listener`, `CombinedHttpListener` and
`ServerTlsPolicy.EnsureSupported` went with them, as did the refusals that existed
only for the native binding (client certificates and SNI selection). Two internal
enums replace `QuicStreamType` and `QuicAbortDirection` in the HTTP/3 transport
boundary, so the server no longer references `System.Net.Quic` at all.

The workarounds for the earlier providers are gone too. `QuicEndpointReleases`
retried a bind for up to a second because MsQuic could keep a disposed listener's
port bound; the EmbedIO.Quic binding releases its socket before stopping returns.
The accept loop's catch for handshake failures that the BCL reported through
`AcceptConnectionAsync` is unreachable with a binding that hands over connections
before their handshake, and was removed. `Http3TransportFailure` recognizes only
the engine's stream and connection failures. The pooled-write comment in
`Http3QuicExchange` now cites the engine's own copy (`ConnectionStreams.TryWrite`
copies each chunk before a write completes) instead of MsQuic's buffer handling.

Tests:

- The MsQuic-only suites were deleted: the native API lifetime, explicit abort,
  standalone module, listener callback fault, native datagram, standalone peer,
  lifetime campaign and ownership audit fixtures, the native web server test, the
  raw-runtime rebind test, the endpoint-release unit tests and the dependency
  evidence helper.
- Eight fixtures that drove the HTTP/3 layer over a server-side `QuicListener` now
  accept through `EmbedIOQuicListenerBinding` (see "HTTP/3 fixtures on the
  EmbedIO.Quic binding" above). The direction-watcher cases use the engine's
  failure types.
- Cases parameterized by the native flag keep their engine variant; the native
  binding fixture became `Http3ListenerBindingTest`, now covering rejected
  handshakes, drain, multi-endpoint rollback and stop-during-accept on the engine.
- The datagram router cases run over the engine's datagram transport, and the
  application datagram interop case runs against the independent aioquic peer
  through the ordinary HTTP/3 listener.
- The retirement gate's drained and contended restart scenarios became ordinary
  `Http3ListenerRestartTest` cases, with no retry left to bridge a failure; its
  idle and served scenarios duplicated existing rebind tests. The multi-connection
  fuzz campaign checks post-drain rebinding for HTTP/3 as well.
- Gates check `QuicConnection.IsSupported` only: the BCL `HttpClient` and
  `QuicConnection` remain the independent clients and still need MsQuic.

CI drops the native module lifetime step, the quarantine classifier for the
raw-runtime rebind case and the Windows credential failure of
[#398](https://github.com/WilliamSmithEdward/embedio-neo/issues/398), the MsQuic
jobs of `diagnostics.yml` (only the runtime close probe remains) and the SDK
canary's rebind exclusion. The pinned MsQuic installs stay, for the test client.
The regression floor is the new discovery count, 7,428. The native module
lifetime and native dependency probe projects, the MsQuic parts of the runtime
close probe, the benchmark's send-buffer and retention probes, the HTTP/3 write
benchmark and its driver, the native lifetime campaign script and the stream
lifetime peer were removed, and the conformance image no longer installs MsQuic.
The benchmark harness still records `System.Net.Quic` provenance, and its Kestrel
baseline still runs an MsQuic server; both concern the comparison, not EmbedIO.

Locally on Windows, the solution and the six affected projects outside it built
without warnings; the 446 affected cases passed; the datagram interop case passed
against aioquic 1.6.1; the full suite reported 7,427 cases (the wildcard-bind case
excluded, as before): 7,424 passed, 3 skipped, none failed. The Windows Schannel
QUIC validation script and the EmbedIO.Quic self-test (10,076 primitive assertions)
passed. A recursive scan of every tracked file with the pinned YARA-X 1.21.0 and
YARA Forge 20261018 rules matched only the remaining accepted entries, so the
acceptances for the removed benchmark, the removed probe and the two rewritten
fixtures were removed. Hosted acceptance is pending.

## Public WebTransport sessions

Applications now opt in through `WebServerOptions.WebTransport`
(`WithWebTransport`) and accept sessions through `IWebTransportContext`, using
`WebTransportSession` and `WebTransportStream`; the
[guide](../guides/webtransport.md) describes the API. This is the context-capability
shape with the draft-02 browser handshake chosen for
[#414](https://github.com/WilliamSmithEdward/embedio-neo/issues/414).

Each HTTP/3 connection of a server that opts in owns an `Http3WebTransport`. It
appends the draft SETTINGS to the server's control stream, and the client's
SETTINGS fix the dialect of every session on the connection: draft-16 when the
client enables it and the connection negotiated datagrams and reliable reset,
draft-02 when the browser dialect is accepted, the client sends its setting and
datagrams were negotiated. Session requests are admitted through the existing
session registry; a request beyond the session limit gets 429 before dispatch and
one the connection did not negotiate is reset with `H3_MESSAGE_ERROR`. The
connection reads the first integer of every client bidirectional stream: the
WebTransport signal hands the stream to its session, and any other value is
replayed to the request reader through `Http3PrefixedStream`. Unidirectional
streams of type 0x54 go to their session the same way.

Found and corrected during integration:

- Server-opened session streams carried no stream header. `SetReliablePrefix`
  marks the first bytes reliable for a later reset but does not send them, so the
  header is now written after the prefix is installed. The independent peer's
  echo mode exposed it.
- A connection whose client never negotiated WebTransport kept buffering streams
  that named sessions it could never have. When the client's SETTINGS arrive
  without WebTransport, the registry now releases what it buffered and refuses
  later streams with `WT_BUFFERED_STREAM_REJECTED`; datagrams are dropped.
- `IWebTransportContext` said only HTTP/3 contexts implement it. HTTP/2 contexts
  implement it too, with `IsWebTransportRequest` false; the documentation now says so.
- A request stream that ended before it was classified, such as an empty request or
  one reset inside its first integer, was never recorded in the registry's bounded
  record of requests that can no longer name a session. Each one left a gap, and
  once ordinary requests had split the record into 128 ranges the connection closed
  with `H3_EXCESSIVE_LOAD`. Every request stream is now recorded when its worker
  ends, except an active session, which records itself when it ends. Gaps are then
  only requests still in progress, one per stream worker at most, so the record
  holds one more range than the 256 workers a connection may run.

`LocalReadAbortEndsAnAbandonedBodyReadWithoutResettingTheResponse` called
`Assert.CatchAsync` without awaiting it. NUnit 5 returns a task from that call, so
the test checked `CanRead` without waiting for the read to end and never checked
the read's exception. CI `39748091670` failed that `CanRead` assertion on macOS
only; the unawaited call explains a failure that depends on when the read's
continuation runs, but this was not reproduced. The test now awaits the call, and
no other test leaves an asynchronous NUnit assertion unawaited.

`CombinedDrainAbortsAFullUndispatchedQueue(False)` timed out on macOS in five of the
last twelve CI runs of this branch. In run `39782520890` the drain finished 5.1
seconds after it began, past the test's 5-second bound, while one QUIC connection
was still in its closing period. RFC 9000 section 10.2 keeps a closing connection for
three probe timeouts, and that runner's RTT estimate (449 ms smoothed, 417 ms
variation, 26 ms acknowledgement delay) made the period 6.4 seconds. The engine does
what the RFC asks. The test now reads the remaining closing period from the
connections and allows it plus one second past the 5-second bound; anything else
that holds the drain still fails it.

Validation on Windows 11 with Schannel, before hosted CI:

- 31 new in-suite cases: 14 for the settings and the mode check, 14 over the
  listener with the .NET QUIC client (advertised SETTINGS, malformed session
  requests on connections without WebTransport, the draft-02 token as an ordinary
  request when that dialect is off, ordinary GET and POST beside WebTransport,
  request streams that end the same way with and without it, streams refused on a
  connection without it, and 300 rounds of requests that fail before
  classification between ordinary ones) and 3 in the session core. The 123
  WebTransport and request-abort cases passed in ten fresh processes. Before the
  request record correction the 300-round case failed at round 128 in each of ten
  runs, and the session-core case could not find the method it tests; after it,
  both passed in ten runs. With the HTTP/1 case described under Endurance below,
  the regression floor is the new discovery count, 7,460.
- The full suite, without the local wildcard-bind case, ran 7,459 cases: 7,455
  passed and 4 skipped, none failed. Three skips are the usual platform ones; the
  fourth, the aioquic datagram case, skips without its peer and passed on its own
  with the peer configured. Its first run caught the test host still passing the
  removed `bool` switch to the connection constructor by reflection, which failed
  17 HTTP/3 adapter cases until the host passed the new options argument.
- `verify_webtransport_production.py`: all 15 quic-go peer modes passed against
  the development host's `serve-webtransport` command, an ordinary `WebServer`
  using the public API.
- The guide's complete program, extracted verbatim and compiled with warnings as
  errors, passed the peer's echo, reset, large and origin modes. Its first version
  opened the reply stream before reading and so answered a reset incoming stream
  with an empty, cleanly finished reply; the reset mode caught it.
- Chromium 152 (the Claude desktop app's browser) opened a draft-02 session from a
  page served on `http://localhost`, pinning a 10-day ECDSA P-256 certificate with
  `serverCertificateHashes`. The request carried `sec-webtransport-http3-draft02: 1`
  and the page's origin; a datagram, a bidirectional stream and a unidirectional
  stream were echoed, and the close code 7 and reason "done" the page sent were
  recorded on both sides.

On Linux, an Ubuntu 24.04 container with SDK 10.0.402 (runtime 10.0.13) built the
development host against the linux-x64 TLS library from CI run `f2b190b0` (native
sources unchanged since) and passed all 15 modes, with the peer built both by Go
1.26.8 and by the pinned Go 1.27.2 image. CI now runs the same steps on every code
change. The `WebTransport peer` job builds the peer once, for linux-x64 and, cross-
compiled, for osx-arm64, in the `golang:1.27.2-trixie` image pinned by digest in
`test/EmbedIO.Quic/peer/Dockerfile` (Go 1.27.3 was three days old), after checking
`go.sum` with `go mod verify`. The `WebTransport interop` jobs then run all modes on
Ubuntu 24.04 and macOS 15 against that run's TLS runtime, re-signing the macOS
binary ad hoc, and keep the logs without the throwaway test key. Dependabot follows
the Go image and the peer's modules. In CI `39794451873` all modes passed on both
platforms. The macOS job needed one rerun: after its modes passed, `dotnet nuget
locals` exited 137 in `setup-dotnet`'s cache cleanup, the failure #401 tracks.

On Linux, the same page and harness ran in the pinned SDK 10.0.402 container against
the packages from CI run `39811758731` (its `quic-runtime-feed` artifact, source
`cff98439`). An ordinary consumer project restored `EmbedIO-Neo` and
`EmbedIO-Neo.Quic.Native` 2.16.0 from that feed only; package-source mapping kept
nuget.org's public 2.16.0 out. Playwright 1.64.0 drove headless browsers:

- Chromium 156.0.8078.4 echoed the datagram and both streams, and the server
  recorded close code 7 and reason "done".
- Firefox 157.0 echoed the datagram and both streams in all nine sessions, but the
  server never received a close code. Firefox's own log (`MOZ_LOG` for its HTTP/3
  stack) shows it closing the whole QUIC connection with H3_NO_ERROR about 3 ms
  after the page's `close()`. The server then either ended the session without a
  code, so `AcceptStreamAsync` returned `null`, or canceled the request first, so it
  threw `OperationCanceledException`; six instrumented runs split three and three.
  Neither leaves anything waiting, and the guide now describes both. tshark could
  not decrypt past the handshake with Firefox's key log, so the missing close
  capsule rests on Firefox's log and the server's observations, not on a capture.
- Playwright's WebKit 27.2 build has no `WebTransport` (`ReferenceError`).

### macOS browsers and draft-14

[#525](https://github.com/WilliamSmithEdward/embedio-neo/issues/525) ran the page on
macOS 27.0.1 (Apple M5 Pro). The server was the guide's complete echo program,
built against CI run `40000994026`'s 2.17.0 packages from `ded53d80`, and printing
how each session ended. The page posts its outcome back to the harness, so Safari ran
without remote automation.

- Chromium 152.0.7977.149, the Claude desktop app's browser, passed in draft-02, and
  the server recorded close code 7 and reason "done".
- Safari 27.0.1 (26A434) failed before `ready` with a `WebTransportError` that gave
  no message. Playwright's macOS WebKit 27.2, unlike its Linux build, has
  `WebTransport` and failed the same way.
- Playwright 1.64.0's Firefox did not start on macOS 27 ("Could not find profile
  folder"). Firefox 158.0.1 from Mozilla (SHA-256 checked against the release's
  `SHA256SUMS`) failed the same way when started directly from the test shell, but
  started when macOS launched it (`open -n -a Firefox.app --args --headless
  --profile <empty folder> <page>`), as Safari was. It passed the page in draft-02,
  three runs against the 2.17.0 packages from nuget.org and three against the
  draft-14 build: datagram, bidirectional and unidirectional echoes, and the page's
  close. Each request carried `sec-webtransport-http3-draft02: 1`, so draft-14
  support does not take Firefox over. As on Linux the server recorded no close code:
  five sessions ended without one and one request was canceled
  ([#537](https://github.com/WilliamSmithEdward/embedio-neo/issues/537)).

A UDP relay recorded Safari's exchange. A local build that also wrote the server's
traffic secrets, never committed, let aioquic decrypt it:

- With `serverCertificateHashes` the handshake finished; without them Safari sent
  TLS alert 46, `certificate_unknown`.
- Safari's SETTINGS carry `SETTINGS_WT_MAX_SESSIONS` under draft-07's `0xc671706a`
  and draft-13/14's `0x14e9cd29`, and the flow-control settings `0x2b61`, `0x2b64`
  and `0x2b65`, but neither `0x2b603742` (draft-02) nor `0x2c7cf000` (draft-15/16).
- Its transport parameters carry `reset_stream_at` as `0x17f7586d2cb571`, the
  codepoint of draft-ietf-quic-reliable-stream-reset-07 and earlier, which draft-14
  cites. Drafts 08 to 10 use `0x1d`, which was all EmbedIO.Quic sent and accepted.
- Without a dialect and reset parameter it knew, Safari never sent the CONNECT: it
  reset request stream 0 with `H3_REQUEST_CANCELLED` and closed with `H3_NO_ERROR`.
  Offering only the reset codepoint, or only the setting, failed the same way.
  With both, Safari sent `:protocol webtransport` with an empty
  `wt-available-protocols`, which 2.17.0 refused with `H3_MESSAGE_ERROR`, because it
  read that token as draft-02 only.

Draft-14 differs from draft-16 only in the enabling setting, the token and two error
codes added later; its streams, capsules and flow-control settings are the same, and
the `RESET_STREAM_AT` frame is the same in both reliable-reset drafts. The server
now advertises `SETTINGS_WT_MAX_SESSIONS` with `MaximumSessionsPerConnection`,
negotiates draft-14 when a client sends that setting and not the draft-16 one, maps
`:protocol webtransport` to draft-14 or draft-02 by what was negotiated, and runs
draft-14 sessions on the draft-16 code. `AcceptBrowserDraft14`, on by default,
refuses it. EmbedIO.Quic sends and accepts `reset_stream_at` under both codepoints.
With draft-02 off, the token stays an ordinary request unless the client declared
draft-14.

With that change, Safari 27.0.1 and Playwright WebKit 27.2 passed the page: datagram,
bidirectional and unidirectional echoes, and the page's close. Chromium 152 still
passed in draft-02 against the same server. Safari sent no close capsule, and the
handler saw `AcceptStreamAsync` return `null` in two of four runs and
`OperationCanceledException` in two, as the guide's Closing section describes. Safari
uses `datagrams.createWritable()`; it has no `datagrams.writable`. Its retransmitted
first Initial packets went out unpadded in 1,038-byte datagrams, against RFC 9000
section 14.1, but its first flight was padded, so this did not affect the session.

The quic-go peer gained `-dialect draft-14`, which sends `0x14e9cd29` and
`:protocol webtransport`; `verify_webtransport_production.py --dialect draft-14`
runs every mode that way, and CI runs both dialects. Locally on macOS all 15 modes
passed in each dialect. One draft-16 `siblings` run timed out in the peer's
handshake ("no recent network activity") and passed in five repeats and a full
rerun. The connection self-tests check that both reset codepoints are offered, that
either enables reliable reset and that a non-empty value of either is refused.

## Oldest macOS

The native build targets macOS 14.0. A one-off CI run on the `macos-14` image
(macOS 14.8.9, image 20260831.0302.1) ran the package consumers against packages
built from `242127bc`'s product source (run `39825588810`). The portable and
RID-specific consumers passed with all three cipher profiles and their
missing-runtime controls. The Native AOT publish failed to link: .NET 10.0.13's
`libSystem.Security.Cryptography.Native.Apple.a` references Swift libraries such as
`swift_math` and `swift_errno` (`__swift_FORCE_LOAD_$_swift_math`) that the image's
default Xcode 15.4 does not have, and every later step was skipped. Microsoft lists
the latest Xcode command line tools as the macOS prerequisite for Native AOT. With
`DEVELOPER_DIR` set to the image's Xcode 16.2 (16C5032a, Apple clang 16.0.0, SDK
15.2), run `39833142236` on the same product source passed all 33 steps on macOS
14.8.9, including the AOT publish, the AOT runs and the full protocol suites. Both
runs came from throwaway branches, not a CI leg; GitHub retires the macOS 14 image
on 2 November 2026.

## Hardened-runtime macOS apps

`validate_hardened.py` signs the package consumers the way an application ships them
and runs them with the hardened runtime ([#524](https://github.com/WilliamSmithEdward/embedio-neo/issues/524)).
It takes `validate.py`'s output for the RID and builds two layouts:

- The Native AOT consumer in an `.app` bundle, with the executable and
  `libembedio_quic_tls.dylib` in `Contents/MacOS`. The dylib is signed first, then the
  bundle with `--options runtime`, and `codesign --verify --strict` must accept it.
- A self-contained JIT consumer as a flat directory. Its managed `.dll` files are not
  code, and `codesign` refuses a bundle that has them in `Contents/MacOS`, so every
  Mach-O file is signed separately and the apphost last, with the hardened runtime.

Each runs the three cipher profiles and the missing-runtime control, and the script
checks that every entry point carries the `runtime` flag and exactly the entitlements
it asked for.

The hardened runtime turns on library validation: the process may load only
libraries signed by Apple or with its own Team ID. An ad-hoc signature has none, so
with `--identity -`, the default, both layouts get
`com.apple.security.cs.disable-library-validation`, and JIT also gets
`com.apple.security.cs.allow-jit`. A control of each without library validation
disabled must be refused, and its error must say the mapping process and the library
"have different Team IDs". That shows the hardened runtime was enforced, and that
nothing else stops the library loading. Library validation needs System Integrity
Protection. On GitHub's `macos-26` image the AOT control loaded the library in run
`40008522741`, so the script records `csrutil status` and requires the refusals only
where it reports SIP enabled; elsewhere it records whether each control loaded.

On an Apple M5 Pro with macOS 27.0.1 (26A434), SDK 10.0.402 and runtime 10.0.13,
against CI run `40000994026`'s packages from `ded53d80`, all 12 steps passed. The AOT
app needed no entitlement besides library validation: the native runtime has no JIT
or writable executable memory, so `allow-jit` and `allow-unsigned-executable-memory`
are not needed. The earlier experiments that led to the script:

| Layout | Entitlements | Result |
| --- | --- | --- |
| AOT, ad-hoc | none | Refused: `libembedio_quic_tls.dylib` has a different Team ID |
| AOT, ad-hoc | disable library validation | Passed |
| JIT, framework-dependent | none or `allow-jit` | Refused before EmbedIO: Microsoft's `libhostfxr.dylib` has a different Team ID |
| JIT, framework-dependent | disable library validation | CoreCLR failed to start (`0x80070008`) without `allow-jit` |
| JIT, framework-dependent or self-contained | `allow-jit` and disable library validation | Passed |

Each refusal ends in an unhandled `PlatformNotSupportedException` from server
construction or a host error, so macOS writes a crash report for it.

Signing with a Developer ID has not run, and will not here: the project has no Apple
Developer ID, so #524 closed with ad-hoc signing as its acceptance. With every file
signed by one team, the AOT app should run with no entitlements and the JIT app with
`allow-jit` only, since ad-hoc signing showed library validation to be the only
obstacle. An application with a Developer ID can check that with `--identity`.
Notarization, Gatekeeper assessment and the App Sandbox are out of scope.

## Oldest glibc and musl

.NET 10's own floors are glibc 2.27 on x64 and Arm64, from Ubuntu 18.04, and musl
1.2.3, from Alpine 3.17 ([.NET 10 supported OS versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#libc)).
The native glibc builds need no symbol newer than 2.27, which their audit checks, so
these are the oldest systems the package can run on. The `floor` job in
`quic-portable.yml` runs the package consumers there on all four Linux RIDs
([#524](https://github.com/WilliamSmithEdward/embedio-neo/issues/524)):

- The consumers are built and checked on the runner first, as in the portable job.
  Then each consumer's server runs in a container of `Dockerfile.floor-glibc`
  (Ubuntu 18.04, glibc 2.27-3ubuntu1.6) or `Dockerfile.floor-musl` (Alpine 3.17.10,
  musl 1.2.3). The images hold only the packages .NET needs, and the one runtime the
  pinned SDK bundles, 10.0.13, is mounted read-only. The aioquic peer stays on the
  host. Each consumer runs all three cipher profiles and the missing-runtime control,
  and the script checks the libc version each image reports.
- On Ubuntu 18.04, .NET's own cryptography, which protects the QUIC packets, uses the
  system OpenSSL 1.1.1 (1.1.1-1ubuntu2.1~18.04.23), the oldest OpenSSL .NET 10
  supports. The native runtime keeps its statically linked OpenSSL 3.5.9 private, and
  both worked in one process.
- A Native AOT binary runs only on the glibc it was built against or a newer one
  ([Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)).
  The AOT consumer published on Ubuntu 24.04 did not start on 18.04: the executable
  itself needs `GLIBC_2.29` and `GLIBC_2.34`, which the native runtime does not. The
  glibc floor therefore publishes its AOT consumer in the image's `build` stage, on
  Ubuntu 18.04 with clang 6.0, the prerequisites Microsoft lists for 18.04 and later,
  and runs that binary. On musl, the AOT consumer built on the validation image's
  Alpine 3.24.2 (musl 1.2.6) ran on Alpine 3.17 unchanged.

Before CI, the job's steps ran locally for linux-x64 and linux-musl-x64 in Docker
against CI run `39898260505`'s packages from `a99319de`. The consumers passed on the
current distributions (19 steps each), then on the floors: 14 steps on glibc 2.27,
including the AOT publish there, and 12 on musl 1.2.3. Every run completed the
consumer's whole request sequence, the request reset mid-response and the request
after it included, and every missing-runtime control was rejected at server
construction. The development host's protocol suites still run only on the current
distributions.

## Last CI fixes

Failures on `1d97aa8e`, `2b466559` and `7d861056` were corrected before the merge:

- The Security report failed on a Semgrep parse error, not a finding. Semgrep 1.181.0
  cannot parse C# primary constructors, and `PendingHeader`, added with the HTTP/3
  header timeout, declared one, so `Http3QuicConnection.cs` went unscanned. Semgrep
  only parses a file when a rule's literal pre-filter matches it, which is why other
  primary constructors in the source have not failed the report. `PendingHeader` is
  now a positional `readonly record struct`, which the pinned image parses.
- On macOS the production interop's `datagram-fault` mode counted no accepted
  session although the peer passed. The peer sent its invalid datagram as soon as it
  read the 200, and the server closed the connection with H3_DATAGRAM_ERROR before
  the handler's `AcceptWebTransportAsync` returned. The peer now completes one stream
  echo first, which the server can only answer after accepting the session; the
  rebuilt peer passed all 15 modes on Windows, and `datagram-fault` ten more times.
- `CombinedDrainAbortsAFullUndispatchedQueue(True)` failed on macOS: the canceled
  drain had not thrown within 5 seconds. A canceled drain still waits for the
  transports to stop, and a QUIC connection holds that through its closing period.
  The canceled case now gets the allowance the uncanceled case already had: what
  remains of the closing period plus one second, with the pending stages reported on
  a miss.
- On `2b466559` that canceled case failed once more on macOS, at the 5-second bound
  with no shutdown stage pending and no QUIC connection left; the drain finished 2 ms
  later. The helper granted its extra second only while a connection was still
  closing, so a closing period that ended at the bound left none. The second now
  applies either way (`7d861056`).
- On `7d861056` the Linux interop's `outbound-reset` mode received H3_REQUEST_CANCELLED
  instead of WT_SESSION_GONE on a server stream, a product defect. A session's streams
  are reset one after another; the first reset woke a handler that disposed the next
  stream before the core reached it, and `AbortFromSession` had marked that stream's
  writes complete before resetting it, so disposal reset it with the generic code. The
  reset now comes first, and a stream disposed after its session ended is reset with
  WT_SESSION_GONE (`ed27dfe8`). A sleep in that window reproduced the failure in 20 of
  20 local runs.

## Endurance

The load benchmark's endurance mode ran on Windows against `750176f0`, with server
CPUs 0-3 and client CPUs 4-7: idle, steady load, three restarts, the fault mix, two
drains under load, steady load, three restarts, the fault mix, steady load and
idle. The run took 18 minutes over nine server generations. All ten steps passed
without a client error; the server answered 1,846,123 validated requests, 264,958 of
them over HTTP/3, and every drain and restart left nothing in flight. Retention comes
from snapshots taken after a forced collection with no request in flight; the first
two are warm-up.

| Quiesced value | First after warm-up | Range | Last |
| --- | --- | --- | --- |
| Managed heap | 4,475 KiB | 3,000 to 4,475 KiB | 3,966 KiB |
| Handles | 589 | 569 to 617 | 583 |
| Timers | 5 | 5 to 6 | 5 |
| Private memory outside the GC's committed memory | 22.5 MiB | 20.9 to 23.7 MiB | 23.7 MiB |

Private memory itself ranged from 37.7 to 59.0 MiB, following what the GC kept
committed after load; without that, the series has no trend. At the 16 bytes per
HTTP/3 request that #278 measured in `System.Net.Quic`, these requests would have
added about 4 MB. One 18-minute run on one platform cannot rule out a smaller leak.

The first attempt, on `fb0031d8`, stopped serving in both fault phases. A failure
while the server finished an HTTP/1 request reached `WebServerBase`'s
fatal-exception path, which disposes the listener; HTTP/2 and HTTP/3 requests were
already excluded from that path. HTTP/1 requests now are too.
`ResetWhileCleanupReadsAnOpenedBodyEndsOnlyItsConnection` failed on the unchanged
source and passed in ten runs with the correction. The benchmark also still
expected the 90-second limit from before v2 for a connection's first request; it now
expects the default `RequestHeadersTimeout` of 30 seconds.

### 30-minute runs

Two later runs used the load benchmark README's plan: idle, steady load for five
minutes, the fault mix for four, WebSockets for two, three drains under mixed load,
five restarts, mixed load for seven minutes and idle, with server CPUs 0-3 and client
CPUs 4-7. Linux ran in the digest-pinned .NET SDK 10.0.402 container (runtime 10.0.13)
limited to those eight CPUs, with the CI-built TLS library (SHA-256 `cdbba337...`) and,
for the clients' .NET `HttpClient`, the libmsquic 2.6.2 package CI pins.

| | Windows, `242127bc` | Linux, `4338b9e0` |
| --- | --- | --- |
| Steps passed | 8 of 8 | 8 of 8 |
| Client errors | 0 | 0 |
| Validated requests | 3,371,913 | 5,043,237 |
| Of them over HTTP/3 | 481,648 | 484,934 |
| Quiesced managed heap, first after warm-up to last | 3,664 to 3,741 KiB | 3,402 to 3,450 KiB |
| Handles, first to last | 571 to 592 | 134 to 134 |
| Threads, first to last | 18 to 22 | 20 to 24 |
| Private memory outside the GC's committed memory, first to last | 21.9 to 24.6 MiB | 212.9 to 249.7 MiB |

On Linux, memory outside the GC rose from 213 MiB after the fault mix to 254 MiB after
the third drain cycle, then held between 246 and 251 MiB through the restarts, seven
minutes of mixed load and the final idle; it did not grow with the 2.44 million
mixed-load requests. That fits a native allocator reaching its high-water mark better
than per-request retention, but 30 minutes cannot rule out slow growth. Apart from
tests, the two revisions differ only in how `PendingHeader` is declared (see
[Last CI fixes](#last-ci-fixes)).

The benchmark's server used to ask `System.Net.Quic` whether QUIC is supported, which
loads MsQuic into the server process although EmbedIO.Quic never uses it. `4338b9e0`
removed that probe. The Windows run predates it: its startup record shows the .NET
runtime's msquic.dll 2.5.11 loaded, unused, in the server.
During the Linux run's steady load, a census of the benchmark processes found
libmsquic mapped only in the client and the EmbedIO TLS library only in the server.
Two earlier Linux attempts are not results: one built a source export with CRLF line
endings, which failed IDE0055, and the next lacked the client's libmsquic, so no
HTTP/3 request left the client.

### 30 minutes on macOS

The README's 30-minute plan also ran on macOS 27.0.1 on an Apple M5 Pro (18 logical
CPUs), with .NET SDK 10.0.402 and runtime 10.0.13, on `a99319de` with two benchmark
fixes merged locally.
[#531](https://github.com/WilliamSmithEdward/embedio-neo/pull/531) records handles and
private memory, which the benchmark had recorded as 0 on macOS
([#529](https://github.com/WilliamSmithEdward/embedio-neo/issues/529)).
[#533](https://github.com/WilliamSmithEdward/embedio-neo/pull/533) adds the opt-in
`--request-body-read-timeout`, set to 30 seconds here, because `faults` steps could
not quiesce while macOS drops some client resets
([#532](https://github.com/WilliamSmithEdward/embedio-neo/issues/532)). macOS has no
CPU pinning, so server and client shared the machine; the client loaded the libmsquic
2.6.2 bottle CI pins. On macOS the benchmark records open file descriptors as handles
and the physical footprint as private memory.

| | macOS |
| --- | --- |
| Steps passed | 8 of 8 |
| Client errors | 0 |
| Validated requests | 5,023,105 |
| Of them over HTTP/3 | 483,214 |
| Quiesced managed heap, first after warm-up to last | 4,470 to 5,206 KiB |
| Open file descriptors, first to last | 137 to 137 |
| Threads, first to last | 56 to 56 |
| Physical footprint outside the GC's committed memory, first to last | 98.1 to 104.0 MiB |

Memory outside the GC stayed between 98 and 104 MiB at every quiesced snapshot after
warm-up, through 2.44 million mixed-load requests. During the drains, 13 first-chance
`NullReferenceException`s were thrown inside the runtime's macOS TLS callback,
`SafeDeleteSslContext.WriteToConnection`, when a TLS write met a stream the drain had
disposed. The runtime caught them, and no client or handler failed. An earlier attempt
is not a result: it launched the runner through `caffeinate`, and macOS strips
`DYLD_*` variables when it starts a protected system binary, so the client could not
find libmsquic and every HTTP/3 request failed.

### Three-hour runs

Windows and Linux then each ran a 2.96-hour plan on `5bc8899e`, whose source 2.18.0
ships unchanged: idle, steady load for ten minutes, the fault mix for four, WebSockets
for two, then six cycles of mixed load for 18 minutes, the fault mix for two, a drain
under mixed load and two idle restarts, then WebSockets for two minutes, steady load
for ten and idle for five. Every cycle carried the same traffic. Server CPUs were 0-3
and client CPUs 4-7, with runtime 10.0.13. Linux ran in the same pinned container as
its 30-minute run, with the CI-built TLS library for this revision (SHA-256
`2968a832...`); during the first steady load, a census found the TLS library mapped
only in the server and libmsquic only in the client.

| | Windows | Linux |
| --- | --- | --- |
| Steps passed | 31 of 31 | 31 of 31 |
| Client errors | 0 | 0 |
| Validated requests | 30,812,608 | 45,611,552 |
| Of them over HTTP/3 | 4,439,726 | 4,411,114 |
| Quiesced managed heap, first after warm-up to last | 3,823 to 3,819 KiB | 4,010 to 3,539 KiB |
| Handles, first to last | 572 to 601 | 134 to 134 |
| Threads, first to last | 18 to 21 | 20 to 21 |
| Working set after the first and last cycles | 86.8 and 87.2 MiB | 145.4 and 151.8 MiB |
| Private memory outside the GC's committed memory, after the first and last cycles | 21.6 and 23.4 MiB | 240.9 and 253.6 MiB |

Every drain and restart left nothing in flight, and no replaced server instance served
a request. Small-request p99 held: on every protocol, the medians of the run's first
and last quarters differ by at most 0.61 ms.

On Linux, .NET reads private memory from VmData (`ProcessManager.Linux.cs` at
v10.0.13), which counts writable private mappings whether or not they are resident.
Thread stacks and allocator arenas count in full, so private memory runs about 110 MiB
above the working set in both Linux runs. The managed heap held and GC committed
memory fell, so what grew is native: the working set rose by 2.7, 1.5, 1.8, -0.3 and
0.7 MiB from one cycle to the next. Every cycle carried about 6.2 million mixed-load
requests, so a fixed cost per request or per cycle would add about the same each time;
these increments shrank, which fits an allocator approaching its high-water mark. On
Windows, memory held flat but handles rose, mostly in the first hour as threads went
from 18 to 21, and then, with 21 threads, from 587 after the second cycle to 601 at
the end. Three hours cannot show that either series stops; the eight-hour runs below
follow both with finer instruments.

### Eight-hour runs

For [#552](https://github.com/WilliamSmithEdward/embedio-neo/issues/552), both
platforms then repeated the three-hour runs' cycle 19 times, 8.0 hours, on `fc6d4dfc`,
whose library source is what 2.18.0 ships;
[#554](https://github.com/WilliamSmithEdward/embedio-neo/pull/554) added only the
instruments these runs read. Each quiesced snapshot also records, on Linux, the
resident set split into anonymous and file-backed pages and glibc's `mallinfo2`
totals, and on Windows the open handles by object type. Before its run, each platform
passed `native-census-check`, which proves its census sees a known change. The setup
otherwise matches the three-hour runs.

| | Windows | Linux |
| --- | --- | --- |
| Steps passed | 81 of 83 | 83 of 83 |
| Client errors | 1 | 0 |
| Validated requests | 87,178,259 | 128,328,216 |
| Of them over HTTP/3 | 12,553,342 | 12,462,202 |
| Handles after the first and last cycles | 573 and 596 | 134 and 134 |
| Event handles in the census, same points | 215 and 235 | - |
| Win32 heaps allocated, same points | 7.7 and 8.3 MiB | - |
| malloc in use, same points | - | 10.2 and 10.6 MiB |
| malloc free, same points | - | 38.0 and 49.9 MiB |
| Resident anonymous memory, same points | - | 60.1 and 72.8 MiB |

All 57 drains and restarts on each platform left nothing in flight, and no replaced
server instance served a request. Small-request p99 held: on every protocol, the
medians of the run's first and last quarters differ by at most 0.36 ms.

Windows' two failed steps are one event, and the only client error in any of these
endurance runs. In the ninth cycle a QUIC stream retirement threw inside a
connection's stream bookkeeping and aborted that connection under the HTTP/3
cancellation workload, and the next drain reported the failure when the listener shut
down. [Stream retirement race](#stream-retirement-race) has the cause and the fix.

On Windows, handles rose 22 over the first nine cycles and 1 over the last nine. The
census puts the early growth on Event handles, which stayed between 232 and 236 for
the last eight cycles. The heaps' allocated total stopped growing after the tenth
cycle. Inside it, the default heap's 24-, 32- and 56-byte blocks grew in bursts, most
of it during the second to sixth and the tenth to twelfth cycles, and by tens of
blocks a cycle at the end, about 2 KB.

On Linux, malloc's in-use bytes held at 10.1 to 10.6 MiB through all 19 cycles, so
live native state did not grow. Its free bytes rose 11.0 MiB over the first nine
cycles and 0.9 MiB over the last nine, and resident anonymous memory followed them:
the growth is memory glibc keeps after the server frees it. Fitted over the last ten
cycles, resident anonymous memory still rises about 0.4 MiB a cycle, and Windows
handles about 0.4 a cycle, both well inside the runs' cycle-to-cycle swings of up to
3.5 MiB and 3 handles. VmData, the private memory .NET reports on Linux, rose 54 MiB,
about 3 MiB a cycle, most of it with no matching resident change.

### Stream retirement race

The eight-hour Windows run's client error was a race over one stream between its
connection and a request's worker
([#555](https://github.com/WilliamSmithEdward/embedio-neo/issues/555)). The
connection retires a request stream once the client is done with it and every frame
about it is acknowledged, which gives the stream back to the client's stream limit.
Since 2.18.0 the HTTP/3 server's worker also holds its stream until it disposes it, so
the stream counts against that limit while the request runs. The connection checked
whether the stream could retire, did its own bookkeeping, then retired it, taking the
stream's lock for the check and again for the retirement. A client that resets a
request and stops its response before the worker starts makes the check pass, and a
worker that took the stream in between made the retirement throw
`Async stream retired before completion`, which closed the connection. Streams the
server opens in one direction retired with no check at all.

The application now decides first: one call checks and retires the stream under its
lock, and a stream its owner still holds stays until the owner disposes it, on both
paths. Two self-tests take them: one takes a cancelled request stream between the
check and the retirement, the other holds a server-opened unidirectional stream after
its writes are acknowledged. On the unchanged source both throw that exception; with
the fix both streams stay, then retire once disposed.

The race needs the worker to start in the short span between the connection's check
and its retirement, and load alone rarely lands there. A 30-minute run of the HTTP/3
cancellation workload alone, at 100 times its endurance rate, on Windows with the
endurance runs' CPU split, did not reproduce it on the unchanged source (fc6d4dfc):
349,052 cancellations at 194 a second and no client error. On the fix (7d9b9102) the
same run made 361,337 cancellations at 201 a second, also without a client error, and
the drain and restart after it left nothing in flight. The self-tests, not the load,
show the fix.

The fix then ran the three-hour plan on both platforms. Linux finished all 31 steps
without a client error: 43,588,549 validated requests, 4,285,996 of them over HTTP/3.
Windows made 30,425,837, 4,404,123 over HTTP/3, without an HTTP/3 error, but two
steps of its first cycle failed. In them three client TCP sockets, on HTTP/1.1 and
HTTP/2 workloads, failed with `NoBufferSpaceAvailable` or `AddressAlreadyInUse`,
Windows' errors for running out of socket resources. The machine carried more outside
load than in the earlier runs: processors busy outside the server, the client's four
included, exceeded 12 of the 16 in 11.0 percent of the five-second samples, against 0.8
percent in the three-hour run on 5bc8899e. HTTP/1.1 and HTTP/2 do not use EmbedIO.Quic.
Neither run hit the retirement failure, and every drain and restart left nothing in
flight. The server's CPU and allocation per request matched the three-hour runs on
5bc8899e. Small-request p99 on Linux stayed within the range of the earlier Linux runs;
on Windows it ran higher than in them on every protocol, the TCP ones included.

## Open-issue audit

On 2026-10-22, after the provider removal and the public WebTransport sessions,
each of the eight open issues was rechecked against this source. The issues whose
column below says the merge is all that remains are closed when this work merges.

| Issue | Disposition on this source | Before closing |
| --- | --- | --- |
| [#414](https://github.com/WilliamSmithEdward/embedio-neo/issues/414) | Implemented: the session core is wired to HTTP/3, the public API, limits and guide exist, and opening a stream that fails after credit was reserved now returns the reservation. quic-go modes pass on Windows, Linux and macOS; Chromium 152 completed a draft-02 session on Windows, and Chromium 156 and Firefox 157 did on Linux. | The merge. Safari and browsers on macOS were not tested. |
| [#303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303) | EmbedIO.Quic implements current-wire `RESET_STREAM_AT`, so the MsQuic request (microsoft/msquic#6376) no longer gates WebTransport. The peer's reset, outbound-reset and missing-reset modes pass on Windows, Linux and macOS; the migration guide and standards matrix now state the support and its limits. | The merge. |
| [#278](https://github.com/WilliamSmithEdward/embedio-neo/issues/278) | The server no longer uses `System.Net.Quic`, so its send-buffer retention reaches only the BCL test client. The runs under Endurance above, 18 and 30 minutes on Windows and 30 on Linux, finished without a client error over more than 1.2 million HTTP/3 requests; quiesced heap and handles held their early levels, and on Linux memory outside the GC rose during the drains and then held flat. | The merge. Nothing ran longer than 30 minutes or on macOS. |
| [#202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202) | The bind retry and the rebind quarantine went with the MsQuic provider, and the restart cases run without them. They passed on Windows, Linux and macOS in CI `39748091670`. | The merge. |
| [#398](https://github.com/WilliamSmithEdward/embedio-neo/issues/398) | The failing path, `QuicListener` loading its credential, no longer exists on the server; EmbedIO.Quic acquires its Schannel credential itself, and the quarantine is gone. Windows Test passed in both runs on the new binding (`39738902765`, `39748091670`). | The merge; reopen if the engine shows a credential failure. |
| [#399](https://github.com/WilliamSmithEdward/embedio-neo/issues/399) | The native MsQuic listener and the tests that failed are removed. | The merge. |
| [#401](https://github.com/WilliamSmithEdward/embedio-neo/issues/401) | Unrelated to the QUIC work: the macOS compatibility audit's `dotnet list` call. The same exit 137 hit `dotnet nuget locals` in `setup-dotnet`'s cleanup on the macOS interop job in CI `39794451873`. | Its own investigation, since finished: on macOS a child that the .NET CLI forked aborted before `exec`, and every workflow now turns CLI telemetry off. |
| [#181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181) | The umbrella program; it stays open for the remaining engine work. | Not applicable. |

## Bounded TLS work under load

[#521](https://github.com/WilliamSmithEdward/embedio-neo/issues/521) asked how native
TLS work queues under sustained and bursty handshake load, for a bound on it, and for
what happens past the bound.

### How the work is scheduled

Each UDP endpoint admits at most 768 connections, and every connection's work, its TLS
handshake steps included, runs as turns on a pool of at most eight workers
(`Math.Clamp(Environment.ProcessorCount, 1, 8)`); a connection has at most one turn
queued or running. Handshakes that do not finish in ten seconds expire. The HTTP/3
listener above the endpoint holds 256 established connections and closes any other
with `H3_EXCESSIVE_LOAD`.

Before this change nothing bounded how long a turn waited for a worker. A full
endpoint dropped new Initials silently, so those clients retransmitted until their own
handshake timeout. A connection the listener had no room for still completed its TLS
handshake, and was then closed.

### The bound

The execution pool measures how long turns wait for a worker: MsQuic's worker average
(seven parts history, one part the latest wait), reset whenever nothing waits, or,
while turns wait, the time since a worker last took one, whichever is longer. A new
connection is refused while that exceeds 250 ms, MsQuic's default worker queue delay,
while the endpoint holds 768 connections, or while the listener has no room for it.
The refusal answers the Initial with an Initial carrying
`CONNECTION_CLOSE(CONNECTION_REFUSED)` (RFC 9000 section 5.2.2), protected with the
client's Initial keys; the server creates no connection and starts no TLS work.
Established connections are unaffected. Refusals are budgeted at 4,096 per second per
endpoint, with at most 512 queued for sending; past either an Initial is dropped. The
binding counts refusals by reason in its closing diagnostics and logs them as a
warning at most every ten seconds.

The listener has room while its established connections, those in their handshake and
those the endpoint has admitted but not yet handed over number fewer than 256. The
last group matters: the endpoint admits a connection on its socket loop, a worker
creates it in its first turn, and the listener's accept loop takes it from a hand-off
queue after that, so a burst admitted between those steps was invisible to the count.
A first version counted only what the listener had accepted. On Linux, with 8,192
attempts in flight at 4,000 per second, it saturated the server's two CPUs at the start
of the phase and completed 512 and 458 connections in two rounds where main completed
2,646 and 2,697. With the hand-off queue counted but not the uncreated connections, it
still closed 2,701 attempts after their handshake in one Windows phase. The endpoint
now counts a connection from its admission until a worker creates it, and passes that
count and the hand-off queue's to the listener. An established connection leaves the
count once it can serve no more requests; see
[Closing connections and their slots](#closing-connections-and-their-slots).

### Overload runs

The load benchmark's `quic-churn` mode opens a connection per attempt, sends one GET
on a request stream, closes the connection, and records every failure with the stage
it ended in. Each run offered a fixed rate for 15 seconds per phase, 5 seconds apart,
to a server pinned to two CPUs, and the client stopped starting attempts while 512
were in flight, counting the ones it skipped. Main was `554d7a3f`, the change was this
branch built on it, and the change's load benchmark was the client for both. Each ran
two rounds, in alternating order. On Windows:

```
dotnet <change runner>/EmbedIO.LoadBenchmark.dll quic-churn --engines embedio --rates 250,1000,2000,4000 --phase-seconds 15 --idle 5 --server-dir <main or change runner> --server-cpus 0-1 --client-cpus 4-11 --max-baseline-busy-cpus 2.0 --process-cpu-diagnostics --output <fresh dir>
```

Windows was Windows 11 build 26300 on a 16-thread Ryzen 7 9800X3D with .NET 10.0.13,
and its .NET client used MsQuic 2.5.11. Linux was a container on eight CPUs in Docker
Desktop's WSL2 VM (kernel 6.18.33) with the same .NET and libmsquic 2.6.2; the client
ran on CPUs 4-7, without `--process-cpu-diagnostics`. The machine is a desktop in use,
so each run started only after the Windows host averaged under 2 busy CPUs for 5
seconds. In the Windows runs other programs used at most 1.7 CPUs in any phase,
mostly a .NET build running elsewhere on the machine. For Linux runs the host's busy
CPUs above the VM's own were 0.1 to 3.0 per phase, which includes the VM's overhead.

"Closed after TLS" counts attempts whose handshake completed before the server closed
the connection with `H3_EXCESSIVE_LOAD`. "Timed out" counts handshake and idle
timeouts at any stage. Each cell is round 1 / round 2.

Windows:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 250/s | main | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 7 / 7 | - / - |
| 250/s | change | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 7 / 7 | - / - |
| 1,000/s | main | 9,557 / 8,885 | 3,887 / 3,537 | 0 / 0 | 5,670 / 5,348 | 0 / 0 | 5,443 / 6,115 | 68 / 72 | - / - |
| 1,000/s | change | 14,938 / 15,000 | 7,131 / 7,386 | 7,702 / 7,516 | 105 / 98 | 0 / 0 | 62 / 0 | 31 / 31 | 6 / 6 |
| 2,000/s | main | 8,557 / 8,543 | 2,620 / 2,648 | 0 / 0 | 5,937 / 5,895 | 0 / 0 | 21,443 / 21,457 | 84 / 84 | - / - |
| 2,000/s | change | 21,689 / 12,932 | 6,631 / 6,722 | 14,969 / 6,150 | 89 / 29 | 0 / 31 | 8,311 / 17,068 | 34 / 39 | 8 / 11 |
| 4,000/s | main | 8,670 / 7,369 | 2,326 / 4,623 | 0 / 0 | 6,344 / 2,746 | 0 / 0 | 51,330 / 52,631 | 87 / 64 | - / - |
| 4,000/s | change | 16,266 / 24,522 | 6,415 / 6,754 | 9,819 / 17,718 | 30 / 50 | 2 / 0 | 43,734 / 35,478 | 34 / 36 | 9 / 10 |

Linux:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 250/s | main | 3,750 / 3,750 | 3,734 / 3,750 | 0 / 0 | 16 / 0 | 0 / 0 | 0 / 0 | 6 / 6 | - / - |
| 250/s | change | 3,750 / 3,750 | 3,750 / 3,718 | 0 / 32 | 0 / 0 | 0 / 0 | 0 / 0 | 6 / 7 | - / 1 |
| 1,000/s | main | 5,244 / 5,214 | 3,206 / 3,703 | 0 / 0 | 2,031 / 1,511 | 7 / 0 | 9,756 / 9,786 | 77 / 73 | - / - |
| 1,000/s | change | 9,876 / 10,275 | 4,469 / 4,675 | 5,368 / 5,594 | 4 / 6 | 35 / 0 | 5,124 / 4,725 | 48 / 57 | 4 / 4 |
| 2,000/s | main | 5,430 / 4,272 | 1,725 / 3,999 | 0 / 0 | 3,705 / 171 | 0 / 102 | 24,570 / 25,728 | 151 / 76 | - / - |
| 2,000/s | change | 11,429 / 15,852 | 4,628 / 4,929 | 6,723 / 10,922 | 3 / 1 | 75 / 0 | 18,571 / 14,148 | 45 / 59 | 5 / 5 |
| 4,000/s | main | 5,253 / 4,268 | 1,467 / 3,752 | 0 / 0 | 3,786 / 516 | 0 / 0 | 54,747 / 55,732 | 149 / 70 | - / - |
| 4,000/s | change | 17,685 / 18,698 | 4,862 / 4,722 | 12,792 / 13,941 | 0 / 4 | 31 / 31 | 42,315 / 41,302 | 55 / 46 | 4 / 4 |

In every overloaded phase of these runs the change completed more connections than
main: 1.5 to 2.8 times as many on Windows and 1.2 to 3.3 times on Linux, where main's
own two rounds differed by up to 2.6 times. It closed at most 105 attempts per phase
after their handshake, where main closed 1,500 to 6,300 (171 and 516 in one Linux
round), and its refusals took a median of 4 to 5 ms on Linux and 6 to 11 ms on
Windows. The client offered the change more attempts because a refused attempt ends at
once and frees its place under the cap.

At 250 attempts per second every attempt completed on Windows. On Linux, two runs had
about 300 attempts in flight at that rate, their closes taking 125 ms at the median and
1.5 and 2.4 s at the 99th percentile, and the listener's 256 slots filled briefly: main
closed 16 attempts after TLS in one and the change refused 32 in the other. Connections
that had already closed held those slots;
[Closing connections and their slots](#closing-connections-and-their-slots) measures it.

With the cap at 512 the two servers face different numbers of attempts. With
`--max-outstanding 8192` at 2,000 and 4,000 attempts per second the change took every
attempt offered (all but 5,426 in one Linux phase), while main's slow failures kept
its client at the cap:

Windows, cap 8,192:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,000/s | main | 19,376 / 19,013 | 3,490 / 3,570 | 0 / 0 | 7,331 / 7,279 | 8,518 / 8,164 | 10,624 / 10,987 | 3,067 / 3,041 | - / - |
| 2,000/s | change | 30,000 / 30,000 | 6,279 / 6,257 | 23,658 / 23,683 | 62 / 60 | 0 / 0 | 0 / 0 | 37 / 44 | 9 / 10 |
| 4,000/s | main | 20,909 / 21,359 | 4,234 / 3,461 | 0 / 0 | 6,267 / 7,210 | 10,408 / 10,686 | 39,091 / 38,641 | 3,041 / 1,167 | - / - |
| 4,000/s | change | 60,000 / 60,000 | 6,082 / 6,124 | 53,581 / 53,717 | 5 / 159 | 332 / 0 | 0 / 0 | 77 / 26 | 14 / 8 |

Linux, cap 8,192:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,000/s | main | 16,290 / 14,672 | 2,798 / 3,099 | 0 / 0 | 3,844 / 2,901 | 9,648 / 8,664 | 13,710 / 15,328 | 3,067 / 3,041 | - / - |
| 2,000/s | change | 30,000 / 30,000 | 4,856 / 4,425 | 25,138 / 25,553 | 6 / 22 | 0 / 0 | 0 / 0 | 73 / 57 | 4 / 4 |
| 4,000/s | main | 18,779 / 19,085 | 2,951 / 2,151 | 0 / 0 | 4,304 / 4,520 | 11,520 / 12,412 | 41,221 / 40,915 | 3,041 / 3,041 | - / - |
| 4,000/s | change | 60,000 / 54,574 | 4,899 / 3,062 | 54,485 / 51,269 | 10 / 4 | 606 / 239 | 0 / 5,426 | 97 / 74 | 11 / 67 |

Main completed 2,151 to 4,234 connections per phase and timed out 8,164 to 12,412
attempts, with a median handshake of 1.2 to 3.1 seconds. The change completed 3,062 to
6,279, 1.4 to 1.8 times main's, while refusing 23,658 to 54,485 attempts, the median
refusal taking 4 to 14 ms (67 ms in one Linux phase). A few attempts failed in the
client itself and are not in the tables: the client's MsQuic ended 37 of main's
streams with an internal error in one Windows phase and 14 across main's Linux phases,
and three Windows handshakes, two of them main's, failed with WSAENOBUFS.

One more run per platform repeated the change at this load with
`EMBEDIO_BENCH_WARNINGS=1`. The listener having no room accounted for all 76,673
refusals on Windows and 81,319 of 81,353 on Linux, where queued work briefly waited
more than 250 ms and the delay bound refused the other 34; the 768-connection limit
never engaged. At 4,000 attempts per second the budget or the send queue left 293
refusals unsent on Windows and 5,234 on Linux, and those clients sent their Initial
again.

The server used 0.3 to 0.6 of its two CPUs on Windows and 1.0 to 1.7 on Linux, so on
Windows its CPUs did not limit how many connections it completed. Its one socket loop,
which handles at most one received and one sent datagram per iteration, is the first
suspect.

### Closing connections and their slots

A connection held its listener slot until the task that served it ended. When either
side closed the connection, that task waited, at most five seconds, for the QUIC
connection to finish its closing or draining period (RFC 9000 section 10.2): three probe
timeouts, `3 * (smoothed RTT + max(4 * RTT variation, 1 ms) + 25 ms)` here. Only then
did it wait for its stream workers and free the slot. A connection that sent GOAWAY,
after a response that did not keep the connection alive, kept its slot until the peer
closed it or its 30-second drain ended. 2.17.0 behaves the same.

To see where a closing connection's time went, builds of main and of the change with a
temporary patch that timestamps each established connection in the listener, the
HTTP/3 connection and the QUIC connection ran the overload scenario once on each
platform. The patch is not committed. Medians for main, in milliseconds per established
connection:

| Platform | Offered | Handshake | Until the response ended | Until the client's close arrived | Closing or draining | Stream workers and disposal |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Windows | 250/s | 3.3 | 0.3 | 0.3 | 149 | <0.1 |
| Windows | 1,000/s | 27 | 24 | 5.5 | 334 | <0.1 |
| Windows | 2,000/s | 24 | 16 | 6.6 | 310 | <0.1 |
| Windows | 4,000/s | 24 | 16 | 8.9 | 320 | <0.1 |
| Linux | 250/s | 5.9 | 0.5 | 0.4 | 97 | <0.1 |
| Linux | 1,000/s | 39 | 43 | 9.6 | 412 | <0.1 |
| Linux | 2,000/s | 31 | 13 | 5.5 | 395 | <0.1 |
| Linux | 4,000/s | 36 | 26 | 7.4 | 445 | <0.1 |

At the median the client's close arrived within 10 ms of its response, and the stream
workers and disposal took under 0.1 ms; the scenario serves no WebTransport. The closing
or draining period took the rest. It grows with the RTT the server measures, which load
inflates: its 99th percentile was 0.4 s on Windows and 2.4 s on Linux at 250 attempts
per second, and 1.1 to 2.5 s at the higher rates. In the Linux run main's slots filled
at 250 per second, and it refused 138 attempts and closed 1 after TLS.

A connection now gives its slot back as soon as it can serve no more requests and none
is still running:

- once it has closed, by sending or receiving `CONNECTION_CLOSE`, and every stream
  worker has ended;
- once it has sent GOAWAY and every request it accepted has ended, while it waits for
  the peer to close it.

The QUIC connection then finishes its closing or draining period as one of the
endpoint's 768. `Http3TransportConnection.CloseAsync` only queues the close now, and the
HTTP/3 connection waits for the period through `WaitClosedAsync`, after its stream
workers rather than before them. A handler that outlives its response holds no slot, as
before: its request's stream worker ends once the request completes or is cancelled.

The instrumented runs, main against the change:

| Platform | Offered | Server | Connections | Slot held p50 / p99 ms | Slot held after the close p50 / p99 ms | Closing or draining p50 / p99 ms | Slots held mean / peak |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| Windows | 250/s | main | 3,750 | 153 / 535 | 149 / 435 | 149 / 439 | 42 / 106 |
| Windows | 250/s | change | 3,750 | 4 / 11 | 0 / 0 | 148 / 179 | 1 / 8 |
| Windows | 1,000/s | main | 7,284 | 466 / 1,319 | 334 / 1,052 | 336 / 1,051 | 114 / 257 |
| Windows | 1,000/s | change | 8,107 | 156 / 588 | 0 / 9 | 411 / 1,263 | 49 / 257 |
| Windows | 2,000/s | main | 6,743 | 399 / 1,555 | 310 / 1,235 | 308 / 1,239 | 221 / 257 |
| Windows | 2,000/s | change | 6,807 | 182 / 731 | 0 / 40 | 471 / 1,975 | 34 / 257 |
| Windows | 4,000/s | main | 6,572 | 416 / 2,481 | 319 / 2,113 | 320 / 2,120 | 216 / 257 |
| Windows | 4,000/s | change | 6,466 | 162 / 1,025 | 0 / 24 | 486 / 2,251 | 78 / 257 |
| Linux | 250/s | main | 3,611 | 107 / 3,254 | 97 / 2,412 | 96 / 2,414 | 79 / 257 |
| Linux | 250/s | change | 3,750 | 6 / 169 | 0 / 8 | 88 / 366 | 5 / 54 |
| Linux | 1,000/s | main | 5,138 | 587 / 2,746 | 412 / 2,173 | 399 / 2,164 | 93 / 257 |
| Linux | 1,000/s | change | 4,770 | 185 / 1,589 | 0 / 64 | 521 / 2,846 | 39 / 257 |
| Linux | 2,000/s | main | 4,715 | 541 / 2,453 | 395 / 2,017 | 378 / 2,019 | 179 / 257 |
| Linux | 2,000/s | change | 4,644 | 107 / 2,186 | 0 / 14 | 446 / 4,028 | 68 / 253 |
| Linux | 4,000/s | main | 4,521 | 606 / 3,089 | 445 / 2,457 | 437 / 2,457 | 149 / 257 |
| Linux | 4,000/s | change | 4,434 | 141 / 1,696 | 0 / 28 | 551 / 3,152 | 48 / 257 |

A slot counts here from the listener's accept loop taking the connection to its release.
The peak can pass 256 by a connection or two, because the admission check reads the count
without the lock. The change's closing periods are longer under load: it serves more
connections at once, and the server measured higher RTTs, a median smoothed RTT of 28
against 20 ms on Windows and 39 against 29 ms on Linux at 1,000 per second. Those periods
no longer hold slots. In the change's instrumented Windows run a python process used
about 6 CPUs at 2,000 and 4,000 per second; the server still had 0.4 of its two CPUs, as
in the other runs.

The development host's self-test runs the actual listener and HTTP/3 connection over an
in-memory transport whose closing period the test ends
(`ProductionListenerSlotTests`). A connection the peer closes frees its slot while its
transport still drains, and only once; it keeps the slot while a stream worker still
runs; a connection that sent GOAWAY keeps the slot while an accepted request runs,
frees it when that request ends before the peer closes, and refuses new requests. Four
mutations of the release points each fail exactly their matching case: no release after
the workers, a release before them, no release after the drain, and a release when
GOAWAY is sent.
`Http3ListenerTest.ADrainedConnectionFreesItsSlotBeforeThePeerCloses` does the GOAWAY
case on the wire with the .NET client, reading the GOAWAY from the server's control
stream, and fails without the drain's release.

The overload scenario then ran without the patch on the same hosts, as above: main
`19ccc261` and the change built on it, the change's load benchmark the client for both,
two rounds each in alternating order. Each cell is round 1 / round 2.

Windows:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 250/s | main | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 7 / 7 | - / - |
| 250/s | change | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 7 / 7 | - / - |
| 1,000/s | main | 14,479 / 15,000 | 6,416 / 7,098 | 7,980 / 7,770 | 83 / 132 | 0 / 0 | 521 / 0 | 32 / 29 | 7 / 5 |
| 1,000/s | change | 8,583 / 9,016 | 7,915 / 8,407 | 668 / 608 | 0 / 1 | 0 / 0 | 6,417 / 5,984 | 46 / 41 | 11 / 10 |
| 2,000/s | main | 17,884 / 23,729 | 7,209 / 6,849 | 10,606 / 16,729 | 69 / 151 | 0 / 0 | 12,116 / 6,271 | 33 / 29 | 9 / 7 |
| 2,000/s | change | 10,457 / 10,865 | 7,433 / 7,978 | 3,024 / 2,885 | 0 / 2 | 0 / 0 | 19,543 / 19,135 | 42 / 41 | 8 / 7 |
| 4,000/s | main | 19,640 / 26,971 | 6,365 / 6,661 | 13,238 / 20,259 | 37 / 51 | 0 / 0 | 40,360 / 33,029 | 41 / 32 | 10 / 9 |
| 4,000/s | change | 12,858 / 8,123 | 7,283 / 7,357 | 5,575 / 731 | 0 / 0 | 0 / 35 | 47,142 / 51,877 | 43 / 35 | 11 / 10 |

Linux:

| Offered | Server | Attempts | Completed | Refused before TLS | Closed after TLS | Timed out | Skipped at cap | Handshake p50 ms | Refusal median ms |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 250/s | main | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 6 / 6 | - / - |
| 250/s | change | 3,750 / 3,750 | 3,750 / 3,750 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 11 / 6 | - / - |
| 1,000/s | main | 10,706 / 11,810 | 4,674 / 4,873 | 6,028 / 6,927 | 4 / 10 | 0 / 0 | 4,294 / 3,190 | 53 / 49 | 4 / 4 |
| 1,000/s | change | 5,438 / 5,125 | 5,246 / 4,895 | 189 / 230 | 0 / 0 | 3 / 0 | 9,562 / 9,875 | 66 / 61 | 16 / 282 |
| 2,000/s | main | 9,046 / 14,325 | 4,411 / 4,540 | 4,635 / 9,784 | 0 / 1 | 0 / 0 | 20,954 / 15,675 | 51 / 55 | 5 / 4 |
| 2,000/s | change | 4,475 / 5,149 | 4,390 / 4,319 | 12 / 814 | 0 / 0 | 73 / 16 | 25,525 / 24,851 | 51 / 65 | 134 / 11 |
| 4,000/s | main | 24,116 / 29,207 | 4,703 / 4,261 | 19,407 / 24,943 | 6 / 3 | 0 / 0 | 35,884 / 30,793 | 62 / 41 | 4 / 4 |
| 4,000/s | change | 5,703 / 4,940 | 5,084 / 4,531 | 617 / 321 | 0 / 0 | 2 / 88 | 54,297 / 55,060 | 65 / 55 | 4 / 40 |

On Windows the change completed 1.18 to 1.23 times as many connections as main's
matching round at 1,000 attempts per second, 1.03 to 1.16 times at 2,000 and 1.10 to
1.14 times at 4,000, and closed at most 2 attempts after their handshake where main
closed 37 to 151. On Linux it completed 0.95 to 1.12 times as many and closed none after
TLS, where main closed up to 10. The freed slots admit attempts that main refused, so the
change refused far fewer; with more of its attempts still in progress, its client reached
the 512 cap more often and started fewer.

Both servers timed out some handshakes on Linux under overload. Across the overloaded
phases of these runs, the instrumented runs and a third round of main, main timed out 0
to 81 per phase, 164 in 12 phases, and the change 0 to 88, 223 in 9. On Windows main
timed out none and the change 35, in one phase. At 2,000 per second on Linux the change's
server used 1.0 to 1.2 of its two CPUs where main's used 1.3 to 1.5, for about as many
completed connections; the single socket loop remains the first suspect.

The machine was in use. Outside the server and client the Windows host was busy 1.6 to
5.6 CPUs per phase; readable processes, builds by other sessions among them, accounted for
at most 2.3, and the rest belongs to processes the tool cannot read, lsass among them,
which was using 0.66 CPUs when sampled during a run. For Linux runs the host's busy CPUs
above the VM's own were 0.1 to 3.8 per phase. Two runs of the change were set aside: a
Windows round in which a node process used 11.7 CPUs at 2,000 per second and the server
got 0.05 to 0.23 of its two CPUs, repeated afterwards, and a third Linux round with 5 to
9 CPUs of outside load in every phase. The third round of main, with up to 4.6 CPUs of
outside load, counts only in the timeouts above.

## Retry and the supported TLS profile

[#520](https://github.com/WilliamSmithEdward/embedio-neo/issues/520) asked for Retry,
resumption, 0-RTT and non-exportable keys on Linux and macOS. Retry is now an option;
the other three stay outside the supported profile, for the reasons below and in the
[HTTP/3 guide](../guides/http3.md#address-validation).

### Retry

`RequireHttp3AddressValidation`, or `WithHttp3AddressValidation()`, turns it on for
`EmbedIOHttp3` and `EmbedIOCombined`.

- The dispatcher answers an Initial without a token with a Retry (RFC 9000 section
  17.2.5) and creates no connection. The Retry's source ID is 16 random bytes, the
  length of the server's connection IDs, and its integrity tag follows RFC 9001
  section 5.8; a test reproduces the RFC 9001 appendix A.4 vector.
- The token is AES-256-GCM under a key each dispatcher draws at startup, with a
  nonce made of a random prefix and a counter, so no nonce repeats under the key. It
  seals the time it was issued, the first Initial's destination ID and the Retry's
  source ID, with the client's IP address as associated data, and lives ten seconds.
- An Initial whose token redeems, from the same address and sent to the Retry's
  source ID, starts the connection. Its transport parameters carry
  `original_destination_connection_id` and `retry_source_connection_id` (RFC 9000
  section 7.3), and its address counts as validated, which lifts the
  anti-amplification limit (section 8.1). A first version set only the connection's
  flag, while the initial path kept its own limit until the handshake; a development
  host test now sends a 9,000-byte first flight and checks that only a Retry lets it
  past 3,600 bytes.
- A token the dispatcher sealed that is expired or sent to another destination ID
  gets an Initial with `CONNECTION_CLOSE(INVALID_TOKEN)` (section 8.1.3) and no
  connection, since the client will not accept a second Retry. A token it cannot
  open, such as another server's or one from a different address, is treated as
  missing and gets a Retry.
- Retries are budgeted at 4,096 per second per endpoint and share the refusals' queue
  limit; past them an Initial is dropped and the client retransmits it.
- Overload refusals (see [Bounded TLS work under load](#bounded-tls-work-under-load))
  come first, so an overloaded endpoint refuses rather than sends a Retry.

With address validation on, no connection starts without a redeemed token, so a
client that completes a session has handled the Retry. The .NET client (MsQuic)
completes requests through a relay that sees the server's Retry
(`Http3ListenerTest.AddressValidationRetriesBeforeTheHandshake`). The independent
quic-go peer's 15 WebTransport modes passed on Windows with address validation on
and off, and CI runs them with it on Linux and macOS (`--address-validation`).

### Left out

- Session resumption, at the time: every connection ran a full handshake and the
  server sent no tickets. [#550](https://github.com/WilliamSmithEdward/embedio-neo/issues/550)
  added it later ([TLS session resumption](#tls-session-resumption)).
- 0-RTT. It depends on resumption, and early data can be replayed (RFC 9001 section
  9.2), so it would need an application policy for which requests may be replayed and
  425 Too Early (RFC 8470) for the rest. See [0-RTT](#0-rtt).
- Keys that cannot be exported, on Linux and macOS. The TLS runtime there is the
  OpenSSL inside `EmbedIO-Neo.Quic.Native`, which receives the key exported from the
  certificate; a hardware- or store-held key never reaches it. A signing callback from
  native code into .NET on every handshake would lift this. Schannel uses the key in
  place, so Windows accepts non-exportable CNG keys.

## TLS session resumption

[#550](https://github.com/WilliamSmithEdward/embedio-neo/issues/550) asked for TLS
session resumption on both TLS paths, measured first, and for 0-RTT either as an
opt-in with replay protection or left out with the measured reason. The server now
issues a session ticket with every handshake on Windows, Linux and macOS. This is
in 2.19.0.

### Tickets and keys

- Each listener credential, one per selected certificate object, owns its ticket keys:
  up to four 80-byte keys, each a 16-byte name and 64 bytes of secret from the system's
  random number generator, kept only in memory. The newest key seals new tickets until
  it is an hour old, and every key opens tickets until it is three hours old, the hour
  plus the two-hour ticket lifetime. Handshakes copy the keys under a lock, and the
  credential's last release zeroes them.
- The keys belong to the credential, so a ticket resumes only under the certificate
  object that issued it. A publication drops the credentials and their keys, and so
  does disposing or evicting a certificate. A certificate whose issuer chain is
  incomplete gets a credential, and keys, for each handshake, so its tickets never
  resume.
- A listener whose `ClientCertificateMode` is `Allow` or `Require` issues no tickets,
  since a resumed session would keep the first connection's client certificate
  without running the policy again.
- On Linux and macOS each connection receives the keys and the lifetime through
  `embedio_quic_tls_set_ticket_keys` (private ABI 4) before its first step, and OpenSSL
  seals with AES-256-CBC and HMAC-SHA-256 under the newest key. OpenSSL resumes a TLS
  1.3 ticket of any age, so the native callback refuses one older than the lifetime. It
  issues a new ticket on every resumption, so a client need not use a ticket twice
  (RFC 8446 appendix C.4).
- On Windows the credential gives Schannel its key list with
  `SetCredentialsAttributesW(SECPKG_ATTR_SESSION_TICKET_KEYS)` whenever the keys change,
  accepts with `ASC_REQ_SESSION_TICKET` and no longer sets
  `SCH_CRED_DISABLE_RECONNECTS`. Schannel announces a ten-hour lifetime and ignores
  `dwSessionLifespan`, so a ticket resumes until its key retires, two to three hours
  after it was issued.
- The OpenSSL path now selects the certificate before OpenSSL reads the ClientHello,
  with the managed ClientHello reader the Schannel path already used. The adapter
  installs the credential and its keys and only then hands over the message, so OpenSSL
  opens a ticket only with the keys of the certificate already chosen. As on Windows, a
  ClientHello after a HelloRetryRequest must keep the server name and transport
  parameters. ABI 4 drops `embedio_quic_tls_server_name` and the step result that
  paused the handshake for selection.

### Measurement

The development host's production listener (`serve-webserver`) served sequential
connections from aioquic 1.6.1, each a handshake, one GET and a close. Each of 12 host
sessions ran 15 warm-up pairs and measured two seconds of idle CPU, then one block of
40 full handshakes and one of 40 resumed ones, in alternating order. On Windows the
server CPU is the process's cycle time, read from the system process list and counted
at the time-stamp counter's rate, plus that of LSASS, where Schannel runs the handshake
and CNG signs with the non-exportable key. On Linux it is the server threads' run time
from `schedstat`. Process times advance in 15.6 ms steps on Windows and 10 ms on Linux,
too coarse for these blocks. The idle CPU is subtracted from each block. Tiered
compilation was off in the host: each session is a new process, and with it on the
first block of every session carried about 22 ms of JIT work per connection.

AMD Ryzen 7 9800X3D (8 cores, 16 threads), .NET 10.0.13, loopback. Windows 11 build
26300 with the fixtures' non-exportable CNG keys, RSA-1024 and ECDSA P-256; Linux in
Docker under WSL2 with four CPUs, the ABI 4 library and an exportable RSA-2048 key.
Medians of the 12 blocks of each kind:

| Platform, key | Handshake | Server CPU per connection | LSASS | Server bytes before the response | Client time to the response |
| --- | --- | ---: | ---: | ---: | ---: |
| Windows, RSA-1024 | Full | 3.44 ms | 0.48 ms | 3,109 | 4.97 ms |
| Windows, RSA-1024 | Resumed | 3.47 ms | 0.50 ms | 2,404 | 4.51 ms |
| Windows, ECDSA P-256 | Full | 3.57 ms | 0.63 ms | 2,855 | 5.18 ms |
| Windows, ECDSA P-256 | Resumed | 3.44 ms | 0.57 ms | 2,404 | 4.57 ms |
| Linux, RSA-2048 | Full | 8.13 ms | | 3,650 | 6.68 ms |
| Linux, RSA-2048 | Resumed | 7.18 ms | | 2,141 | 3.58 ms |

- On Linux a resumed connection cost the server 0.95 ms less, 12 percent, and the
  client had its response 3.1 ms sooner.
- On Windows neither the server's nor LSASS's CPU changed measurably; LSASS's own
  background work spread its blocks from 0 to 2.8 ms per connection. On one thread
  .NET's CNG signs in 57 us with RSA-1024 and 119 us with ECDSA P-256 on this machine,
  1.5 to 3 percent of a connection, so these keys leave little to save. A resumed
  handshake skips 290 us with RSA-2048, 917 us with RSA-3072 and 2.07 ms with
  RSA-4096. The response came 0.46 ms sooner with RSA-1024 and 0.61 ms with ECDSA.
- A resumed handshake sends no certificate, so the server sent 450 to 1,509 fewer bytes
  before the response.
- The client is aioquic in Python, and its own certificate checks are part of its times.

### Validation

- `verify_session_tickets.py` runs the actual HTTP/3 listener with the SNI fixture and
  keys that rotate every two seconds for tickets that live six. Its 12 cases: a full
  handshake; a resumed one; that ticket presented under the other certificate, refused;
  the other certificate's own full and resumed handshakes; its ticket presented for the
  first server name, refused; a full handshake that rotates the keys; a ticket from the
  previous key, resumed; a ticket past its lifetime, refused on Linux and macOS and
  resumed on Windows; a ticket from a retired key, refused; and a ticket from before a
  publication, refused after it. Every handshake must yield a ticket that allows no
  early data, and the listener must abort nothing, release its credentials and rebind.
  It passed on Windows and in the Linux image. CI runs it in the Windows Schannel script
  and through the package consumers on all six runtime identifiers.
- `verify_tls_listener.py` now requires one ticket per connection, five in all.
  `verify_client_certificate.py` requires one ticket in its two cases where the server
  asks for no client certificate and none in the rest.
- `SessionTicketKeyTests` in `self-test` checks rotation and retirement on a manual
  clock, the constructor's bounds, disposal with the credential's last release and
  concurrent copies while the keys rotate. Three deliberate defects each failed it:
  retiring a key at the lifetime instead of the period plus the lifetime, never
  rotating, and keys that outlive their credential.
- The native ownership test adds the ticket-key rules (a null owner or key list, an
  empty, partial or five-key list, a lifetime of zero or past seven days, a second call
  and a call after the first step) and passed under ASan, UBSan and LeakSanitizer.
  The 28 native unit tests passed with one platform skip, the audit requiring the ABI 4
  symbol version and export inventory.
- The NUnit HTTP/3, QUIC, WebTransport, certificate and TLS subset, 915 cases: Windows
  912 passed and 3 platform skips; Linux, with the ABI 4 library, 914 passed and 1
  skip. `self-test`, `verify_tls_listener.py` and `verify_sni.py` passed on both.
- `verify_client_certificate.py` with its full profile passed 23 cases on Windows and 24
  on Linux, the latter with CI's linux-x64 build of the library, along with the
  256-case `fuzz_client_certificate.py` run and `verify_server_chain.py`. The first CI
  run stopped in it: the verifier still required that no case receive a ticket, and the
  earlier local runs had used only its `ci` profile, which has no case without a
  client-certificate request.

### 0-RTT

0-RTT is left out, and the tickets allow no early data, which the verifier checks. The
measurements above show what resumption alone brings: the certificate's bytes, the
signature and, on Linux, 12 percent of the server's CPU per connection. 0-RTT would add
only the round trip a resumed client waits before its first request, and only where it
can run:

- Schannel's QUIC interface defines no early-data traffic secret, and MsQuic's Schannel
  layer marks 0-RTT unsupported (`src/platform/tls_schannel.c`), so Windows, the lead
  platform, could not offer it.
- On Linux and macOS it would need early data through OpenSSL's QUIC TLS interface,
  0-RTT packet protection in EmbedIO.Quic, the transport parameters remembered in each
  ticket and never reduced (RFC 9000 section 7.4.1), and protection against replayed
  tickets across the listener (RFC 8446 section 8).
- Early data can be replayed (RFC 9001 section 9.2), so each application would have to
  say which requests may be replayed and answer the rest with 425 Too Early (RFC 8470).
  That needs a new public option, which this change does not add.

### Left out

- Ticket keys live in one process. After a restart, or at another instance behind a
  load balancer, a ticket does not open and the client runs a full handshake. No API
  sets or shares the keys.
- TCP listeners use `SslStream`, which handles its own resumption; this change does not
  touch them.

## Startup, warm and tail latency

These are the first latency results for EmbedIO.Quic ([#522](https://github.com/WilliamSmithEdward/embedio-neo/issues/522)).
[HTTP/3 request path performance](http3-performance.md) measured the earlier
`System.Net.Quic` provider. Like that record, these are loopback development
measurements that locate costs, not a public benchmark.

### Method

- Source `a99319de`, whose product code is 2.17.0's, plus this change's `cold` mode in
  `test/EmbedIO.LoadBenchmark`, which touches only the harness.
- Windows: AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300 on
  the Ultimate Performance power scheme, .NET 10.0.13. Server on logical CPUs 0-3,
  client on 4-11.
- Linux: the same machine, in a container on Docker Desktop's WSL 2 VM (kernel
  6.18.33.2) from the SDK 10.0.402 image `sha256:747cbade...`, limited to 8 CPUs.
  Server on 0-3, client on 4-7. MsQuic 2.6.2 serves the .NET client and Kestrel, and
  EmbedIO.Quic uses main CI's TLS library (SHA-256 `dc116366...`). A VM's loopback
  is slower than bare metal, so compare Linux figures with each other, not with
  Windows.
- Three servers: EmbedIO as the benchmark builds it (JIT), the same runner published
  ReadyToRun (`-p:PublishReadyToRun=true`), and Kestrel 10.0.13 from the ASP.NET Core
  shared framework, which ships precompiled. HTTP/1.1, HTTP/1.1 over TLS and HTTP/2
  over TLS run beside HTTP/3.
- Every run held the benchmark lock under `scripts/guarded_load_benchmark.py`, and
  every response was validated.

### Cold start

`cold` starts a new server process for each measurement; one client process, warmed
by an excluded first iteration, sends 32 requests on a new connection and one on a
second connection. 30 measured iterations per engine and protocol, engine order
alternating. No measurement failed (372 per platform). Medians in milliseconds, with
the 90th percentile in brackets.

| Platform | Server | Listening after process start | First HTTP/3 request | Second HTTP/3 connection | First HTTP/2 over TLS request |
| --- | --- | --- | --- | --- | --- |
| Windows | EmbedIO | 94.5 [105.0] | 106.4 [112.1] | 7.2 [7.9] | 65.5 [68.3] |
| Windows | EmbedIO, ReadyToRun | 82.4 [86.8] | 47.8 [50.8] | 6.9 [7.6] | 35.3 [38.3] |
| Windows | Kestrel | 112.1 [115.1] | 34.6 [36.8] | 3.8 [4.2] | 34.2 [39.5] |
| Linux | EmbedIO | 84.5 [91.4] | 141.5 [148.8] | 9.2 [12.1] | 84.7 [92.6] |
| Linux | EmbedIO, ReadyToRun | 71.5 [77.9] | 86.6 [93.8] | 9.0 [11.7] | 55.9 [60.5] |
| Linux | Kestrel | 105.8 [118.3] | 80.6 [84.5] | 4.1 [9.1] | 30.4 [32.1] |

"Listening" is the process's own uptime when its listener had started; the HTTP/3
column's server. Linux gives a process's start time in 10 ms clock ticks, which adds
up to 10 ms to each Linux listening figure. The full tables, with HTTP/1.1, the
second request, warm medians and the machine's busy CPUs, are in the run
directories' `summary.md`.

- EmbedIO starts listening sooner than Kestrel in every case, by 18 to 34 ms on
  HTTP/3 and up to 55 ms on TLS over TCP.
- JIT compilation is most of EmbedIO's first request. ReadyToRun cuts the first
  HTTP/3 request from 106 to 48 ms on Windows and from 142 to 87 ms on Linux, and the
  first HTTP/1.1 request from 27 to 10 ms on both. Counted from process start,
  listening plus the first HTTP/3 response, ReadyToRun EmbedIO answers sooner than
  Kestrel: 130 against 147 ms on Windows and 158 against 186 ms on Linux.
- The rest of the HTTP/3 gap is per connection. A second connection to a warm server
  costs EmbedIO 6.9 to 7.2 ms on Windows and 9.0 to 9.2 ms on Linux, against 3.8 and
  4.1 ms for Kestrel, with or without ReadyToRun; over TCP the two are within 0.3 ms.
  EmbedIO.Quic sets up TLS state for each connection (see below).

### Warm steady state and tail

The ordinary `run` mode, three rounds with engine order alternating, eight HTTP/3
and eight HTTP/2-over-TLS scenarios: 13-byte responses on 64 single-stream
connections (`c64x1`) and on 8 connections with 32 concurrent streams each (`c8x32`),
the same on a route that reads request properties (`inspect`), 1 MiB responses
written at once (`large1m`) and flushed every 16 KiB (`stream1m`), 1 MiB uploads, and
a new connection for every request (`churn`). Medians of the kept rounds. A round
with more than 7 busy CPUs outside the server and client was set aside, 14 of 144 on
Windows and 17 of 144 on Linux, while other work ran on the machine. The benchmark
cannot see the host from inside the VM, so for Linux that count is the Windows host's
busy CPUs, which include the VM's, less the server's and client's own CPU during each
round's measurement. No measurement failed.

Windows, server on CPUs 0-3:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | EmbedIO | 25,587 | 137 us | 96 KB | 2.41 | 2.82 | 3.17 | 11.1 |
| | EmbedIO, ReadyToRun | 25,264 | 140 us | 96 KB | 2.43 | 2.84 | 3.25 | 11.2 |
| | Kestrel | 116,137 | 69 us | 2.5 KB | 0.36 | 1.37 | 3.53 | 15.6 |
| h3 small c8x32 | EmbedIO | 32,835 | 117 us | 105 KB | 6.86 | 14.95 | 18.64 | 27.9 |
| | EmbedIO, ReadyToRun | 32,890 | 116 us | 106 KB | 6.86 | 14.95 | 18.23 | 28.7 |
| | Kestrel | 316,826 | 20 us | 2.8 KB | 0.55 | 1.59 | 8.70 | 16.6 |
| h3 large1m c4x4 | EmbedIO | 78 | 42 ms | 9.9 MB | 156 | 564 | 682 | 786 |
| | Kestrel | 1,173 | 4.9 ms | 1.6 MB | 13.3 | 18.4 | 28.3 | 44.7 |
| h3 upload1m c4x4 | EmbedIO | 169 | 21 ms | 11.6 MB | 88.5 | 149 | 192 | 301 |
| | Kestrel | 1,377 | 3.5 ms | 17 KB | 10.4 | 17.6 | 19.6 | 23.2 |
| h3 churn c16 | EmbedIO | 1,044 | 1.05 ms | 825 KB | 14.3 | 24.4 | 30.7 | 37.3 |
| | Kestrel | 2,147 | 0.93 ms | 134 KB | 6.2 | 15.3 | 20.1 | 25.1 |
| h2 TLS small c8x32 | EmbedIO | 475,193 | 8.0 us | 9.3 KB | 0.46 | 1.16 | 1.51 | 3.6 |
| | Kestrel | 887,412 | 4.2 us | 39 B | 0.27 | 0.42 | 0.80 | 2.3 |
| h2 TLS large1m c4x4 | EmbedIO | 2,504 | 1.6 ms | 47 KB | 6.35 | 9.01 | 10.9 | 14.5 |
| | Kestrel | 3,510 | 1.1 ms | 9.8 KB | 4.51 | 5.84 | 7.48 | 16.8 |

Linux, server on CPUs 0-3 of the container's eight:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | EmbedIO | 13,979 | 213 us | 97 KB | 4.20 | 8.40 | 10.55 | 13.7 |
| | EmbedIO, ReadyToRun | 13,511 | 223 us | 97 KB | 4.35 | 8.55 | 11.67 | 22.2 |
| | Kestrel | 60,535 | 66 us | 2.5 KB | 0.87 | 2.56 | 4.20 | 7.7 |
| h3 small c8x32 | EmbedIO | 30,485 | 103 us | 93 KB | 7.42 | 17.10 | 23.55 | 31.1 |
| | EmbedIO, ReadyToRun | 30,425 | 103 us | 93 KB | 7.42 | 17.41 | 24.78 | 35.6 |
| | Kestrel | 159,784 | 21 us | 3.1 KB | 1.33 | 3.64 | 5.84 | 10.1 |
| h3 large1m c4x4 | EmbedIO | 39 | 82 ms | 7.0 MB | 305 | 1,101 | 1,271 | 1,634 |
| | Kestrel | 1,368 | 3.3 ms | 1.9 MB | 11.6 | 17.0 | 21.7 | 37.3 |
| h3 upload1m c4x4 | EmbedIO | 89 | 40 ms | 11.0 MB | 158 | 334 | 541 | 947 |
| | Kestrel | 2,275 | 1.7 ms | 9.8 KB | 6.25 | 13.5 | 21.1 | 26.6 |
| h3 churn c16 | EmbedIO | 647 | 5.5 ms | 611 KB | 24.2 | 37.7 | 43.8 | 50.8 |
| | Kestrel | 1,683 | 1.4 ms | 134 KB | 9.0 | 15.8 | 25.0 | 50.0 |
| h2 TLS small c8x32 | EmbedIO | 347,765 | 9.7 us | 9.2 KB | 0.61 | 1.55 | 2.59 | 5.4 |
| | Kestrel | 500,637 | 5.5 us | 41 B | 0.42 | 1.10 | 1.82 | 3.3 |
| h2 TLS large1m c4x4 | EmbedIO | 3,172 | 1.2 ms | 45 KB | 4.69 | 8.45 | 11.8 | 22.0 |
| | Kestrel | 3,285 | 1.1 ms | 13 KB | 4.69 | 7.48 | 9.83 | 14.0 |

ReadyToRun rows for the other scenarios match the JIT build within the spread
between rounds, except as noted below; the full tables are in the run directories.

- ReadyToRun costs nothing warm on Windows. Its HTTP/3 and HTTP/2 throughput, CPU
  and tails match the JIT build in every scenario, so its cold-start gain comes with
  no warm regression. On Linux, where rounds vary more, it matched everywhere but
  HTTP/2-over-TLS connection churn: its three kept rounds served 3,233 to 4,108
  connections per second against the JIT build's 4,314 and 4,346. Its HTTP/3 churn
  matched, and Windows showed no such gap; more rounds would tell whether it is real.
- Kestrel's HTTP/3 server used up to twice the four CPUs it was given on Windows and
  up to 1.2 times them on Linux: MsQuic's threads do not stay within the process's
  CPU set. Its HTTP/3 requests per second are therefore not a same-CPU comparison;
  its CPU per request is.
- Small HTTP/3 requests cost EmbedIO.Quic 2 to 6 times Kestrel's CPU on Windows and
  3 to 5 times on Linux, with about 100 KB of allocation each against 2.5 to 3.1 KB.
  On 64 single-stream connections the Windows tails are close (p99 3.2 against
  3.5 ms), since each client waits for its response; on Linux the p99 is 10.6 against
  4.2 ms. With 32 streams per connection EmbedIO's Windows p50 is 6.9 ms against
  0.55 ms.
- 1 MiB HTTP/3 transfers are the widest gap: 6 to 10 times the CPU per response on
  Windows and 24 to 33 times on Linux, with 7 to 11 bytes allocated per byte sent or
  received.
- New HTTP/3 connections run at half Kestrel's rate on Windows while the server uses
  28% of its CPUs, so the per-connection cost below, not CPU, limits them. On Linux
  they run at 38% of Kestrel's rate and each costs 5.5 ms of server CPU against
  1.4 ms, most of it the TLS setup measured below, which runs in the server process.
- HTTP/2 over TLS stays close to Kestrel: 1.1 to 1.9 times its CPU per request on
  Windows and 0.8 to 1.8 times on Linux. Its largest gap is allocation, 9 KB per
  small request against about 40 bytes.

### Per-connection TLS setup

EmbedIO.Quic prepares TLS for every connection from scratch, after it selects the
connection's certificate. A probe timed those steps alone, with the benchmark's kind
of certificate (self-signed, exportable key), 2,000 times each on one thread:

| Platform | Step | Median | 90th percentile |
| --- | --- | ---: | ---: |
| Windows | Copy the certificate and acquire a Schannel credential (`AcquireCredentialsHandleW`) | 0.81 ms | 0.90 ms |
| Linux | Export the certificate and key as PKCS#12 | 2.95 ms | 3.23 ms |
| Linux | Create the OpenSSL state and load the PKCS#12 into it | 0.68 ms | 0.73 ms |
| Linux | Build the outgoing chain (`X509Chain.Build`) | 0.15 ms | 0.21 ms |

The times are elapsed time on the calling thread. They make up 0.8 ms of the 3.1 to
3.4 ms per-connection gap to Kestrel on Windows and 3.8 ms of the 4.9 to 5.1 ms on
Linux. MsQuic loads its credential once per listener configuration and reuses it.
Caching the credential and the exported key per certificate would take these steps
out of every handshake after the first; it is not part of this change.
[Per-certificate TLS credentials](#per-certificate-tls-credentials) below does it.

## Per-certificate TLS credentials

[Per-connection TLS setup](#per-connection-tls-setup) above found TLS work that
EmbedIO.Quic repeated for every HTTP/3 connection after selecting its certificate: a
certificate copy and a Schannel credential (`AcquireCredentialsHandleW`) on Windows, and
on Linux and macOS a PKCS#12 export of the certificate and key, its parse into new
OpenSSL state and the outgoing chain (`X509Chain.Build`). MsQuic loads a credential once
per listener configuration and reuses it. EmbedIO.Quic now does the same per
certificate.

### The credential cache

Each HTTP/3 listener binding owns an `EmbedIOQuicTlsFactory`, which creates the TLS session
of each new connection and holds a `ServerCredentialCache` of the credentials those
sessions share.

- The key is the certificate object the handshake selected, the source's current
  certificate or the one an SNI selector returned, compared by reference. A listener's TLS
  policy is fixed, so the credential's client-certificate flag is too. Two objects holding
  the same certificate never share a credential. A selector that returns the same instance
  for a host name gets the reuse; one that loads a new object per handshake prepares a
  credential each time, as before.
- A handshake selects its certificate exactly as before, after the complete ClientHello,
  then takes the cached credential or prepares one. The cache and every session that uses
  a credential hold a reference and release it once; the last release frees the native
  handle.
- `ServerCertificateSource.Publish` increments the source's version, and the next
  handshake drops every cached credential. Sessions that hold one keep it until their
  connection closes. A handshake that captured its fallback before a publication gets a
  credential of its own, so the replaced certificate does not return to the cache; the
  factory reads the version before the certificate, so a publication between the two
  reads marks the handshake as older.
- A certificate the application has disposed can no longer be selected, so its entry goes
  at the next handshake. That covers rotation inside an SNI selector, which leaves the
  source's version alone, when the application disposes what it replaced.
- The cache holds at most 32 certificates and drops the least recently used. The binding
  disposes the factory after its connections, which frees the rest.
- A credential prepared while the certificate stores could not complete the issuer chain
  is not cached: that handshake uses it and the next one prepares again, as every
  handshake did before. Otherwise a transient failure to find an issuer, or one installed
  later, would leave the partial chain in every handshake until a publication. On Linux
  and macOS the chain the credential sends decides this. On Windows, where Schannel builds
  the chain, a managed `X509Chain` build at preparation does, without downloading issuers;
  a self-issued certificate skips it.
- On Windows a credential is the certificate copy and its Schannel credential handle.
  Sessions use the handle value and still create their own security context.
- On Linux and macOS private native ABI 3 adds a credential object: the key and leaf
  parsed once from the PKCS#12 export, and the issuer chain the connection sends. Managed
  code clears the exported bytes as soon as the parse returns. A connection installs the
  credential by reference (`SSL_use_certificate`, `SSL_use_PrivateKey`, `SSL_set1_chain`)
  and holds its own OpenSSL references, so the session releases its lease at once. ABI 2's
  per-connection `set_credential` and `add_chain` are gone; an older library fails the
  preflight check.
- Each connection still creates its OpenSSL state (`SSL_CTX_new` and `SSL_new`), which the
  probe below measured at 0.05 ms. Sharing one `SSL_CTX` per listener would save that and
  would need a further ABI change.

What did not change: SNI selection runs once per handshake with the original name,
client-certificate policy validates every handshake on its own security context, and the
cipher profiles are the same. There is still no resumption: Schannel credentials keep
`SCH_CRED_DISABLE_RECONNECTS`, and each OpenSSL connection keeps its own `SSL_CTX` with the
session cache off and no tickets. `verify_tls_listener.py` and
`verify_client_certificate.py` now record session tickets and require none; the second
runs in Windows CI. An aioquic server that does issue tickets makes the same client
handler record one, so the check is not vacuous.

### Per-connection setup

A probe timed the per-connection TLS work on one thread, 2,000 times per step after 50
warm-up runs, with the load benchmark's kind of certificate (ECDSA P-256, self-signed,
exportable key), in two rounds in reversed order. "First flight" is the production path
for a new connection: the listener's factory creates the session, it reads a complete
ClientHello, selects the certificate, takes or prepares the credential and produces the
server's first flight (ServerHello to Finished), and the session is disposed. One
ClientHello is reused, so the client's key generation is not timed. Windows ran on the
host, Linux in the measurement image on CPUs 0-3, as #522's probe did; #522's probe of
main's separate steps ran beside it. Medians, both rounds:

| Platform | Per new connection | Main | This change |
| --- | --- | ---: | ---: |
| Windows | First flight | 1.58 ms | 0.41 ms |
| Windows | Certificate copy and Schannel credential | 0.81 ms | cached (0.81 to 0.83 ms once per certificate) |
| Linux | First flight | 4.06 to 4.11 ms | 0.16 to 0.19 ms |
| Linux | PKCS#12 export | 2.92 to 3.04 ms | cached |
| Linux | OpenSSL state and its credential | 0.65 to 0.68 ms, PKCS#12 parsed in | 0.05 ms, cached credential installed |
| Linux | Outgoing chain (`X509Chain.Build`) | 0.14 to 0.17 ms | cached |

On Linux a credential costs 3.81 ms once per certificate: the export, the parse and the
chain. Taking a cached credential and releasing it costs 0.1 microseconds or less on both
platforms, and installing it in a connection's OpenSSL state adds under 2 microseconds to
that state's 0.05 ms.

### Second connection

The load benchmark's `cold` mode ([Cold start](#cold-start)), for HTTP/3 only, on the
machine and CPUs of [Method](#method): a fresh server process per measurement and one
long-lived client, 32 requests on a new connection, then one on a second connection. 30
measured iterations per server, in alternating order; the change against main
`19ccc261` and Kestrel 10.0.13. On Linux each EmbedIO runner used the TLS library built
from its own source. Medians, with the 90th percentile in brackets, in milliseconds:

| Platform | Server | First HTTP/3 request | Second HTTP/3 connection |
| --- | --- | ---: | ---: |
| Windows | EmbedIO, main | 107.6 [113.1] | 7.36 [7.88] |
| Windows | EmbedIO, this change | 109.9 [112.9] | 6.07 [6.66] |
| Windows | Kestrel | 34.8 [37.2] | 4.01 [4.75] |
| Linux | EmbedIO, main | 141.6 [147.2] | 9.03 [11.32] |
| Linux | EmbedIO, this change | 142.2 [149.3] | 5.76 [6.68] |
| Linux | Kestrel | 82.7 [86.7] | 3.66 [8.53] |

- A second connection costs 1.3 ms less on Windows and 3.3 ms less on Linux, and its 90th
  percentile 1.2 and 4.6 ms less. The gap to Kestrel falls from 3.35 to 2.06 ms on
  Windows and from 5.37 to 2.10 ms on Linux. Earlier runs on the previous commit measured
  6.12 against 7.39 ms on Windows and 5.76 against 8.63 ms on Linux.
- Requests on a warm connection are unchanged. The second half of the first
  connection's 32 requests has a median of 0.18 ms on Windows and 0.51 ms on Linux for
  both, and its second request is within 0.1 ms.
- The first request of a fresh Windows process is 2.3 ms slower, in both Windows runs.
  Timing only the first handshake of 20 fresh processes per side puts the difference
  there: 16.4 against 14.3 ms. The self-issued check accounts for 0.05 ms of that; the
  rest is not attributed, most likely the first compilation of the new code on that path.
  Linux differs by 0.5 ms. A first version of the Windows chain check built an
  `X509Chain` at every first preparation. The first build in a process took 11.7 to
  16.1 ms in three fresh processes, and it added 10 ms to the first request (118.0
  against 107.7 ms) until self-issued certificates skipped it.
- The host's busy CPUs, sampled every second, averaged 2.7 to 3.6 during each run, the
  benchmark's own processes included, with one-second peaks to 11.4. One Windows repeat
  ran during 13 to 16 busy CPUs of outside load; it was discarded and run again.

### Validation

New and changed checks:

- The development host's `self-test` runs `ServerCredentialCacheTests` with counted
  stand-in credentials: sharing by object, publication, handshakes older than a
  publication, uncacheable credentials, disposed certificates, eviction order, disposal
  (also racing a preparation, and a failed preparation), 4,000 leases on eight threads
  while the source publishes, and chain completeness.
- `schannel-self-test` checks with real Schannel credentials that handshakes selecting one
  certificate share its credential, by fallback and by SNI, that a publication drops the
  cache's credentials while a handshake keeps its own, that disposing the factory leaves a
  running handshake its credential, that a stopped listener refuses new handshakes, and
  that the native counts return to zero.
- The NUnit test `Http3ConnectionsShareTheCertificatesCredential` keeps four HTTP/3
  connections open through the real `WebServer` and requires them to hold at most one more
  native credential, and none after the server stops, on all three platforms.
- The production listener host behind the publication, SNI and client-certificate
  verifiers, and the package consumer on all six runtime identifiers, require zero native
  credentials after the listener stops. `ServerChainTests` requires the partial-chain
  profile to cache nothing and the other four one credential.
- The native ownership test now covers a credential with 32 issuers that four connections
  install and that is freed before them, a retired credential and a missing one. Under
  ASan, UBSan and LeakSanitizer it ran 8,192 connection and 3,072 credential lifecycles and
  retained nothing. `test_audit.py` requires the ABI 3 symbol version and export inventory.

Five deliberate defects, each built and run: no sharing, no drop on publication, no
eviction, caching handshakes older than a publication, and a factory that keeps its
credentials. Each failed `self-test` or `schannel-self-test`. A factory that never
disposes its cache does not compile (CA2213).

Local results on commit `04c82d03`; the later commits change only the cache's entry type,
formatting and documentation, and merge main:

- NUnit HTTP/3, WebTransport and certificate-rotation subset, 856 cases: Windows 854
  passed and 2 platform skips; Linux, in the measurement image with the change's native
  library, 855 passed and 1 skip.
- `scripts/run-quic-schannel.ps1`, the Windows CI script, passed in full, the six
  client-certificate cases with no session tickets, and `verify_tls_listener.py` passed.
- The native build's binary audit for linux-x64 and the 28 native unit tests (one
  platform skip) passed.

On `4b77cda2`, which merges main with #543's slot release, `self-test`,
`schannel-self-test`, the source-quality, formatting and both suppression gates, the
AnalyzerGuard and test discovery (7,504 cases) passed.

The Security report then failed on Semgrep parse errors, not findings. Semgrep 1.181.0
could not parse the two `Credential` classes' primary constructors or `STACK_OF(X509)` in
`native.c`'s credential struct, so those three files went unscanned. The classes now have
explicit constructors, and the struct names `struct stack_st_X509`, which the macro
expands to. The pinned image with CI's rule packs parses all three files without errors,
and the build, source-quality, formatting and suppression gates and the AnalyzerGuard
passed again.

A private adversarial review of the cached key material, with an independent read-only
pass, is kept outside the repository. It found no use-after-free, double free or
wrong-certificate path. The partial-chain and disposed-certificate rules above came from
it. It accepts three residual risks: a cached key lives until publication, eviction,
disposal or the listener stops rather than until the last connection that used it; a
client can tell from the handshake's timing whether anyone selected a host name's
certificate since the last publication or eviction, as with .NET's own credential caches;
and freed PKCS#12 parse buffers inside OpenSSL are not cleansed, as before, though now
once per credential rather than per handshake.

### Left out

- Sharing the OpenSSL `SSL_CTX` per listener (0.05 ms per connection, above).
- Reuse across different objects that hold the same certificate. .NET's SslStream keys its
  credential cache by thumbprint; keying by reference keeps a credential tied to the
  object, and so the key, it came from.
- Dropping credentials at the moment of publication. They go at the next handshake, and no
  handshake after the publication can use them.

## Socket loop and per-request cost

Under overload in [#521](https://github.com/WilliamSmithEdward/embedio-neo/issues/521)'s
runs the Windows HTTP/3 server used 0.3 to 0.6 of its two CPUs, and the warm runs above
found small requests costing 2 to 6 times Kestrel's server CPU with about 100 KB
allocated per request, 1 MiB transfers costing about 9 times the CPU at around 10 bytes
allocated per byte, and new connections completing at half Kestrel's rate.

### What the profiles showed

Sampled CPU and allocation profiles of the warm scenarios (`run --profile`) located the
costs:

- A 13-byte response allocated 96 KB, 64 KiB of it a receive ring that each request
  stream allocated whole when its first byte arrived. Each connection also allocated
  384 KiB of CRYPTO reassembly storage, 64 KiB of bytes and 64 KiB of flags per
  encryption level.
- The socket loop handled one received and one sent datagram per iteration and
  allocated on every wait. A received packet was copied and decrypted into new arrays,
  and a sent one was assembled through a `MemoryStream` and several intermediate arrays.
- Every frame left in a packet of its own, and every ack-eliciting 1-RTT packet was
  acknowledged at once in another.
- Loss recovery scanned every outstanding packet, and up to 4,096 packets retained after
  loss, for each ACK frame and each 2 ms tick. Each acknowledged packet scanned the loss
  history, and each loss sorted up to 12,288 acknowledgment times.
- Header protection called `Aes.EncryptEcb`, which imports the key again on every call
  (`BCryptImportKey` on Windows), for every packet sent and received.

### Changes

The socket loop:

- Receiving, sending and the 2 ms timer run as three loops, each taking one lock around
  its dispatcher calls, so the dispatcher still has one owner at a time and keeps its
  output order, refusal budget and admission accounting
  ([#540](https://github.com/WilliamSmithEdward/embedio-neo/pull/540)). The old loop
  awaited `Task.WhenAny` over a receive, the timer and the output channel and handled at
  most one received and one sent datagram per iteration.
- A receive that finds a queued datagram completes at once, so every datagram already
  waiting is read before the loop waits. The send loop sends every queued datagram, in
  the dispatcher's order, before it waits. No wait allocates: the receive and send use
  the `SocketAddress` overloads with cached peers, the send loop waits on a one-item
  channel the dispatcher signals, and the dispatcher queues datagrams as structs.
- A socket bound to one address no longer asks for each datagram's destination; only
  wildcard sockets read packet information.
- Both socket buffers are 4 MiB where the operating system allows, as MsQuic sizes its
  own. .NET has no batched UDP send, so each datagram is still one system call.

Per datagram:

- A received datagram is copied once, into pooled memory, and its packets are
  unprotected and decrypted in place there. A connection's turn processes up to 16
  queued datagrams.
- A sent packet is assembled and sealed in one array: header, frames and padding are
  written where they go and encrypted in place.
- Header protection keeps one AES-ECB encryptor per key.
- With packet sharing, which the listener binding turns on, frames queued together share
  a packet of up to 1,200 bytes. A 1-RTT packet is acknowledged at once when it arrives
  out of order or is the second unacknowledged ack-eliciting one, otherwise with the next
  packet sent or after 1 ms (RFC 9000 sections 13.2.1 and 13.2.2). MAX_STREAMS goes out
  with other frames, or alone once the peer has fewer than half its streams left. A small
  response, its acknowledgment and the stream credit it frees usually leave in one
  packet. The engine's packet-level tests keep sharing off, as they expect one frame per
  packet; `PacketSharingTests` covers it there.
- Loss recovery keeps each packet number space's outstanding packets, and those retained
  after loss, in packet-number order. An ACK range finds its packets by binary search,
  loss detection walks only the outstanding packets below the largest acknowledged one,
  and the probe timeout no longer scans every packet. Persistent congestion is evaluated
  only when the lost packets span its duration.

Per stream and per request:

- A stream's receive ring starts at 512 bytes and grows, pooled, toward the 64 KiB window
  as data arrives. In-order STREAM data passes from the packet to the stream without the
  reassembly pages.
- A write is framed straight from the application's buffer when the stream has no earlier
  write queued; only what flow control holds back is copied. Acknowledged frames leave a
  stream's retransmission list from its front, not by a pass over it.
- CRYPTO reassembly storage grows with the handshake, to the same 64 KiB bound.

One fix the faster engine exposed:

- A request stream now counts against the peer's stream limit until its worker disposes
  it, as a stream does in MsQuic until the application closes it. Before, a request
  stream retired, returning its MAX_STREAMS credit, as soon as both directions had
  finished, even while its handler was still completing. With 32 streams per connection
  a client could then push a connection past its 128 outstanding application callbacks,
  and the server reset requests with H3_EXCESSIVE_LOAD (0x107). The first profiled run of
  the change failed that way after 20,736 requests. Main has the same window: two of its
  six Windows `inspect c8x32` rounds in the comparison below failed that way.

### Profiled results

One profiled round per engine on Windows: the machine above, server on CPUs 0-3 and
client on 4-11, 3 s warmup, 8 s measurement, `dotnet-trace` attached to the server, run
under `scripts/guarded_load_benchmark.py`. The change is `86694f02`, before the formatter
pass and before main's #542 and #543 were merged in; main is `19ccc261`. These locate the
gains; the role-alternated comparison below measures them.

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p99 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | main | 24,488 | 144 us | 97 KB | 2.51 | 3.48 |
| | change | 91,154 | 40 us | 14 KB | 0.56 | 1.66 |
| h3 small c8x32 | main | 27,598 | 138 us | 103 KB | 7.48 | 41.0 |
| | change | 201,088 | 19 us | 12 KB | 1.05 | 3.99 |
| h3 large1m c4x4 | main | 76 | 41.8 ms | 10.0 MB | 162 | 682 |
| | change | 156 | 22.2 ms | 3.1 MB | 102 | 138 |
| h3 upload1m c4x4 | main | 159 | 22.8 ms | 11.9 MB | 86.8 | 351 |
| | change | 417 | 8.2 ms | 0.86 MB | 38.5 | 50.8 |
| h3 churn c16 | main | 881 | 1.73 ms | 877 KB | 16.0 | 54.9 |
| | change | 998 | 1.55 ms | 237 KB | 15.0 | 30.7 |

Most of a small request's remaining 12 to 14 KB is the request context it shares with
HTTP/2. In the 1 MiB transfers, sending is now the largest single cost: one system call
per datagram.

### Role-alternated comparison

The ordinary `run` mode on the change's final code, `a5e0ee3e`, against main `85e18b2a`,
which includes #543 and #544: the eight `h3-` scenarios, three rounds with engine order
alternating, in both role orders, once with the change orchestrating as the harness's
candidate and once with main in that role. The machine, container and CPU sets are those
of [Method](#method); the Linux runners were built in the container from the two commits,
with main CI's TLS library (SHA-256 `f8b6b3ed...`). Medians of the six rounds per build,
less those with more than 7 busy CPUs outside the server and client: 19 of 96 on Windows
and 8 of 96 on Linux. On Linux that count is the Windows host's busy CPUs less the
server's and client's, as before.

Windows, server on CPUs 0-3:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | main | 25,624 | 135 us | 96 KB | 2.38 | 2.83 | 3.34 | 14.2 |
| | change | 104,121 | 34 us | 13 KB | 0.54 | 0.93 | 1.22 | 8.4 |
| h3 small c8x32 | main | 33,154 | 116 us | 105 KB | 6.86 | 14.75 | 18.43 | 27.0 |
| | change | 241,338 | 15 us | 12 KB | 0.96 | 1.84 | 2.79 | 9.6 |
| h3 inspect c64x1 | main | 25,202 | 139 us | 102 KB | 2.46 | 2.87 | 3.17 | 11.5 |
| | change | 99,887 | 36 us | 18 KB | 0.55 | 1.02 | 1.36 | 6.9 |
| h3 inspect c8x32 | main | 32,508 | 119 us | 112 KB | 7.01 | 14.95 | 18.33 | 25.2 |
| | change | 208,805 | 18 us | 17 KB | 1.09 | 2.18 | 3.25 | 11.1 |
| h3 large1m c4x4 | main | 79 | 40.8 ms | 9.8 MB | 155 | 551 | 675 | 760 |
| | change | 151 | 19.7 ms | 3.0 MB | 101 | 161 | 216 | 272 |
| h3 stream1m c4x4 | main | 74 | 44.2 ms | 10.3 MB | 165 | 590 | 708 | 839 |
| | change | 159 | 21.2 ms | 3.2 MB | 101 | 118 | 126 | 141 |
| h3 upload1m c4x4 | main | 167 | 22.0 ms | 11.8 MB | 89.3 | 152 | 213 | 387 |
| | change | 423 | 7.7 ms | 0.87 MB | 37.9 | 40.6 | 46.5 | 50.2 |
| h3 churn c16 | main | 1,677 | 1.11 ms | 847 KB | 8.29 | 18.8 | 23.8 | 29.1 |
| | change | 1,669 | 0.94 ms | 186 KB | 8.09 | 19.9 | 25.2 | 31.1 |

Linux, server on CPUs 0-3 of the container's eight:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | main | 16,037 | 198 us | 97 KB | 3.74 | 7.37 | 9.22 | 12.7 |
| | change | 65,331 | 39 us | 13 KB | 0.83 | 2.18 | 3.48 | 5.6 |
| h3 small c8x32 | main | 34,417 | 95 us | 93 KB | 6.66 | 14.85 | 20.28 | 27.4 |
| | change | 174,787 | 18 us | 12 KB | 1.22 | 3.33 | 4.71 | 7.4 |
| h3 inspect c64x1 | main | 15,017 | 204 us | 102 KB | 3.97 | 7.68 | 9.88 | 13.9 |
| | change | 53,811 | 49 us | 18 KB | 0.97 | 2.55 | 4.25 | 6.8 |
| h3 inspect c8x32 | main | 30,987 | 103 us | 98 KB | 7.37 | 16.38 | 22.53 | 30.7 |
| | change | 150,206 | 21 us | 17 KB | 1.43 | 3.79 | 5.38 | 7.7 |
| h3 large1m c4x4 | main | 47 | 70.2 ms | 7.1 MB | 249 | 904 | 1,167 | 1,382 |
| | change | 87 | 37.3 ms | 3.0 MB | 182 | 236 | 262 | 282 |
| h3 stream1m c4x4 | main | 42 | 80.1 ms | 10.2 MB | 288 | 1,035 | 1,252 | 1,527 |
| | change | 84 | 39.7 ms | 3.2 MB | 190 | 233 | 247 | 270 |
| h3 upload1m c4x4 | main | 71 | 49.9 ms | 11.3 MB | 213 | 370 | 531 | 668 |
| | change | 218 | 15.0 ms | 0.79 MB | 72.1 | 102 | 121 | 149 |
| h3 churn c16 | main | 1,682 | 1.35 ms | 608 KB | 9.16 | 15.9 | 19.1 | 24.1 |
| | change | 1,839 | 1.01 ms | 90 KB | 8.45 | 14.1 | 16.9 | 21.5 |

- Small requests: the change served 3.6 to 7.3 times main's requests per second, with
  13 to 26 percent of its server CPU per request and 11 to 18 percent of its allocation.
  p99 fell to 15 to 43 percent of main's and p99.9 to 25 to 60 percent. The 32-stream
  scenarios gained most: on Windows 241,338 requests per second against 33,154.
- 1 MiB transfers: 1.9 to 3.1 times the throughput, at 30 to 53 percent of the server CPU
  per response and 7 to 43 percent of the allocation, with p99 at 18 to 32 percent of
  main's. An upload allocates about 0.8 bytes per byte received, against about 11.
- The change's 1 MiB p99 varied with load outside the benchmark. Of its six Windows
  `large1m` rounds, the two with at most 1.1 busy CPUs outside the server and client had
  p99 of 133 and 136 ms, three with 3.9 to 5.5 had 216 to 331 ms, and one with 8.0, set
  aside, had 147 ms. The nine rounds of the flood check below, whose engine code differs
  only in how header protection is set up, had 136 to 149 ms. Main's p99 was 668 to
  786 ms in every round.
- Connection churn on Linux: 1.09 times main's rate at 0.75 times its server CPU per
  connection. On Windows the TLS handshake work that Schannel does in `lsass` counts
  outside the server and client: in a separate churn round per build with per-process
  counters, `lsass` used 4.4 CPUs for either build, most of the 5.6 to 6.0 CPUs outside
  them. Windows churn rounds therefore come close to the ceiling with nothing else
  running, and only one of the change's six stayed under it. Over all six rounds each the
  change served 0.99 times main's connections per second at 0.74 times its server CPU per
  connection. The server used 34 percent of its four CPUs for the change and 44 percent
  for main, so its CPU does not bound the connection rate.
- Main failed three Windows rounds and the change none: two `inspect c8x32` rounds
  reset requests with H3_EXCESSIVE_LOAD during warmup, the window described under the
  changes, and in one `upload1m` round, which ran its 15 seconds at the ordinary rate, a
  client connection ended with QUIC_STATUS_ABORTED. That abort's cause was not
  investigated. No Linux round failed.

### Review findings

An independent review of the change found no memory-safety or cross-connection exposure.
It found three protocol and budget defects, fixed here, and raised one availability
question:

- With MAX_STREAMS deferred, the count granted to the peer could trail the window's room by
  up to half its capacity, and a stream opened in between was accepted instead of failing
  with STREAM_LIMIT_ERROR, as RFC 9000 section 4.6 requires. A peer-initiated stream is now
  checked against the count granted.
- A deferred acknowledgment waited for the owner's next tick, which runs as a turn of its
  own. When turns wait long for a worker, that second wait could take the acknowledgment
  past the 25 ms max_ack_delay peers assume of this endpoint; before the change it left in
  the turn that received the packet. A turn that waited 5 ms or more since it was queued
  now sends a waiting acknowledgment itself.
- The connection owner's 256 KiB queue budget counted datagram lengths, but each copy is a
  pooled array rounded up to a power of two, so datagrams just past one could hold nearly
  twice the budget, about 384 MiB across 768 connections. It now counts the arrays.
- The three loops share one lock, and .NET's `Monitor` does not hand a released lock to the
  thread that waited longest. Under a sustained flood the receive loop, whose receives then
  complete at once, could in principle take the lock back each time before the send or
  timer loop got it.

`PacketSharingTests` covers the stream limit on both sides and a late turn's
acknowledgment, and `ConnectionOwnerTests` the late turn through the owner and the byte
budget. Each new test fails with its fix removed.

For the last question, the load benchmark's `run` mode gained `--udp-flood-cpus` and
`--udp-flood-threads`, which run a `flood` process on its own CPUs during each HTTP/3
sample. It sends 1-RTT datagrams with unknown connection IDs to the server's port; the
receive loop reads each one, takes the lock to look it up, finds no connection and drops
it. Windows, server on CPUs 0-3, client on 4-9, the flood on CPU 10 (one thread) or 10-11
(two), three rounds of each, the change with these fixes (`bb77c121`) against main with #544
(`c1251f6a`). Medians of the kept rounds: 5 of the 18 rounds without a flood were set
aside for more than 7 busy CPUs outside the server and client, and none of the flooded
ones.

| Scenario | Server | Junk datagrams/s | req/s | p99 ms | req/s against none | p99 against none |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| h3 small c8x32 | main | none | 33,965 | 18.6 | | |
| | | 184,177 | 30,275 | 23.4 | 0.89 | 1.25 |
| | | 380,868 | 31,933 | 19.0 | 0.94 | 1.02 |
| | change | none | 252,296 | 2.69 | | |
| | | 174,997 | 220,803 | 3.10 | 0.88 | 1.15 |
| | | 364,135 | 213,468 | 3.17 | 0.85 | 1.18 |
| h3 small c64x1 | main | none | 25,880 | 3.06 | | |
| | | 149,359 | 24,461 | 3.53 | 0.95 | 1.15 |
| | change | none | 95,609 | 2.44 | | |
| | | 143,248 | 91,862 | 1.66 | 0.96 | 0.68 |
| h3 large1m c4x4 | main | none | 79 | 668 | | |
| | | 157,775 | 73 | 721 | 0.93 | 1.08 |
| | | 369,481 | 72 | 747 | 0.91 | 1.12 |
| | change | none | 159 | 137 | | |
| | | 151,376 | 154 | 141 | 0.97 | 1.03 |
| | | 341,339 | 149 | 144 | 0.94 | 1.05 |

Under up to 381,000 junk datagrams a second the change kept 85 to 97 percent of its
throughput, and main 89 to 95 percent; p99 rose by up to 18 percent for the change and 25
percent for main. The send and timer loops kept getting the lock: no scenario stalled, and
with two flood threads the change's 32-stream connections still served 213,468 requests a
second, six times main's rate without a flood. The later commits of the change set up
header protection through a helper and bring in #544's credential cache, which runs per
handshake; neither touches the receive loop.

### Left

- Batched UDP sends (`UDP_SEGMENT` on Linux, `UDP_SEND_MSG_SIZE` on Windows), as MsQuic
  uses, for the per-datagram system call: added in
  [Segmented UDP sends](#segmented-udp-sends).
- Sending STREAM data from a stream's send buffer when the packet is built, rather than
  as frames held for retransmission, and pooling packet buffers: a 1 MiB response still
  allocates about 3 bytes per byte.
- Fewer thread-pool hops per request, and ticks only for connections with a timer due:
  every connection is woken every 2 ms.

## Draft revisions

[#551](https://github.com/WilliamSmithEdward/embedio-neo/issues/551) follows the drafts
under WebTransport over HTTP/3 until they are published as RFCs. The IETF datatracker
on 2026-10-27:

| Draft | Latest revision | Dated | What EmbedIO speaks |
| --- | --- | --- | --- |
| draft-ietf-webtrans-http3 | 16 | 6 July 2026 | 16, and 14 and 02 for browsers |
| draft-ietf-webtrans-overview | 13 | 6 July 2026 | Defines no wire format |
| draft-ietf-quic-reliable-stream-reset | 11 | 6 September 2026 | `reset_stream_at` as `0x1d` and as revision 07's `0x17f7586d2cb571`, frame `0x24` |

The reliable-reset draft is now in the RFC Editor queue. On 28 September 2026
[IANA](https://www.iana.org/assignments/quic/quic.xhtml) made its codepoints permanent,
citing the RFC to be: `reset_stream_at` is `0x1d` among the transport parameters and
`RESET_STREAM_AT` is `0x24` among the frame types. These are the codepoints EmbedIO.Quic
already uses. Revision 07's `0x17f7586d2cb571`, which EmbedIO.Quic also sends and
accepts, is not registered. Neither WebTransport draft has reached IESG review.

Revision 11 of draft-ietf-quic-reliable-stream-reset keeps revision 10's codepoints: the
`reset_stream_at` transport parameter `0x1d` and the `RESET_STREAM_AT` frame `0x24`. Apart
from wording it changes three rules:

- A sender must not send `RESET_STREAM_AT` unless the peer offered `reset_stream_at`.
  EmbedIO.Quic sends `RESET_STREAM` otherwise.
- A receiver ignores an increase in the Reliable Size and keeps the smallest it received.
  Revisions 07 to 10 said to ignore any frame that increases it, with the same outcome.
- An endpoint that answers STOP_SENDING with `RESET_STREAM_AT` should set the Reliable
  Size to zero. EmbedIO.Quic answers through the same reset as any other, whose Reliable
  Size is the stream's reliable prefix: zero, except on a WebTransport data stream, where
  it is the stream header. draft-ietf-webtrans-http3-16 requires a Reliable Size of at
  least the header whenever a WebTransport data stream is reset.

Checking the receiving side against section 5.2 found two departures, both now fixed.
The two rules involved read the same in every revision from 07 to 11.

- A `RESET_STREAM_AT` with a larger Reliable Size than an earlier reset of the stream
  closed the connection with PROTOCOL_VIOLATION ("Reliable size increased after reset").
  Such a frame is an earlier one arriving late, or retransmitted after the sender lowered
  the size, and the draft says to ignore the increase. The smallest size now stands.
- A later reset that changed the stream's Application Error Code was accepted, and the
  first code kept. Where the peer offered `reset_stream_at` it now closes the connection
  with STREAM_STATE_ERROR, as the draft requires. Without the extension RFC 9000 sets no
  such rule, and nothing changes.

A second check, against sections 3 and 4 and the final-size rule of section 5.2, found
four more departures, also fixed:

- A peer that had not offered `reset_stream_at` could not send `RESET_STREAM_AT`: the
  frame closed the connection with PROTOCOL_VIOLATION. The parameter announces what an
  endpoint can receive, and EmbedIO.Quic offers it on every connection, so section 3
  lets any peer send the frame. It is now accepted from any peer. Sending one still
  waits for the peer's offer.
- A Reliable Size larger than the Final Size closed the connection with
  PROTOCOL_VIOLATION. Section 4 requires FRAME_ENCODING_ERROR in every revision from 07,
  and it is now sent.
- A final size past the stream's or the connection's flow-control limit closed the
  connection with PROTOCOL_VIOLATION. Section 4 requires FLOW_CONTROL_ERROR from
  revision 08 on, as RFC 9000 section 4.1 does for any frame, and STREAM, `RESET_STREAM`
  and `RESET_STREAM_AT` frames past either limit now get it. So does data past the
  largest stream offset, 2^62 - 1, which RFC 9000 section 19.8 lets an endpoint answer
  with FLOW_CONTROL_ERROR or FRAME_ENCODING_ERROR.
- A reset or FIN that changed the final size of an open stream, or data past its final
  size, closed the connection with PROTOCOL_VIOLATION. Section 5.2 requires
  FINAL_SIZE_ERROR from revision 08 on, and RFC 9000 section 4.5 recommends it for any
  frame; revision 07 named STREAM_STATE_ERROR in section 5.2 and FINAL_SIZE_ERROR in
  section 5. EmbedIO.Quic now sends FINAL_SIZE_ERROR whether the stream is open or
  already closed.

Conflicting bytes at an offset already received still close the connection with
PROTOCOL_VIOLATION, which RFC 9000 section 2.2 allows.

### Validation

- `StreamDiscardTests` in `self-test` adds a late larger Reliable Size, before and after
  a `RESET_STREAM` that lowers it to zero, and a changed error code in `RESET_STREAM_AT`
  and in `RESET_STREAM`. On the unchanged engine the Reliable Size cases stopped at
  "Reliable size increased after reset", and the error-code cases, run first, failed with
  "Stream discard regression: RESET_STREAM_AT error code". With the change `self-test`
  passed.
- The NUnit HTTP/3, WebTransport and QUIC subset on Windows, 821 cases: 819 passed and 2
  were skipped, one for want of an aioquic peer and one that runs only on Linux.

For the second check:

- `ConnectionCoreTests` in `self-test` sends each case to a whole connection and reads
  back the CONNECTION_CLOSE frame: a `RESET_STREAM_AT` from a peer that did not offer
  `reset_stream_at`, STREAM data past the stream's credit and past the largest offset, a
  reset past the stream's credit, a changed final size, and a Reliable Size above the
  Final Size. The same six cases on the unchanged engine each closed the connection with
  PROTOCOL_VIOLATION (0x0a). With the change the first leaves the connection open and
  the others close it with 0x03, 0x03, 0x03, 0x06 and 0x07.
- `StreamDiscardTests`, `BridgeTests`, `StreamWindowTests` and `StreamStorageTests` name
  the code each refusal must carry, and the stream model fuzzer checks the code of every
  refusal it predicts.
- `self-test` passed, as did the QUIC development campaign for seeds 1729, 106458 and
  211187, each with 100,000 property cases, 100,000 mutation cases and the recovery and
  lifecycle state machines.
- The NUnit subset again: 821 cases, 819 passed and the same 2 skipped.

## Segmented UDP sends

[#548](https://github.com/WilliamSmithEdward/embedio-neo/issues/548) asked for the
batched sends left above, on Windows and Linux with the fallback tested, and for a
comparison of the eight `h3-` scenarios against 2.18.0 that records the 1 MiB costs on
both platforms with no regression in the small-request or churn scenarios. The HTTP/3
listener now sends consecutive datagrams for one peer in one system call where its
socket can segment them. This is in 2.19.0.

### Sends

- After binding, the listener asks its socket whether it can segment sends:
  `UDP_SEND_MSG_SIZE` on Windows, from Windows 10 version 2004, and `UDP_SEGMENT` on
  Linux, from kernel 4.18, each read with `Socket.GetRawSocketOption`. A socket that can
  is made non-blocking. macOS has neither option, and its sends are unchanged.
- The send loop gathers consecutive datagrams for one peer into a run, each the length
  of the first except the last, which may be shorter and ends the run. A run holds at
  most 64 datagrams, the most Linux sends in one call, and 65,000 bytes, below the
  65,507 bytes one IPv4 UDP send can carry. The dispatcher hands out one datagram at a
  time and counts it against its output limits until the send loop is done with it, so
  each datagram is copied into the run's pinned 65,000-byte buffer as it is taken and
  released at once.
- A run leaves when it is full, when the next datagram cannot join it, and when nothing
  more is queued, so no datagram waits for a later one. A run of two or more leaves in
  one `WSASendMsg` carrying a `UDP_SEND_MSG_SIZE` control message, or one `sendmsg`
  carrying `UDP_SEGMENT`, with the segment length; the operating system, or the network
  adapter where it offloads the work, divides it into the datagrams. A run of one leaves
  through `SendToAsync`, as every datagram did before. Only consecutive datagrams for one
  peer gather, so output that alternates between connections leaves one datagram per
  call.
- A segmented send that fails loses nothing: its datagrams then leave one at a time
  through `SendToAsync`. That covers a full send buffer (`WSAEWOULDBLOCK`, `EAGAIN`), no
  buffer space, a message-size or argument error, and a datagram path error pending from
  an earlier send, which is counted once, as for a single datagram. A socket that
  answers that it cannot segment at all (`EOPNOTSUPP`, `ENOPROTOOPT`, or `EIO`, which
  Linux returns when the outgoing path cannot segment) is not asked again.
- The binding's diagnostics, which the development host reports, count the segmented
  sends, the datagrams they carried and the fallbacks.

### Validation

- `SegmentedUdpSendTest`, 17 cases: one segmented send from an IPv4 socket, an IPv6
  socket, and a dual-mode socket sending to an IPv4 peer at its mapped address reaches
  a plain receiver as separate datagrams with the sent bytes; a run takes and refuses
  datagrams by peer, length and both limits; and the 13 Linux errno values the listener
  acts on map to their `SocketError`.
- `Http3ListenerTest`, 4 cases on the actual listener with an HTTP/3 client: two 1 MiB
  responses arrive intact through segmented sends with no fallback; with segmentation
  removed, responses arrive with no segmented send; when the first five segmented sends
  fail with `WouldBlock`, `NoBufferSpaceAvailable`, `MessageSize`, `InvalidArgument`
  and `ConnectionReset`, the responses arrive intact, five fallbacks and one path error
  are counted, and later sends segment; and a socket that answers
  `OperationNotSupported` is asked once.
- All 21 passed on Windows 11 build 26300 and in an Ubuntu 24.04 container (glibc 2.39,
  WSL 2 kernel 6.18), segmenting on both. The 17 socket and run cases passed on Alpine
  3.24.2 with musl 1.2.6, where `LibraryImport("libc")` resolves to musl's own library;
  the listener cases were not run there.
- The full Windows suite, 7,536 cases: 7,531 passed, 4 skipped for the platform and 1
  failed. `ListenerBoundaryRegressionTest.AcceptBurstsAndResetsAllowShutdownAndRestart(True,True)`
  could not open an HTTP/1.1 client connection (`NoBufferSpaceAvailable`, WSAENOBUFS),
  as in the run recorded for #549 in
  [response header allocation](response-field-allocation.md); its class's 62 cases then
  passed three runs in a row. After the formatter pass, of the 212 HTTP/3 listener and
  segmented send cases 211 passed and 1 was skipped for the platform, and the
  development host's `self-test` passed.

### Measurement

The ordinary `run` mode on the change as measured, `3827f30b` on main `92a4dfe9` and
before the formatter pass, against 2.18.0 (`168d4752`): the eight `h3-` scenarios in
four runs of two scenarios each, three rounds per run with engine order alternating,
in both role orders, 5 s warmup and 15 s measurement. The machine and CPU sets are
those of [Method](#method): on Windows the server on CPUs 0-3 and the client on 4-11;
on Linux, the Docker container's eight CPUs under WSL 2, server on 0-3 and client on
4-7. Linux ran the same managed runners as Windows, 2.18.0 with its own TLS library
(private ABI 3) and the change and main with main's (ABI 4), which this change does not
touch. Medians of the six rounds per build, less those with more than 7 busy CPUs
outside the server and client: 14 of 96 on Windows and 7 of 96 on Linux, counted on
Linux from the Windows host as before. One more Windows round failed (see below).

Windows, server on CPUs 0-3:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | 2.18.0 | 108,090 | 34 us | 14 KB | 0.53 | 0.91 | 1.20 | 9.0 |
| | change | 108,552 | 34 us | 12 KB | 0.54 | 0.92 | 1.15 | 6.1 |
| h3 small c8x32 | 2.18.0 | 263,538 | 14 us | 12 KB | 0.88 | 1.66 | 2.27 | 11.2 |
| | change | 286,825 | 13 us | 10 KB | 0.81 | 1.51 | 2.05 | 11.8 |
| h3 inspect c64x1 | 2.18.0 | 96,637 | 36 us | 18 KB | 0.55 | 1.08 | 1.63 | 7.0 |
| | change | 101,012 | 36 us | 16 KB | 0.56 | 0.99 | 1.28 | 6.9 |
| h3 inspect c8x32 | 2.18.0 | 210,696 | 18 us | 17 KB | 1.06 | 2.12 | 2.97 | 13.0 |
| | change | 225,467 | 17 us | 15 KB | 1.00 | 1.97 | 3.06 | 13.4 |
| h3 large1m c4x4 | 2.18.0 | 151 | 20.7 ms | 3.05 MB | 103 | 129 | 143 | 159 |
| | change | 222 | 15.2 ms | 3.04 MB | 71.3 | 90.1 | 99.9 | 123 |
| h3 stream1m c4x4 | 2.18.0 | 157 | 21.9 ms | 3.21 MB | 102 | 118 | 128 | 145 |
| | change | 219 | 15.4 ms | 3.19 MB | 72.9 | 86.0 | 93.4 | 116 |
| h3 upload1m c4x4 | 2.18.0 | 421 | 7.9 ms | 885 KB | 38.1 | 40.6 | 45.9 | 49.6 |
| | change | 534 | 6.2 ms | 753 KB | 29.7 | 33.8 | 39.9 | 51.4 |
| h3 churn c16 | 2.18.0 | 1,818 | 0.84 ms | 186 KB | 7.58 | 17.4 | 22.7 | 29.1 |
| | change | 2,024 | 0.85 ms | 184 KB | 6.91 | 15.4 | 20.0 | 27.2 |

Linux, server on CPUs 0-3 of the container's eight:

| Scenario | Server | req/s | server CPU per request | allocated per request | p50 | p95 | p99 | p99.9 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| h3 small c64x1 | 2.18.0 | 63,653 | 41 us | 13 KB | 0.84 | 2.23 | 3.61 | 6.2 |
| | change | 62,153 | 41 us | 11 KB | 0.85 | 2.32 | 3.79 | 6.3 |
| h3 small c8x32 | 2.18.0 | 155,253 | 19 us | 12 KB | 1.32 | 3.76 | 5.71 | 8.3 |
| | change | 160,378 | 18 us | 10 KB | 1.27 | 3.84 | 5.53 | 8.2 |
| h3 inspect c64x1 | 2.18.0 | 63,590 | 43 us | 18 KB | 0.87 | 2.16 | 3.40 | 6.2 |
| | change | 64,503 | 42 us | 16 KB | 0.86 | 2.12 | 3.43 | 5.8 |
| h3 inspect c8x32 | 2.18.0 | 143,439 | 21 us | 17 KB | 1.48 | 4.10 | 5.71 | 8.2 |
| | change | 149,041 | 20 us | 15 KB | 1.40 | 3.97 | 5.63 | 8.0 |
| h3 large1m c4x4 | 2.18.0 | 80 | 40.9 ms | 3.03 MB | 197 | 256 | 285 | 321 |
| | change | 176 | 18.7 ms | 3.05 MB | 90.5 | 121 | 136 | 152 |
| h3 stream1m c4x4 | 2.18.0 | 88 | 37.7 ms | 3.19 MB | 180 | 226 | 254 | 282 |
| | change | 190 | 17.4 ms | 3.16 MB | 83.6 | 106 | 120 | 134 |
| h3 upload1m c4x4 | 2.18.0 | 212 | 15.3 ms | 791 KB | 72.1 | 106 | 127 | 152 |
| | change | 403 | 8.2 ms | 781 KB | 38.1 | 63.9 | 80.3 | 94.2 |
| h3 churn c16 | 2.18.0 | 1,835 | 1.03 ms | 100 KB | 8.50 | 14.1 | 17.0 | 20.7 |
| | change | 1,836 | 1.01 ms | 109 KB | 8.45 | 14.2 | 17.2 | 21.8 |

The change also ran against main `92a4dfe9` the same way, without the inspect scenarios
on Windows and without `inspect c8x32` on Linux, to separate it from main's own changes
since 2.18.0.

- 1 MiB responses: 1.40 to 1.48 times 2.18.0's throughput on Windows at 71 to 73 percent
  of its server CPU per response, and 2.16 to 2.20 times on Linux at 46 percent. p99 fell
  to 70 to 73 percent of 2.18.0's on Windows and to 47 to 48 percent on Linux. Main
  without the change was within 6 percent of 2.18.0 on these, and against main the change
  had 1.41 to 1.42 times the throughput at 71 to 72 percent of the CPU on Windows and
  2.10 to 2.15 times at 46 to 47 percent on Linux. Allocation per response did not
  change.
- 1 MiB uploads on Windows: 1.27 times 2.18.0's throughput at 79 percent of its server
  CPU and 85 percent of its allocation per upload, and against main 1.29 times at 81
  and 82 percent. The allocation drop was not traced.
- 1 MiB uploads on Linux ran in two modes with the change. Of its nine kept rounds
  across both comparisons, four served 346 to 408 uploads per second at 8.0 to 9.0 ms of
  server CPU each and five served 213 to 246 at 11.3 to 13.3 ms. 2.18.0 and main served
  189 to 248 at 13.5 to 16.5 ms in all twelve of theirs. The table's median falls in the
  faster mode, and against main, with three slower rounds of four, it is 1.06 times the
  throughput at 79 percent of the CPU. What sets the mode was not investigated.
- Small requests: against 2.18.0 the 32-stream scenarios had 1.03 to 1.09 times the
  throughput at 92 to 95 percent of the server CPU on both platforms. The single-stream
  scenarios were at most 2.4 percent below 2.18.0's throughput and 2.0 percent above its
  server CPU, with p99 at most 5 percent above. Against main, measured on Linux, they
  were at most 2.5 percent below its throughput and above its CPU, with p99 at most 7
  percent above. The kept single-stream rounds of each build on Linux differed by 7 to
  38 percent in throughput and 5 to 24 percent in server CPU, so these differences are
  not resolved. The 11 to 17 percent less allocation per small request is main's, from
  #549.
- Connection churn: on Linux 1.00 times 2.18.0's connection rate at 0.98 times its server
  CPU per connection, and 0.98 and 1.00 against main. Allocation per connection rose 9
  percent against 2.18.0, from 100 to 109 KB, and main allocated the same 109 KB, so the
  rise came with main's changes since 2.18.0 (see
  [ClientHello reader allocation](#clienthello-reader-allocation)). On Windows only three
  rounds of 2.18.0 and two of the change stayed under the 7-CPU limit, since Schannel's
  handshakes run in LSASS, outside the server and client (see
  [Role-alternated comparison](#role-alternated-comparison)): 1.11 times 2.18.0's rate
  at 1.01 times its CPU per connection. Against main, with four and three rounds kept,
  0.99 times the rate at 1.00 times the CPU.
- One Windows churn round of the change failed: the client's QUIC connection setup
  returned WSAENOBUFS (0x80072747). The client is .NET's `HttpClient` on MsQuic in its own
  process, and main's clients have met the same error under load on this machine (see
  [Overload runs](#overload-runs)). No Linux round failed.

### Left

- macOS still sends one datagram per system call.
- Receiving still reads one datagram per call; neither `UDP_GRO` on Linux nor
  `UDP_RECV_MAX_COALESCED_SIZE` on Windows is used.
- What sets the Linux upload mode. [Linux upload modes](#linux-upload-modes) narrows
  it down.

## ClientHello reader allocation

The churn comparison in [Segmented UDP sends](#segmented-udp-sends) found Linux
connections allocating 9 percent more on main than on 2.18.0, 109 KB against 100 KB, at
the same server CPU. [TLS session resumption](#tls-session-resumption) moved the OpenSSL
path onto the managed ClientHello reader, `TlsClientHello.Read`, which the Schannel path
has used since 2.17.0. To refuse a repeated extension the reader allocated an 8 KB table,
one bit per extension type, for every ClientHello. It now keeps the table on the stack.
This is in 2.19.0.

### Cause

- `h3 churn c16` on Linux, measured as in [Segmented UDP sends](#segmented-udp-sends),
  with session resumption's merge (`71ae5271`) against main just before it (`8dcf7150`),
  three rounds of each in both role orders: medians of 111 KB allocated per connection
  against 100 KB, at 1.01 against 0.98 ms of server CPU per connection.
- A profiled run of two rounds each sampled 10.7 KB more of byte arrays per connection
  with session resumption and 1.0 KB more of strings. Each ClientHello allocated one
  8,216-byte array, the 8,192-byte table and its header. The rest was not traced further;
  session resumption also gives the OpenSSL path a buffer for each ClientHello, starting at
  1 KB, and a copy of the ticket keys for each handshake.

### Change

- `TlsClientHello.Read` keeps the table in an 8,192-byte `stackalloc` span, cleared before
  use. The duplicate check is unchanged.

### Validation

- The hello cases of `self-test` read a ClientHello twice and require the second read to
  allocate less than 1 KB on its thread. That check failed on the unchanged reader and
  passes with the change, as does the rest of `self-test`.
- The NUnit HTTP/3, QUIC, WebTransport, certificate and TLS subset on Windows, 916 cases:
  913 passed and 3 were skipped, two that run only on other platforms and one for want of
  an aioquic peer.

### Measurement

`h3 churn c16`, the change (`396ea46d`) against main (`5c9d1340`), three rounds of each
in both role orders, by the method of [Segmented UDP sends](#segmented-udp-sends). One
Windows round of main failed in its warmup when the client's QUIC connection setup
returned WSAENOBUFS (0x80072747), as in that section. Medians of the kept rounds:

| Platform | Server | connections/s | server CPU per connection | allocated per connection | p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| Windows | main | 2,065 | 0.84 ms | 184 KB | 19.0 |
| | change | 2,075 | 0.82 ms | 176 KB | 19.7 |
| Linux | main | 1,941 | 1.00 ms | 109 KB | 16.3 |
| | change | 1,916 | 1.01 ms | 101 KB | 16.7 |

- Allocation per connection fell by 8 KB on both platforms, on Linux back to within 1 KB of
  2.18.0's 100 KB. Connection rate, server CPU and p99 stayed within the spread of the
  rounds.
- Two of the change's six Linux rounds allocated 76 and 92 KB per connection, and two of
  session resumption's six 77 and 79 KB. Single rounds of 2.18.0 (78 KB), main (98 KB) and
  the [segmented sends](#segmented-udp-sends) change (87 KB) showed the same lower level;
  none of the six rounds before session resumption did. What sets it was not traced.

## Linux upload modes

[Segmented UDP sends](#segmented-udp-sends) left open why its 1 MiB uploads on Linux ran
in two modes. Every run here measured `h3 upload1m c4x4` alone, one round per server
process, in the measurement container of [Method](#method), with the server on CPUs 0-3
and the client on 4-7 unless stated. The builds were the change at `fc6301c2`, and for
some rounds a copy with counters that wrote datagram and receive statistics to the
server's stderr once a second, never committed. Around each round the container read the
kernel's UDP counters and every UDP socket's queue and drop count, and for eight rounds
the scheduling statistics of every server and client thread.

### What the modes are

- 63 rounds fell into three bands: 39 served 216 to 272 uploads a second at 11.2 to
  13.3 ms of server CPU each, 8 served 340 to 363 at 8.7 to 9.0 ms, and 16 served 412 to
  479 at 7.5 to 8.1 ms. Main served 217 to 221 at 14.9 to 15.1 ms in its four rounds.
- A round keeps its band for the whole 15-second measurement. Every warmup ran at the same
  rate, and the band shows once the measured connections open.
- The four connections of a round share its band. In every round each carried 22 to 31
  percent of the datagrams, so the band belongs to the round, not to single connections.
- Each upload carried the same traffic in every band. The server received 780 to 851
  datagrams averaging 1,414 to 1,444 bytes, about 97 percent of them 1,401 to 1,452 bytes
  long, and sent 386 to 400 datagrams in 193 to 246 calls, 57 to 82 of them segmented.
- What changes is what that traffic costs, in both processes. In the eight rounds with
  thread statistics, a fast round's server ran about 950 timeslices per upload with about
  820 involuntary context switches, a slow round's 1,300 to 1,550 with 1,150 to 1,400. The
  client spent 4.3 ms of CPU per upload against 5.1 to 5.2 ms, and blocked 47 to 48 times
  per upload against 85 to 90.

### What does not set it

- Socket loss: in 16 rounds the kernel counted no receive-buffer errors and no socket
  drops, and the server's receive queue peaked at 770 KB of its 8 MB.
- Thread-pool size: rounds of every band started with 12 to 24 thread-pool threads.
- Hyperthreads: the guest pairs its CPUs as siblings (0-1, 2-3 and so on), so the server's
  four CPUs are two cores. With the server on 0, 2, 4 and 6 and the client on 8, 10, 12
  and 14, one CPU per core, four of eight rounds still ran at 243 to 246 and four at 429
  to 444.
- The GC's dynamic heap count: with `DOTNET_GCDynamicAdaptationMode=0` the three bands
  remained, three rounds at 257 to 272, two at 354 to 363 and three at 444 to 479.
- The receive loop: 97 to 98 percent of its reads completed synchronously in every band,
  with 19 to 24 waits for the socket per upload.
- Host clocks: Windows reported the busy processors at 114.5 to 114.8 percent of nominal
  performance in every round.

What sets the band is still open. It is shared by a round's connections, settles when they
open and changes what the same datagrams cost in both processes. The client is .NET's
`HttpClient` on MsQuic, whose congestion and pacing state these runs could not see.
Windows showed no bands: in [Segmented UDP sends](#segmented-udp-sends) the change's
uploads cost 6.0 to 6.4 ms of server CPU in every round kept there.

### Datagrams the engine drops

- A connection's owner queues at most 64 datagrams, or 256 KiB, for its next turn (see
  [Scheduled connection ownership](#scheduled-connection-ownership)). The dispatcher
  counts the rest as `rejected` and drops them, and the client resends their data. In
  every upload round of every band it dropped 4.2 to 8.5 percent of the datagrams that
  arrived, 33 to 71 per upload. At 52,000 to 90,000 datagrams a second per connection, 64
  arrive within 0.7 to 1.2 ms, so a turn that waits that long for a CPU overflows the
  queue.
- A copy with room for 256 datagrams and 512 KiB per connection dropped none and received
  about 40 fewer datagrams per upload, but was slower. Run alternately with the
  64-datagram copy, four of its five rounds served 184 to 203 uploads a second at 13.8 to
  14.9 ms of server CPU and the fifth 322 at 9.3 ms, against 262 to 437 at 7.7 to 11.4 ms.
  The datagrams it no longer dropped waited in the queue instead, and in these runs the
  drops were the cheaper outcome.
- The Windows rounds were not instrumented to count these drops.

### Left

- What sets the band. Reading the client's congestion and pacing state in each round is
  the next step.
- A way to bound a connection's queued input other than dropping its newest datagrams.

## Received frame error codes

[Draft revisions](#draft-revisions) brought the stream rules of RFC 9000 and
draft-ietf-quic-reliable-stream-reset into line under
[#551](https://github.com/WilliamSmithEdward/embedio-neo/issues/551). This change does
the same for the rest of the frame loop, `QuicConnection.Frames`: each refusal there was
compared with the code RFC 9000 names for it, and with RFC 9221 for DATAGRAM frames. This
is in 2.19.0.

### What changed

| Input from the client | Rule | Before | Now |
| --- | --- | --- | --- |
| A frame of unknown type, at any packet level | RFC 9000 12.4 | `PROTOCOL_VIOLATION` | `FRAME_ENCODING_ERROR` |
| A frame cut short, or a length past its packet | RFC 9000 12.4, 20.1 | `PROTOCOL_VIOLATION` | `FRAME_ENCODING_ERROR` |
| `MAX_STREAMS` above 2^60 | RFC 9000 19.11 | `PROTOCOL_VIOLATION` | `FRAME_ENCODING_ERROR` |
| `STREAMS_BLOCKED` above 2^60 | RFC 9000 19.14 | accepted | `FRAME_ENCODING_ERROR` |
| `STREAM_DATA_BLOCKED` for a send-only or unopened local stream | RFC 9000 19.13 | accepted | `STREAM_STATE_ERROR` |
| `STREAM_DATA_BLOCKED` past the stream limit | RFC 9000 4.6 | accepted | `STREAM_LIMIT_ERROR` |
| CRYPTO data past the 64 KiB buffer, or past 2^62-1 | RFC 9000 7.5, 19.6 | `PROTOCOL_VIOLATION` | `CRYPTO_BUFFER_EXCEEDED` |
| A packet with no frames | RFC 9000 12.4 | accepted | `PROTOCOL_VIOLATION` |
| A DATAGRAM frame of at most 1,200 bytes with more than 1,100 bytes of payload | RFC 9221 3 | `PROTOCOL_VIOLATION` | accepted |
| `max_udp_payload_size` above 65,527 | RFC 9000 18.2 | `TRANSPORT_PARAMETER_ERROR` | accepted |

- A `STOP_SENDING` for a stream past the client's limit closed the connection with
  `STREAM_LIMIT_ERROR` before and after, but the application heard of the stream first.
  The limit is now checked before it does.
- Unchanged, and now covered by a test: `NEW_TOKEN` and `HANDSHAKE_DONE` from a client
  close with `PROTOCOL_VIOLATION` (RFC 9000 sections 19.7 and 19.20), as do a frame that
  Initial packets cannot carry (12.4) and a DATAGRAM frame above 1,200 bytes (RFC 9221).

### Change

- The frame loop reads its packet with a `WireReader` given `FRAME_ENCODING_ERROR`, so a
  field cut short or a length past its limit raises that code. Other readers, such as the
  one for transport parameters, still throw `InvalidDataException`, which their callers
  turn into their own codes. The connection-ID and path frames had each caught that
  exception to raise `FRAME_ENCODING_ERROR`, and no longer need to.
- A frame type other than RFC 9000's 0x00 to 0x1e, `RESET_STREAM_AT` (0x24) and DATAGRAM
  (0x30 and 0x31) is refused before the check for frames that Initial and Handshake
  packets cannot carry.
- CRYPTO reassembly raises `CRYPTO_BUFFER_EXCEEDED` for data past its 64 KiB bound, which
  counts from offset 0 and so also covers data past 2^62-1.
- `STREAM_DATA_BLOCKED` checks its stream the way a STREAM frame does but opens nothing;
  the stream still opens with its first data.
- A DATAGRAM frame is measured, with its type and length fields, against the 1,200 bytes
  the server advertises. The stream owner's own 1,100-byte check on received payloads is
  gone. Frames the server sends stay within 1,100 bytes.

### Validation

- `ConnectionCoreTests` in `self-test` sends 33 cases to an established connection, each
  in one 1-RTT packet, and reads the CONNECTION_CLOSE code, or checks that the connection
  stays active and what reached the application. On the unchanged engine (`5327364d`), 25
  gave another outcome: the unknown types, the truncated frames, both `MAX_STREAMS` cases
  and both CRYPTO cases closed with `PROTOCOL_VIOLATION`; the empty packet, both
  `STREAMS_BLOCKED` cases and the three `STREAM_DATA_BLOCKED` refusals were accepted; the
  three DATAGRAM frames with 1,150 to 1,199 bytes of payload closed the connection; and
  the `STOP_SENDING` past the limit reached the application before the connection closed.
  All 33 pass with the change.
- Two Initial-level cases: an unknown frame type closed with `PROTOCOL_VIOLATION` on the
  unchanged engine and closes with `FRAME_ENCODING_ERROR` now; `MAX_DATA`, which Initial
  packets cannot carry, closes with `PROTOCOL_VIOLATION` in both.
- `max_udp_payload_size`: 1,199 is refused and 1,200 and 65,527 are accepted by both;
  65,535 and 2^62-1 were refused and are accepted.
- The native fuzz case that used a `max_udp_payload_size` of 65,528 as an invalid
  parameter now uses an `ack_delay_exponent` of 21, which RFC 9000 section 18.2 rules out.
- The rest of `self-test` passes. `run-quic-development.py --rounds 3 --coverage-seconds 0`
  passed all 15 runs for seeds 1729, 106458 and 211187. The native Schannel fuzz passed
  2,000 cases for each of the RSA and ECDSA profiles at seed 1729, and the aioquic 1.6.1
  peers for connection IDs, stateless reset, local connection IDs, path controls and path
  validation passed.
- The NUnit HTTP/3, QUIC and WebTransport subset on Windows, 825 cases: 822 passed and 2
  were skipped, one for want of an aioquic peer and one that runs only on Linux.
  `CombinedListenerDispatchesEveryProtocol(96)` failed when HttpClient's TCP connect to
  localhost returned WSAENOBUFS; it passed three times alone. With the aioquic peer
  configured, the skipped datagram test passed, echoing its seven sizes up to 1,000 bytes.

### Left

- RFC 9000 section 12.4 allows, but does not require, refusing a frame type encoded in
  more bytes than it needs. EmbedIO.Quic reads such a type as the type it encodes.
- The error codes RFC 9001 sets for the TLS handshake were not part of this check.

## macOS auth-fuzz client ports

The osx-arm64 Portable TLS job's `auth-fuzz` step failed now and then, on main (runs
39896006269 and 40016524876) and on pull requests, about 4 runs in 60. One case's client
sent five Initials over three seconds, received no datagram and idled out, while the
server served every other case. The client's socket was the cause, not the server. Only
the test scripts changed.

### Cause

- aioquic's `connect()` binds every client to an IPv6 dual-stack socket on port 0, and
  macOS chooses that port without checking IPv4 sockets. On a `macos-26` runner (macOS
  26.6.2, arm64, run 40591664723), 8,000 such binds beside 256 IPv4 sockets took one of
  their ports 119, 137 and 135 times in three rounds, close to the 125 expected if the
  IPv4 sockets were not considered at all. Each time, a datagram sent to `127.0.0.1` at
  that port reached the IPv4 socket, whether it was bound to `127.0.0.1` or `0.0.0.0`, and
  never the client. Ubuntu 24.04 gave no such port in 24,000 binds. On both, an IPv4 bind
  to a port a dual-stack socket holds fails with `EADDRINUSE`, so only this direction goes
  unchecked.
- During `auth-fuzz` the IPv4 sockets in macOS's ephemeral range, 49152 to 65535, are the
  host's own listener and a few daemons'. With about 257 client sockets a run, a few such
  ports put a collision in a few percent of runs.
- The four saved failures fit both kinds, though they recorded no ports. Two counted 8
  and 9 datagrams rejected because a first Initial failed authentication, which is what
  the listener's own Initials do if they come back to it as new connections: their keys
  come from the wrong connection ID. That happens when the client holds the listener's
  port. One of the two also counted an expired handshake. The other two failures counted
  no rejections, as when the replies go to some other socket.

### Change

- The peer scripts connect through `test/EmbedIO.Quic/quic_client.py`, which follows
  aioquic 1.6.1's `connect()` but binds an IPv4 socket, on `127.0.0.1` for a loopback
  server, when the server's address is IPv4. macOS does not give an IPv4 socket a port
  another IPv4 socket holds. An IPv6 server keeps aioquic's socket.
- `test_client_socket_peer.py`, which `validate.py` runs with the other peer regressions
  on every RID, holds 256 IPv4 ports and requires 2,048 client sockets from the helper to
  avoid them. It also requires that no peer script import aioquic's `connect()`, and
  completes one handshake through the helper.
- `auth-fuzz` failure files record the client's port and the listener's.

### Validation

- On `macos-26` (run 40593929541), `auth-fuzz` ran against a development host built from
  the package feed of #569's head, the same tree as main `9c50b418`. While 4,096 IPv4
  ports were held, all 3 runs through aioquic's dual-stack client failed and all 3 through
  the helper passed their 256 cases. In two of the failures the stuck client's port was
  one of the held ports, and its qlog shows five Initials sent and nothing received in 5
  seconds, as in the CI failures. In the third, the healthy sibling connection never
  connected. Without held ports, 28 runs through aioquic's client and 12 through the
  helper all passed.
- The peer regressions passed on that runner, except the import check, which named the
  probe's dual-stack copy of the harness, as it should.
- On Windows, against the Schannel host, the 12 peer suites that
  `scripts/run-quic-development.py --schannel` runs passed, the 256-case `auth-fuzz` among
  them, and so did the 16 peer regressions.
