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

Numbers for the provisional pass are recorded in this file only once the
provisional pass and the final pass against the merged base exist; until then
the section below is empty by design.

## Results

Pending.

## Limits

- Measurements apply to the recorded source revisions and this host; they are
  development evidence, not a public benchmark.
- Direct dispatch runs a handler's synchronous prefix on the connection's
  thread. A handler that blocks synchronously occupies a thread-pool worker
  either way; the harness's blocking-handler case checks that other connections
  keep being served, not that blocking is free.
- HTTP/3 still uses the pull loop; moving it to the dispatcher is a follow-up
  once the HTTP/3 listener files are free of concurrent changes.
