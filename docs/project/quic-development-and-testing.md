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
| Recovery and congestion | Physical packet records retain logical delivery ownership through retransmission and late ACKs. Monotonic-time tests exercise packet/time loss, RTT, PTO probes, pacing, NewReno, application-limited ACKs and persistent congestion. |
| HTTP/3 and WebTransport bridge | Existing H3/QPACK/capsule/session code is linked into the prototype. The bridge handles SETTINGS, authorized CONNECT, exact Origin admission, bidi/uni streams, datagrams, independent sessions and reliable partial resets. quic-go provides a separate wire peer. |
| Sustained transfers | A fixed sliding receive window retains final-size history and compacts consumed H3 input. MAX_DATA/MAX_STREAM_DATA track consumption, including reset gaps after their reliable prefix. Independent cases transfer 512 KiB, with small peer windows and loss. |
| Connection lifetime | Active, closing, draining and closed states use injected time. Idle expiry is silent; close replies are bounded; repeated sends or duplicate close traffic do not extend retirement. Windows ICMP reset notifications during retirement are handled explicitly. |
| Fuzz integration | All existing fuzz engines now have direct QUIC core entry points. Packet-header and ACK-range parsing are shared between the actual connection and fuzz targets. Dedicated native campaigns also exercise fresh OpenSSL contexts through authenticated Initial frames. |

These stages are a bounded transport profile. They are not completion of
full RFC conformance, production provider integration or browser support.

## Source and responsibility map

The project and namespace are named **EmbedIO.Quic**.

All files in this table are under `test/EmbedIO.Quic` unless stated.

| Surface | Files | Test approach |
| --- | --- | --- |
| Bytes and packet boundaries | `Wire.cs`, `PacketHeader.cs`, `AckRanges.cs` | Independent integer decoding, canonical sizes, header bounds, valid token construction, malformed/descending ACK ranges. |
| Packet protection | `PacketKeys.cs` | RFC vector self-tests, encrypt/decrypt identity, authenticated tamper rejection, key update separation and retained header protection. |
| Receive state | `StreamReassembly.cs`, `Wire.cs` | Absolute-position dictionary oracle against the sliding ring; reversed/duplicated CRYPTO fragments; final-size, window, overlap and once-only delivery checks. |
| Recovery/congestion | `Recovery.cs` | Deterministic algorithm cases, logical/physical accounting, duplicate ACKs, discard, loss/PTO/retransmit traces, resource bounds and independent connection ownership. |
| Lifetime | `ConnectionLifetime.cs` | Deadline regressions, monotonic phases, silent draining, bounded close responses and inability to revive a closed connection. |
| H3 bridge and credit | `ConnectionStreams.cs`, `WebTransportApplication.cs` | Bounded malformed H3/datagram inputs, partial reset gaps, send-credit transitions, a healthy independent connection and exact datagram echo. |
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

CI run 39470892485 on the preceding pushed head passed on attempt 2. Its original
macOS failure occurred during checkout DNS resolution; the targeted retry passed.
This is evidence for that head, not for subsequent uncommitted changes.

## Remaining production acceptance work

1. A real multi-connection listener with demultiplexing, owned cancellation and
   bounded resource admission; current multi-actor fuzzing is offline state
   isolation, not a production listener load test.
2. Sustainable stream-count replenishment and packet/ACK-history retirement,
   retransmission under longer transfer/loss patterns, and repeated key updates.
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
