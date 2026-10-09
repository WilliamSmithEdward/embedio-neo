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
hashes do not depend on the build directory. `runners.json` records revisions,
uncommitted paths and SHA-256 hashes.

`run` options: `--scenarios all|<prefix>,...`, `--engines candidate,baseline,kestrel`,
`--rounds 3`, `--warmup 5`, `--duration 15`, `--idle 2`, `--server-cpus`/`--client-cpus`
(for example `0-7`), `--time-wait-limit 4000`, `--profile`. The output directory must
be new or empty, so earlier evidence is never overwritten.

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
