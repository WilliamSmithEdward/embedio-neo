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
`EmbedIO.dll` in the baseline copy. Builds use `ContinuousIntegrationBuild` so
hashes do not depend on the build directory. The core's informational version
embeds the current commit, so its hash changes with every commit even when the
source does not. `runners.json` records revisions, uncommitted paths and SHA-256
hashes.

`run` options: `--scenarios all|<prefix>,...`, `--engines candidate,baseline,kestrel`,
`--baseline-all-protocols` (alias for `--modern-baseline`; also run the baseline on HTTP/2 and HTTP/3 scenarios, for a
baseline core that serves them, such as an earlier engine revision passed to the prepare
script with `--baseline <revision>`),
`--rounds 3`, `--warmup 5`, `--duration 15`, `--idle 2`, `--server-cpus`/`--client-cpus`
(for example `0-7`), `--time-wait-limit 4000`, `--profile`, `--trace-tool <dotnet-trace>`.
The output directory must be new or empty, so earlier evidence is never overwritten.
Set `EMBEDIO_BENCH_EXCEPTION_DETAIL=1` to key the server's first-chance exception
counts by message and write the first stack trace per key to its stderr log; leave it
unset for comparison runs.

Scenarios ending in `-close100` are controls: the client closes every connection
after 100 requests, matching the managed listener's per-connection cap, so all
engines pay the same reconnect cost.

Scenarios named `-inspect-` request `/inspect?id=42&name=neo%20bench&tag=a&tag=b`
with User-Agent, Accept, Cookie, Referer and X-Request-Id headers. The handler
reads the query (including the repeated `tag`), those headers, both cookies, the
referrer, body framing and the endpoints, and answers 400 naming the first
mismatched property. Body framing is read but HasEntityBody is not required to be
false over HTTP/3, where a GET without content-length reports an unknown length. Churn scenarios are capped (5 s for HTTP/1.1,
1.5 s for HTTP/2) to stay within the host's TIME_WAIT and ephemeral-port capacity.

## Profiling

Profile separately from comparisons. `--profile` aggregates runtime events in the
server (sampled allocation by type, exceptions, contention). `--trace-tool` runs
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
- Machine busy CPU minus server and client CPU estimates background load per sample.
- Server GC (concurrent) for every engine. Runtime, OS, CPU, power scheme, `DOTNET_*`
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

## Comparing two modern engine revisions

The default baseline filter deliberately excludes HTTP/2 and HTTP/3 because the
pinned main baseline does not implement them. When the baseline DLL is a recorded
modern-engine revision that supports the selected protocols, pass
`--modern-baseline` explicitly. The environment record captures this choice;
response validation and failure handling remain unchanged. Do not use this flag
with the historical main baseline. Build identical runner copies and swap only
the core DLL, retaining revision and binary hashes for both.
