# HTTP connection teardown exception attribution

[Issue #282](https://github.com/WilliamSmithEdward/embedio-neo/issues/282)
tracks profiling and reduction of routine connection-teardown exceptions under
[PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182). This is an
initial attribution result, not a production performance correction or completed
resolution of the issue. No production code, error propagation, cancellation
contract, dependency or listener default changes in this work.

The engine measured here is `b3e55b5a41e68fb252a95f2ba22dafc1a6637cbb`.
The historical roughly 25 throws per 1,000 requests came from a different
revision and workload under PR #276. That rate cannot be treated as a current
per-request constant: changing requests per connection changes the denominator.

## Measurement correction

The load harness previously canceled its resource sampler's `PeriodicTimer` at
the end of every measurement. Its own `OperationCanceledException` entered the
process-wide first-chance census. It now disposes the timer, completing its
outstanding tick with `false`, without throwing. The `exception-accounting-check`
command fails with the unchanged base sampler and passes with the correction.
It checks immediate and pending-tick shutdown, excludes a pre-window control,
and proves caught application errors and caller cancellation still count.
This corrects this harness's observations; it does not establish that the
separate endurance campaign's reported rate had this same source.

Cumulative exception snapshots separate startup, warmup/settle, load/client
cleanup, post-load idle, and listener stop/disposal. The summary retains each
exception key and reports both requests and opened client connections. Counters
are observations at phase boundaries, not synchronization barriers for every
transport callback. The load phase includes client disposal; delayed teardown
can appear in the following idle phase. Rethrows count again. A stack's first
capture by type/message is not an exhaustive ownership attribution.

## Initial Windows observations

The retained final campaign uses the same frozen runner for EmbedIO and Kestrel
on Windows 10.0.26300 x64, .NET/ASP.NET Core 10.0.12, Ryzen 7 9800X3D (16 logical
CPUs), with server CPU set 0–1 and client CPU set 2–3. It uses one connection and
one stream, 0.2-second warmup, 0.5-second load and 0.5-second idle, two rounds with
engine order reversed on round two. Diagnostic stacks are enabled. The engine
assembly and runner identities are retained in the campaign's environment record.

The host was running other endurance campaigns and test suites. More than 10,000
TCP TIME_WAIT entries were observed before profiling. The diagnostic run uses a
15,000-entry admission threshold rather than the usual 4,000; no OS limit or
firewall rule was changed. These short, contended runs establish exercised paths
and throw counts only. CPU, allocations, tail latency and retention observations
remain recorded but do not establish quiet-host engine ratios or an improvement.

The preceding corrected one-round campaign passed all 14 samples, with complete
response validation, successful child exits, no forced child termination and
zero remaining server TCP sockets. Its load/client-close results were:

| Workload | EmbedIO requests / opened connections | EmbedIO throws | Kestrel requests / opened connections | Kestrel throws |
| --- | ---: | ---: | ---: | ---: |
| HTTP/1.1 plain, close every 100 requests | 6,635 / 67 | 0 | 7,068 / 71 | 0 |
| HTTP/1.1 TLS, persistent | 6,432 / 1 | 0 | 6,318 / 1 | 0 |
| HTTP/2 cleartext, persistent | 3,722 / 1 | 0 | 4,865 / 1 | 0 |
| HTTP/2 TLS, persistent | 2,228 / 1 | 0 | 4,002 / 1 | 0 |
| HTTP/2 TLS, one request per connection | 126 / 126 | 132 | 131 / 131 | 140 |
| HTTP/3, persistent | 1,151 / 1 | 18 | 1,832 / 1 | 12 |
| HTTP/3, one request per connection | 94 / 94 | 1,688 | 112 / 112 | 1,344 |

All post-load idle phases in that campaign recorded zero throws. TCP listener
stop/disposal recorded seven throws for EmbedIO and two for Kestrel; HTTP/3
recorded seven and six respectively. Those shutdown counts are kept separate
from request-normalized rates.

HTTP/2 churn stacks show socket/TLS errors in both engines: EmbedIO's `IOException`
and Kestrel's `ConnectionResetException`, including reset/aborted peer shutdown.
HTTP/3 stacks show runtime `QuicStream.HandleEventShutdownComplete` and
`ResettableValueTaskSource.TryComplete`, closed-channel awaits, and cancellation
throws. Persistent HTTP/3 still closes its one client connection after the load;
its 18 throws are not 18 faulty application responses. The corresponding churn
rate is about 18 throws per connection for EmbedIO and 12 for Kestrel, including
runtime events and rethrows. The first stack for a closed-channel exception is
an exception-dispatch frame; it does not identify which owned channel closed.

## Evidence and reproduction

The test-only [load harness](../../test/EmbedIO.LoadBenchmark/README.md) documents
commands and the phase-summary tool. Local evidence lives under ignored
`TestResults/issue-282/worktree/TestResults`: `profile-low-concurrency`,
`profile-corrected`, `profile-final`, their logs and summaries, and the sampler
before/after regression logs. The initial sandbox access failure, TCP-admission
blocked attempt and invalid CPU-affinity attempt are retained. They are not
successful samples. A compact committed final-campaign record accompanies this
report; it preserves raw-file hashes, environment, counts and diagnostic stacks.
The [final two-round record](evidence/teardown-exceptions-282.json) contains all
28 successful samples. Their response validation, child exits and TCP socket
cleanup checks passed; this remains diagnostic evidence from a shared host.

The ten Python regression tests reject missing/reset/invalid counters, failed
samples and unsuccessful cleanup, while checking phase differences and unavailable
denominators. Both profiler checks are part of the existing CI static-analysis job.
The test-only harness remains outside shipped packages and the ordinary solution.

## Current-source controlled measurements

PR #351's campaign [38289079183](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38289079183)
measured head `333aa45d1d0226107ecee0c12887bf58d3d01f3c`, based on engine
`6810599e7f5723664d02266283a80fa4909cf7a6`, on a disposable Linux runner.
All 112 normal samples validated: 28 diagnostic profiles, 42 identical-binary A/A
samples and 42 Kestrel cost comparisons. The whole campaign **failed** its original
fault-control qualification. Artifact `11767068625` and the earlier counter-enabled
preparation run `38287772112`/artifact `11767096653` remain retained. They are not
relabeled as passing or as measurements of subsequent engine changes.

Cost and A/A children explicitly disable the inherited first-chance observer;
exception counts are unavailable, not observed zero. Profiles record actual loaded
runtime, core and MsQuic identities. CI loaded the official MsQuic 2.6.2 LTTng
library (SHA-256 `8a67643a29cde90b05fc58c6e84d45143006f793a2529f8bb4f49421f7c634d5`)
on runtime 10.0.12. A/A copies have identical core and runner assembly hashes.
Every arm uses 32 requests/second, alternating engine order, a quiet baseline and
phase background ceilings of 0.5 busy CPUs. Failed or contaminated samples are
preserved rather than retried or omitted.

The first profiled round observed zero load/idle throws for both engines' normal
HTTP/1 and HTTP/2 variants. HTTP/3 persistent connections observed 18 for EmbedIO
and 12 for Kestrel; 160 churned connections observed 2,890 and 1,920 respectively.
These include runtime notifications and rethrows, not distinct failed responses.
Roughly 18 versus 12 per closed HTTP/3 connection is lifetime-specific; a
per-request rate changes with reuse. Closed-channel dispatch stacks alone do not
establish original ownership.

Median observer-free cost samples are whole-process comparisons, not incremental
exception costs. HTTP/3 persistent CPU was within the 3% comparison noise floor;
allocation differences remain descriptive. HTTP/1 and HTTP/2 differences occurred
without routine throw notifications. No causal exception-cost estimate, performance
improvement, exception suppression, or production optimization is justified by
these measurements. The test-only harness remains outside shipped packages.

## Diagnostic qualification and remaining control gap

The original bounded full-stack keys overflowed under intentional faults. Revised
`throw-site-v2` groups count type/message, the synchronous notification's first
throw frame and nearest owned frame, preserving a representative exception and
notification stack. A group without an owned frame remains unattributed. Atomic
per-type totals must agree with every group's count. The 512-group/16 KiB key
bounds remain enforced; representative truncation is explicit, with full-content
SHA-256 and original length. Genuine group overflow still fails qualification.
This is a declared observable-site census, not every asynchronous stack variant.

EmbedIO lifetime accounting uses its server meter. Diagnostic Kestrel profiles
observe the original connection-delegate task and independently retain its meter
witness. A bounded settle wait does not force pending counts to zero. Meter coverage
ending before final delegate completion is explicitly distinguished from a live,
unexplained residual. Neither delegate completion nor a logical meter proves native
QUIC heap release.

Kestrel's deliberate HTTP/3 abort control retained unfinished delegate entries
through host stop/disposal. Weak task-target collection did not count as completion;
strong-reference experiments altered shutdown observability. A separately labelled
public Abort experiment made two actual calls and still left two unfinished entries
(50 entered, 48 completed), with roughly 33 seconds of shutdown/observer waiting.
It did not qualify as a fix. These functional container probes are not quiet cost
measurements, and do not establish a production Kestrel defect or native leak.

William approved requiring normal profiles and observer-free A/A/cost comparisons
for both engines, plus every EmbedIO intentional-abort/recovery case. Kestrel fault
experiments remain visible diagnostic controls: unsuccessful cases are recorded as
`failed-control`, never passing. The unresolved control gap is tracked separately
in [#361](https://github.com/WilliamSmithEdward/embedio-neo/issues/361). All genuine
EmbedIO recovery failures, missing requests, incomplete identities, contaminated
normal samples, unbalanced lifetimes and census overflow remain blocking.

PR #351 is unmerged pending a fresh current-source campaign and every exact-head
check. Historical source-specific evidence remains unchanged. No production API,
default, target or runtime dependency changes or release are included.
