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
requests served by a drained instance, zero reported validation failures (status, version,
framing, length, every body byte, every echoed message). Every phase reached zero
in-flight requests and WebSockets within 23 ms of traffic stopping. Final graceful
stop took 4.5 ms; the server exited with code 0.

## Findings

### 1. Native (unmanaged) memory grows linearly — attributed to System.Net.Quic

Attributed in [Native memory attribution](#native-memory-attribution-issue-278): HTTP/3
requests leak one 16-byte native allocation each inside .NET's `System.Net.Quic`, with
Kestrel as much as with EmbedIO. The original observations follow.

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
so the measured growth is outside the observed managed heap. Flat handle counts
do not identify the native allocation owner or exclude leaked native state. Growth occurs
under the steady mix alone (quiesced private bytes rose from 570.9 to 589.0 MiB across
the final 10-minute steady phase).

A full dump at 7.9 h (`dumps/04-server-7.9h-idle.dmp`, taken during the final idle
step) shows 584 MiB of committed private memory against an 11 MiB GC heap; 540 MiB
are in 35 native allocations of about 15.8 MiB each, roughly 75% zero bytes. Sampled
contents are loopback socket addresses with client ephemeral ports, Schannel
material (`Microsoft SSL Protocol Provider`, `CN=localhost`, `nistP256`,
`ChainingModeGCM`) and request fragments for the TLS/QUIC port. This is consistent
with a native heap (Schannel, MsQuic or socket-layer state) growing with connection
or TLS churn. The attribution below found that the growth comes from HTTP/3 requests,
not from TLS or connection churn as such.

### 2. HTTP/3 handshake timeouts and multi-second stalls under host CPU saturation

15 of the 17 client errors were HTTP/3 connection-churn handshakes that did not
complete within the client's 10 s QUIC handshake timeout; the screening also saw one
`H3_STREAM_CREATION_ERROR` connection close, which the server reports when it cannot
open its critical streams within its own 10 s startup deadline. All of them, and
every small-request stall above 2 s (up to 15 s, on all five protocols at the same
moment), occurred in the first ~5 hours, while the host was CPU-saturated: in the
worst windows the server used all 8 of its CPUs (140 CPU-seconds in a 17 s sampling
gap) with the machine at 21 of 24 busy. See [host contention](#host-contention).
None occurred in the remaining cycles. This is an observed correlation with host
load; a quiet-host control is still needed to distinguish contention from engine
or runtime limits.

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

### Observed healthy outcomes

- Health checks after every phase passed (34/34); quiesce never stalled.
- Drain/replace under load completed with zero in flight and zero requests served by
  a replaced instance; restart took 3–5 ms.
- Latency of the paced small-request workloads in the last quarter of the run (after
  the contention ended), median of per-minute p99: 2.2 ms (HTTP/1.1), 3.0 ms
  (HTTP/1.1+TLS), 3.6 ms (h2c), 4.1 ms (h2), 4.6 ms (HTTP/3).

## Native memory attribution (issue #278)

Tracking issue #278 asked whether finding 1 belongs to EmbedIO, Schannel, MsQuic or
the socket layer. It belongs to none of them: it is a leak in .NET's `System.Net.Quic`.

### Setup

- Production source b3e55b5 (PR #182 head when the campaign started), with this
  harness. .NET 10.0.12, bundled msquic.dll 2.5.10 unless stated otherwise.
- Host: a different, physical machine from the original campaign. Windows 11 Pro
  10.0.26300, AMD Ryzen 7 9800X3D (16 logical processors), 63 GiB. No benchmark lock,
  CPU pinning or quiet window; several runs shared the host at once, and up to 67%
  machine CPU was observed. Memory retention, unlike latency, does not depend on that.
- One fresh server process per engine, transport and workload. Plan
  `idle:30,load:steady:300` x6`,idle:30`, settle 20 s. The column below is private bytes
  minus GC committed bytes at each quiesced snapshot, the part the managed heap cannot
  explain. Slopes exclude the first two snapshots.

### Per protocol (steady mix, 30 minutes each)

| Engine | Transport | Requests | Native MiB, first load snapshot to last | Slope MiB/h |
| --- | --- | --- | --- | --- |
| EmbedIO | HTTP/1.1 | 2,409,399 | 26.0 to 26.9 | +1.5 |
| EmbedIO | HTTP/1.1 TLS | 2,317,650 | 27.7 to 30.8 | +8.3 |
| EmbedIO | h2c | 1,321,610 | 25.6 to 27.6 | +3.6 |
| EmbedIO | h2 TLS | 1,271,116 | 29.1 to 31.0 | +1.8 |
| EmbedIO | HTTP/3 | 1,217,572 | 44.3 to 81.2 | +80.7 |
| Kestrel | HTTP/1.1 | 2,549,422 | 35.8 to 34.9 | -1.5 |
| Kestrel | HTTP/1.1 TLS | 2,406,025 | 36.2 to 35.9 | -2.2 |
| Kestrel | h2 TLS | 1,305,183 | 28.2 to 35.8 | -4.5 |
| Kestrel | HTTP/3 | 1,203,001 | 49.4 to 89.4 | +76.9 |

Only HTTP/3 grows, on both engines and at similar rates. TCP with TLS, including TLS
connection churn, does not. Splitting HTTP/3 showed growth in both its request
workloads (small, 1 MiB response, upload, stream; +99 and +111 MiB/h on the two
engines) and its connection churn (+37 to +55 MiB/h, which also carries requests).
Replacing the bundled MsQuic with app-local 2.5.11 or 2.6.2 (official
`Microsoft.Native.Quic.MsQuic.Schannel` packages, Microsoft-signed, via the
`System.Net.Quic.AppLocalMsQuic` switch) left the growth unchanged at +88 to +99 MiB/h.

### Allocator

Quiesced snapshots now record `HeapSummary` for every Win32 heap and, for the default
process heap, live (busy) block counts by size from `HeapWalk`. No block contents are
read and no dump is taken. Over a 30-minute HTTP/3 run the default heap's live bytes
rose from about 10 to 40 MiB and its committed bytes from about 12 to 80 MiB; the other
heaps stayed bounded after warm-up. Committed memory growing about twice as fast as live
bytes matches small blocks plus heap overhead. Between the first and last load snapshot:

| Run | Requests | Extra live 16-byte blocks | Per 1,000 requests |
| --- | --- | --- | --- |
| EmbedIO, HTTP/3 steady | 1,220,486 | 1,232,586 | 1,009.9 |
| Kestrel, HTTP/3 steady | 1,232,365 | 1,238,414 | 1,004.9 |
| EmbedIO, HTTP/3 1 MiB responses only | 430,225 | 430,257 | 1,000.1 |
| EmbedIO, HTTP/3 small requests only | 1,258,351 | 1,258,383 | 1,000.0 |

No other block size grew by more than a few per 1,000 requests.

### Cause

`QuicStream` keeps its send buffers in a `MsQuicBuffers` struct field and, in its
constructors, hands the stream handle `Disposable = _sendBuffers`. That boxes a copy
taken before any write, when the copy's `QUIC_BUFFER*` is still null. The first
`WriteAsync` allocates the 16-byte `QUIC_BUFFER` array into the field
(`NativeMemory.AllocZeroed`), and `SEND_COMPLETE` frees only the data it points to. When
the handle closes it disposes the boxed copy, which frees nothing, so the array of
every stream that ever sent data is orphaned. Every HTTP/3 response sends data on a
request stream, hence one block per request. The handoff came from
[dotnet/runtime#100228](https://github.com/dotnet/runtime/pull/100228) (March 2024):
`release/9.0`, `release/10.0` and `main` contain it, `release/8.0` does not. Only .NET
10.0.12 was measured.

`quic-send-buffer-probe` (below) confirms this without HTTP, EmbedIO or Kestrel: one
process, loopback QUIC, 2,000 server streams per round, everything disposed before
counting.

| Server streams | Extra live 16-byte blocks | Disposed streams still owning the array |
| --- | --- | --- |
| Write one byte | +2,002, then +2,002 | 2,000, then 2,000 |
| Complete with an empty write | +1, then 0 | 0, then 0 |

An empty write never allocates the array, which is the toggle that isolates it.

### Consequences for EmbedIO

- No EmbedIO lifetime defect was found, and nothing in EmbedIO frees or retains this
  allocation. TCP protocols, WebSockets and connection churn without HTTP/3 are flat.
- Long-running HTTP/3 servers on .NET 9 and 10 grow by about 16 live bytes, roughly 35
  committed bytes, per request: around 35 MiB per million HTTP/3 requests, never
  returned until the process exits. Server replacement does not help because the
  memory belongs to disposed streams.
- No configuration avoids it, since every response write allocates the array. A
  reflection-based release of the private field after disposal could free it, but it
  would be a private-runtime shim with a use-after-free risk if a late send-completion
  ever reached the stream; none is added. The in-tree native MsQuic adapter does not go
  through `QuicStream`. Until .NET ships a fix, hosts with heavy HTTP/3 traffic can
  recycle processes periodically or leave HTTP/3 disabled where it is not needed.
- The leak is reported as
  [dotnet/runtime#135583](https://github.com/dotnet/runtime/issues/135583), with a
  standalone repro.

### Repeating the attribution

```sh
dotnet build test/EmbedIO.LoadBenchmark -c Release -o <runner>
dotnet <runner>/EmbedIO.LoadBenchmark.dll quic-send-buffer-probe --streams 2000
dotnet <runner>/EmbedIO.LoadBenchmark.dll endurance --output TestResults/endurance/h3 --engine embedio --transports h3 --plan idle:30,load:steady:300,load:steady:300,load:steady:300 --settle 20
```

Compare `quiesced.nativeHeaps.defaultHeapBlocks` (pairs of block size and count) across
snapshots in `steps.jsonl`. When a fixed .NET runtime ships, the probe's "write" rows
should report no growth and no streams still owning the array.

The heap walk holds the heap lock, so it runs inside a GC no-GC region. In one run,
before that change, a walk deadlocked the server with a GC, stalling every managed
thread at a quiesced snapshot. That run's orchestrator then failed every later step with
"stream in use". A timed-out control read now marks the child unusable with an explicit
error instead. With the fix, a full 30-minute rerun of the configuration that
stalled completed all 8 steps with a heap walk at every quiesced snapshot.

## Host contention

For the same workload, server CPU per request was about 4× higher and client CPU per
request 3–5× higher in the first ~5 hours (cycles 1–10) than afterwards, with an
abrupt change after cycle 10. The client never loads EmbedIO, so the change is
consistent with host-wide contention, but does not establish its cause or exclude
engine/runtime limits. Throughput
targets were paced, so achieved rates were lower during contention. Results from
those hours are kept and reported, not removed.

## Coverage gaps

- The memory attribution runs were 30 minutes per configuration on a different host;
  the leak's per-request rate, not a multi-hour slope, carries the conclusion. Only
  Windows was measured; the leaking code is platform-independent managed code, but
  Linux and macOS were not run.
- Additional multi-hour representative runs (steady-only, fault-only, WebSocket-only)
  were not run; the 8-hour run interleaves all of them.
- WebSockets over HTTP/3 (RFC 9220) are not exercised: `ClientWebSocket` does not
  offer them.
- QUIC connections are not visible in the TCP census.
- HTTP/1.1 initial-request idle timeouts (90 s) were observed only within long phases.
- The host is a shared VM with dynamic memory; no dedicated-hardware results.

## Next steps

1. Done: the native growth is attributed to `System.Net.Quic` (issue #278, above).
   When a .NET runtime with a fix ships, rerun `quic-send-buffer-probe` and an HTTP/3
   endurance run on it.
2. Reproduce the post-replacement connect timeout with repeated drain-and-replace
   under HTTP/1.1 connect churn only.
3. Repeat a shorter mixed run on a quiet host to test whether the HTTP/3 handshake
   timeouts depend on contention.

## Integration review

The campaign figures above remain historical evidence for the named tested SHA.
The current harness also fails a step when a retired listener reports a drain or
server-run error, rejects missing drain reports, and fails the campaign when the
server exits unsuccessfully. A healthy replacement cannot erase an earlier drain
failure. No new measured endurance campaign is implied by these tooling changes.
Remaining investigations are tracked separately in issues #278 through #283.

The original classifier counted all exceptions in planned disruption windows as
disrupted outcomes, so its historical zero reported validation failures do not
prove that no validation exception occurred inside those windows. The reviewed
classifier exempts only expected transport/cancellation failures; detected byte,
status, version and framing mismatches remain errors during replacement too.

Review validation on the engine base at `d8d445b`: locked restore and warning-free
harness build, parser/suppression guards and formatting passed. The standalone
`endurance-verdict-check` passes clean, failed and malformed drain reports and
disruption classification. A synthetic analysis check excludes active, missing-counter
and stuck snapshots from retention trends. Short HTTP and WebSocket load phases
passed, as did small-request drain/replacement. The broader six-second steady
drain phase initially failed with four late HTTP/1 streaming read errors at about
ten seconds in both EmbedIO and Kestrel. A paced HTTP/1 worker could wake after the
phase ended and start another exchange on a connection retired during drain.
The worker now checks the phase after its wait. Repeating the same bounded control
passes on both engines with zero errors. Failed attempts remain retained; these
short functional checks are not new endurance or comparative performance measurements.
