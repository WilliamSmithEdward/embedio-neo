# Apple Silicon load and interoperability evidence (October 2026)

Independent macOS measurements for program #181, taken on one Apple Silicon machine. They
compare engines and revisions **on this machine only**. Absolute rates are not comparable
with the Windows tables in [the managed engine load comparison](http-engine-load-comparison.md),
and no Windows-versus-Mac difference is an engine improvement or regression.

## Environment and provenance

| Item | Value |
| --- | --- |
| Host | Apple M5 Pro: 18 cores in two performance levels (6 "Super", 12 "Performance"), 48 GB, macOS 27.0.1 (26A434), AC power |
| Runtime | .NET SDK 10.0.401, Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.12 (Kestrel from the shared framework) |
| QUIC | MsQuic 2.6.2 Homebrew bottle on `DYLD_FALLBACK_LIBRARY_PATH` (libmsquic SHA-256 `662c2a12...43bcd`), Homebrew OpenSSL 3.6.5 |
| Candidate | `codex/managed-http-engine` `a4f7105` source, runner built from `b56e5fb` (this branch: harness-only changes) |
| Baseline | main `1445c23`, built from `git archive` by `scripts/prepare_load_benchmark.py` |
| CPU sets | none: macOS cannot pin processes, so server and client share every core (`cpuAffinity: none`) |
| Schedule | 3 rounds, engine order alternating, fresh server and client process per sample, 5 s warmup, 15 s measurement, 2 s idle |
| Guard | PR #235's `scripts/guarded_load_benchmark.py` (`66f33e0`), unmodified: shared lock plus foreign-process watch; no attempt was invalidated |

The machine was otherwise idle; another agent's QUIC campaigns finished before the first
block and the shared lock was held throughout. Raw data: `TestResults/load-benchmark/macos`
in the benchmark worktree (per-sample JSON with full latency histograms, `environment.json`,
`summary.md`, `runners.json`, guard journals).

## Harness portability

The harness was written for Windows and Linux. Before any comparison on macOS:

- `--server-cpus`/`--client-cpus` were silently not applied on macOS but still recorded as if
  they were. They are now refused there, and `environment.json` records `cpuAffinity`.
- Machine busy CPU (for background load) read zero; it now comes from `host_statistics`.
- .NET's `GetActiveTcpConnections` omits TIME_WAIT on macOS, so the TIME_WAIT gate and the
  server socket census saw nothing. macOS now parses `netstat -an -p tcp`.
- `Process.HandleCount` and `PrivateMemorySize64` read 0. Handles are the open file-descriptor
  count; private bytes is reported as unavailable instead of a flat 0.
- A run that selects HTTP/3 without a loadable MsQuic now stops before starting. The first
  campaign attempt here lacked the library path and was aborted after nine HTTP/1.1 sample
  pairs; it is kept as `aborted-1-*` and is not used.

## Noise

Identical-binary (A/A) runs give the noise per scenario. Most scenarios agree within about
1-3 % on requests/s, CPU and allocation; h2 over TLS with 32 streams per connection varies
about 10 %. `scripts/compare_load_benchmark.py` marks a difference only when it exceeds the
A/A ratio, each engine's own sample spread and a 3 % floor.

## Results

Medians of three valid samples (`compare-a1`, 189 samples). "Beyond noise" means outside the
A/A ratio, both engines' own sample spread and a 3 % floor; the full tables, with every
metric and ratio, are `compare-vs-baseline.md` and `compare-vs-candidate.md` beside the raw data.

### Candidate versus main (HTTP/1.1)

