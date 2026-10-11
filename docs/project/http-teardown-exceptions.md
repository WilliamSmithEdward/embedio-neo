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

## Remaining work

Issue #282 remains open for a quiet-host campaign with longer alternating samples,
A/A noise controls, protocol-isolated paced traffic, explicit deliberate-fault
phases, and direct server closed-connection accounting. The current client-opened
denominator and TCP-only socket census do not prove QUIC native cleanup.
The historical combined-listener endurance workload was not repeated here; this
harness's HTTP/3 scenario uses the explicit HTTP/3 listener.

Before a production change, attribute owned versus runtime-origin throws at
specific sites and measure material CPU/allocation/tail-latency cost, including
shutdown races, reset/cancellation and healthy subsequent connections. No
catch-and-ignore filter, exception suppression or runtime/OS setting change is
justified by these counts alone. No release is authorized or published.
