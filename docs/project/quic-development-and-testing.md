# EmbedIO.Quic development and testing record

This record covers the development path, test architecture, reproducible local
campaigns and remaining acceptance work for the original C# QUIC engine. It is
maintained with the implementation. The feature is pending in draft PR #506;
production HTTP/3 still uses its existing MsQuic provider. No package release or
public WebTransport API is implied by this record.

See [the feasibility profile](quic-engine-feasibility.md) for standards references,
exact dependency pins, bounds and independent-peer setup. The implementation is
standards-based, not a formally separated clean-room project. OpenSSL supplies
TLS; .NET cryptography supplies AES-GCM, AES and HKDF. Our code owns the transport.

## Development sequence

| Stage | Implementation and validation |
| --- | --- |
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
| Fuzz integration | All existing fuzz engines now have direct QUIC core entry points. Packet-header and ACK-range parsing are shared between the actual connection and fuzz targets. Dedicated native campaigns also exercise fresh OpenSSL contexts through authenticated Initial frames. |

These stages are a bounded transport profile. They are not completion of
full RFC conformance, production provider integration or browser support.

## Source and responsibility map

The project and namespace are named **EmbedIO.Quic**.

All files in this table are under `test/EmbedIO.Quic` unless stated.

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
| Connection ownership | `ConnectionSet.cs`, `SharedListener.cs`, `ConnectionSetTests.cs`, `verify_connections.py` | Capacity/deadline/disposal regressions, seeded admission/retirement mutations and simultaneous independent clients on one UDP port. |
| Native TLS boundary | `OpenSslTls.cs`, `ProbeConnection.cs`, `NativeFuzz.cs` | Fresh native contexts, authenticated Initial frame/CRYPTO mutations, unauthenticated-packet isolation, cleanup and amplification accounting. This is not coverage instrumentation of OpenSSL itself. |
| Independent transport peer | `verify.py` | aioquic positive/negative handshake, exact echo, corruption, ALPN, flow-control and packet-loss/reordering cases. |
| Independent H3 peer | `peer/main.go`, `verify_webtransport.py` | quic-go/qpack interoperability, sessions, resets, datagrams, credit, sustained transfers, key update and close/idle behavior. |

`QuicFuzzChecks`, `StreamModel`, `QuicFuzzState` and `BridgeFuzz` are test-only
oracles/adapters. Merely loading the managed fuzz facade neither opens a socket nor
loads OpenSSL. No new production dependency group or library API is introduced.

## Fuzzing engines

The seven managed target selectors are integers, packet protection, reassembly,
recovery, lifetime, WebTransport bridge, and packet headers/ACKs. Raw inputs are
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

- **FsCheck:** seven additional properties use arbitrary bounded byte arrays with
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

The 373e7df checkpoint's 38 hosted checks are green, including the corrected
Windows tus recovery fixture. The async increment needs its own exact-head
hosted results after the next push.

CI run 39470892485 on the preceding pushed head passed on attempt 2. Its original
macOS failure occurred during checkout DNS resolution; the targeted retry passed.
This is evidence for that head, not for subsequent uncommitted changes.

## Remaining production acceptance work

1. Promote the bounded shared listener into the production ownership/transport
   interfaces, with application configuration, certificate lifetime and graceful
   shutdown parity. Its current admission limits and ten-second host deadline are
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
| [#303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303), reliable reset | Close after current-wire reset is integrated and validated in the actual supported provider, beyond this experiment. |
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
