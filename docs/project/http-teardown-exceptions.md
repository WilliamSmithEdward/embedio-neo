# HTTP connection teardown exception attribution

[Issue #282](https://github.com/WilliamSmithEdward/embedio-neo/issues/282)
investigated whether routine teardown notifications justify a production change.
The qualified normal profiles and observer-free comparisons below support keeping
production error and cancellation behavior unchanged. First-chance notifications
include runtime notifications and rethrows; they are not failed-response counts.
Whole-process comparisons do not isolate incremental exception cost.

The source work was accepted into [PR #182's integration branch](https://github.com/WilliamSmithEdward/embedio-neo/pull/182)
as `367155e835886dace8c70600a6c6c6324aa97fe7` through [PR #351](https://github.com/WilliamSmithEdward/embedio-neo/pull/351).
This work changes test tooling and documentation, with no production API, default,
target, dependency or error-contract change relative to the accepted engine base.
It has not been merged to main or published in a release.

Historical measurements remain attributed to their actual source and workload.
The initial Windows engine was `b3e55b5a41e68fb252a95f2ba22dafc1a6637cbb`.
The historical roughly 25 throws per 1,000 requests came from another revision
and workload under PR #276; changing requests per connection changes that rate.

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
CPUs), with server CPU set 0â€“1 and client CPU set 2â€“3. It uses one connection and
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

<a id="remaining-work"></a>
<a id="current-source-follow-up-in-progress"></a>

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

The failed campaigns remain historical source-specific evidence. The qualified
source-specific campaign below completes the approved scope. No production API,
default, target or runtime dependency changes or release are included.


The final campaign tooling reports coarse phase/sample-file progress without
claiming that file presence is a validated result. It retains the original
per-child ceilings and adds a 52-minute overall ceiling within the existing
60-minute job, reserving artifact-upload time. Overall expiry and unattempted work
remain failed results, with bounded owned-process cleanup and retained provenance.


## Qualified preliminary observer-v2 campaign

[Run 38302277640](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38302277640)
completed the approved scope on head `c92a1b4055efa63e3a3c1880bb4d4659f52d6522`,
engine base `7ca4a860b362c0c1f633fbc255e32aa65440dc5a`. Its checkout was
`15a197558a7538bc7b26d70db95c2fbe275a5577`; artifact `11772353682` is retained.
The [compact evidence record](evidence/teardown-cost-282-preliminary.json) preserves
all 112 normal sample hashes, actual observer modes, phased counts, final lifetime
and meter witnesses, and all eight recovery controls. Every sample used the same
core SHA-256 `560005ee2bc5125fc78c637bac5f5464eb5464a55d057f848ab33e1bcc117da2`;
A/A copies matched. Loaded framework/runtime 10.0.12 and MsQuic identities agree
across engines and samples.

All 28 profiles, 42 A/A and 42 cost samples qualified, as did all four EmbedIO
recovery cases. Its four logical lifetime pairs were 6,017/6,017 (HTTP/1 plain),
1,555/1,555 (HTTP/1 TLS), 279/279 (HTTP/2 TLS) and 322/322 (HTTP/3), with no pending
entries. Kestrel's deliberate HTTP/2 TLS control remained 973/971 with two pending;
HTTP/3 remained 622/589 with 33 pending. They are recorded as `failed-control`,
not passing comparisons, and remain actionable in #361. Logical completion still
does not establish native heap release.

Normal persistent HTTP/1 and HTTP/2 observed no load/idle throws in the first
profiled round. HTTP/2 churn observed three EmbedIO cancellation notifications
and one Kestrel disposed-socket notification across 160 validated responses.
EmbedIO's observable sites show the cancellation token and dispatch/await rethrows;
this is not three distinct failed responses or a demonstrated material cost.
HTTP/3 recorded 18 versus 12 notifications per persistent lifetime, and 2,880
versus 1,920 across 160 churned lifetimes. All measured responses validated.

The observer-free HTTP/3 persistent CPU comparison was within its 13% noise band;
allocation differences were observable. HTTP/1 and persistent HTTP/2 CPU/allocation
differences occurred without routine notifications. These whole-process
comparisons do not isolate exception cost, and justify no catch-and-ignore change
or production optimization. Later accepted transport changes require their own
comparison; this preliminary record is not relabeled as final engine evidence.

All 37 exact-head PR checks were terminal: 30 successful and seven intentional
skips. Desktop results reported all 5,250 cases per platform with no failures,
plus passing reset/module/ZIP stress receipts. These preliminary measurements
remain source-specific; the qualified source-specific result follows. No main merge
or release occurred.

## Qualified source-specific campaign

[Run 38314623765](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38314623765)
qualified head `fd0a8ee08465772f3e1a1686c4afac370aafa46d` against accepted engine base
`c154244ee217096b391e29fda9eb0076739f46a4`. The checkout was `d10fba27cbc8bb89a851064cdd959791c0c39154`;
artifact `11777053681` is retained. The [compact evidence record](evidence/teardown-cost-282-final.json)
preserves hashes of all 112 normal samples and eight recovery controls, actual
observer modes, phased counts, logical lifetime/meter witnesses, metrics and
loaded binary identities. All 28 profiles, 42 identical-binary A/A samples and
42 Kestrel comparisons passed independent qualification. All four required
EmbedIO intentional-abort/recovery cases passed, including validated healthy
traffic before and after the faults and clean child exits.

Core SHA-256 was `1ee9af4a41c2414fdddb228bafad62a323d6fd612ea3c0bcabbb2a436e12cc79`; copied A/A core and runner identities
matched. The loaded framework/runtime was .NET 10.0.12 and the official
MsQuic 2.6.2 LTTng library was verified against its pinned package.
Measurements apply to this source and these identities, not later changes.

Normal samples retain a 32-request-per-second pace, complete responses and
clean cleanup. Cost/A/A arms have both exception observers disabled; missing
counts are unavailable, not zero. Profile runs provide attribution, not timing
ratios. Quiet-host ceilings, workload, discovery minimums and recovery assertions
remain enforced; no failed sample was converted to a passing result.

| Protocol | Engine | Entered / completed / pending after recovery | Outcome |
| --- | --- | --- | --- |
| Http1 plain | embedio | 4955 / 4955 / 0 | balanced |
| Http1 plain | kestrel | 8947 / 8947 / 0 | balanced |
| Http1 TLS | embedio | 1401 / 1401 / 0 | balanced |
| Http1 TLS | kestrel | 1431 / 1431 / 0 | balanced |
| Http2 TLS | embedio | 235 / 235 / 0 | balanced |
| Http2 TLS | kestrel | 837 / 836 / 1 | failed-control |
| Http3 TLS | embedio | 286 / 286 / 0 | balanced |
| Http3 TLS | kestrel | 551 / 522 / 29 | failed-control |

This run records 2 Kestrel fault controls as `failed-control`.
These and the retained earlier control limitations are tracked in
[#361](https://github.com/WilliamSmithEdward/embedio-neo/issues/361). Other
errors, unknown failures, crashes, timeouts, identity mismatches, malformed
counters or failed healthy traffic would still block qualification for either
engine. These task observations do not establish a production Kestrel defect
or native leak. Logical lifetime completion is not native heap release.

Median unprofiled whole-process costs follow. The CPU comparison band is the
largest observed A/A difference or within-engine spread, with a 3% floor;
it is a descriptive noise bound, not a statistical confidence interval.

| Workload | EmbedIO CPU us/request | Kestrel CPU us/request | EmbedIO bytes/request | Kestrel bytes/request | CPU comparison band |
| --- | ---: | ---: | ---: | ---: | ---: |
| h1-plain-small-c64-close100 | 2,832 | 1,874 | 12,720 | 5,602 | 6% |
| h1-tls-small-c64 | 3,015 | 2,045 | 12,758 | 5,538 | 13% |
| h2-plain-small-c64x1 | 3,021 | 2,184 | 14,203 | 5,612 | 7% |
| h2-tls-churn-c32 | 6,479 | 5,848 | 37,748 | 42,737 | 6% |
| h2-tls-small-c64x1 | 3,040 | 2,291 | 14,258 | 5,826 | 17% |
| h3-churn-c16 | 7,693 | 6,392 | 125,999 | 140,488 | 8% |
| h3-small-c64x1 | 2,853 | 2,733 | 19,543 | 9,202 | 10% |

No catch-and-ignore change, exception suppression or production optimization
is justified by the whole-process comparisons. A future targeted optimization
would need its own reproduction, causal measurement and compatibility checks.
The long campaign is scoped to benchmark/tooling changes and manual dispatch;
ordinary documentation changes do not schedule it.

Every check on the measured PR #351 head was terminal and passing or an
intentional skip, with overall success. The desktop regression matrix reported
all 5,475 cases per platform: Windows 5,463 passes/12 expected skips, Linux
5,461/14, and macOS 5,458/17. All 20 main and supplemental TRX reports had
zero unsuccessful counters. The source campaign also ran the 54 evidence
acceptance tests and executable census/pacing controls.

## Combined integration provenance

The measured checkout `d10fba27cbc8bb89a851064cdd959791c0c39154` and
PR head `fd0a8ee08465772f3e1a1686c4afac370aafa46d` had tree
`e7cd43062e1de3ec0247504540d378e0dd98fd8c`, based on `c154244e`.
Before source PR #351 merged, independent [PR #363](https://github.com/WilliamSmithEdward/embedio-neo/pull/363)
advanced the integration branch to `916621b3`, adding optional forwarded-header
handling and raising the desktop discovery floor to 5,517. Source PR #351 was
accepted as `367155e835886dace8c70600a6c6c6324aa97fe7`, with parent
`916621b3941822238ea91911281cf15cef7a26b8` and combined tree
`1dbbb931c12dfeeca6cadfed63ea72e8c330963f`.

The ten-file difference is exactly the independent forwarded-header contribution;
the measured source work is retained unchanged. GitHub's PR base and synthetic
merge reference still reported the earlier base at final inspection. These views
were insufficient to establish the live integration tree, which was checked
after acceptance. Future merge verification must fetch the live base branch
directly as well as checking the PR head, checks and synthetic merge tree.

The performance evidence remains specific to `c154244e` and its recorded binaries,
not the newer combined core. Fresh ordinary correctness checks on the combined
integration are required for this final documentation PR, retaining the 5,517
discovery floor. No newest-core timing comparison or performance improvement is
claimed.
