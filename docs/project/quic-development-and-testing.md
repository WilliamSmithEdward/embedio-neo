# EmbedIO.Quic development and testing record

This record covers the development path, test architecture, reproducible local
campaigns and remaining acceptance work for the original C# QUIC engine. It is
maintained with the implementation. The feature is pending in draft PR #506;
production HTTP/3 still uses its existing MsQuic provider. No package release or
public WebTransport API is implied by this record.

See [the feasibility profile](quic-engine-feasibility.md) for standards references,
exact dependency pins, bounds and independent-peer setup. The implementation is
standards-based, not a formally separated clean-room project. OpenSSL supplies
TLS in the cross-platform development profile; a separate Windows adapter uses
Schannel's public recordless SSPI interface. .NET cryptography supplies AES-GCM,
AES and HKDF. Our code owns the transport.
William permits consulting MsQuic and other open-source implementations where
their licenses permit it; implementation code must be original, with no verbatim
copying from those projects.

## Development sequence

| Stage | Implementation and validation |
| --- | --- |
| Core packet and connection owners | `QuicConnection` and `QuicConnectionSet` own packet/TLS/recovery state and bounded destination-ID routing in the source core. Factories supply TLS and applications; development wrappers add protocol error mapping and observations. Simple echo, multiplexed streams and H3 use the same stream owner. Production socket and policy integration remain pending. |
| Windows certificate selection | Complete ClientHello/SNI framing precedes a once-only certificate selector. A selected certificate is cloned for the native credential; the source remains application-owned. Focused tests exercise publication timing, selector errors and disposal during selection. The independent selection profile uses two non-exportable CNG key types and six aioquic cases; production policy/worker integration is pending. |
| Windows TLS credentials | Original managed SSPI binding, provider-neutral TLS state and non-exportable CNG RSA/ECDSA test credentials. Native credential/context/buffer ownership, bounded ClientHello parsing and once-only traffic-secret installation are exercised before production integration. The development cipher profile remains AES-128-GCM/SHA-256. |
| TLS and packet feasibility | Original wire integers, Initial secrets, packet/header protection, packet-number reconstruction, fixed CRYPTO reassembly and public OpenSSL third-party QUIC callbacks. RFC vectors and aioquic establish the first encrypted stream exchange. |
| Key generations | Current, previous and next read keys share header protection; ACK-confirmed local updates track full generations, enforce encryption/integrity usage limits and retain reordering boundaries. Independent command/packet oracles and native wire error checks complement the repeated-update peer case. |
| Receive packet history | A sliding floor and at most 128 ACK ranges retain a 4096-number window. Retired packet numbers are never processed again; bounded history can advance without a connection-wide receive packet quota. |
| Recovery and congestion | Bounded retired-packet metadata and acknowledgement barriers permit long-running recovery histories. Physical packet records retain logical delivery ownership through retransmission and late ACKs. Monotonic-time tests exercise packet/time loss, RTT, PTO probes, pacing, NewReno, application-limited ACKs and persistent congestion. |
| HTTP/3 and WebTransport bridge | Existing H3/QPACK/capsule/session code is linked into the prototype. The bridge handles SETTINGS, authorized CONNECT, exact Origin admission, bidi/uni streams, datagrams, independent sessions and reliable partial resets. quic-go provides a separate wire peer. |
| Sustained transfers | A fixed sliding receive window retains final-size history and compacts consumed H3 input. MAX_DATA/MAX_STREAM_DATA track consumption, including reset gaps after their reliable prefix. Independent cases transfer 512 KiB, with small peer windows and loss. A separate bounded endurance case transfers 8 MiB over two streams with frequent local key updates. |
| Application boundary | `StreamApplication` and its owner-bound operations separate raw streams/datagrams from the H3 adapter. Consumption returns receive credit; reliable prefixes and critical-stream rules belong to the application. The subsequent async API connects this boundary to application workers; production integration remains pending. |
| Async application ownership | `AsyncStreamApplication` and `AsyncQuicStream` provide owned read/write/open/accept/datagram/close operations, explicit cancellation and independent stream halves. A bounded signal wakes the UDP owner; FIN-ACK retirement and asynchronous disposal retain delivery ownership. The console host exercises application workers on a real connection. |
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

