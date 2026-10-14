# Direct request dispatch from TCP connections

The managed TCP engine hands each admitted HTTP/1.1 and HTTP/2 request straight
from its connection to the server's handler. This record explains the path it
replaces, what stays the same on the lifecycle, how the change was measured and
what the measurements show. It belongs to the extreme-performance round of
program #181 (PR #182).

## The path it replaces

Before this change every admitted request took three hops that had nothing to
do with the request:

1. The connection actor registered the context with its listener, which placed
   it in `ListenerAcceptEpoch`: a `ConcurrentDictionary` keyed by context id, a
   `ConcurrentQueue` for order and a `SemaphoreSlim` to wake the accept loop.
2. `WebServer.ProcessRequestsAsync` woke from `GetContextAsync`, claimed the
   context (dictionary removal plus dequeue) and queued the handler with
   `Task.Run`.
3. The handler ran on that work item. When it completed the response, the
   actor's response-end signal queued the actor's continuation on yet another
   work item.

Perf Hunt's EventPipe profile of `h1-plain-small-c64` on the engine head
52513d46 counted 3.65 thread-pool work items per request against Kestrel's
2.07, with the actor's `_connectionSync` spin concentrated where the response
end woke it.

## The direct path

The server installs one dispatcher on its listener before it serves
(`IDispatchingHttpListener`, internal). `TcpListenerCoordinator.Register`
keeps every admission and drain check it had; with a dispatcher installed it
hands the context to the handler and returns the handler's `Task` instead of
queueing the context. The publication borrow and the listener gate are both
released before the handler runs, so `SealAdmission` and `Stop` never wait for
application code.

- HTTP/1.1: the handler starts on the connection's own thread, inside the
  actor's admission step, and runs up to its first suspension there. The actor
  then waits for the response to end, as before. After each served request the
  actor yields once to the thread pool's global queue (`InlineRequestBudget`)
  before it parses the next head. The first measured revision did not yield:
  a socket completion resumes the actor on the completing thread, whose local
  queue is last-in-first-out, so with the handler inline the newest
  completions were served first and the oldest starved. At 64 connections p50
  fell sixfold while p99 rose tenfold, and at 256 connections p99 reached
  50 ms against the base's 9.6 ms. Kestrel dispatches every receive completion
  to the pool for the same reason.
- Combined mode (TCP plus QUIC) forwards the dispatcher to its TCP listener,
  so its HTTP/1.1 and HTTP/2 requests take the same path; QUIC requests keep
  the pull loop.
- HTTP/2: `RegisterMultiplexedContext` takes the same `Register`. Each exchange
  already runs on its own work item (`Http2Dispatcher.StartApplication`), so
  the handler never runs on the connection's frame loop and concurrent streams
  stay concurrent.
- `WebServer.DispatchAsync` prepares the context (server token, route) for both
  the direct path and the remaining pull loop, and owns the handler's task: a
  fault the handler leaves uncaught is observed and logged, and a failure
  before the handler starts becomes a faulted task that closes the HTTP/1.1
  connection or resets the HTTP/2 stream.
- `WebServer.ProcessRequestsAsync` keeps its pull loop for listeners without a
  dispatcher, today HTTP/3.

`IHttpListener.GetContextAsync` is public and unchanged. Standalone
`HttpListener` users and the listener tests drive the queue through it; the
queue is fed only when no dispatcher is installed. The connection has one code
path, and the listener holds the one decision.

## Lifecycle semantics that stay

- A handler may keep running after it completes its response, and the
  connection's next keep-alive request does not wait for it. The actor waits
  for the response end, never for the handler's task. The first revision of
  this change awaited the handler and broke `Issue570_ResponseOwnership`
  (late `Close`/`Dispose` of a completed response) and the raw-body lifecycle
  audit on every desktop OS; that design was withdrawn.
- Graceful drain releases a connection whose response has ended whether or not
  its handler returned (`HttpConnection.ReleaseIdleOwner`, unchanged), and
  still waits for a response that is in progress. Stop and Dispose behave as
  before.
- Admission after `SealAdmission` and during a drain is refused with the same
  `HttpListenerException` (995) on both paths.
