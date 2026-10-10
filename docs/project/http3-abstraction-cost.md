# HTTP/3 transport abstraction cost (October 2026)

Program #181 is moving the HTTP/3 engine from `System.Net.Quic` to a native
QUIC provider (draft PR #231). The first step inserted a provider-neutral
ownership boundary, `Http3TransportConnection` and `Http3TransportStream`,
between the protocol workers and the transport. The application listener
still runs on `System.Net.Quic` through the `SystemQuic*` wrappers, so this
document measures the cost of that boundary alone. It is not a measurement of
the native provider, which does not yet carry application traffic.

Measurements use the separate-process harness in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md) with
the same scenarios as the [HTTP/3 request path work](http3-performance.md).
They are loopback development measurements on a shared machine, not a public
benchmark.

## Result

The boundary costs nothing measurable on large responses, uploads and
connection churn, and a small, consistent allocation on every request stream.
On 13-byte responses with 8 connections x 32 streams the candidate allocated
about 67 bytes more per request in every round; its throughput and CPU medians
were 5% and 4% worse, but the two engines' rounds interleave (candidate rounds
ranged 211,607 to 274,714 req/s, baseline 215,512 to 242,390), so that
difference is inside the round-to-round spread of one engine.

| Scenario | Base `e733402` | Candidate `e52da5f` | Candidate / base |
| --- | --- | --- | --- |
| 13 B, 8 x 32 (4 rounds) | 227,117 req/s, 28.9 us, 12,457 B, p99 11.83 ms | 215,666 req/s, 30.0 us, 12,524 B, p99 13.16 ms | 0.950 / 1.038 / 1.005 / 1.113 |
| QUIC handshake per request, 16 concurrent (3 rounds) | 1,823 req/s, 1,071.6 us, 116,402 B, p99 24.78 ms | 1,807 req/s, 1,073.8 us, 120,084 B, p99 24.99 ms | 0.991 / 1.002 / 1.032 / 1.008 |
| 1 MiB response, 4 x 4 (3 rounds) | 1,773 req/s, 2,562.6 us, 14,868 B, p99 12.60 ms | 1,760 req/s, 2,554.5 us, 15,038 B, p99 12.70 ms | 0.993 / 0.997 / 1.011 / 1.008 |
| 1 MiB upload, 4 x 4 (3 rounds) | 1,627 req/s, 2,522.0 us, 85,311 B, p99 12.39 ms | 1,635 req/s, 2,501.1 us, 85,466 B, p99 12.80 ms | 1.005 / 0.992 / 1.002 / 1.033 |
| 1 MiB flushed per 16 KiB, 4 x 4 (3 rounds) | 1,672 req/s, 3,146.2 us, 44,515 B, p99 11.67 ms | 1,649 req/s, 3,254.5 us, 44,649 B, p99 12.08 ms | 0.986 / 1.034 / 1.003 / 1.035 |

Cells are medians of accepted samples: requests per second, server CPU
microseconds per request, server bytes allocated per request, client-observed
p99. The ratio column is candidate over base for those four values in order.
Every sample validated every response byte and every uploaded byte; no
accepted sample failed and no server socket remained open after any sample.
Kestrel ran as a reference on the small scenario only: 347,210 req/s, 19.5 us
and 2,529 B per request, p99 8.04 ms (4 rounds), on the same host and client.

First-chance exceptions are unchanged in kind and count per window. On the
small scenario both engines raise about 75 to 83 `QuicException` and 20 to 40
cancellation exceptions per 15-second window of about 3.3 million requests.
On churn both raise one `QuicException` per connection (about 262,000 to
335,000 per window), which is the runtime's stream shutdown behaviour and is
present on the base as well.

## Where the extra bytes come from

A separate profile run (`--profile`, runtime allocation-tick sampling, one
round per engine, small scenario) attributes the difference. The harness kept
only its thirty largest allocation types until this change; the wrapper
objects sit far below that cut, so the profile runner was rebuilt to keep every
sampled type (candidate uncommitted changes `ServerHost.cs` and `README.md`
only, runner SHA-256 `35651A06...E7059F`, candidate core unchanged, baseline
core rebuilt as `3C26D246...A1DED2` from the same commit). Sampled totals
matched the exact counters within 0.3% (12,639 sampled against 12,603 exact
bytes per request on the base, 12,674 against 12,640 on the candidate).

