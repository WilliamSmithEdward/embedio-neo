# HTTP/3 handshake and latency on a less loaded host (#283)

Interim evidence for issue #283 under program #181. The PR #276 endurance campaign saw
HTTP/3 churn handshakes miss the client's 10 s QUIC handshake timeout while its
Hyper-V host was CPU-saturated. This page records a paced comparison on current
source, with handshake and stream latency measured separately, and a CPU-saturation
control.

## Tool

`quic-churn` mode of `test/EmbedIO.LoadBenchmark` (see its README). Each attempt runs
on a raw `QuicConnection` and is timed in three phases: handshake (`ConnectAsync`, runtime
default handshake timeout), one validated `GET /plaintext` on a new request stream, and
the graceful close. Attempts are scheduled open-loop at a fixed rate. Optional mixed work
sends paced small (50/s) and 1 MiB (2/s) GETs over four warm `HttpClient` HTTP/3
connections. A one-second timeline records host, server, client and background CPU
next to the attempt counters and the largest handshake. Every failure is kept with its
phase. Nothing is retried. `--burn-threads` adds a separate spinning process.

## Setup

- Source: PR #182 head b3e55b5 plus this tool (harness-only change).
- Host: AMD Ryzen 7 9800X3D, 16 logical CPUs, bare-metal Windows 10.0.26300, .NET 10.0.12,
  bundled MsQuic 2.5.10 (SHA-256 B7158D42...57E8). The core, runtime and MsQuic hashes
  are recorded in each run's `environment.json`.
- Server pinned to CPUs 0-7, client to 8-15. Phases: 4, 16, 64 and 128 connections/s,
  45 s each. Engines alternate order by round.

The host was **not quiet**. Other agents were running test suites, and background CPU
(host busy minus server and client) averaged 5 to 8.5 CPUs per phase, reaching 15 to 16
CPUs in some seconds. No exclusive lock was used.

## Results

Two rounds without added load (`quiet-a1`, `quiet-a2`): 38,160 churn attempts and about
37,000 mixed requests over the two engines, with **no failures of any kind**.

| engine | rate/s | handshake p50 ms | handshake p99 ms | handshake max ms | first stream p99 ms |
|---|---:|---:|---:|---:|---:|
| EmbedIO | 4 | 4.0-4.1 | 6.1-6.7 | 7.1 | 1.9-3.9 |
| EmbedIO | 16 | 4.1-4.3 | 9.3-15.2 | 167 | 4.9-11.2 |
| EmbedIO | 64 | 3.7-4.2 | 5.6-24.4 | 95 | 1.1-13.1 |
| EmbedIO | 128 | 4.0-4.6 | 8.8-9.9 | 65 | 3.1-3.8 |
| Kestrel | 4 | 4.2 | 6.6-7.7 | 10 | 1.5-1.6 |
| Kestrel | 16 | 4.0 | 16.2-34.4 | 77 | 9.0-22.5 |
| Kestrel | 64 | 3.5-3.7 | 5.7-8.4 | 53 | 1.5-3.5 |
| Kestrel | 128 | 4.0-4.1 | 7.4-8.4 | 21 | 2.2-3.5 |

Ranges span the two rounds. Median handshakes are about 4 ms on both engines. The p99
values move between rounds with the background load, not with the rate. The largest
handshakes stayed below 170 ms, far from the 10 s timeout.

### Saturation control (`burn16-a1`, EmbedIO only)

Sixteen spinning threads in another process kept the host at 14 to 16.7 busy CPUs.
This run was stopped after the EmbedIO arm, so it has **no Kestrel control**.

| rate/s | started | failed | handshake p50 ms | handshake p99 ms | handshake max ms | first stream p99 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 4 | 180 | 0 | 7.2 | 84 | 89 | 99 |
| 16 | 720 | 0 | 21 | 308 | 391 | 387 |
| 64 | 2,880 | 5 | 63 | 806 | 2,062 | 714 |
| 128 | 5,760 | 12 | 45 | 819 | 1,310 | 786 |

Saturation raised handshake p99 by roughly two orders of magnitude, and stream latency
grew by the same amount. No attempt reached the 10 s handshake timeout. All 17 failures
were handshakes that the server refused with QUIC `CONNECTION_REFUSED` (transport error
2) after 88 to 1,373 ms. The mixed warm-connection traffic had no failures.

## Reading

- Without added load, nothing failed on current source at up to 128 handshakes/s, and
  both engines had similar median and tail latency. This supports the contention
  explanation for the #276 failures, though the host had background activity during
  these runs as well.
