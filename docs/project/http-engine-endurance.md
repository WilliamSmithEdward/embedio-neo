# Managed engine endurance campaign (October 2026)

Sustained-load stability and resource-retention evidence for program #181 (PR #182).
Throughput comparisons are covered separately in
[the load comparison](http-engine-load-comparison.md); absolute rates here belong to
one virtual machine and must not be compared with other hosts. This is an interim
report: the per-protocol attribution runs and the Kestrel control described under
[Next steps](#next-steps) were stopped before they finished.

## Environment and provenance

| Item | Value |
| --- | --- |
| Tested revision | `a4f710574748090ef5bad0871d68f28bcf9f03be` (`codex/managed-http-engine`, #238), pinned in a detached worktree for the whole campaign |
| Host | Hyper-V guest, Windows 11 Pro 10.0.26200.9457, AMD Ryzen AI 9 HX 370 (24 virtual processors), dynamic memory (about 8–9 GiB visible, 3–6 GiB available during runs), power scheme High performance |
| Background load | Not a quiet host: other desktop applications were running. Background CPU is recorded per sample; see [host contention](#host-contention) |
| SDK / runtime | .NET SDK 10.0.401, runtime 10.0.12 (`95017c7`), installed locally under ignored `TestResults` (no system changes) |
| QUIC | msquic.dll 2.5.10.154561281 from the runtime, SHA-256 `B7158D42…DE9157E8` |
| Core under test | `EmbedIO.dll` SHA-256 `48E10C87…F51528128`, byte-identical in every runner copy |
| Affinity | Server CPUs 0–7, client CPUs 8–19 (inherited at process creation) |
| OS limits, firewall, power | Unchanged |

Raw evidence (ignored, local): `TestResults/endurance-181/` — `env/`, `logs/commands.log`
(every command with timestamps), `results/<run>/` (environment, steps, server samples,
client reports and intervals, `endurance-summary.md`) and `dumps/`.

## Tooling

`test/EmbedIO.LoadBenchmark` gains an `endurance` mode (see its README): one
long-lived server process hosting a cleartext managed listener (HTTP/1.1, h2c prior
knowledge) and an `EmbedIOCombined` TLS listener (HTTP/1.1 and h2 by ALPN, HTTP/3) with
the comparison handler and a WebSocket echo endpoint; paced client mixes with
validated responses; cancellations, abrupt disconnects, slow readers/writers, slow
heads and idle holds; WebSocket churn and long-lived echo over HTTP/1.1 and h2;
graceful drain/replace cycles under load; and quiesced forced-GC snapshots plus a
health check after every phase. `scripts/analyze_endurance.py` summarizes a run and
`scripts/summarize_minidump_memory.py` summarizes committed memory in a full dump
without a debugger. A Kestrel control host (`--engine kestrel`) and workload filters
(`--transports`, `--kinds`) support attribution. No production, workflow, dependency
or default changes.

All measurements above used the harness built at the tested revision. The tooling
was then rebased onto `4be9d24` alongside the separate `recovery` mode; a
functional run on that base (mixed load, drain-and-replace, idle restart, Kestrel
control) passed, but no measurements were repeated on it.

## Runs

| Run | Plan | Outcome |
| --- | --- | --- |
| Smoke | All 25 comparison scenarios, candidate only, 1 round, 2 s warmup, 5 s measure | 25/25 valid, no open server sockets after |
| Screening (30 min) | idle, steady 5 min, faults 4 min, WebSocket 2 min, 3 drain cycles under mixed load, 5 idle restarts, mixed 7 min, idle | 7/8 steps passed; 2 HTTP/3 churn errors in the mixed step |
| Diagnostic | mixed 7 min with exception detail; drain under steady, WebSocket-only and fault-only load | Drain attribution below; 1 harness false positive (idle-timeout clock), corrected |
| 8-hour mixed | idle, steady 10 min, 16 × (mixed 25 min, drain/replace under mixed load, 2 idle restarts), steady 10 min, idle 5 min — 8.2 h, one server process | 52 steps; 45 passed, 7 failed on 17 client errors (below); all 34 health checks passed |

8-hour totals: 88,596,949 validated client exchanges (92.9 million server requests),
708,888 deliberate client cancellations/resets, 193,131 WebSocket connections with
1,161,801 echoed messages, 49 server instances (48 drain-and-replace cycles), zero
requests served by a drained instance, zero validation failures (status, version,
framing, length, every body byte, every echoed message). Every phase reached zero
in-flight requests and WebSockets within 23 ms of traffic stopping. Final graceful
stop took 4.5 ms; the server exited with code 0.

## Findings

### 1. Native (unmanaged) memory grows linearly — open, highest priority

Quiesced snapshots (traffic stopped, zero in flight, 20 s settle, two forced
compacting GCs):

| Hour | Private MiB | GC committed MiB | Managed heap MiB | Handles | Threads |
| --- | --- | --- | --- | --- | --- |
| 0.2 | 71 | 15.9 | 4.1 | 960 | 44 |
| 2.6 | 233 | 27.4 | 2.9 | 1,009 | 48 |
| 4.5 | 332 | 8.5 | 4.6 | 1,025 | 50 |
| 6.9 | 510 | 18.9 | 2.0 | 1,021 | 52 |
| 8.2 | 589 | 10.6 | 3.6 | 1,008 | 52 |

After two warm-up snapshots the least-squares slope is about +63 MiB/hour of private
bytes, steady across the whole run (before and after the host contention described
below) and not released by 48 drain-and-replace cycles. The managed heap, GC
committed memory, handles, threads, thread-pool threads and timers stay bounded,
so this is neither managed retention nor leaked sockets or handles. Growth occurs
under the steady mix alone (quiesced private bytes rose from 570.9 to 589.0 MiB across
the final 10-minute steady phase).

A full dump at 7.9 h (`dumps/04-server-7.9h-idle.dmp`, taken during the final idle
step) shows 584 MiB of committed private memory against an 11 MiB GC heap; 540 MiB
are in 35 native allocations of about 15.8 MiB each, roughly 75% zero bytes. Sampled
contents are loopback socket addresses with client ephemeral ports, Schannel
material (`Microsoft SSL Protocol Provider`, `CN=localhost`, `nistP256`,
`ChainingModeGCM`) and request fragments for the TLS/QUIC port. This is consistent
with a native heap (Schannel, MsQuic or socket-layer state) growing with connection
or TLS churn, but the owner is **not yet identified**, and it is not yet known
whether Kestrel on the same runtime grows the same way.

### 2. HTTP/3 handshake timeouts and multi-second stalls under host CPU saturation

15 of the 17 client errors were HTTP/3 connection-churn handshakes that did not
complete within the client's 10 s QUIC handshake timeout; the screening also saw one
`H3_STREAM_CREATION_ERROR` connection close, which the server reports when it cannot
open its critical streams within its own 10 s startup deadline. All of them, and
every small-request stall above 2 s (up to 15 s, on all five protocols at the same
moment), occurred in the first ~5 hours, while the host was CPU-saturated: in the
worst windows the server used all 8 of its CPUs (140 CPU-seconds in a 17 s sampling
gap) with the machine at 21 of 24 busy. See [host contention](#host-contention).
None occurred in the remaining cycles. Treat these as overload effects of this host
unless they recur on a quiet one.

### 3. TCP connects unanswered for 21 s after drain-and-replace — open

Two of the 17 errors were HTTP/1.1 client connects that timed out (SYN unanswered,
`SocketError.TimedOut`), each in a drain-and-replace cycle: one started inside the
disruption window and was reported after it; the other started about 40 s after the
replacement, while the host was quiet. Not yet reproduced in isolation.

### 4. Graceful drain waits for WebSockets until its deadline

Every drain under mixed load took the full 10 s deadline (16/16, 9.99–10.43 s), while
the 32 idle drains took at most 6 ms. Isolation on the same build: steady HTTP load
drained in 17–31 ms; fault load in 5.2 s (slow readers legitimately finishing);
WebSocket-only load with 9 open connections in 10.0 s. Accepted requests always
completed (in flight after drain: 0). Drain does not ask the WebSocket module to
close its connections (for example with 1001 Going Away), so upgraded connections
are cut at the deadline. Whether drain should close WebSockets is a design decision.

### 5. Idle keep-alive connections end with a reset

When the managed HTTP/1.1 listener's 15 s keep-alive idle timeout fires, the client
observes `ConnectionReset` rather than a FIN (HTTP/1.1 and HTTP/1.1+TLS, every
observation). `HttpConnection.OnTimeout` disposes the socket without `Shutdown`
(`CloseTransport(false)`). A client that reuses the connection just as it times out
cannot tell this from a failure. The behavior appears to be inherited; it was not
compared with the main baseline.

### 6. First-chance exceptions in connection teardown

Without injected faults (steady mix: paced requests plus rate-limited connection
churn) the server raises about 25 first-chance exceptions per 1,000 requests —
QUIC, I/O, cancellation and channel-closed exceptions, roughly ten per closed HTTP/3
connection. Over 8 hours: 15.2 million `IOException`, 5.5 million
`OperationCanceledException`, 3.5 million `QuicException`, 3.3 million
`TaskCanceledException`. This is CPU cost, not a correctness failure.

### Not defects

- Health checks after every phase passed (34/34); quiesce never stalled.
- Drain/replace under load completed with zero in flight and zero requests served by
  a replaced instance; restart took 3–5 ms.
- Latency of the paced small-request workloads in the last quarter of the run (after
  the contention ended), median of per-minute p99: 2.2 ms (HTTP/1.1), 3.0 ms
  (HTTP/1.1+TLS), 3.6 ms (h2c), 4.1 ms (h2), 4.6 ms (HTTP/3).

## Host contention

For the same workload, server CPU per request was about 4× higher and client CPU per
request 3–5× higher in the first ~5 hours (cycles 1–10) than afterwards, with an
abrupt change after cycle 10. The client never loads EmbedIO, so the change is
host-wide (Hyper-V contention from other activity), not an engine effect. Throughput
targets were paced, so achieved rates were lower during contention. Results from
those hours are kept and reported, not removed.

## Coverage gaps

- Per-protocol attribution of the memory growth and the Kestrel control were started
  and stopped after about 7 minutes (inconclusive); see [Next steps](#next-steps).
- Additional multi-hour representative runs (steady-only, fault-only, WebSocket-only)
  were not run; the 8-hour run interleaves all of them.
- WebSockets over HTTP/3 (RFC 9220) are not exercised: `ClientWebSocket` does not
  offer them.
- QUIC connections are not visible in the TCP census.
- HTTP/1.1 initial-request idle timeouts (90 s) were observed only within long phases.
- The host is a shared VM with dynamic memory; no dedicated-hardware results.

## Next steps

1. Attribute the growth: one fresh server per engine and protocol, steady mix,
   three 10-minute phases, for EmbedIO on `h1`, `h1tls`, `h2c`, `h2tls` and `h3` and for
   Kestrel on `h1`, `h1tls`, `h2tls` and `h3`, then compare quiesced private-byte slopes:

   ```sh
   dotnet <runner>/EmbedIO.LoadBenchmark.dll endurance --output TestResults/endurance/attr-<engine>-<transport>      --engine <embedio|kestrel> --transports <transport> --plan idle:60,load:steady:600,load:steady:600,load:steady:600      --settle 20 --server-cpus 0-7 --client-cpus 8-19 --revision <sha>
   ```
2. If TLS or QUIC is implicated, compare two dumps from the same process to identify
   the owning heap (a debugger with heap commands is needed for heap ownership).
3. Reproduce the post-replacement connect timeout with repeated drain-and-replace
   under HTTP/1.1 connect churn only.
4. Repeat a shorter mixed run on a quiet host to confirm the HTTP/3 handshake
   timeouts are contention-only.
