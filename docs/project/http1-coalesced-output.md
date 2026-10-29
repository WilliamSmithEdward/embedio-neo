# HTTP/1.1 response completion and held output

This candidate is part of [#577](https://github.com/WilliamSmithEdward/embedio-neo/issues/577).
The measurements below are **INCOMPLETE descriptive evidence**: 95 of 96 planned
warm samples completed successfully. The reversed warm attempt remains
`failed`, exit 1, with its `INVALID` marker retained. Its failed Kestrel TLS
chunk sample has null performance metrics; no replacement or imputation is used.
The separate cold workload completed all 240 measured samples and 12 predeclared
warmups. These results establish neither overall equivalence nor an overall win
against Kestrel.

The wider goal remains lower cold-start and warm latency and higher throughput
than Kestrel. Warm steady-state and tail latency take priority when tradeoffs are
necessary. Windows desktop and embedded hosting are the lead environment. The
implementation and measurements here describe the source checkpoints below;
the change is absent from the published 2.19.0 packages.

## Output ownership

While the HTTP/1 connection dispatches a handler for a request without an entity
body inline, an incomplete response can hold up to 64 KiB of encoded output. Its
final write or synchronous completion combines the held head, body chunks and
ending into one transport write when they fit. A response that reaches its
declared length writes through immediately. Informational responses and
upgrade or tunnel heads also remain immediate.

When the handler returns or suspends, the connection closes the holding window
and sends remaining output. Subsequent writes pass through. With
`IgnoreWriteExceptions = false`, application writes and flushes always pass
through so transport exceptions remain observable by the caller. Requests with
entity bodies also retain write-through behavior, including a response committed
before a later request-body read fails.

If a handler for a body-free request throws after committing output, accepted
bytes are sent before the connection aborts, without a clean chunk terminator.
Committed-error cleanup uses the strong drain operation and waits for an earlier
send before aborting. Intentional response abort and listener stop attempt to
submit accepted bytes when the output gate is immediately available, then close
the transport without waiting for that send. Backpressure therefore cannot
extend shutdown. Transport-error and deadline retirement discard unsent held
output. An aborted connection does not guarantee delivery of submitted bytes.

A handler can flush and then block synchronously without returning to the
connection. One process-wide timer sends output held across two tick increments,
at a 10 ms interval. A fast inline handler has at least one complete tick to
finish. This is a scheduling interval, not a real-time latency guarantee. No
public option or API is added.

Each connection owns one registration, created on its first eligible output
window and removed at transport retirement. It enters the shared watchlist only
when output is held. Fast successive responses reuse that active epoch. A tick
prunes a quiescent entry with a publication recheck; once pruned, that idle
registration is absent from subsequent scans until another hold activates it.
An empty watchlist stops the timer. Publishing output does not register another
callback or queue a work item. A sender rechecks output age under the existing
write gate. A slow connection has at most one heartbeat sender and holds no
shared registration lock during I/O. Pooled held bytes are cleared before return,
including on write failure and connection retirement. Sent-body telemetry
advances after successful transport writes, excluding abandoned held bytes.

A completing write, or a flush of a complete zero-length or suppressed response,
closes its own holding window under the gate before awaiting transport I/O. That
operation owns its held prefix, ending and failure cleanup. The actor-only
window-close path can skip queuing a waiter behind that owner. The strong
committed-error drain remains separate and still waits for an accepted send.
The connection continues to wait for response completion before processing the
next pipelined request.

The first candidate queued a ThreadPool sender for every response's first held
byte. A controlled transport test on `4fc61c43` confirmed that the sender could
own the gate across an asynchronous write while synchronous completion blocked
in `SemaphoreSlim.Wait`. Releasing the controlled write allowed completion to
continue. This proves the contention path, without quantifying its contribution
to end-to-end benchmark results.

On `41b18af2`, five of eleven additional controlled cases exposed an unnecessary
actor waiter behind completing I/O; six cancellation, validation and drain
preservation cases passed. All eleven pass with the completing-writer ownership
change. This proves the scheduling behavior without attributing a measured tail
difference to it.

## Source, configuration and chronology

The common harness and candidate production checkpoint are
`9d62d6b0af37c1a4e8f4dda117d90e1cf71e3d05`; main is
`b30f186436b23b0a924952b9e2a231266666d01f`. Kestrel uses the same .NET 10.0.13
runtime. Binary identities are SHA-256:

| Component | SHA-256 |
|---|---|
| Common runner | `a492449af3553562e0e9d49a25e4fa42d38f28196c83ffb7aea20cbdb316eeec` |
| Candidate EmbedIO | `998281aab698a8a0eb2aa3f242dcf48506afd0870b4dc0c6a6d871c8a0c4dd20` |
| Main EmbedIO | `0f5402e283b83832c6b5e592bcd2f6ba6fe4ec742aefab219ffce0bbbcae265d` |
| Kestrel Core | `9cff42e8b03915c3b91d831cf586e5e9052d0492a08d804355578ab336be194d` |

The host is Windows 10.0.26300 x64, an AMD Ryzen 7 9800X3D with 16 logical
processors, using the Ultimate Performance plan. Server CPUs are 0-3 and client
CPUs are 4-11, with child runtime processor counts of four and eight respectively.
All engines use the same Server GC and concurrent GC configuration. Profiling is
disabled and no optional GC/JIT environment overrides are recorded. The benchmark lock
and both heavy-work slots are used. The guard detects configured competing work;
it does not establish absence of other host activity.

Each warm sample uses fresh server and client processes, five seconds of warmup,
ten seconds of requested measurement and two seconds of idle observation. Every
response is validated. There are two repetitions per engine and scenario in each
orientation, with engine order alternating by round. The candidate occupies the
candidate runner slot in AB and the baseline runner slot in BA. Kestrel uses the
common harness in both orientations. JSON retains each engine's negotiated
compression defaults.

All times below are Pacific, October 28, 2026 (UTC-07:00).

| Workload | Start | End | Retained outcome |
|---|---|---|---|
| Warm AB | 16:03:24 | 16:18:51 | Guard completed, exit 0; 48/48 response-valid |
| Original warm BA | 16:21:03 | 16:36:45 | All 48 retained separately as host-activity history |
| Observed warm BA | 16:39:54 | 16:55:11 | Guard failed, exit 1, INVALID retained; 47/48 response-valid |
| Cold AB | 16:57:43 | 16:58:25 | Guard completed, exit 0; 120 measured plus 6 warmups |
| Cold BA | 17:00:59 | 17:01:30 | Guard completed, exit 0; 120 measured plus 6 warmups |
| Same-binary A/A | 17:04:21 | 17:10:11 | Guard completed, exit 0; 18/18 response-valid |

## Warm descriptive measurements

The selected warm cohort contains all 96 planned cells: 95 response-valid observations
and one failed measurement. The failed cell is Kestrel TLS chunk, BA round 1. Its
warmup completed 851,882 requests over 64 connections; the measured phase ended
with a client-connect `SocketException`, native error 10055, after 3,160 requests
and 24 opened connections in about 0.027 seconds. The client exited 3, the server
exited 0, and neither was killed. The failed cell's partial counters are retained
as diagnostics, with null performance metrics. The cause is not established.

The table uses the median of the two orientation medians, calculated separately
for each metric. These are descriptive summaries of response-valid samples;
the BA attempt's failed guard qualification remains unchanged. Percentile
columns summarize per-sample quantiles; they are not quantiles of a pooled request
CDF. CPU and allocation figures are server CPU microseconds and allocated bytes
per completed request. Full sample values, maxima, pooled ranges and per-role
counts remain in the [measurement JSON](evidence/http1-coalesced-output-windows.json).

| Scenario | Engine | Valid/planned | req/s | CPU us/req | B/req | p99 ms | p99.9 ms |
|---|---|---:|---:|---:|---:|---:|---:|
| h1-plain-small-c64 | main | 4/4 | 226251 | 17.191 | 3079.5 | 0.5903 | 1.6783 |
| h1-plain-small-c64 | candidate | 4/4 | 225190 | 17.213 | 3055.6 | 0.6399 | 1.7407 |
| h1-plain-small-c64 | Kestrel | 4/4 | 255200 | 15.372 | 32.4 | 0.4703 | 1.1183 |
| h1-plain-small-c256 | main | 4/4 | 223112 | 17.470 | 3072.2 | 1.8175 | 3.0143 |
| h1-plain-small-c256 | candidate | 4/4 | 221890 | 17.481 | 3103.6 | 1.9199 | 3.0591 |
| h1-plain-small-c256 | Kestrel | 4/4 | 248865 | 15.787 | 33.1 | 1.9647 | 3.0463 |
| h1-plain-pipe16-c16 | main | 4/4 | 279546 | 13.960 | 3013.9 | 1.6831 | 2.8863 |
| h1-plain-pipe16-c16 | candidate | 4/4 | 279939 | 14.026 | 3045.8 | 1.7663 | 2.7967 |
| h1-plain-pipe16-c16 | Kestrel | 4/4 | 2345977 | 1.669 | 32.0 | 0.2767 | 1.8751 |
| h1-plain-chunk13-c64 | main | 4/4 | 139234 | 27.829 | 3229.6 | 0.8943 | 2.3871 |
| h1-plain-chunk13-c64 | candidate | 4/4 | 222630 | 17.462 | 3287.6 | 0.5855 | 2.0159 |
| h1-plain-chunk13-c64 | Kestrel | 4/4 | 249407 | 15.665 | 192.4 | 0.5119 | 1.6511 |
| h1-plain-json1k-c64 | main | 4/4 | 109116 | 35.525 | 4403.6 | 1.0591 | 2.6687 |
| h1-plain-json1k-c64 | candidate | 4/4 | 150376 | 25.863 | 4426.6 | 0.7967 | 2.3039 |
| h1-plain-json1k-c64 | Kestrel | 4/4 | 166501 | 23.199 | 1256.7 | 1.3263 | 2.4767 |
| h1-plain-stream1m-c16 | main | 4/4 | 4607 | 860.552 | 3245.3 | 5.0815 | 6.5919 |
| h1-plain-stream1m-c16 | candidate | 4/4 | 7523 | 526.619 | 3303.0 | 3.6991 | 4.3391 |
| h1-plain-stream1m-c16 | Kestrel | 4/4 | 8445 | 463.475 | 6652.3 | 2.9247 | 10.0863 |
| h1-tls-small-c64 | main | 4/4 | 207198 | 18.562 | 3107.4 | 0.6479 | 2.1087 |
| h1-tls-small-c64 | candidate | 4/4 | 210786 | 18.509 | 3106.9 | 0.5919 | 1.6991 |
| h1-tls-small-c64 | Kestrel | 4/4 | 231853 | 16.957 | 32.5 | 0.6239 | 1.7231 |
| h1-tls-chunk13-c64 | main | 4/4 | 133676 | 28.779 | 3276.9 | 0.9695 | 2.1759 |
| h1-tls-chunk13-c64 | candidate | 4/4 | 206357 | 18.853 | 3326.4 | 0.6063 | 1.2927 |
| h1-tls-chunk13-c64 | Kestrel | 3/4 | 213671 | 18.214 | 192.5 | 0.5583 | 1.3039 |

Every group has two valid samples per orientation except Kestrel TLS chunk:
two in AB and one in BA, three of four planned samples. Its role medians still
receive equal weight; the missing observation is not inferred. Comparisons
involving that group have incomplete coverage. Complete candidate/main pairs do
not make the overall campaign complete.

The small plaintext rows do not establish tail equivalence with main. Candidate
p99 is 0.6399 versus 0.5903 ms at concurrency 64 and 1.9199 versus 1.8175 ms at
concurrency 256. The later same-binary calibration below provides additional
variation observations, without dismissing those differences automatically.

## Separate cold measurements

Cold uses a fresh server process for every sample and one long-lived client
warmed by an excluded iteration 0. Each sample validates 32 requests on its first
connection and one on a second connection. There are 20 measured iterations per
engine, transport and orientation: 240 measured samples and 12 excluded warmups,
all response-valid. The exclusions were declared before measurement and remain
in the evidence.

Ready is process start to the server's READY report. First is the first full
request/response on a new connection, including connection and TLS setup where
applicable; it is not TTFB. Second is the next complete request on that connection.
Second connection measures one full request on another connection. These figures
are startup progression, separate from warm steady-state load tails. Fresh
processes do not imply cold OS or file caches. Values are medians of the two
orientation medians, in milliseconds.

| Transport | Engine | n | Ready ms | First ms | Second ms | Second connection ms |
|---|---|---:|---:|---:|---:|---:|
| h1 | main | 40 | 110.354 | 28.058 | 4.142 | 0.965 |
| h1 | candidate | 40 | 109.163 | 28.437 | 4.260 | 0.895 |
| h1 | Kestrel | 40 | 120.747 | 17.320 | 0.895 | 1.011 |
| h1tls | main | 40 | 124.738 | 54.400 | 3.980 | 3.291 |
| h1tls | candidate | 40 | 125.274 | 54.502 | 4.118 | 3.256 |
| h1tls | Kestrel | 40 | 150.354 | 32.765 | 0.469 | 3.264 |

All 240 measured cold rows recorded whole-host averages below 15 busy logical
CPUs; the AB maximum was 12.3062 and the BA maximum was 5.0062. All six predeclared
cold AB warmups recorded at least 15, with a maximum of 16.0905. Saved snapshots
identify concurrent nonbenchmark test activity after the warm orientation ended
and during part of the cold warmup phase. The snapshots are sampled windows with
gaps. They do not establish the cause of every cold timing, the earlier warm
stall, or native error 10055. No full-attempt quiet claim is made. See the
[measurement context](evidence/http1-coalesced-output-measurement-context.json).

## Same-binary A/A calibration

Both runner roles execute the same candidate source and binaries above. Three
rounds cover plaintext small responses at concurrency 64 and 256 and TLS small
responses at concurrency 64. All 18 samples completed successfully; the guard
completed with exit 0. Recorded whole-host averages are finite and below 15 busy
logical CPUs, with a maximum of 10.7099. This is a separate descriptive workload
and is not pooled with the candidate/main or cold measurements.

Here candidate and baseline mean harness slots, not different implementations.
Each percentile entry is a per-role median followed by the full sample range.

| Scenario | Harness role | n | req/s median | p99 ms median [min, max] | p99.9 ms median [min, max] |
|---|---|---:|---:|---|---|
| h1-plain-small-c64 | candidate slot | 3 | 224966 | 0.5951 [0.5823, 0.6847] | 2.1247 [1.7407, 2.2527] |
| h1-plain-small-c64 | baseline slot | 3 | 222937 | 0.5887 [0.5695, 0.7167] | 2.0735 [1.8943, 2.1503] |
| h1-plain-small-c256 | candidate slot | 3 | 221910 | 1.8943 [1.8175, 1.9455] | 3.0975 [3.0719, 3.3279] |
| h1-plain-small-c256 | baseline slot | 3 | 223693 | 1.7663 [1.7663, 1.7919] | 2.9951 [2.6879, 3.0975] |
| h1-tls-small-c64 | candidate slot | 3 | 210465 | 0.6079 [0.5631, 0.7231] | 2.5855 [2.2527, 3.9423] |
| h1-tls-small-c64 | baseline slot | 3 | 205442 | 0.6335 [0.6271, 0.6463] | 2.0991 [2.0223, 3.6351] |

For the identical binary, the concurrency-256 p99 role medians differ by 7.25%
and p99.9 by 3.42%; the TLS p99.9 role medians differ by 23.17%. These observations
are not a calibrated noise floor, a significance test, or proof that a production
regression can be ignored. They do not prove TLS stability or explain earlier
stalls. CPU, allocation, per-round ratios, maxima and full ranges are retained in
the [A/A JSON](evidence/http1-coalesced-output-aa.json).

## Retained host-activity history

The original BA orientation is retained in full, including its healthy and
stalled samples, in the [host-activity JSON](evidence/http1-coalesced-output-host-activity.json).
Four separate measurement windows averaged at least 15 whole-host busy logical
CPUs while measured child CPU was low. The approximate sample-boundary interval
is 16:26:50-16:28:15 Pacific; it is a chronology of separate roughly ten-second
windows with gaps, not a continuous CPU trace over that interval. The source of
the activity is unidentified. Residual CPU is whole-host CPU minus measured
server and client CPU; the windows are not perfectly aligned and this subtraction
does not identify a process or cause.

The entire original orientation is absent from the selected warm table. Healthy
rows were not selected from it as replacements. The one subsequent full BA
orientation is the failed attempt reported above, and its failed cell remains
present. Earlier fixed-role first-look figures in #577 and earlier source
checkpoints are historical context, not evidence for a heartbeat gain or a TLS
repair. Historical captures with a similar host-activity phenotype are not pooled
with the current cohort.

## Validation

All 55 new cases and 96 existing compatibility cases pass on Windows, 151 total.
That run preceded an equivalent explicit-local-type to `var` edit in one test.
The final formatted source passes all 55 new cases plus all 36 cases in the
affected typed-route family, 91 total. Four selected single-write assertions fail
on exact main: synchronous variants use three writes, while asynchronous variants
already use two asynchronous writes before completion. The baseline proof adapts
only the unavailable holding-window setup; main source and the selected assertion
method are hash-verified unchanged.

The source-quality scripts pass all 588 source files and 16 project scans.
Changed production source and tests have no informational, warning or error
diagnostics; hidden hints remain report-only. Debug and Release builds pass for
both library targets. Changed-file formatting, whitespace and analyzer guards
pass, as do all four existing allocation ceiling checks. Allocation-gate timings
are informational, separate from the warm measurements.

The staged complete local Windows suite reports 7,591 cases in 8m14.6s:
7,586 passed, four expected skips and one client-connect failure with native
Windows error 10055, before an unrelated route handler runs. Its production
implementation is the source later committed as `9d62d6b0`; its copied test
runner preceded the equivalent local-style edit. The later 91-case run is a
focused confirmation, not a successful replacement full-suite run. Exact-head
hosted full-suite checks are required for the PR; this guide makes no hosted-pass
claim. Test discovery has a floor of 7,591.

A new BCL-only .NET 10.0.13 control observes one native 10055 in 18,000 sequential
loopback bind/dispose operations, without loading EmbedIO. The failure follows
selected port 65,531; the next independent operation selects 49,153. This records
an independent bind-allocation exposure on the host, without establishing the
cause of either client-connect failure. It is not an exact reproduction of a
historical probe whose source was unavailable. No OS settings, assertions or
request retries were changed. Counts, source hashes, runner identities and gate
provenance are retained in the [validation JSON](evidence/http1-coalesced-output-validation.json).

The changed HTTP/1 benchmark client also passes separate build, whitespace and
informational-diagnostic checks. A one-connection refusal probe verifies the
failure-only stack, endpoint and socket-code diagnostics, unchanged failure
result, no retry and cleanup. Successful binding, response validation and
certificate pinning remain unchanged; the cold client uses a separate unchanged
factory.

Regression coverage includes one-write inline responses, blocking-handler flush
promptness, strict-mode errors, pipelining and keep-alive order, window closure,
tick aging, active-watchlist epochs, registration retirement, held-buffer cleanup,
telemetry, committed errors and nonblocking shutdown under backpressure.

## Remaining work and reproduction

Pipelined response batching and scheduling remain substantial work: the observed
pipeline rates are about 280k req/s for the candidate and 2.346M for Kestrel.
Small-response allocation remains about 3 KiB per request versus about 32 bytes
for Kestrel in these workloads. All valid allocation observations are retained,
including candidate plaintext-small values from about 2,900 to 3,114 B/req and
TLS-small values from about 2,990 to 3,164 B/req; their variation is not explained
or removed. Cold first and second requests remain slower than Kestrel in this
measurement. Small-response warm tails, TLS stalls and the native socket
failures require further evidence. The cold figures and A/A observations do not
justify declaring warm equivalence or the overall #577 goal complete.

The [benchmark README](../../test/EmbedIO.LoadBenchmark/README.md) documents
preparation, role reversal, cold semantics and shared-host controls. Reproduce
the recorded source hashes, scenarios, CPU assignments and timing settings before
comparing another campaign. Portable evidence is split by purpose:

- [Incomplete warm/cold measurements](evidence/http1-coalesced-output-windows.json): all 348 cells, failed metrics null, per-role and pooled ranges, role-balanced summaries and coverage.
- [Validation](evidence/http1-coalesced-output-validation.json): tests, gates, source binding and the independent socket control.
- [Original host-activity orientation](evidence/http1-coalesced-output-host-activity.json): all 48 original BA rows and historical identity context.
- [Measurement context](evidence/http1-coalesced-output-measurement-context.json): sampled later concurrent activity, retained warmups and explicit limits.
- [Same-binary A/A](evidence/http1-coalesced-output-aa.json): all 18 samples, role and round comparisons, counters and ranges.