| Scenario | Requests/s | Server CPU/request | Server bytes/request | Reading |
| --- | --- | --- | --- | --- |
| 13 B GET, 64 keep-alive | 128,716 vs 116,220 (+11 %) | -7 % | 7,107 vs 10,422 (-32 %) | better |
| 13 B GET pipelined x16 | 568,874 vs 411,119 (+38 %) | -28 % | -35 % | better |
| 13 B GET, close after 100 | within noise | within noise | -31 % | allocation only |
| 1 MiB upload (both lifetimes) | -6 to -7 % | -17 to -19 % | -64 % | slower, not CPU-bound |
| 1 MiB response | -4 % | +9 % | -12 % | slightly slower |
| 1 MiB flushed per 16 KiB | -5 % | -8 % | -12 % | slightly slower |
| HTTPS 13 B, 64 keep-alive | 119,299 vs 1,660 (72x) | -58 % | -34 % | main defect, fixed |
| HTTPS handshake per request | 54 vs 15 (3.5x) | -52 % | -23 % | main defect, fixed |
| HTTPS 1 MiB response / upload | 3.3x / 3.0x | -27 % / -17 % | equal / -64 % | main defect, fixed |

The 1 MiB HTTP/1.1 throughput deficit repeats in every round and is beyond noise while the
candidate uses less CPU; its cause is not profiled yet. Main's HTTPS collapse is
macOS-specific (main reaches 168,670 requests/s on the Windows host): see the defects below.

### Candidate versus Kestrel 10.0.12

| Scenario | Candidate / Kestrel requests/s | Notes |
| --- | --- | --- |
| HTTP/1.1 13 B, 64 keep-alive (plain / TLS) | 0.98x / 0.96x | Kestrel ~30 B/request, candidate ~7.1-7.5 KB |
| HTTP/1.1 pipelined x16 | 0.40x | Kestrel 7 us CPU/request vs 20 |
| HTTP/1.1 1 MiB flushed per 16 KiB | 0.59x | |
| HTTP/1.1 1 MiB upload (plain / TLS) | 3.6x / 2.5x | Kestrel spends ~7 ms CPU per upload on this host |
| h2c / h2 TLS, 8 x 32 streams | 2.5x / 3.6x | outside the 10 % h2 TLS noise; same `HttpClient` client |
| h2 TLS, 64 x 1 stream | 1.00x | |
| h2 TLS 1 MiB upload | 0.54x | consistent with the fixed 65,535-byte connection receive window |
| h2 TLS 1 MiB flushed | 1.33x | |
| HTTP/3 13 B, 8 x 32 / handshake per request | 0.95x / 0.94x | |
| HTTP/3 1 MiB response / upload / flushed | 1.34x / 1.08x / 1.37x | Kestrel allocates ~457 KB per 1 MiB HTTP/3 response |

Kestrel's HTTP/2 multiplexed and HTTP/1.1 upload rates here are far below its Windows rates;
that is a Kestrel-on-macOS observation, not evidence about EmbedIO, and was not investigated.

### Platform effects common to every engine

- TLS 1 MiB responses allocate about 1.07 MB per request on every engine, Kestrel included.
- TLS connection setup tops out at about 55 per second for the candidate and Kestrel.
- HTTP/3 bulk transfer reaches only 275-380 MiB/s with MsQuic 2.6.2 on this host.
- HTTP/1.1 one-request-per-connection churn is reset by the host for every engine (Kestrel
  1/3, candidate 0/3, main 0/3 valid; A/A 1/6), so it is not an engine comparison here.

## Defects and limitations found on macOS

1. **Bursts beyond the macOS listen backlog reset connections (host limit, both engines).**
   `kern.ipc.somaxconn` is 128, so `Listen(500)` and Kestrel's backlog are clamped and XNU drops
   connections beyond about 1.5x that (192) pending. With the harness client, every EmbedIO
   `h1-plain-small-c256` sample fails in warmup (18/18, candidate and main) and a sweep fails from
   192 connections up while Kestrel passes; with a stdlib asyncio burst against cold servers the
   result inverts (EmbedIO 256/256 in 3/3 trials, Kestrel resets 8-25 of 256 in 3/3). Kernel
   overflow counters are not readable without root, so the overflow is inferred. This is not an
   established EmbedIO defect, and the 256-connection scenario is not a valid engine comparison
   on default macOS limits; OS limits were not changed.