- One deliberate difference, recorded as a fix: a request whose dispatch fails
  before the handler starts was previously never answered, and its connection
  stayed open without a deadline until the client gave up. It is now logged and
  its connection closed, or its stream reset.

`DirectDispatchTest` pins these: keep-alive ordering with the accept queue
asserted empty, 200-deep pipelining on one raw connection, a throwing handler,
a handler outliving its response while the next request is served and a drain
completes, a handler failing after its response, a handler waiting on
cancellation when the server stops, a drain that waits for an in-flight
handler and refuses new connections, an HTTP/2 slow stream that does not delay
a later stream, two synchronously blocking handlers that do not stall other
connections, and the queue-versus-dispatcher registration contract of a
standalone listener.

## Measurement method

The separate-process harness in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md)
compares three engines in identical runner copies that differ only in
`EmbedIO.dll`: the candidate (this branch), the base it was built on, and
Kestrel from the installed shared framework. Server processes are pinned to
logical CPUs 0-3 and the client to 4-7, as in Perf Hunt's profiles. Runs are
taken under the shared workstation lock with both heavy-work slots held.
Idle keep-alive memory uses the harness's `idle` mode, added with this change:
one `HttpClient` per connection, one request each, then a hold while the server
reports collected snapshots.

## Results

Fixed build (budget 1), candidate 6f729146 (core SHA-256
5732b1545c65f8f93fc59fdb23094e4dabe86b9591a4ce277e66ada7f4689de7) against the
base c9eb1d23 (core 678d5c3fbf3ff8c95b04c78b3ff64760f1c5986a949c86749f3e9ecb0fc9c607)
and Kestrel from the shared framework, .NET 10.0.12 on SDK 10.0.401, Windows 11
Ryzen 7 9800X3D, server CPUs 0-3, client CPUs 4-7, 2 rounds x 8 s per orientation,
3 s warmup, 2 s idle. Every scenario ran twice, once per runner-role orientation,
and the table pools both; the per-orientation tables and the process census taken
at the start and end of every chunk are retained beside the raw samples
(`TestResults/perf-direct-dispatch/load-final` in the branch worktree,
`report.md`). Medians over valid samples. Work items are the server's completed
thread-pool work items divided by completed requests; contentions are lock
contention events per 100,000 requests.