| Type | Base B/req | Candidate B/req |
| --- | --- | --- |
| `SystemQuicTransportStream` (one per accepted request stream) | 0 | 32 |
| `AsyncStateMachineBox<Http3TransportStream, SystemQuicTransportConnection.AcceptInboundStreamAsync>` | 0 | 24 |
| Closure `<>c__DisplayClass40_0` / `41_0` (same closure, renumbered by the added constructor) | 62 | 61 |
| `CancellationPromise<VoidTaskResult>` (cancellable `WaitAsync` per request, pre-existing) | 201 | 207 |
| `System.Net.Quic.ResettableValueTaskSource` (runtime, pre-existing) | 273 | 263 |
| `System.Net.Quic.QuicStream` (runtime, pre-existing) | 183 | 183 |

Every other type moved by at most 6 bytes per request in either direction,
which is sampling noise. The two new rows sum to 56 sampled bytes against the
67 exact bytes measured in the comparison; the remainder is the sampling
granularity (one tick per roughly 100 KB per heap). The 24-byte row is the
boxed state machine of the `async` wrapper method, allocated only when the
runtime's accept has not completed by the time the wrapper awaits it; its
weight shows that accepts complete asynchronously on roughly a third of
request streams at this load.

Outbound wrapper streams (control, QPACK encoder and decoder) are created
once per connection and did not register per request. Writes and reads pass
through virtual calls without allocating; the three asynchronous methods of
`Http3TransportStream` return the runtime's own `ValueTask` instances.

### Proposed fixes, in order of value

1. **Make the native stream the transport stream.** When the native provider
   carries application streams, its stream object should derive from
   `Http3TransportStream` directly and be reused per connection or pooled,
   rather than wrapping a second object. That removes the 32-byte row and
   avoids a new one. This is the main agent's design decision in PR #231.
2. **Remove the `async` wrapper boxes.** `SystemQuicTransportConnection.AcceptInboundStreamAsync`
   and `OpenOutboundStreamAsync` can test `IsCompletedSuccessfully` on the
   runtime's `ValueTask` and return a completed `ValueTask<Http3TransportStream>`
   synchronously, falling into an `async` helper only for the pending case.
   That removes about 24 bytes per request on the System.Net.Quic path at no
   semantic cost and is the same pattern the native provider should use for its
   own accept path.
3. **Pre-existing per-request costs are larger than the boundary.** The
   cancellable `WaitAsync` promise (about 200 B), the runtime's
   `ResettableValueTaskSource` and `QuicStream` (about 450 B together),
   cancellation `CallbackNode`s (about 310 B), the request `Uri` (about 110 B)
   and `Byte[]` buffers (about 360 B) each cost more than the whole boundary.
   They are listed in the [HTTP/3 request path](http3-performance.md) document
   and are unchanged here; the native provider removes the two runtime rows
   only if its own stream objects are reused.