- Saturating the CPUs reproduces multi-hundred-millisecond to multi-second handshake and
  stream stalls, consistent with the stalls in #276. It did not reproduce the 10 s
  timeouts at this rate and duration.
- The `CONNECTION_REFUSED` handshakes came from the listen backlog. See the next section.

## Handshake refusals: listen backlog

`System.Net.Quic` (v10.0.12 `QuicListener.HandleEventNewConnection`) reserves one unit of
`ListenBacklog` for each new connection and returns it only when `AcceptConnectionAsync`
dequeues that connection. The backlog therefore counts handshakes in progress as well as
connections waiting to be accepted, and a connection beyond it is answered with
`QUIC_STATUS_CONNECTION_REFUSED`. EmbedIO's accept loop dequeues right away, so in-progress
handshakes fill the backlog. EmbedIO's HTTP/3 listener set `ListenBacklog = 128`. The
runtime default (`QuicDefaults.DefaultListenBacklog`) and Kestrel's
`QuicTransportOptions.Backlog` are both 512. Under saturation, 64 handshakes/s taking about
2 s each keep roughly 128 in progress, which matches where refusals began.

The refusals reproduce without saturating the machine. The server is pinned to one CPU
and the client to four, with churn at 128, 256 and 512 connections/s for 20 s each
(`constrained-128`, `constrained-512`):

| backlog | engine | 128/s failed | 256/s failed | 512/s failed | 512/s handshake p99 ms |
|---|---|---:|---:|---:|---:|
| 128 | EmbedIO | 0 | 0 | 1,075 of 10,240, all `CONNECTION_REFUSED` | 1,049 |
| 128 | Kestrel (512) | 0 | 0 | 0 | 25 |
| 512 | EmbedIO | 0 | 0 | 0 | 15 |
| 512 | Kestrel (512) | 0 | 0 | 0 | 1,009 |

The listener now uses 512. Background CPU from other work was 6 to 12.5 CPUs during these
runs, so tail latency varies between runs (the Kestrel 512/s tail in the second run
included client cap skips during a background burst, with no failures). The handshake
deadline and the 256-connection active limit are unchanged. A burst that outlasts 512
in-progress handshakes is still refused; that refusal is the intended overload response.
The opt-in native MsQuic listener has its own accept queue (256 queued connections) and was
not part of these runs.

## Remaining work for #283

1. Repeat the saturation control with Kestrel and with the 512 backlog on a disposable
   host or CI runner. Do not saturate every core on a workstation.
2. Repeat on an idle host with no other agents running, and on Linux where practical.
3. Longer runs at the endurance campaign's mixed load if refusals or timeouts recur.

Raw evidence is under ignored `TestResults/issue-283` in the issue worktree.

## Current-source disposable CI control

Issue #283 has a dedicated `HTTP3 host controls` workflow: Ubuntu 24.04 is the
qualified quiet reference; Windows 2025 retains ambient and contention repeats. It builds the checked-out core and identical comparison harness,
uses SDK 10.0.401 and the existing pinned Linux MsQuic 2.6.2 prerequisite, and records
the actual source revision and loaded core/runtime/native binary hashes.

Each engine receives two alternating rounds of 15-second rate phases. The quiet
arm uses 4, 16, 64 and 128 connections/second; the contention arm uses 64 and 128.
Both retain 32 small requests/second and two 1 MiB requests/second over four warm
HTTP/3 clients. Connection establishment, first-stream service and warm requests
are measured separately. No handshake deadline is increased.

On Linux, a 15-second baseline must average at most 0.5 busy CPUs before either arm starts.
Windows records its ambient baseline without a quiet assertion.
Each quiet phase must also average at most 0.5 background CPUs; the contention arm
must measure at least 80% host CPU. These thresholds are declared before the run.
Missing samples, cap omissions, any request failure, unsuccessful server exit or
missing native hash fail acceptance. A contaminated host is an inconclusive failed
control, not evidence of an engine defect or a quiet run. The script preserves all
raw JSON and logs, including failed campaigns, and does not retry them automatically.

