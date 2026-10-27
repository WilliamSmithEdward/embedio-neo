# Separate-process load comparison

This harness compares the PR #182 managed engine (`candidate`) with the current
`main` core (`baseline`) and ASP.NET Core Kestrel from the installed shared
framework. It is test-only, outside `EmbedIO.sln` and never packed. Results are
development evidence for program #181, not public benchmark submissions.

## Prepare and run

```sh
python scripts/prepare_load_benchmark.py
dotnet TestResults/load-benchmark/runners/candidate/EmbedIO.LoadBenchmark.dll run --output TestResults/load-benchmark/results-<label> --baseline-dir TestResults/load-benchmark/runners/baseline --server-cpus 0-7 --client-cpus 8-15
```

The prepare script builds this runner from the checkout, builds the baseline core
from a pinned revision exported with `git archive` (default `1445c23`, the main
commit PR #182 last merged), and copies the runner twice, swapping only
`EmbedIO.dll` in the baseline copy. Builds use `ContinuousIntegrationBuild`, but the core's hash still
changed between two builds of the same commit in different output directories, and
its informational version embeds the current commit, so its hash changes with every
commit even when the source does not. Treat `runners.json` as the record of the
binaries actually run, not as a reproducibility proof. `runners.json` records revisions, uncommitted paths and SHA-256
hashes.

`run` options: `--scenarios all|<prefix>,...` (a name ending in `$` selects only the
scenario with exactly that name, so `h1-plain-small-c64$` leaves out
`h1-plain-small-c64-close100`), `--engines candidate,baseline,kestrel`,
`--baseline-all-protocols` (alias for `--modern-baseline`; also run the baseline on HTTP/2 and HTTP/3 scenarios, for a
baseline core that serves them, such as an earlier engine revision passed to the prepare
script with `--baseline <revision>`),
`--rounds 3`, `--warmup 5`, `--duration 15`, `--idle 2`, `--server-cpus`/`--client-cpus`
(for example `0-7`), `--time-wait-limit 4000`, `--profile`, `--trace-tool <dotnet-trace>`.
The output directory must be new or empty, so earlier evidence is never overwritten.
Set `EMBEDIO_BENCH_EXCEPTION_DETAIL=1` to key the server's first-chance exception
counts by message and write the first stack trace per key to its stderr log; leave it
unset for comparison runs.

Exception attribution now records cumulative censuses after startup, after warmup
and idle, after load and client cleanup, after post-load idle, and after server
stop **and disposal**. `serverStop.stopMilliseconds` includes disposal; older
reports stopped that clock before disposal. Warmup reports also include opened
connections and server-initiated closes. Use
`python scripts/summarize_teardown_exceptions.py <results-directory>` to print
each phase, every exception key, throws per 1,000 completed requests and throws
per opened client connection. Failed samples, incomplete snapshots, unsuccessful
child exits and nonzero open server TCP sockets fail the summary. QUIC cleanup
is outside the TCP census. Missing denominators are reported as unavailable.

`--connections N` and `--streams N` override scenario concurrency identically for
all engines; zero retains each default. The actual values appear in every sample.
HTTP/1.1 requires one stream per connection. This is useful for short diagnostic
passes; overrides do not make a busy host quiet. For example:

```sh
EMBEDIO_BENCH_EXCEPTION_DETAIL=1 dotnet <runner>/EmbedIO.LoadBenchmark.dll run \
  --output <fresh-directory> --engines candidate,kestrel \
  --scenarios h1-plain-small-c64-close100,h2-tls-churn-c32,h3-churn-c16 \
  --connections 1 --streams 1 --rounds 2 --warmup 2 --duration 5 --idle 2
python scripts/summarize_teardown_exceptions.py <fresh-directory>
dotnet <runner>/EmbedIO.LoadBenchmark.dll exception-accounting-check
```

Use the equivalent process environment assignment in PowerShell. Detailed
first-chance stacks perturb timing and may include runtime rethrows. Opened
connections are a client-side denominator; they are **not** a server close census
or a count of distinct errors. The load phase includes the client's connection
disposal, so persistent versus churn comparisons help distinguish request work
from teardown. Deliberate fault workloads must remain separate. The resource
sampler stops by disposing its timer, which completes a pending tick normally;
it no longer injects a benchmark-owned cancellation into the exception census.
Its executable regression retains genuine caught-error and caller-cancellation
controls. See [the initial attribution report](../../docs/project/http-teardown-exceptions.md)
for evidence and remaining work under issue #282.

Scenarios ending in `-close100` are controls: the client closes every connection
after 100 requests, so all engines pay the same reconnect cost. (They were added when
the managed listener capped keep-alive connections at 100 requests; the current
engine has no such cap.)

Scenarios named `-inspect-` request `/inspect?id=42&name=neo%20bench&tag=a&tag=b`
with User-Agent, Accept, Cookie, Referer and X-Request-Id headers. The handler
reads the query (including the repeated `tag`), those headers, both cookies, the
referrer, body framing and the endpoints, and answers 400 naming the first
mismatched property. Body framing is read but HasEntityBody is not required to be
false over HTTP/3, where a GET without content-length reports an unknown length. Churn scenarios are capped (5 s for HTTP/1.1,
1.5 s for HTTP/2) to stay within the host's TIME_WAIT and ephemeral-port capacity.

Scenarios named `-json` request `/json/<length>`, a deterministic JSON document
(`test/Shared/CompressibleJson.cs`), with a browser's `Accept-Encoding: gzip, deflate, br`.
Each engine compresses it with its own defaults: EmbedIO through `ResponseSerializer.None`,
the path `SendDataAsync` takes, and Kestrel through ASP.NET Core's response compression
middleware, added for `/json` only. Response bytes are the compressed body bytes, and the
client report counts responses per content coding (`contentCodings`). The client decodes
the first body of each coding and compares it with the document; a later body with the same
bytes is accepted as it stands, and any other body is decoded and compared.

## Idle keep-alive connections

`idle` measures what the server retains per idle connection, for any engine and
protocol the server host supports:

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll idle --output <fresh-directory> \
  --baseline-dir <baseline runner> --engines candidate,baseline,kestrel \
  --protocol Http1 [--tls] --connections 1000 --hold 5 --settle 2 [--rounds 1] [--server-cpus 0-3]
```

Each engine starts in its own process as in `run`. The orchestrator opens one
`HttpClient` per connection (`MaxConnectionsPerServer = 1`, infinite pool
lifetime), sends one `GET /plaintext` with the exact requested version, then
holds every connection idle for `--hold` seconds. The server reports a collected
snapshot before the connections open, while they are idle and `--settle`
seconds after the client closed them all. `summary.md` lists managed heap,
working set, private bytes and handles per idle connection, thread and
thread-pool counts before, idle and after, what remains after the close, and the
server-side socket states at both points. HTTP/3 implies TLS. The figures are
retained state after two forced collections; a client-side pool holds the
connections, so the client's own cost is excluded by design.

## Load failure accounting controls

Run the bounded, socket-free controls against the prepared runner:

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll load-failure-check
```

The six controls exercise the actual multiplexed worker through an internal
`HttpMessageHandler` factory, without starting a listener. They cover:

- A response-header failure canceling a blocked sibling stream.
- A body mismatch after a valid byte canceling a blocked sibling stream.
- Caller cancellation during active work and before worker entry, both producing
  a non-null result error.
- Sibling cancellation preserving an already recorded primary error.
- Exactly one fully validated response and latency sample retained before a later
  stream fails; failed work does not enter the totals.
- Two healthy streams preserving both validated responses and latency samples.

HTTP/2 and HTTP/3 stream failures are recorded and cancel siblings before the
worker awaits their combined completion. The worker still joins all streams and
owns handler disposal; status, protocol, body and byte-count validation remain
required. Cancellation is skipped as a secondary error only when a primary error
has already been recorded. A caller abort cannot become a successful result merely
because workers observe cancellation and leave their loops without throwing.

These are the V46/V47 harness corrections described in the
[audit report](../../docs/project/performance-cleanup-audit.md). The retained
pre-fix run timed out in the first blocked-sibling control; it did not reach the
caller-cancellation control and is not separate negative baseline proof for V47.
All six controls passed after correction. This socket-free evidence does not prove
that the original Kestrel measurement stall was caused or repaired by these
changes, nor does it establish HTTP/3 transport or native-provider acceptance.

## Cancellation and recovery

`recovery` keeps one server process alive through healthy load, an abort storm, idle drain,
healthy load again, optional sustained load and a timed shutdown, and writes `recovery.json`
with every phase's resource snapshot, open server sockets, the recovery throughput ratio,
sustained growth slopes and shutdown time. It exits non-zero with its findings listed when
descriptors, open sockets, working set or managed heap keep growing, recovery throughput drops
below 80 %, or shutdown does not complete.

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll recovery --output <fresh dir> --engine embedio|kestrel \
  --protocol Http1|Http2|Http3 [--tls] [--connections 16] [--streams 8] [--healthy-seconds 10] \
  [--storm-seconds 20] [--storm-workers 32] [--storm-mode both|upload|download] [--idle 5] \
  [--sustain-minutes 0] [--snapshot-interval 60] [--server-dir <runner copy>]
```

## Paced QUIC churn

`quic-churn` starts one HTTP/3 server process per engine and schedules QUIC connections
open-loop at each `--rates` value. Every attempt is timed separately for the handshake,
one validated `GET /plaintext` on a new request stream (a minimal static-table QPACK
client), and the graceful close. Failures are kept with their phase, and nothing is
retried. `--mixed-rate`/`--large-rate` add paced small and 1 MiB GETs on warm
`HttpClient` HTTP/3 connections. `--burn-threads` runs a separate CPU-spinning process as
a contention control. Each run writes `environment.json` (hashes, idle baseline), one JSON
per engine run with a per-second CPU timeline, and `summary.md`.

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll quic-churn --output <fresh dir> [--engines embedio,kestrel] \
  [--rates 4,16,64] [--phase-seconds 30] [--idle 5] [--rounds 1] [--mixed-rate 0] [--large-rate 0] \
  [--mixed-connections 4] [--max-outstanding 512] [--handshake-timeout <seconds>] [--burn-threads 0] \
  [--server-cpus 0-7] [--client-cpus 8-15] [--source-revision <text>] [--server-dir <runner copy>]
```

See [HTTP/3 handshake and latency on a less loaded host](../../docs/project/http3-quiet-host-validation.md).

Past the server's capacity the same mode shows how it sheds load. On two server CPUs,
`--engines embedio --rates 250,1000,2000,4000 --phase-seconds 15 --server-cpus 0-1`
overloads EmbedIO's HTTP/3 listener, and each failure keeps its error, so refusals
(`ConnectionRefused`), connections closed after their handshake (`ConnectionAborted`
with application error 263, H3_EXCESSIVE_LOAD) and handshake timeouts count apart. The
client stops starting attempts while `--max-outstanding` of them (512 by default) are in
flight, so a server that fails attempts quickly is offered more of them than one that
fails them slowly; with `--max-outstanding 8192` a server that refuses at once faced
every attempt at 2,000 and 4,000 per second. Set `EMBEDIO_BENCH_WARNINGS=1` to write
EmbedIO's warnings, including the listener's refusal counts by reason, to the server's
stderr log. See
[Bounded TLS work under load](../../docs/project/quic-development-and-testing.md#bounded-tls-work-under-load).

## Cold start

`cold` measures how a new server process answers its first requests. Every
measurement starts a fresh server; one client process, started once and warmed by
an excluded first iteration, sends the requests.

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll cold --output <fresh dir> [--iterations 20] [--requests 32] \
  [--transports h1,h1tls,h2tls,h3] [--engines candidate,candidate-r2r,baseline,kestrel] \
  [--r2r-dir <ReadyToRun runner>] [--baseline-dir <baseline runner>] [--server-cpus 0-3] [--client-cpus 4-11]
```

Each measurement records the time from process start to the server's `READY`
line, the process uptime when the listener had started, the server's own start
time, the first request on a new connection, the second request, the median and
maximum of the remaining requests on that connection, one request on a second
connection, and the busy CPUs of the whole machine while it ran. Every response
is checked. The engine order alternates by iteration, and `summary.md` gives each
figure's median, 90th percentile and range.

`--r2r-dir` adds the same runner published with ReadyToRun as `candidate-r2r`.
Kestrel runs from the ASP.NET Core shared framework, which ships precompiled, so
this separates EmbedIO's JIT time from the rest of its first request:

```sh
dotnet publish test/EmbedIO.LoadBenchmark/EmbedIO.LoadBenchmark.csproj -c Release -r <rid> \
  --self-contained false -p:PublishReadyToRun=true -p:NuGetLockFilePath=<scratch>/r2r.lock.json \
  -o <ReadyToRun runner>
```

A RID-specific restore rewrites lock files, so the command points them at a
scratch file and leaves the repository's locks alone. `run` accepts the same
ReadyToRun runner as `--baseline-dir` with `--modern-baseline`, to check that
precompiling does not cost warm throughput or tail latency.

## Comparing results against A/A noise

`scripts/compare_load_benchmark.py <result dir> --reference baseline --aa <A/A dir>` prints each
engine's median requests/s, CPU and bytes per request against a reference and calls a
difference real only when it exceeds the identical-runner (A/A) ratio, both engines' own sample
spread and a 3 % floor (`--floor`).

## Running on a shared machine

Two scripts turn one orchestrator invocation into evidence that can be checked
after the fact:

```sh
python -I scripts/guarded_load_benchmark.py --owner "<agent and branch>" \
    --output TestResults/load-benchmark/results-<label> \
    --own-marker <absolute path of this checkout> -- \
    dotnet TestResults/load-benchmark/runners/candidate/EmbedIO.LoadBenchmark.dll run \
    --output TestResults/load-benchmark/results-<label> --baseline-dir TestResults/load-benchmark/runners/baseline \
    --modern-baseline --scenarios h3- --rounds 3 --server-cpus 0-7 --client-cpus 8-15
python -I scripts/summarize_load_benchmark.py TestResults/load-benchmark/results-<label>-a1 --compare baseline candidate
```

`guarded_load_benchmark.py` waits until `TestResults/BENCHMARK-LOCK.txt` is absent
and no foreign `EmbedIO.Tests`, load-benchmark, `dotnet test`, conformance or fuzz
process has been seen for `--idle-seconds` (120), creates the lock with
`O_CREAT | O_EXCL` (a lock written by another owner is never overwritten), then
starts the command and checks every `--watch-interval` seconds that the lock is still
its own and that no foreign process appeared. On either event it kills the command's
process tree, writes `INVALID.txt` and `attempt.json` into that attempt's output and
retries in a fresh `-a<n>` directory after the idle gate. Invalid attempts are kept.
The command's own `--output` is rewritten to the attempt directory; a command that
fails on its own (for example a failed sample) is not retried, because the failure is
the result. The script's own shells and the command's descendants are excluded from the
foreign check; pass `--own-marker` for any other process of this checkout whose command
line would otherwise match, such as a build.

`summarize_load_benchmark.py` prints every sample of the given result directories,
failed and invalid ones included, with per-engine medians of accepted samples, and
with `--compare baseline candidate` a table of candidate-to-baseline ratios per
scenario (requests per second, server CPU and allocated bytes per request, client p50
and p99). A result directory carrying `INVALID.txt` or `ABORTED.txt` is listed and
contributes no accepted samples.

The reviewed watchdog excludes its ancestor processes themselves and expands only
owned descendants, so sibling jobs remain visible. Monitoring errors clean up the
owned live child before releasing the lock; failed cleanup retains its lock.
The summary uses the same complete, finite core-metric policy as the comparison
helper and displays missing client CPU as unavailable. Historical runs made with
the earlier ancestor-expansion bug need independent isolation evidence or a rerun.

## Profiling

Profile separately from comparisons. `--profile` aggregates runtime events in the
server (sampled allocation by every type seen, exceptions, contention); a
before/after diff of the per-type totals divided by completed requests attributes
an allocation change to its type even when it is a few dozen bytes per request. `--trace-tool` runs
dotnet-trace (`dotnet-sampled-thread-time`) against the server during the
measurement window and writes the `.nettrace` plus top-60 exclusive and inclusive
method reports next to each sample. The tool is not a project dependency; install a
pinned version yourself, for example
`dotnet tool install dotnet-trace --version 10.0.745401 --tool-path TestResults/tools/dotnet-trace`.
To see which EmbedIO callers account for a runtime frame, convert the trace and
attribute it:

```sh
TestResults/tools/dotnet-trace/dotnet-trace convert <sample>.nettrace --format Speedscope -o <sample>
python scripts/attribute_trace_frames.py <sample>.speedscope.json --target Monitor.Enter_Slowpath --caller "EmbedIO!"
```

## Method

- One fresh server process and one fresh client process per sample. On Windows the
  children inherit their CPU set at creation (so server GC heaps and the thread pool
  are sized for it); on Linux they start under `taskset`. Use disjoint physical cores.
  macOS cannot pin processes: `--server-cpus`/`--client-cpus` are refused there,
  `environment.json` records `cpuAffinity: none`, and server and client share every
  core, including Apple Silicon's separate performance levels (`processorTopology`).
- Every engine runs the same handler work: route parse, a cached static body or a
  server-validated upload, and asynchronous writes. Bodies use a non-periodic byte
  pattern; the client checks status, protocol version, framing, length and every
  body byte, and the server checks every uploaded byte.
- Sequence: start server, client warmup, client closes, idle, server snapshot after
  forced GC, synchronized measurement window, client closes, idle, second snapshot,
  graceful server stop. Engine order alternates by round.
- The first error in a sample aborts it and is recorded. Nothing is retried.
- Server metrics: CPU (total and user), allocated bytes, GC counts and pause time,
  lock contentions, thread-pool work items, peak working set/private bytes/threads,
  first-chance exceptions by type. Retained state: managed heap, working set,
  private bytes, handles and threads after idle and forced GC, compared with the
  post-warmup snapshot; and server-side TCP sockets still open after the client exits.
- Client metrics: completed requests, every latency in a log-linear histogram
  (p50/p90/p95/p99/p99.9/max, raw buckets kept), connections opened, server-initiated
  closes, client CPU and allocation.
- Machine busy CPU minus server and client CPU estimates background load per sample
  (Windows `GetSystemTimes`, Linux `/proc/stat`, macOS `host_statistics`).
- Server GC (concurrent) for every engine. Runtime, OS, CPU and topology, power scheme (Windows) or `pmset` state (macOS), `DOTNET_*`
  variables and hashes of the runner, both cores, Kestrel and QUIC assemblies go in
  `environment.json`.

HTTP/1.1 uses a raw socket client (optionally TLS with ALPN `http/1.1`, pipelining,
keep-alive or one request per connection). HTTP/2 and HTTP/3 use `SocketsHttpHandler`
with one handler per connection and a fixed number of concurrent streams. Cleartext
HTTP/2 is prior knowledge. Kestrel offers `h2` and `http/1.1` by ALPN on TLS, matching
the candidate. HTTP/3 uses QUIC-only listeners on both engines (`EmbedIOHttp3`).

## Limits

- Loopback on one host. Network latency, NIC offloads, packet loss and real clients
  are absent. These are closed-loop measurements (each connection waits for its
  response), so overload behaviour and coordinated omission are not characterized.
- The client can saturate its CPUs before a fast server does. Check
  `clientCpuUtilization`: when it is near 100% the result measures the client.
  The HTTP/2 and HTTP/3 client is `HttpClient`, which is heavier than the raw
  HTTP/1.1 client.
- A shared development machine has background load; it is reported per sample,
  not removed. Treat small differences as noise unless they repeat across rounds.
- Churn and per-connection request caps fill the TCP TIME_WAIT table. The orchestrator
  waits for it to drain below `--time-wait-limit` before each sample and records the
  wait; OS network limits are never changed.
- `--profile` adds runtime event listeners (allocation ticks, contention, exceptions)
  and perturbs timing. Profile runs are separate from comparison runs.
- QUIC connections are not visible to the socket-cleanup check.
- macOS: .NET's `GetActiveTcpConnections` omits TIME_WAIT there, so the TIME_WAIT gate and
  socket census parse `netstat -an -p tcp`. `HandleCount` and `PrivateMemorySize64` read 0, so
  `handles` is the open file-descriptor count (`/dev/fd`) and private bytes is reported as
  unavailable (null). macOS keeps TIME_WAIT for 30 s (`net.inet.tcp.msl` 15000) and has 16,384
  ephemeral ports, so HTTP/1.1 churn at tens of thousands of connections per second can reuse a
  4-tuple the server still holds in TIME_WAIT; compare every engine before reading churn failures.

## Comparing two modern engine revisions

The default baseline filter deliberately excludes HTTP/2 and HTTP/3 because the
pinned main baseline does not implement them. When the baseline DLL is a recorded
modern-engine revision that supports the selected protocols, pass
`--modern-baseline` explicitly. The environment record captures this choice;
response validation and failure handling remain unchanged. Do not use this flag
with the historical main baseline. Build identical runner copies and swap only
the core DLL, retaining revision and binary hashes for both.

## Endurance mode

Use `--listener-profile tcp-only` for ordinary TCP-only endurance. Both endpoints
then use the managed EmbedIO listener: cleartext HTTP/1.1 and HTTP/2 prior knowledge,
and TLS HTTP/1.1 and HTTP/2 by ALPN. This profile requires `--engine embedio`,
defaults client and health transports to `h1,h1tls,h2c,h2tls`, and rejects HTTP/3
before starting a host. A narrower `--transports` list is allowed. Client filtering
alone does not change the default `combined` server, which still enables HTTP/3.

The TCP-only profile skips the MsQuic module check and native process-heap
walking; it retains ordinary GC/process/socket metrics and forced managed-GC
snapshots. The server never asks System.Net.Quic whether QUIC is supported, because
that loads MsQuic, which EmbedIO.Quic does not use; only the clients load it, for
.NET's HttpClient. Profile and listener mode are recorded explicitly. Endurance records
runtime/GC facts and explicit options without enumerating environment values.
`endurance-profile-check` validates accepted/rejected configuration without starting
a host; it also runs as part of `endurance-verdict-check`.

`endurance` keeps one server process alive for a whole plan, so retained state
accumulates as it would in a long-running application. The server hosts two
listeners with the comparison handler plus a WebSocket echo endpoint at `/ws`:
a cleartext `EmbedIO` listener (HTTP/1.1 and HTTP/2 prior knowledge) and an
`EmbedIOCombined` TLS listener (HTTP/1.1 and HTTP/2 by ALPN, HTTP/3 over QUIC).
Every step uses a fresh client process.

Run `dotnet <runner>/EmbedIO.LoadBenchmark.dll endurance-verdict-check` to check
the verdict boundary without network timing. It covers clean, failed and malformed
drain reports, retained failures and validation errors during planned disruption.
`native-census-check` checks that the native census in quiesced snapshots sees a known
change: on Windows the handle census must count 64 created events under `Event` and
agree with the process handle count, and on Linux malloc's allocated and mapped bytes
and resident anonymous memory must rise and fall with 1,024 one-KiB blocks and one
written 64 MiB block. CI runs the Linux check.

```sh
dotnet <runner>/EmbedIO.LoadBenchmark.dll endurance --output TestResults/endurance/<label>   --plan idle:60,load:steady:300,load:faults:240,load:ws:120,drain:mixed:60:3,restart:5,load:mixed:420,idle:60   --settle 20 --server-cpus 0-7 --client-cpus 8-19 --revision <sha>
python scripts/analyze_endurance.py TestResults/endurance/<label>
```

On macOS, pass `--request-body-read-timeout 30`. The `faults` mix ends partial uploads
with an RST, and macOS sometimes drops an RST that arrives while the server's receive
window is closed; a plain Python server that reads slowly loses some too, so this is the
kernel, not EmbedIO. The server then keeps an ESTABLISHED connection whose client is gone,
and the request stays in flight. With no read deadline only the default 240 B/s minimum
body rate ends it, after about the bytes already received divided by 240 seconds (over an
hour for most of a 1 MiB upload), so the step fails to quiesce within two minutes. The
option sets `RequestBodyReadTimeout` on both EmbedIO listeners and is recorded in
`environment.json`; it is off by default, so plans on other platforms are unchanged.

Plan steps, comma separated:

- `idle:<s>`: wait, then take a quiesced snapshot (the baseline).
- `load:<mix>:<s>`: run a client mix for `<s>` seconds.
- `drain:<mix>:<s>:<n>`: `n` cycles of load; at the midpoint the server drains both
  listeners with `WebServer.DrainAsync`, is disposed and is replaced by new instances
  on the same ports while the client keeps running.
- `restart:<n>`: `n` idle drain-and-replace cycles.

After every load step the client stops, the orchestrator waits for zero in-flight
requests and WebSockets (at most two minutes, otherwise the step fails), waits
`--settle` seconds, takes a forced, compacting GC snapshot and then runs a 10-second
validated health check on every protocol plus WebSocket echo. Retention is judged
only from these quiesced snapshots; `analyze_endurance.py` compares early and late
values and fits a least-squares slope after excluding warm-up snapshots. It also lists
the snapshots taken at the same point of every cycle (the most frequent label, or
`--series-label`) with the change from each to the next, which shows whether a series
levels off, and compares the first and last of them by handle type and by default-heap
block size where the snapshots record those.

Mixes (`--rate-scale` multiplies paced rates):

| Mix | Workloads |
| --- | --- |
| `steady` | Paced small GETs, 1 MiB responses, 1 MiB uploads and 1 MiB flushed streams on HTTP/1.1, HTTP/1.1+TLS, h2c, h2 and HTTP/3; rate-limited new-connection churn on HTTP/1.1, HTTP/1.1+TLS, h2 and HTTP/3. |
| `faults` | Client cancellations (HTTP/1.1 connection reset mid-body; HTTP/2 and HTTP/3 stream cancellation before and during the body, each followed by a validated request on the same connection), abrupt disconnects (resets mid-head, mid-upload and mid-TLS-handshake, a partial h2c HEADERS frame, abandoned HTTP/2 and HTTP/3 connections with streams in flight), slow readers (1 MiB at about 160 KiB/s) and slow writers (256 KiB at about 160 KiB/s), byte-at-a-time request heads, and idle connections held until the server's 30 s initial or 15 s keep-alive timeout. |
| `ws` | WebSocket connect/echo/close churn and long-lived echo connections over HTTP/1.1, HTTP/1.1+TLS, h2c and h2 (RFC 8441), with validated text (including multibyte UTF-8) and binary messages up to 64 KiB. |
| `mixed` | All of the above concurrently. |
| `saturate` | Unpaced small GETs on every protocol, for short stress segments. |
| `health` | One closed-loop small GET worker per protocol plus WebSocket churn over HTTP/1.1 and h2. |

Validation is the same as in comparisons: status, protocol version, framing, length
and every body byte; uploads are checked by the server; WebSocket echoes are compared
byte for byte with their message type. Failures are counted and the first 20 per
workload are kept with timestamps; the worker then opens a new connection, so one
failure does not hide later ones. Nothing is retried into a pass: any unexpected
failure fails the step. Client-initiated cancellations and resets are counted as
`clientAborts` with outcome categories, not as successes. Failures inside the
drain/restart window (and 3 s after it) are counted separately as `disrupted`;
validation failures are never excused.

Attribution options: `--transports h1,h1tls,h2c,h2tls,h3` and `--kinds small,large,...`
restrict every load step to those workloads (health checks keep the transport filter).
`--engine kestrel` hosts ASP.NET Core Kestrel from the shared framework instead, with
the same handler: cleartext HTTP/1.1 on the plain port and HTTP/1.1, HTTP/2 and HTTP/3
on the TLS port, without h2c or WebSockets, so plans for it must filter those out. It
separates engine retention from runtime, TLS and MsQuic retention. Set
`EMBEDIO_BENCH_EXCEPTION_DETAIL=1` for diagnostic runs to key first-chance exceptions by
message and log the first stack per key to `server.stderr.log` (written at exit).
`scripts/summarize_minidump_memory.py <dump>` summarizes committed memory in a full
dump (for example from `dotnet-dump collect --type Full`) by region type and
allocation, without a debugger.

Evidence in the output directory:

- `environment.json`: revision, runner/core/msquic hashes, runtime, CPU, memory, plan.
- `server-samples.jsonl`: every 5 s (`--sample-seconds`), non-forcing: CPU, allocation,
  GC counts/pause/generation sizes/fragmentation, managed heap, working set, private
  bytes, handles, threads, thread-pool threads/pending/completed, lock contention,
  active timers, in-flight requests, active WebSockets, first-chance exceptions by type,
  server TCP connections by state, available physical memory and machine busy CPU.
  `staleGenerationRequests` counts requests or WebSocket messages handled by a server
  instance that had already been drained and replaced; any nonzero value fails the step.
  .NET reports zero handles and private bytes on macOS, so there `handles` counts open
  file descriptors and `privateBytes` is the physical footprint from `proc_pid_rusage`
  (Activity Monitor's Memory column). Available physical memory is recorded on Windows
  only (-1 elsewhere).
- `steps.jsonl`: per step the samples at load start and stop, the drain/restart
  results, the quiesced snapshot, quiesce time, the machine TCP census and health.
  On Windows each quiesced snapshot also has `nativeHeaps`: allocated, committed and
  reserved bytes of every Win32 heap (`HeapSummary`) and, in `defaultHeapBlocks`, live
  block counts by size in the default process heap (`HeapWalk`, sizes only, the 80
  largest totals). A size whose count grows with requests fingerprints retained native
  allocations without a dump. The walk runs inside a GC no-GC region because it holds
  the heap lock; it is skipped (null) when the region cannot be entered. `handleTypes`
  counts the open handles by object type from a snapshot of the handle table
  (`PssCaptureSnapshot`; handles it cannot duplicate, such as ETW registrations, are
  named with `NtQueryObject`). Only type names are read, never object names; the total
  runs a few handles below `handles`, which also counts handles opened around the
  snapshot.
  On Linux each quiesced snapshot has `linuxMemory`: `status` from `/proc/self/status`
  (`VmRSS`, `RssAnon`, `RssFile`, `RssShmem`, `VmData`, `VmSwap`), `smapsRollup` from
  `/proc/self/smaps_rollup` (`Rss`, `Pss`, `Anonymous`, `Private_Clean`,
  `Private_Dirty`, `Swap`), all in bytes, and `malloc` from glibc's `mallinfo2`, which
  sums every arena: `arenaBytes` from the system, split into `allocatedBytes` and
  `freeBytes`, plus `mmappedBytes` for blocks mapped separately. Growth in allocated
  bytes is live native state; growth in free bytes is memory the allocator keeps.
  `privateBytes` there is `VmData`, which also counts mappings that are not resident.
  `malloc` is null with musl or a glibc older than 2.33.
- The server's `READY` record (first line of `steps.jsonl`) names the msquic.dll actually
  loaded, with its version and SHA-256, which differs from `environment.json`'s runtime
  copy when `System.Net.Quic.AppLocalMsQuic` selects an application-local build.
- `clients/<step>-<mix>.report.json` and `.intervals.jsonl`: per-workload counts,
  outcomes, first errors and full latency histograms, plus per-interval p50/p99/max.

The endurance client paces load so throughput stays constant across hours (missed
slots are skipped rather than replayed), so its latency excludes queueing delay that
a fixed-schedule open-loop generator would attribute to the server. QUIC connections
are not visible in the TCP census. Absolute rates depend on the host and must not be
compared across machines; compare revisions only on the same host with the same plan.

The `quic-send-buffer-probe` command, which isolated the System.Net.Quic send-buffer
leak of issue #278 without HTTP, was removed with the earlier HTTP/3 providers; see
[the endurance record](../../docs/project/http-engine-endurance.md) for its results.

## Disposable quiet-host control

`http3-quiet-host.yml` runs the issue #283 control on separate Windows and Linux
GitHub runners. `scripts/http3_quiet_campaign.py` is restricted to those disposable
runners because its contention arm spins one thread per logical processor. It must
not be used for an all-core burn on a development workstation.

Both engines receive identical paced churn and warm small/1 MiB requests, with
alternating engine order and two rounds. The runtime handshake deadline is unchanged.
`--max-baseline-busy-cpus 0.5` rejects a busy 15-second baseline before starting any
load. The Linux quiet arm additionally requires mean background activity at most 0.5
busy CPUs in every phase. Windows retains the same first-arm requests as an ambient
repeat, with no quiet claim. Both contention arms must measure at least 80% host CPU.
Every attempted request, cap omission, latency histogram, CPU timeline, error and
server exit is checked. Missing data or a failed request fails the campaign rather
than being retried or discarded. Loaded Linux MsQuic and runtime/core hashes are
recorded. Original artifacts remain available even if acceptance fails.

These are bounded controls, not an exhaustive endurance or public performance claim.

William approved Linux as the qualified quiet reference and Windows as an explicitly
ambient repeat, retaining every request and contention case. Windows verdicts state
that quiet acceptance is not established; its rejected quiet attempts remain preserved.
The Windows per-process census reads CPU counters only and reports inaccessible
processes. Linux retains its passive baseline ceiling; Windows records ambient baseline
activity without a quiet precondition. See the
[control report](../../docs/project/http3-quiet-host-validation.md) for scope and evidence.

## Current teardown cost controls (#282)

`run --request-rate 32` reserves paced request slots across client workers. The opt-in
rate is limited to 64 requests/second; paced churn can therefore use longer samples
without the unpaced TIME_WAIT safety cap. A missing paced request fails the campaign.
Rate zero retains the existing closed-loop behavior and churn caps.

`--connection-census` observes EmbedIO.Server's logical connection meter and
Kestrel's original connection-delegate tasks with an independent meter witness.
The census is used only in diagnostic profiles; timing/allocation comparisons
leave it off. It counts logical server lifetimes, not native QUIC allocations or heap release.
The `teardown-accounting-check` command verifies both meters, isolation from other
meters, unsupported deltas, paced cancellation and expired slots.

When detailed exception profiling is explicitly enabled, typed observable-site
counts accompany type/message counts. Capture is bounded at 512 groups and 16 KiB
per key, with overflow recorded and rejected by required diagnostic profiles.
Representative trace truncation is explicit and retains full-content hashes.
Stack presence alone does not identify every asynchronous rethrow's original owner.

`scripts/teardown_cost_campaign.py` runs only on disposable Linux GitHub runners:
seven protocol/lifetime scenarios, two alternating profiled rounds, then three
unprofiled A/A and Kestrel comparison rounds. A/A copies have byte-identical runner
and core assemblies. Every arm uses 32 requests/second, one connection/stream,
quiet baseline and phase background ceilings of 0.5 busy CPUs. It preserves all
failed runs and does not retry them. Separate EmbedIO intentional-abandonment/recovery
cases keep the existing recovery and cleanup gates. Kestrel fault controls remain
explicitly diagnostic, with unsuccessful results retained separately under #361.
Profiles are not timing claims.
No production API, defaults, error propagation or dependency changes are involved.

The long cost campaign is deferred while its PR is a draft. Marking the PR ready
for review schedules it on that source. A skipped draft campaign is not acceptance;
its successful current-source run and all ordinary checks are required before merge.

The cost and identical-binary A/A arms use `--disable-exception-census`, which does
not register the inherited lightweight first-chance counter. The actual child
observer mode is recorded and checked. Those arms report exception counts as
unavailable, not zero. Detailed profiles and deliberate-fault arms retain counters;
ordinary harness defaults are unchanged. The earlier counter-enabled campaign is
preparation evidence and cannot establish observer-free production cost.


### Revised diagnostic qualification

Detailed `throw-site-v2` groups use type/message, the notification throw frame and
nearest owned frame, with atomic typed totals and bounded representative stacks.
Representatives retain full-content hashes, original lengths and explicit truncation;
missing owned frames remain unattributed. Group overflow remains a failure.
Kestrel profiles return the original connection-delegate task, retain its meter as
an independent witness, and never count weak-target collection as completion.
Logical lifetime accounting does not prove native heap release.

Normal profile/A/A/cost arms for both engines and every EmbedIO recovery case are
required. William approved Kestrel deliberate-fault cases as diagnostic comparison
controls: their actual failures remain `failed-control` limitations in `verdict.json`
and logs, not passing results. See issue #361 for unfinished HTTP/3 control tasks.
This scope does not exempt normal Kestrel traffic, alter ordinary cost workloads,
change EmbedIO recovery gates, or retroactively accept earlier failed campaigns.

`recovery --control-hard-cleanup` is a separate diagnostic Kestrel experiment that
strongly retains active contexts and records actual public Abort calls. It is off
by default, is not enabled in ordinary campaign comparisons, and did not qualify
as a shutdown fix in the captured HTTP/3 probe. Strong references can themselves
alter lifetime observability. Retain the actual pending-task and exit evidence.


Campaign progress output reports the active phase, elapsed time, sample-file count
and newest file every 30 seconds. File presence is explicitly not validation or
acceptance. This reporting runs in the campaign parent; cost server children still
have their exception observers disabled.

A 52-minute whole-campaign ceiling reserves time for artifact retention within the
existing 60-minute job. Individual arm/fault ceilings remain 1,500/180 seconds and
are capped by the remaining overall budget. Timeout cleanup kills only the owned
process group and waits at most ten seconds for exit. Unattempted phases/controls,
overall expiry and incomplete required results are recorded as failures, never
partial success. All original requests, rounds, quiet ceilings and acceptance
checks remain required; the campaign does not retry unsuccessful measurements.


Only the verified unfinished Kestrel lifetime observation qualifies for the
separate control limitation after clean driver/healthy traffic, complete site
capture and valid counter/witness evidence. Unknown failures, crashes, timeouts,
invalid identities or malformed evidence remain blocking for either engine.
The campaign enforces the actual child engine, disabled experimental hard cleanup,
loaded core hash and byte-identical copied A/A runner, rather than trusting labels.