## Environment and method

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET SDK 10.0.401, runtime 10.0.12; MsQuic `msquic.dll` SHA-256 `B7158D42...9157E8`; System.Net.Quic `AF5B52CD...E8BBE73` |
| Kestrel | shared framework 10.0.12 (core `62B5AB79...CC239`, QUIC transport `6A6919A8...B591DE`) |
| Base | `e73340201e95e21fb2c06071a7120ac04a16220c` (PR #230 merged into `codex/managed-http-engine`), core SHA-256 `9DD3ADCEFFD897A38C6C05A4A8C54DAD7F86FFA3C1E7B380BDE3AC32D89BF4F0` |
| Candidate | `e52da5f17657c3fc6f1a711983c68018fb5b9389` (PR #231 head when the runners were built), core SHA-256 `C9C56616FD7B9CBF21EE47B703C499C700E933D42D4E154DA96856CAB3DB44DA`. PR #231 merged as `54f4c5c` from head `33d65a6` while the runs were in progress; between `e52da5f` and `33d65a6` only the native provider files (`MsQuicApi`, `MsQuicNativeConnection`, `MsQuicNativeListener`, `MsQuicNativeStream`) changed, and none of them is on the application listener's path, so the measured boundary is the one in the merged base |
| Runner | one harness build, SHA-256 `476EFB25AA5E93E4B5585644796213E037848A87CF1B152BB5EAB588387EC8E6`; the base copy differs only in `EmbedIO.dll` |
| CPU sets | server logical CPUs 0-7, client 8-15, set at process creation |
| Schedule | `--modern-baseline`; engine order alternating by round; 5 s warmup, 15 s measurement (churn capped at 5 s each), 2 s idle, fresh processes per sample, no retries |

Both cores were built with `ContinuousIntegrationBuild` from `git archive`
snapshots by `scripts/prepare_load_benchmark.py`; the candidate checkout had
no uncommitted production or harness changes (`runners.json`). The core's
informational version embeds its commit, so the two core hashes differ even
where the source does not. Two builds of the base commit from `git archive`
snapshots in different directories also produced different core hashes
(`9DD3ADCE...` for the comparison, `3C26D246...` for the profile), so the
harness README's statement that hashes are independent of the build directory
does not hold for the core; record the hash of the binary actually run, as
`runners.json` does.

The machine was shared with two other agents running builds and test suites.
Every accepted run was started by `scripts/guarded_load_benchmark.py`: it waited
for the shared `TestResults/BENCHMARK-LOCK.txt` to be absent and for 120 seconds
without a foreign test or load process, created the lock only if absent, and
checked every 10 seconds during the run that the lock was still its own and
that no foreign process had appeared. Earlier attempts are kept and are not
evidence:

- The first small-response and churn-plus-large runs (09:50 to 10:07 UTC) were
  made by a revision of the guard that locked a path inside the worktree instead
  of the primary checkout's `TestResults`. They did not hold the shared lock, and
  the main agent's builds and test suites ran during both. Their directories are
  marked `INVALID`; one of their samples (base, 1 MiB response, 993 req/s with
  the client at 21% CPU) shows the disturbance. The guard now resolves the lock
  beside the common git directory, so every worktree uses the same file.
- Two runs waited for other owners' locks (`http3-native-audit`, the main agent's
  native acceptance suites) before starting; the wait is in each `*.guard.log`.

Raw samples, per-sample server and client stderr, `environment.json`,
`runners.json`, guard journals and the invalid attempts are under the ignored
`TestResults/load-benchmark` of the benchmark worktree.

## Repeating the comparison for the native provider

The harness compares two core assemblies with identical harness code, so the
same procedure measures the native provider once it carries application
streams. Nothing in the harness selects a provider: the server uses
`HttpListenerMode.EmbedIOHttp3`, and whichever transport that mode binds is
what gets measured. When provider selection becomes an option of the listener,
the harness's `ServerHost` needs a corresponding server argument; until then,
build the candidate from the commit whose default is the native provider.

```sh
# in a clean worktree of the candidate commit
python -I scripts/prepare_load_benchmark.py --baseline e52da5f17657c3fc6f1a711983c68018fb5b9389
python -I scripts/guarded_load_benchmark.py --owner "<agent> (<branch>)" \
    --output TestResults/load-benchmark/results-h3 --own-marker "<absolute worktree path>" -- \
    dotnet TestResults/load-benchmark/runners/candidate/EmbedIO.LoadBenchmark.dll run \
    --output TestResults/load-benchmark/results-h3 \
    --baseline-dir TestResults/load-benchmark/runners/baseline --modern-baseline \
    --candidate-revision <candidate> --baseline-revision e52da5f17657c3fc6f1a711983c68018fb5b9389 \
    --scenarios h3- --engines candidate,baseline,kestrel --rounds 3 --server-cpus 0-7 --client-cpus 8-15
python -I scripts/summarize_load_benchmark.py TestResults/load-benchmark/results-h3-a1 --compare baseline candidate
```

Use `e52da5f` (this document's candidate) or a later wrapper-based commit as
the baseline for that comparison, so the native provider is measured against
the same abstraction rather than against the pre-abstraction engine. Keep the
small, churn, large, upload and flushed-streaming scenarios; the native provider
changes the per-stream, per-connection and per-write paths that they separate.
A run that completes in one orchestrator invocation keeps engine order
alternating by round; splitting scenarios across invocations, as was done here
to fit the operator's command timeout, keeps that property within each
invocation. Profile separately (`--profile`, and `--trace-tool` with a pinned
dotnet-trace) and never from comparison samples.

## Limits

- Loopback on one Windows host; closed-loop `HttpClient` clients at 41% to 75%
  of their CPUs. Overload behaviour, packet loss and real networks are absent.
- Three rounds for all but the small scenario. The small scenario's medians move
  by several percent when one round is an outlier, as the candidate's fourth
  round shows; the allocation difference is the only effect that repeats in every
  round.
- Background CPU on the churn scenario is about 80 to 105 CPU-seconds per
  15-second window on both engines and on the earlier accepted runs of the same
  host, so it is loopback UDP and QUIC work outside the two measured processes,
  not foreign load.
- Linux and macOS were not measured. The native provider's own performance is
  not characterized here at all.