The contention arm runs only on disposable GitHub runners. It does not authorize
an all-core burn on a shared development workstation. Actual outcomes and exact-source acceptance are recorded in
[issue #283](https://github.com/WilliamSmithEdward/embedio-neo/issues/283) and the
[PR #346 checks](https://github.com/WilliamSmithEdward/embedio-neo/pull/346/checks).
Configuring this workflow alone is not validation.

### Retained initial control attempt

Run [38260741994](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38260741994)
rejected both quiet baselines before generating load: Linux measured 0.682 busy CPUs
and Windows 0.943, exceeding the declared 0.5 ceiling. These are inconclusive quiet
controls. The subsequent contention arms completed without request failures or cap
omissions on both platforms; every original sample is retained in their run artifacts.

The setup now waits a fixed 30 seconds after compilation before starting measurement.
This is preparation, not a discarded baseline or automatic retry, and no acceptance
threshold or deadline changed. A fresh campaign is still required. The initial security
report also exposed PowerShell syntax that a Bash-specific Semgrep rule could not
parse; separate plain restore/build steps replace that block without a scanner waiver.

### Windows residual CPU investigation

Run [38261965704](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38261965704)
passed both Linux arms. Its quiet baseline measured 0.010 busy CPUs and its contention
baseline 0.008. Windows completed every request without errors or cap omissions, but
several quiet phases exceeded the residual/background CPU ceiling. Its quiet baseline
was 0.106 busy CPUs; higher residual activity during load is not sufficient to identify
which external process or kernel work caused it. Windows quiet acceptance remains
unconfirmed; the threshold is not relaxed.

The next diagnostic captures per-process CPU time, names and instance identities on
Windows, with inaccessible or exited processes explicitly recorded. It reads no
process memory, command lines or credentials. This adds attribution evidence without
excluding processes or changing the quiet-host gate. Artifact names include the run
attempt so an explicit rerun cannot replace earlier failure evidence.

### Approved reference scope

William approved using Linux as the qualified quiet-host reference, with every Windows
case retained as an explicitly ambient platform repeat and a measured contention arm.
Windows quiet acceptance is **not established**. This does not retroactively pass the
rejected Windows quiet runs or exclude OS processes from their CPU records.

New artifact manifests and verdicts identify `quietReference` explicitly; Windows' first
arm is named `ambient`, and its verdict retains `windowsQuietStatus: not established`.
All request counts, complete latency/CPU data, native/core/runtime hashes, runtime
handshake deadlines, zero-error/cap-omission checks and clean server exits remain
mandatory. Linux's quiet ceilings and both platforms' measured contention requirement
are unchanged. Linux retains the initial passive baseline ceiling. Windows records its ambient
baseline without asserting that it is quiet.
The Windows process census remains diagnostic; protected read failures are recorded.

The Windows CPU census in run 38264278426 showed substantial `lsass` activity increasing
with handshake rate for both engines (up to about 0.43 busy CPUs in phase means), plus
update and compatibility tasks. Microsoft's [CNG key-storage documentation](https://learn.microsoft.com/en-us/windows/win32/seccng/key-storage-and-retrieval)
explains that private-key operations can run in the LSA process. Workload-related crypto
is therefore a plausible contributor to the residual; the census does not establish
exact attribution of all service activity. No process is excluded to obtain a quiet result.

Linux passed quiet and contention controls in both runs 38261965704 and 38264278426.
Each complete per-platform campaign measured 24,240 connection handshakes/first-stream
GETs and 12,240 mixed warm requests, excluding setup and health requests. Windows
completed those same requests without failures or cap omissions in both runs, while its
quiet assessment remained inconclusive. PR acceptance requires a fresh run under the approved scope
and all normal checks passing on the exact current head. These are bounded hosted-VM controls, not exhaustive endurance,
original-host reproduction, a public ranking or proof of the sole historical cause.

### Ambient setup correction

Run [38266929424](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38266929424)
completed all Windows ambient requests without failures, but did not start its contention
arm: a leftover quiet-baseline prerequisite rejected 0.533 busy CPUs. The ambient
scope does not promise a quiet Windows baseline. The corrected script records that
measurement and applies quiet prerequisites only to the Linux reference. Invalid CPU
measurements still fail on both platforms, and Windows must execute and pass every
request and measured contention case on fresh source. The rejected setup is retained;
it is not counted as a completed contention run.

### Retained Windows handshake failure and timing correction

Run [38269242512](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38269242512)
passed Linux and the Windows contention arm, but Windows ambient EmbedIO round two
at 16 connections/second recorded 22 handshake refusals out of 240 attempts. Warm
requests completed, with multi-second latency. The host's compatibility process was
active and the scheduler accumulated substantial lag, but this does not establish
the refusals' root cause. The run remains failed; no request failure is accepted.

The stall also exposed mismatched CPU sampling intervals: the initial counter read
preceded the process census while its timestamp followed it, and subsequent loop
timestamps preceded potentially blocking reads. CPU deltas now use each counter's
own acquisition interval, including a timestamp for each process CPU read. This
corrects telemetry only, without changing request scheduling, deadlines or gates.
Fresh source validation remains required and the original failure is preserved.