Tests, peers, native TLS/console socket ownership and HTTP application adapters remain under
`test/EmbedIO.Quic`, whose executable assembly is `EmbedIO.Quic.Development`.
A project reference and one explicit friend assembly connect the host to the core;
both projects remain nonpackable and production packages do not yet reference
them. The table names implementation files in those source groups and their
corresponding host/test files.

| Surface | Files | Test approach |
| --- | --- | --- |
| Bytes and packet boundaries | `Wire.cs`, `PacketHeader.cs`, `AckRanges.cs`, `PacketNumberWindow.cs` | Independent integer decoding, canonical sizes, header bounds, valid token construction, malformed/descending ACK ranges and independent packet-history set models. |
| Packet protection | `PacketKeys.cs`, `KeyUpdateState.cs`, `KeyUpdateTests.cs` | RFC vectors, encrypt/decrypt identity, tamper rejection, per-key encryption limits, packet-number truncation, repeated key generations, ACK/cooldown policy models and retained header protection. |
| Receive state | `StreamReassembly.cs`, `Wire.cs` | Absolute-position dictionary oracle against the sliding ring; reversed/duplicated CRYPTO fragments; final-size, window, overlap and once-only delivery checks. |
| Recovery/congestion | `Recovery.cs`, `RecoveryRetentionTests.cs` | Deterministic algorithm cases, history compaction/late-ACK models, logical/physical accounting, duplicate ACKs, discard, loss/PTO/retransmit traces, resource bounds and independent connection ownership. |
| Async ownership and admission | `AsyncQuicStream.cs`, `AsyncStreamApplication.cs`, `AsyncStreamEcho.cs`, `AsyncStreamTests.cs` | Original error codes, borrowed-buffer release, reliable-prefix credit, pre/post-copy cancellation, sparse reads, outbound bidi/uni streams, packet/stream backpressure, unaccepted FINs, late controls, datagram bounds, close/dispose and independent-owner command oracles. |
| Generic application ownership | `StreamApplication.cs`, `StreamEchoApplication.cs`, `StreamApplicationTests.cs` | Empty stream admission, original reset codes, deferred consumption/flow credit, raw bidi/uni ACK retirement, independent owners and rejection of operations after disposal. |
| Stream counts and retirement | `StreamCountWindow.cs`, `StreamCountTests.cs`, `StreamLifecycleTests.cs` | Independent closed-ID sets, 100,000 sequential retirements, implicit stream opening, delayed/late ACKs, repeated requests, closed references, pending session association, final-size checks and buffer disposal. |
| Lifetime | `ConnectionLifetime.cs` | Deadline regressions, monotonic phases, silent draining, bounded close responses and inability to revive a closed connection. |
| H3 bridge and credit | `ConnectionStreams.cs`, `WebTransportApplication.cs` | Bounded malformed H3/datagram inputs, partial reset gaps, send-credit transitions, a healthy independent connection and exact datagram echo. |
| Connection ownership | `QuicConnection.cs`, `QuicConnectionSet.cs`, `QuicConnectionLimits.cs`, `ConnectionCoreTests.cs`, `ConnectionSetTests.cs`, `SharedListener.cs`, `verify_connections.py` | Authentication before TLS allocation, configured stream credit, rejection after disposal, sibling cleanup after a failing disposer, capacity/deadline regressions, direct managed ownership mutations and simultaneous independent clients on one UDP port. |
| Scheduled execution | `QuicExecutionPool.cs`, `QuicConnectionOwner.cs`, `ExecutionPoolTests.cs`, `ConnectionOwnerTests.cs` | Fixed worker count, one executing turn per owner, coalesced wakeups, fair requeueing, queued/in-flight input budgets, copied packet ownership, shutdown during native work and once-only cleanup. Lifecycle mutations and protected/corrupted packet inputs use the shared fuzz facade. Socket-listener integration remains pending. |
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
python scripts/run-quic-development.py --openssl 'C:\path\to\OpenSSL-3.5.7\bin'
```

The defaults perform 20 seeds, each separated by 104729, starting at 1729:

| Campaign | Default local workload |
| --- | ---: |
| QUIC FsCheck | 14,000,000 generated target cases (7 x 100,000 x 20) |
| Existing FsCheck properties | 12,000,000 cases (6 x 100,000 x 20) |
| QUIC deterministic mutations | 2,000,000 inputs |
| Recovery/lifetime state machines | 400,000 traces; 19,200,000 commanded transitions |
| Native connection/TLS mutation | 100,000 fresh contexts when `--openssl` is supplied |
| Shared-owner native traces | 100,000 admission/retirement commands when `--openssl` is supplied |
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
dotnet $hostDll schannel-self-test
python test/EmbedIO.Quic/verify.py --dotnet dotnet --assembly $hostDll --openssl schannel-rsa --output TestResults/quic-schannel-rsa
python test/EmbedIO.Quic/verify_webtransport.py --dotnet dotnet --assembly $hostDll --openssl schannel-ecdsa --peer TestResults/quic-feasibility/webtransport-peer.exe --output TestResults/quic-schannel-ecdsa-h3
python test/EmbedIO.Quic/verify_connections.py --dotnet dotnet --assembly $hostDll --openssl schannel-rsa --peer TestResults/quic-feasibility/webtransport-peer.exe --output TestResults/quic-schannel-connections
python test/EmbedIO.Quic/verify_sni.py --dotnet dotnet --assembly $hostDll --output TestResults/quic-schannel-selection
dotnet $hostDll fuzz-native schannel-rsa TestResults/quic-schannel-public.pem unused-for-schannel 104729 100000 TestResults/quic-schannel-native
```