| Scenario | Engine | n | req/s | p50 ms | p99 ms | p99.9 ms | max ms | CPU us/req | work items/req | B/req | gen0/100k | gen1/100k | gen2/100k | GC pause ms/s | contentions/100k | peak WS MiB | WS after MiB | retained WS KiB | peak threads | peak pool threads | bg busy CPUs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| h1-plain-chunk13-c64 | candidate | 4 | 132,204 | 0.461 | 0.877 | 3.507 | 32.45 | 29.8 | 1.98 | 6,547 | 56.2 | 0.14 | 0.09 | 22.5 | 0.0 | 78.9 | 63.8 | -11516 | 27 | 8 | 0.58 |
| h1-plain-chunk13-c64 | baseline | 4 | 127,652 | 0.477 | 1.741 | 4.941 | 19.76 | 30.5 | 3.81 | 7,359 | 59.0 | 0.29 | 0.05 | 21.6 | 10.1 | 80.1 | 62.8 | -10582 | 26 | 7 | 0.93 |
| h1-plain-chunk13-c64 | kestrel | 4 | 254,484 | 0.243 | 0.451 | 1.229 | 18.76 | 15.4 | 2.07 | 192 | 7.0 | 0.00 | 0.00 | 4.0 | 0.2 | 63.9 | 55.1 | -1214 | 24 | 7 | 0.59 |
| h1-plain-churn-c64 | candidate | 3 | 19,289 | 1.536 | 3.789 | 13.517 | 21.41 | 112.6 | 2.90 | 17,261 | 181.9 | 62.20 | 0.00 | 12.7 | 280.8 | 78.7 | 63.2 | -12816 | 25 | 8 | 1.11 |
| h1-plain-churn-c64 | baseline | 3 | 19,487 | 1.613 | 3.149 | 15.974 | 24.43 | 124.8 | 5.64 | 18,389 | 313.2 | 71.64 | 0.00 | 17.6 | 211.6 | 78.1 | 56.7 | -13864 | 24 | 8 | 0.81 |
| h1-plain-churn-c64 | kestrel | 3 | 20,050 | 2.765 | 3.635 | 6.656 | 17.65 | 127.6 | 5.48 | 9,053 | 264.2 | 2.99 | 0.00 | 12.9 | 2.0 | 67.2 | 56.2 | -1412 | 25 | 8 | 0.94 |
| h1-plain-pipe16-c16 | candidate | 4 | 255,010 | 0.496 | 1.235 | 9.421 | 30.36 | 15.4 | 1.06 | 6,204 | 61.1 | 0.07 | 0.07 | 26.5 | 0.1 | 74.2 | 60.8 | -5206 | 27 | 8 | 0.66 |
| h1-plain-pipe16-c16 | baseline | 4 | 252,012 | 0.432 | 2.035 | 6.912 | 22.44 | 15.7 | 2.67 | 6,809 | 66.4 | 0.10 | 0.10 | 28.7 | 6.7 | 75.0 | 61.3 | -4854 | 26 | 7 | 0.76 |
| h1-plain-pipe16-c16 | kestrel | 4 | 2,400,324 | 0.101 | 0.188 | 0.470 | 19.10 | 1.6 | 0.14 | 32 | 1.2 | 0.00 | 0.00 | 4.8 | 0.1 | 58.3 | 54.3 | -2554 | 24 | 7 | 0.67 |
| h1-plain-small-c256 | candidate | 4 | 203,763 | 1.190 | 2.086 | 3.456 | 42.87 | 19.2 | 1.98 | 6,241 | 22.1 | 10.58 | 0.12 | 25.1 | 0.3 | 105.2 | 76.8 | 476 | 28 | 8 | 0.86 |
| h1-plain-small-c256 | baseline | 4 | 187,903 | 1.184 | 9.011 | 39.731 | 75.67 | 20.7 | 3.66 | 6,958 | 28.9 | 9.25 | 0.10 | 27.1 | 25.2 | 107.5 | 81.1 | -14774 | 26 | 8 | 1.03 |
| h1-plain-small-c256 | kestrel | 4 | 254,977 | 0.966 | 1.766 | 4.736 | 18.14 | 15.4 | 2.02 | 33 | 0.4 | 0.05 | 0.00 | 0.8 | 0.1 | 68.7 | 62.0 | 4880 | 24 | 7 | 0.68 |
| h1-plain-small-c64 | candidate | 4 | 205,463 | 0.288 | 0.621 | 1.830 | 26.36 | 19.0 | 1.97 | 6,243 | 51.9 | 0.06 | 0.00 | 28.1 | 0.1 | 77.6 | 63.3 | -12696 | 27 | 8 | 0.87 |
| h1-plain-small-c64 | baseline | 4 | 198,608 | 0.302 | 1.203 | 3.008 | 16.12 | 19.6 | 3.71 | 6,997 | 52.7 | 0.13 | 0.06 | 27.3 | 9.9 | 81.2 | 65.6 | -10370 | 26 | 7 | 0.91 |
| h1-plain-small-c64 | kestrel | 4 | 261,089 | 0.240 | 0.422 | 1.139 | 16.53 | 15.0 | 2.06 | 32 | 0.8 | 0.00 | 0.00 | 0.7 | 0.2 | 64.3 | 59.3 | 3504 | 24 | 7 | 0.80 |
| h1-plain-small-c64-close100 | candidate | 4 | 197,147 | 0.291 | 0.806 | 2.649 | 26.36 | 19.6 | 1.98 | 6,332 | 35.3 | 11.09 | 2.86 | 29.1 | 3.2 | 99.0 | 71.4 | -12748 | 26 | 8 | 1.07 |
| h1-plain-small-c64-close100 | baseline | 4 | 185,307 | 0.296 | 1.696 | 6.246 | 25.71 | 21.1 | 3.75 | 7,120 | 42.9 | 13.46 | 2.58 | 31.8 | 9.7 | 89.6 | 72.2 | -8686 | 28 | 9 | 1.47 |
| h1-plain-small-c64-close100 | kestrel | 4 | 244,363 | 0.243 | 0.470 | 1.510 | 20.90 | 15.8 | 2.17 | 126 | 1.2 | 1.12 | 0.00 | 2.6 | 0.1 | 66.3 | 61.1 | -2296 | 24 | 8 | 1.11 |
| h1-tls-small-c64 | candidate | 4 | 193,182 | 0.309 | 0.649 | 1.856 | 40.45 | 20.3 | 1.99 | 6,342 | 53.4 | 0.13 | 0.06 | 26.5 | 0.1 | 93.3 | 73.2 | -8328 | 30 | 10 | 0.63 |
| h1-tls-small-c64 | baseline | 4 | 184,261 | 0.322 | 0.806 | 1.805 | 15.48 | 21.1 | 3.83 | 7,249 | 51.4 | 0.14 | 0.07 | 27.8 | 9.3 | 97.9 | 75.2 | -8662 | 30 | 10 | 0.88 |
| h1-tls-small-c64 | kestrel | 4 | 233,047 | 0.262 | 0.454 | 2.957 | 25.39 | 16.8 | 2.07 | 33 | 0.2 | 0.03 | 0.00 | 0.3 | 0.2 | 85.4 | 80.9 | 9486 | 26 | 8 | 0.66 |
| h2-plain-small-c8x32 | candidate | 4 | 388,361 | 0.531 | 2.061 | 7.731 | 30.58 | 9.9 | 3.07 | 9,760 | 44.0 | 20.69 | 0.60 | 102.8 | 17.6 | 102.1 | 80.5 | -12510 | 28 | 8 | 0.63 |
| h2-plain-small-c8x32 | baseline | 4 | 349,667 | 0.611 | 2.138 | 9.062 | 26.62 | 11.1 | 4.57 | 10,228 | 45.4 | 26.66 | 0.80 | 109.7 | 38.8 | 100.7 | 80.6 | -13344 | 28 | 9 | 0.68 |
| h2-plain-small-c8x32 | kestrel | 4 | 925,891 | 0.256 | 0.877 | 1.433 | 25.86 | 3.1 | 1.20 | 32 | 1.0 | 0.00 | 0.00 | 3.7 | 0.4 | 78.2 | 60.9 | -11224 | 25 | 8 | 0.72 |

