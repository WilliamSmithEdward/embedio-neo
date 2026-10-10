# Performance checks

Run the allocation checks from the repository root:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --verify-allocations
```

This dependency-free, unpackaged project is intentionally outside `EmbedIO.sln`.
Desktop CI runs it alongside the regression suite. Allocation ceilings catch
regressions; timings are informational and should be compared on the same machine.

See [the additional audit](../../docs/user-reports/performance-audit-second-pass.md) for negotiation, header, byte-order and history workloads. See [the audit report](../../docs/user-reports/performance-audit.md) for the baseline,
measurement method, coverage and limits. Omit `--verify-allocations` for baseline
measurements, using the same runner source against each revision.

## Cold-start checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-cold-start
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --cold-start base-100
```

The verifier launches three fresh child processes per workload. Individual samples
measure the workload on its caller thread without cache/JIT warmup; they do not
include process launch or first HTTP response time. The delayed-start workload
uses a socket-free server with an intentional 50 ms preparation delay.
See [the cold-start audit](../../docs/user-reports/cold-start-audit.md) for baseline
measurements, comparison controls and startup limits.

## Listener queue checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-queue --verify-allocations
```

This socket-free workload measures insertion/signalling and the real accept queue at
burst sizes 1, 16 and 256. Context creation is outside measurement. The 300 B/request
ceiling guards against snapshots growing with the queued request count; timing is
informational. See [the HTTP listener audit](../../docs/user-reports/http-listener-audit.md).

## HTTP/HTTPS transport checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-listener-http
```

The loopback client and server share a process. JSON includes throughput, latency
percentiles, process allocations, GC counts and memory. Add --retain-connections
for retained-reference cleanup diagnostics; --requests, --rounds, --concurrency
and --payload-bytes select workloads. Timing is informational. Heavy repeated
churn can exhaust Windows socket/TIME_WAIT capacity; failures abort without retries.
See [the lifetime report](../../docs/user-reports/listener-connection-lifetimes.md)
for methodology, baseline measurements and compatibility coverage.

## Listener body and header allocations

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release -- --listener-allocations
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-listener-allocations
```

Measures the actual drain and header serializer with fixture setup excluded.
The consumed-body fixture stays at EOF; the unread-body fixture resets its source
position and remaining length between operations (that reset is included). Headers
must match exact expected UTF-8 bytes. Allocation budgets, rather than timing, gate CI.

Add `--request-body-bytes 65536` to `--listener-http` for POST, and select
`--body-consumption full`, `partial` or `none`. Full is the default; partial consumes
half the body. The handler validates every consumed byte. Existing GET behavior is
unchanged. `--connection-policy keep-alive`, `close` or `both` controls connection
churn; the default is both for GET/full POST and keep-alive for partial/unread POST.

Partial/unread POST requires keep-alive and fewer than 100 requests per worker,
including eight warmups. The existing connection reuse limit can force an early
close while an unread body is still being sent. A larger baseline workload hit a
TLS connection reset; it was not a valid performance sample. The library's close
and drain policy is preserved. Benchmarks do not retry failures or hide them.

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --request-body-bytes 65536 --body-consumption full --connection-policy keep-alive --concurrency 16 --requests 40 --rounds 5
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --request-body-bytes 65536 --body-consumption none --concurrency 16 --requests 8 --rounds 5
```

The JSON adds requestBodyBytes and bodyConsumption. Allocation and memory figures
include the loopback client and server; timings are informational. Verification
with `--verify-listener-http --request-body-bytes 65536` uses bounded workloads and
checks terminal resource cleanup. Full consumption verifies HTTP/HTTPS with both
connection policies (four workloads); partial/unread verifies keep-alive (two).

## Chunk framing and charset extraction

`--listener-wire` measures actual chunk-prefix formatting and charset extraction;
`--verify-listener-wire` enforces allocation budgets and expected outputs. Timings
are informational. Optional `--chunk-bytes 1024` on `--listener-http` enables
chunked responses with that maximum write size; existing response defaults remain
unchanged. Use `--payload-bytes 65536` for a 64 KB response, and
`--verify-listener-http` for bounded HTTP/HTTPS/churn cleanup verification. Client
response bytes are validated after decoding transfer framing; dedicated regression
cases check the exact raw chunk framing separately.

## Managed HTTP/1 response writes

