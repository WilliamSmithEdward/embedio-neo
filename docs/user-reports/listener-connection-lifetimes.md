# Listener connection lifetimes and end-to-end benchmarks

This work follows the HTTP listener audit at main commit `2a5d5ed`. Public APIs,
supported frameworks, defaults, routing rules and TLS/certificate selection are
preserved. It changes cleanup after terminal closure and adds test-only workloads.

## Confirmed cleanup gap

Ten initial HTTP/HTTPS cases retained completed request contexts after response
closure, forced closure, listener Stop/Dispose and peer disconnect. All ten failed:
the socket closed, but the associated transport wrapper and request timer remained
undisposed. A retained context therefore also retained connection-owned buffers
and, for TLS, stream resources. This demonstrates delayed cleanup with retained
references; it does not establish a permanent leak when objects can be collected.

Terminal closure now detaches the socket once, unregisters the connection, disposes
its timer and transport, and clears connection-owned receive/parser buffers.
Ordinary keep-alive preserves the live transport. Forced closure is recorded before
ResponseStream.Dispose calls back into response closure, so that callback cannot
briefly restart the reader. Connection synchronization prevents a closing reader
from reinitializing buffers after disposal. The request reader runs outside that
lock to preserve listener/connection lock ordering.

The terminal helper owns unbinding and cleanup instead of repeating that work in
each read-error/EOF/timeout branch. Timer shutdown tolerates disposal racing a read
completion. Request metadata remains accessible. Timeout values and when they are
armed remain unchanged; no stricter header deadline is introduced.

## End-to-end workload

The dependency-free, unpackaged EmbedIO.Performance project hosts the real managed
listener on loopback and drives HTTP/1.1 requests with a client in the same process.
It reports requests/second, p50/p95/p99 latency, process allocation bytes/request,
GC collections, managed memory after shutdown/collection, private bytes and working
set. These allocation and memory measurements include both client and server;
they are not server-only costs. TLS uses the existing test certificate helper and
pins the generated leaf while retaining hostname validation.

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --listener-http
```

Defaults: HTTP and HTTPS, keep-alive and close-per-request, concurrency 1 and 16,
1,024-byte responses, eight warmup requests per worker, and three measured rounds
of 20 requests per worker. Every response is checked for status and exact bytes.
For churn, the server explicitly sets Response.KeepAlive=false as well as the
client sending Connection: close; observed connections validate the actual policy.
The existing 100-reuse limit remains in effect on longer keep-alive runs.

Options --requests, --rounds, --concurrency and --payload-bytes select workloads.
Use --retain-connections to intentionally hold diagnostic references and report
transport/timer disposal after shutdown. Its reflection and tracking overhead is
part of that workload; leave it off when measuring ordinary traffic.

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --retain-connections
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-listener-http
```

The verifier runs a bounded concurrency-four workload and checks every retained
transport and timer is disposed. Desktop CI runs it alongside existing allocation
budgets; timing is informational, with no unstable throughput or latency gate.

## Observed results and limits

The same runner was built against exact baseline source and the candidate. A
Windows x64/.NET 10.0.11 retained-reference run used concurrency 16, five measured
rounds of 100 requests per worker and 1,024-byte responses. Each churn case held
8,128 connections, including 128 warmup connections. All candidate transports and
timers were disposed; the baseline retained 8,128 undisposed wrappers and timers.

| Churn workload | Baseline managed bytes after GC | Candidate managed bytes after GC |
| --- | ---: | ---: |
| HTTP | 113,104,368 | 43,696,360 |
| HTTPS | 118,818,672 | 50,229,888 |

These are whole-process observations with deliberately retained references, not
general production-memory ceilings. Private bytes and working set can stay high
because allocators and TLS/array pools retain capacity.

| Workload, concurrency 16 | Baseline median requests/s | Candidate median requests/s | Baseline median p95 ms | Candidate median p95 ms |
| --- | ---: | ---: | ---: | ---: |
| HTTP keep-alive | 123,508 | 119,854 | 0.158 | 0.202 |
| HTTP churn | 14,908 | 14,557 | 1.104 | 1.161 |
| HTTPS keep-alive | 94,484 | 91,380 | 0.306 | 0.345 |
| HTTPS churn | 3,829 | 3,864 | 4.482 | 4.963 |

Timing varied across the short and longer comparisons; this change claims prompt
resource release and lower retained managed memory, not faster HTTP throughput.
A subsequent high-churn baseline repeat failed with Windows socket error 10055
(insufficient buffer space/queue capacity) after many short-lived connections.
This is a workload/environment limit, not evidence that the listener caused a
permanent leak. Failed requests abort the runner rather than being retried or
reported as successes. Keep default runs small, allow TCP TIME_WAIT capacity to
recover between heavy churn runs, and use an otherwise idle host for comparisons.
No system TCP limits, firewall rules or listener defaults were changed.

## Validation

Twenty-two new cases cover retained contexts, response/forced/peer closure,
Stop/Dispose races, idle HTTP, incomplete TLS, 40 sequential POST requests on one
transport, and WS/WSS echo plus graceful close or shutdown. All 22 passed. The
combined focused parser/transport/lifetime suite passed 120 cases. The full Windows
suite passed 967 cases with two existing skips (969 total); both library targets
built with analyzers. Bounded HTTP/HTTPS cleanup verification and existing
hot-path, queue and cold-start allocation budgets passed.

Baseline/candidate runner sources, JSON, comparison scripts, build logs and TRX
reports are under ignored TestResults/listener-lifetimes. Required cross-platform,
native HTTPS, security and malware gates remain mandatory before merge. No release
or version bump is included.