Excluded samples:
- TestResults\perf-direct-dispatch\load-final\results-D-normal\samples\h1-plain-churn-c64\kestrel-r2.json: Warmup failed: System.Net.Sockets.SocketException: An operation on a socket could not be performed because the system lacked sufficient buffer space or because a queue was full. (socket NoBufferSpaceAvailable)
- TestResults\perf-direct-dispatch\load-final\results-D-swapped\samples\h1-plain-churn-c64\baseline-r1.json: Client: System.Net.Sockets.SocketException: An operation on a socket could not be performed because the system lacked sufficient buffer space or because a queue was full. (socket NoBufferSpaceAvailable)
- TestResults\perf-direct-dispatch\load-final\results-D-swapped\samples\h1-plain-churn-c64\candidate-r2.json: Warmup failed: System.Net.Sockets.SocketException: An operation on a socket could not be performed because the system lacked sufficient buffer space or because a queue was full. (socket NoBufferSpaceAvailable)

The three excluded churn samples hit `NoBufferSpaceAvailable` on the client
during connection churn, one per engine, which is the host's ephemeral-port
budget and not an engine property; each churn median rests on three samples.

Against the base, with the budget-1 yield:

- Throughput and CPU: +3.5% requests/s at 64 connections, +8% at 256, +6% on
  the close-after-100 control, +4% chunked, +5% TLS, +1% pipelined, +11% on
  h2c with 32 streams; CPU per request lower everywhere (-2% to -11%), and
  about 10% fewer bytes allocated per request.
- Work items per request: 1.97 against 3.71 on HTTP/1.1 keep-alive (Kestrel
  2.06); 1.06 against 2.67 pipelined; 3.07 against 4.57 on HTTP/2.
- Tail latency: p99 roughly halved at 64 connections (0.62 against 1.20 ms) and
  cut by 77% at 256 (2.09 against 9.01 ms); p99.9 lower in every HTTP/1.1
  scenario except pipelining (9.4 against 6.9 ms) and TLS (flat).
- Connection churn: throughput within noise (19,289 against 19,487 requests/s),
  CPU per request -10%, p99 20% higher and p99.9 15% lower on three samples
  each; not a resolved difference either way.
