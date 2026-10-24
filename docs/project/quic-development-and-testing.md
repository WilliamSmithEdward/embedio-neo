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
   resumption, 0-RTT and other post-handshake TLS messages stay outside the supported
   profile, and Unix TLS keeps needing an exportable key
   ([Retry and the supported TLS profile](#retry-and-the-supported-tls-profile)).
   [#520](https://github.com/WilliamSmithEdward/embedio-neo/issues/520).
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
4. Endurance longer than 30 minutes, and on macOS.
   [#523](https://github.com/WilliamSmithEdward/embedio-neo/issues/523).
5. Resolved: ad-hoc signed hardened apps, AOT and JIT, run in CI
   ([Hardened-runtime macOS apps](#hardened-runtime-macos-apps)), as do the oldest
   glibc and musl that .NET 10 supports
   ([Oldest glibc and musl](#oldest-glibc-and-musl)). Developer ID signing is not
   validated; the project has no Developer ID.
   [#524](https://github.com/WilliamSmithEdward/embedio-neo/issues/524).
6. Resolved: Safari 27, Chromium and Firefox on macOS pass
   ([macOS browsers and draft-14](#macos-browsers-and-draft-14)). Alt-Svc discovery is
   in the source after 2.17.0 (unreleased): `EmbedIOCombined` advertises HTTP/3, and a
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

- Session resumption. Every connection runs a full handshake and the server sends no
  tickets. Resumption needs a ticket-encryption key that the server creates, rotates
  and protects, on both the Schannel and the OpenSSL path. It would save one
  certificate signature and the chain per reconnection; no measurement here calls for
  it.
- 0-RTT. It depends on resumption, and early data can be replayed (RFC 9001 section
  9.2), so it would need an application policy for which requests may be replayed and
  425 Too Early (RFC 8470) for the rest.
- Keys that cannot be exported, on Linux and macOS. The TLS runtime there is the
  OpenSSL inside `EmbedIO-Neo.Quic.Native`, which receives the key exported from the
  certificate; a hardware- or store-held key never reaches it. A signing callback from
  native code into .NET on every handshake would lift this. Schannel uses the key in
  place, so Windows accepts non-exportable CNG keys.

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