`--response-write` measures the managed HTTP/1 response stream against a
socket-free counting transport: the first write that commits the response head
(fixed-length and chunked bodies of 13 bytes, 16 KiB and 1 MiB), a complete small
chunked response including synchronous disposal, and subsequent chunks of 1 KiB,
16 KiB, 64 KiB, 80 KiB and 1 MiB on the array, memory and synchronous paths. Each
row reports median time, managed allocation, transport submissions and bytes per
operation. The `first-*` rows include construction of a socket-free context; the
`context-only` row is that control. Use the same runner against baseline and
candidate cores (copy the output and replace only `EmbedIO.dll`), alternate
process order, and set `DOTNET_TieredCompilation=0` for stable-JIT comparisons.
Submission and byte counts are exact; timings exclude kernel, TLS and application
work and do not establish server throughput.

## Managed engine parser

`--engine-parser` measures context construction, header parsing and handoff for
buffered batches of 1, 16 and 64 requests. It validates the resulting request path,
header value and complete consumption. Reflection observes the old MemoryStream/cursor or the new pending byte segment;
their boxing and field-access costs differ and are included. URI finalization,
sockets and application work are excluded.
Use the same runner binary against baseline and candidate core assemblies. These
figures are parser microbenchmarks, not end-to-end server throughput.


## Separate-process engine comparison

Build this runner once, copy its output to a baseline directory, and replace only
that copy's EmbedIO.dll with the baseline core built for the same target. Keep the
candidate core in the original output. Run from PowerShell 7:

```powershell
./test/EmbedIO.Performance/CompareEngine.ps1 -BaselineRunner <baseline>/EmbedIO.Performance.dll -CandidateRunner test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll -OutputDirectory TestResults/engine-comparison -Rounds 3 -Seconds 15
```

The script uses independent server/client processes, five seconds of warmup and
an explicit measurement handshake. It compares plaintext GET at pipeline depths
1 and 16, with 16 concurrent connections by default. Each connection sends 80
requests and explicitly closes on its final request; the client verifies server
EOF before disposing the socket. Every response payload is checked. Workload
errors abort the comparison and are retained in stderr.log; they are not retried.
Use a fresh output directory for each experiment to preserve failed evidence.

On machines with at least eight logical CPUs, server and client use disjoint
affinity masks 15 and 240. Record CPU topology, OS, runtime, source revision and
background activity alongside results. JSON includes the core assembly hash,
throughput, sampled batch-send-to-response p50/p95/p99, server CPU time,
allocations and GC counts. Divide server allocations and CPU by validated client
requests for per-request figures. Samples every 67 responses distribute across
pipeline positions. This is a closed-loop, same-host micro workload: it excludes
connection setup from latency samples and cannot characterize open-loop overload,
Internet latency, TLS, uploads, or HTTP Arena performance. Retained-memory and
broader workload comparisons remain separate validation work.

## Managed WebSocket reader

`--websocket-read` measures masked frame parsing, unmasking and incoming text
validation from a MemoryStream. It checks every payload byte and full wire
consumption. Workloads use 16, 1,024, 65,536 and 65,538-byte UTF-8 payloads, one or sixteen
frames, and both text and binary opcodes. A reader is reused across messages;
the 16-byte and 65,538-byte sixteen-frame text workloads split multibyte code
points across frames. Each sample uses 5,000 messages and records generation 0/1/2 collection counts
to make GC-related timing variation visible.
Five samples follow warmup. JSON identifies the core assembly hash, runtime,
OS, architecture, nanoseconds/message and allocated bytes/message.

Use the same built runner for baseline and candidate; copy its output and
replace only EmbedIO.dll in the baseline copy. Run each process separately on an
otherwise quiet machine and retain every sample. Reflection and content checks
are included equally on both versions. This isolates reader costs; it excludes
network I/O, message reassembly, application callbacks and sender work, and is
not end-to-end throughput or tail-latency evidence.