2. **Main serializes TLS handshakes on its accept loop (fixed in the candidate).** At `1445c23`
   the accept loop calls `BeginReadRequest` inline, and its first step awaits
   `AuthenticateAsServerAsync`; on macOS the handshake runs synchronously long enough to stall
   accepts (15 handshakes/s, about 66 ms each). The candidate queues connection start to the
   thread pool. Released 1.0.x packages likely share main's behaviour; not verified.

## Cancellation, recovery and sustained load

`EmbedIO.LoadBenchmark recovery` (added on this branch) drives one long-lived server: 10 s of
validated healthy load, a 20 s abort storm, idle drain, healthy load again, 8 x 60 s of sustained
load with a snapshot (after forced GC) between runs, then a timed shutdown. The HTTP/1.1 storm
reads 64 KiB of a 1 MiB response and resets the connection; the HTTP/2 and HTTP/3 storms cancel
streams mid-response and mid-upload and drop every 16th connection abruptly. Raw results:
`TestResults/load-benchmark/macos/recovery`. HTTP/1.1 and HTTP/2 rows used `d1983e6`; the
HTTP/3 rows were rerun with the following commit after a client-side storm fix.

| Run | Storm aborts | Recovery throughput | Retained after storm | Open server sockets | Sustained working set | Shutdown |
| --- | --- | --- | --- | --- | --- | --- |
| HTTP/1.1 candidate | 253,053 | 0.996x | +0.0 MiB heap, +12 descriptors | 0 | flat (279 MB) | 0.01 s |
| HTTP/1.1 Kestrel | 233,127 | 1.009x | +76 MiB heap | 0 | flat (355 MB) | 0.01 s |
| h2 TLS candidate | 1,108 | 0.92x (inside h2 TLS noise) | +9.5 MiB, drained during sustained load | 0 | flat (308 MB) | 0.01 s |
| h2 TLS Kestrel | 1,104 | 0.975x | +129 MiB | 0 | flat (430 MB) | 0.01 s |
| HTTP/3 candidate | 17,054 | 0.983x | none | - | follow-up | 0.02 s |
| HTTP/3 Kestrel | 16,170 | 0.948x | none | - | follow-up | 0.02 s |

No storm produced an unexpected client error, no server process crashed, recovery requests were
all valid, and no server socket stayed open on HTTP/1.1 or HTTP/2. The h2 storm rate is bounded
by the ~55/s TLS connection setup on this host. HTTP/3 sustained-memory results behave the same
on both engines and are being followed up separately; they are not reported here. QUIC
connections are not visible to the socket census.

## Reproduction

```sh
python scripts/prepare_load_benchmark.py
export DYLD_FALLBACK_LIBRARY_PATH=<MsQuic 2.6.2 lib directory>
python -I <guard>/guarded_load_benchmark.py --owner <who> --output TestResults/load-benchmark/macos/compare -- \
  dotnet TestResults/load-benchmark/runners/candidate/EmbedIO.LoadBenchmark.dll run \
  --output TestResults/load-benchmark/macos/compare --baseline-dir TestResults/load-benchmark/runners/baseline --rounds 3
python -I scripts/compare_load_benchmark.py TestResults/load-benchmark/macos/compare-a1 --reference baseline \
  --aa <candidate A/A dir> --aa <baseline A/A dir> --markdown compare-vs-baseline.md
dotnet <runner>/EmbedIO.LoadBenchmark.dll recovery --output <fresh dir> --engine embedio --protocol Http2 --tls \
  --connections 8 --streams 32 --storm-seconds 20 --storm-mode both --sustain-minutes 8
```

A/A runs use the same `run` command with `--candidate-dir` and `--baseline-dir` pointing at two
copies of one runner (`--modern-baseline` for HTTP/2 and HTTP/3).

## HTTP/3 scope

HTTP/3 here is the application listener on `System.Net.Quic`. The native MsQuic provider is
not wired into the listener at `a4f7105`, so no application-level native HTTP/3 performance is
claimed. Native QUIC datagram interoperability was validated separately (PR #243).