The existing Windows CI test leg is configured to reuse its already provisioned,
hash-locked aioquic peer for both credential profiles, native lifetime checks and
1,000 native mutation cases per profile plus the six certificate-selection cases. This adds no job or dependency installation.
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
in progress and for native cleanup. The caller supplies a monotonic clock. This component is ready for socket routing
integration; the existing development socket loop has not switched to it yet.

Local execution/packet ownership regressions, 10,000 FsCheck cases per property,
100,000 managed mutations, 4,000 recovery/lifecycle traces and 72 existing
independent-peer cases passed. Two concurrent first-flight cases passed with each
of OpenSSL, Schannel RSA and Schannel ECDSA, followed by packet-key cleanup and
Schannel native-resource checks. This is first-flight evidence for scheduled
owners; the full peer cases still use the existing socket loop.

The eighth managed target and lifecycle traces exercise worker/owner cleanup;
both coverage runners instrument these source types. Fresh 60-second campaigns
completed 148,194 libFuzzer and 25,737 AFL++ executions without retained crashes
or hangs. Ordinary build hashes remained unchanged. AFL reported 40.04% map
stability with concurrent scheduling, so the execution counts do not imply
deterministic edge coverage. Debug/Release diagnostics, changed-file formatting,
source-quality and suppression guards passed.

## Remaining production acceptance work

1. Promote the bounded shared listener into the production ownership/transport
   interfaces, with application configuration, certificate lifetime and graceful
   shutdown parity. Complete SNI/client-certificate/credential-reload behavior,
   non-exportable-key support on every supported platform, cipher-suite parity and
   bounded native TLS worker scheduling. Its current admission limits and ten-second host deadline are
   development-host bounds; the offline actor model is not a listener load test.
2. Validate longer transfer/loss/endurance patterns and expose sustainable stream
   lifecycle through the production application transport boundary. The development
   host already replenishes stream counts and retires stream/application buffers.
   Receive and recovery histories retire within fixed bounds; packet encryption
   enforces per-key AES-GCM limits and ACK-confirmed local/peer key generations.
3. Complete transport/application error classification, stateless reset, Retry,
   path handling, post-handshake TLS, resumption and any intentionally supported
   0-RTT policy. Features outside the supported profile must fail explicitly.
4. Production HTTP/3 provider integration, request dispatch, application auth,
   WebTransport API/controller integration, cancellation and disposal parity.
5. Independent peers on Windows/Linux/macOS, browser compatibility, dependency
   packaging, and startup/warm/tail-latency measurements. Windows embedded hosting
   leads; meaningful warm regressions are not accepted for cold-start gains.
6. Exact-source final build/diagnostic/security/package gates and full existing
   HTTP/1-3 regressions before any change of the production provider.

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