For stable-JIT comparisons, set `DOTNET_TieredCompilation=0` in each benchmark
process environment (and restore the shell's previous setting afterward).
The JSON records this value. Ordinary tiered runs remain useful but tier
transitions can distort a short workload; do not combine those samples with
stable-JIT runs. Record CPU topology and source revisions as well as assembly
hashes. The 2026-10-08 validation-cost experiment is recorded in
[the engine program](../../docs/project/http-engine.md#utf-8-reader-cost-experiment-2026-10-08).

## Managed WebSocket echo

`--websocket-host --url <prefix>` runs a managed-listener echo module, and
`--websocket-load --url <prefix> --size <bytes> --fragments <n> --connections <n> --seconds <s> [--text]`
is an independent RFC 6455 client in a separate process. The client masks
every frame, stamps a sequence number into each payload, verifies every
echoed byte, opcode and length, and finishes each connection with a full close
handshake. Latency spans the client's send through the complete verified echo
(closed loop, one outstanding message per connection). The host reports
allocations, GC counts, CPU time, and the bytes promoted by a forced, blocking,
compacting full collection both with connections open and idle and after they
close. Only public EmbedIO APIs are used.

`CompareWebSocket.ps1 -BaselineRunner <dir>/EmbedIO.Performance.dll -CandidateRunner <dir>/EmbedIO.Performance.dll -OutputDirectory <dir>`
alternates baseline and candidate hosts per round across six workloads. Copy
one harness build into two directories and replace only EmbedIO.dll in the
baseline copy. The client always comes from the candidate directory. With eight
or more logical processors the host is pinned to CPUs 0-3 and the client to
4-7. Each run writes one JSON per side and round, plus the raw host lines.

### Results for #190 (2026-10-09)

Environment: AMD Ryzen 7 9800X3D (8 cores, 16 threads), 64 GB, Windows 11 Pro
10.0.26300, .NET 10.0.12, loopback TCP, 8 s measured after a 2 s warmup,
three alternating rounds; medians shown. Baseline: PR #182 head `9e1fe09`
(core SHA-256 `101d2192...`). Candidate: branch `codex/websocket-hardening`
at `50c0d33` (core `ecc64587...`). Harness assembly `30c52773...` on both sides.

| Workload | msg/s before | msg/s after | p50 us before / after | p99 us before / after | Allocated B/msg before / after |
|---|---:|---:|---:|---:|---:|
| 32 B binary, 1 connection | 28,552 | 35,918 | 32.6 / 25.3 | 83.9 / 68.3 | 1,464 / 832 |
| 32 B binary, 16 connections | 254,924 | 277,275 | 60.2 / 55.3 | 96.4 / 97.3 | 1,464 / 832 |
| 1 KiB text, 4 connections | 101,139 | 140,063 | 36.6 / 27.6 | 65.6 / 43.5 | 7,808 / 3,928 |
| 64 KiB binary, 4 connections | 4,730 | 37,282 | 782 / 59 | 1,050 / 141 | 281,203 / 66,440 |
| 64 KiB as 16 fragments, 4 connections | 4,554 | 31,276 | 794 / 82 | 1,137 / 243 | 350,309 / 138,608 |
| 1 MiB binary, 1 connection | 82 | 876 | 8,816 / 552 | 25,039 / 7,507 | 4,455,501 / 1,051,731 |

Bytes surviving a full collection after load, connections open and idle
(before / after): 32 B 413 / 417 KiB, 16 connections 690 / 726 KiB,
1 KiB 472 / 498 KiB, 64 KiB 472 / 1,251 KiB, fragmented 473 / 1,509 KiB,
1 MiB 413 / 2,147 KiB. After every client closed, both sides read 20 to 95 KiB
higher than with connections open, and the gap between them is unchanged.

Limitations and retained measurements:

- An A/A run (baseline on both sides) varied by about 10% in throughput and
  far more in p99, so the single-connection 32 B gain is within noise: in one
  of three rounds the baseline was faster. Allocation counts were identical
  from round to round. The 64 KiB and 1 MiB gains are an order of magnitude
  larger than that noise.
- The 1 MiB candidate p99 varied from 2.5 to 13.9 ms between rounds. Each
  1 MiB payload is a large-object allocation; the candidate ran 2,336 gen2
  collections in 8 s against 440 for the baseline, which handled a tenth of
  the messages.
- The larger retained figures after 64 KiB and 1 MiB load are buffers held
  by the process-wide `ArrayPool<byte>.Shared` (64 KiB send frames rent from
  its 128 KiB bucket; large receives use 64 KiB chunks). The pool bounds
  this per processor and trims under memory pressure; it does not grow with
  the number of connections. A run with 16 KiB send frames held 0.4 to
  0.6 MiB less but lost 17 to 32% of large-message throughput and doubled the
  1 MiB p99, so it was not adopted.
- The first comparison measured retained memory with `GC.GetTotalMemory`,
  which returned negative values in this host on .NET 10; those columns were
  discarded and the host now uses `GC.GetGCMemoryInfo`.
- One machine, loopback only, no TLS, no HTTP/2 tunnel, no slow peers and no
  Linux or macOS runs. Not an HTTP Arena result.

### HTTP/2 flow scheduler comparison

Run `python scripts/compare_http2_flow.py` from the repository root. The default
baseline is the pre-scheduler commit `759db37b396c4727f6fbb63540aed7b2126fcd2b`;
`--baseline` can select another compatible revision. Generated sources, hashes,
build log and raw/summary measurements go under ignored
`TestResults/http2-flow-comparison` (`--output` must stay under `TestResults`).
The baseline class is renamed so both exact implementations share one runner;
no reflection occurs in the measured operations. Three stable-JIT processes each
run five alternating samples. The writable case measures reserve/replenish cycles;
blocked batches include opening streams, queueing one-byte reservations and
releasing shared connection credit with a cancellable token. Allocation totals
include asynchronous workers. These are .NET 10 component comparisons, not whole
server throughput, fairness under arbitrary workloads or tail-latency evidence.


## QPACK response planner

```sh
dotnet build test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --qpack-response
```

This mode writes JSON lines: runtime/OS/architecture, GC and tiering settings,
assembly SHA-256/source revision, then all seven rounds of CPU time, allocated
bytes and field-section bytes. Each mode warms up for 5,000 calls and measures
25,000 operations per round; mode order rotates. Inputs cover status-only,
static-table, repeated dynamic and sensitive fields. Reflection and compiled
delegate setup are outside measurement. The warmed planner includes immediate
Section Acknowledgment processing; stateless encoding needs no acknowledgment.

Compare the same runner/input/settings on the same host. For comparisons without
tier transitions, set `DOTNET_TieredCompilation=0` for the process; retain the
recorded setting with the results. There is no elapsed-time CI gate. This
single-thread component benchmark excludes field construction, the connection
gate, HTTP/QUIC/TLS, application work and cold-table instruction traffic. It does
not establish request throughput or tail latency. See
[the engine record](../../docs/project/http-engine.md) for measured changes and
independent decoder validation.


### QPACK table pressure

Run the same built runner with `--qpack-churn` to measure table replacement
costs. Three synthetic workloads rotate through 2,048 preconstructed field
lists: `unique` changes the resource value each call (and revisits it next
cycle), `bursts` repeats each list eight times, and `mixed` combines a stable
field with each changing resource value. A fresh planner per round receives
4,096 warmup operations followed by 32,768 measured operations. Seven rounds
alternate stateless/planner order. Reflection, delegate compilation, input
construction, input hashing and JSON output are outside the timed loop.

Measurements include response encoding, draining encoder instructions, immediate
insert-count credit and Section Acknowledgment handling. The driver supplies
feedback directly; it does not time an independent decoder or network delivery.
It reports section bytes and encoder-stream bytes separately and together,
plus time/allocation per operation and a SHA-256 of the workload description.
Feedback bytes and transport framing are not included in the byte totals.
The planner uses capacity 4096 with no permitted blocked streams. Fresh state
per round and a fixed schedule make baseline/candidate workloads comparable.
The static control uses the same fields, array indexing and delegate dispatch.

For a before/after comparison, copy this runner's complete output to separate
ignored directories and replace only `EmbedIO.dll` in the baseline copy. Retain
both hashes and every sample, use the same tiering setting, and alternate process
order across repeats. Do not run competing benchmarks concurrently. These
component results do not establish server throughput, tail latency, delayed-peer
behavior or retained connection-memory costs, and impose no elapsed-time CI gate.

## Brotli request reader

`--brotli-request-read` compares `ReadByte` and 256-byte bulk reads of the actual
.NET 10 request decoder, with 0, 64 and 4,096-byte deterministic bodies. Every
byte and final decoded length are checked. It records five alternating mode
rounds after 128 warmup streams, with 1,024 measured streams per round. JSON
includes input/wire/core/runner hashes, runtime, OS, architecture, JIT/GC settings,
managed allocations, collection counts and time per stream.

Use the same runner binary with baseline and candidate core assemblies. Fixture
encoding and reflection/delegate construction occur before measurement; stream
construction, runtime decoding, content validation and disposal are measured.
The reusable bulk output buffer is outside measurement. Native allocations,
transport and application work are excluded. Timings are informational and
require repeated, controlled process comparisons; this is a codec component
measurement and does not establish server throughput or retained-memory behavior.