- Maximum latency is higher in several scenarios (for example 26 against 16 ms
  at 64 connections, 40 against 15 ms over TLS). The maximum is a single sample
  per run and the p99.9 columns move the other way, so it is reported, not
  explained.

Kestrel remains ahead on every small-request scenario: about 1.25x the
candidate's throughput on HTTP/1.1, 9.4x on 16-deep pipelining, 2.4x on h2c,
with 32 bytes allocated per request against about 6,200.

### Budget sweep

Three cores built from the same source with `InlineRequestBudget` 1, 4 and 16,
each against the 56e8e7aa base core, both orientations, h1-plain-small-c64,
its close100 control and pipe16:

| Budget | small-c64 req/s (base) | p50 ms | p99 ms (base) | p99.9 ms (base) | work items/req | pipe16 req/s (base) | pipe16 p99 (base) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 203,671 (200,196) | 0.289 | 0.630 (1.177) | 1.93 (3.07) | 1.97 | 252,007 (249,914) | 1.28 (2.05) |
| 4 | 206,894 (198,614) | 0.046 | 1.440 (1.267) | 2.13 (4.17) | 1.21 | 264,208 (251,466) | 1.24 (2.11) |
| 16 | 211,354 (198,285) | 0.049 | 4.301 (1.267) | 5.48 (3.48) | 1.03 | 266,336 (251,981) | 1.39 (2.05) |

Budget 1 is the only value whose p99 and p99.9 beat the base while pipelined
throughput holds, and the only one without the bimodal p50; budget 4 buys about
4% more throughput at a p99 above the base. The no-yield revision measured
before the sweep had p99 14.2 ms at 64 connections and 50 ms at 256.

### Idle keep-alive connections

1,000 idle connections per engine, one request each, 5 s hold, collected
snapshots; the harness's new `idle` mode, both orientations. Per connection:

| Protocol | Engine | Heap B | Working set B | Private B | Handles | Threads before -> idle |
| --- | --- | --- | --- | --- | --- | --- |
| HTTP/1.1 | candidate | 320 / 475 | 30,417 / 26,178 | 27,574 / 26,292 | 1.02 | 18 -> 21 |
| HTTP/1.1 | base | 576 / 394 | 26,186 / 25,969 | 25,375 / 25,199 | 1.03 | 18 -> 21 |
| HTTP/1.1 | Kestrel | 8,576 / 8,596 | 16,953 / 20,423 | 14,442 / 15,462 | 1.02 | 19 -> 21 |
| HTTP/1.1 TLS | candidate | 13,838 / 13,840 | 45,281 / 45,572 | 36,917 / 37,167 | 2.20 | 18 -> 22 |
| HTTP/1.1 TLS | base | 13,797 / 13,674 | 45,507 / 45,535 | 37,097 / 37,274 | 2.20 | 18 -> 22 |
| HTTP/1.1 TLS | Kestrel | 12,049 / 12,004 | 41,542 / 41,497 | 34,419 / 34,349 | 2.08 | 20 -> 23 |
| h2c | candidate | 17,401 / 13,166 | 33,206 / 33,427 | 26,989 / 30,597 | 1.02 | 18 -> 21 |
| h2c | base | 13,299 / 14,632 | 33,276 / 31,887 | 30,401 / 29,073 | 1.02 | 18 -> 21 |
| h2c | Kestrel | 31,643 / 31,635 | 52,462 / 53,015 | 47,026 / 46,985 | 1.02 | 19 -> 21 |

Values are normal / swapped orientation. Direct dispatch holds no per-connection
state, and the candidate matches the base within the orientation spread; the
HTTP/1.1 plain working-set figures differ by up to 4 KB between orientations
for both engines. Thread counts are identical. All server sockets were in
`Established` while idle and none remained after the client closed.

## Limits

- Measurements apply to the recorded source revisions and this host; they are
  development evidence, not a public benchmark.
- Direct dispatch runs a handler's synchronous prefix on the connection's
  thread. A handler that blocks synchronously occupies a thread-pool worker
  either way; the harness's blocking-handler case checks that other connections
  keep being served, not that blocking is free.
- HTTP/3 still uses the pull loop; moving it to the dispatcher is a follow-up
  once the HTTP/3 listener files are free of concurrent changes.
