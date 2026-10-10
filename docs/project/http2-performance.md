# HTTP/2 output and dispatch performance (October 2026)

Profiling-led changes to the managed HTTP/2 engine for program #181, measured
against the engine revision they start from and against Kestrel. Like the
[managed engine load comparison](http-engine-load-comparison.md), these are
loopback development measurements that locate costs, not a public benchmark.
The harness and its limits are described in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md).

## Result

On 13-byte responses with 8 connections and 32 streams each, the engine now
serves about twice as many requests per second with about half the server CPU
per request. Kestrel still uses about 3.4x less CPU per request on cleartext
and 2.4x less over TLS.

| Scenario | Base `4c531fe` | Candidate | Kestrel 10.0.12 |
| --- | --- | --- | --- |
| h2c 13 B, 8 x 32 | 287,831 req/s, 26.1 us, 13,585 B | 561,887 req/s, 13.4 us, 10,069 B | 1,653,514 req/s, 3.9 us, 32 B |
| h2 TLS 13 B, 8 x 32 | 241,451 req/s, 30.1 us, 14,156 B | 572,298 req/s, 12.8 us, 10,128 B | 1,307,020 req/s, 5.4 us, 42 B |
| h2 TLS 13 B, 64 x 1 | 159,174 req/s, 46.0 us, 11,358 B | 313,466 req/s, 23.3 us, 8,741 B | 356,524 req/s, 20.2 us, 287 B |
| h2 TLS 1 MiB response, 4 x 4 | 4,349 req/s, 1,647 us | 5,772 req/s, 1,265 us | 5,963 req/s, 1,278 us |
| h2 TLS 1 MiB flushed per 16 KiB, 4 x 4 | 4,093 req/s, 1,784 us | 5,341 req/s, 1,263 us | 4,659 req/s, 1,633 us |
| h2 TLS 1 MiB upload, 4 x 4 | 923 req/s, 2,622 us | 1,150 req/s, 2,593 us | 2,812 req/s, 2,637 us |

Figures are medians of clean samples: requests per second, server CPU
microseconds per request, server bytes allocated per request. Every sample
validated every response byte; no sample failed. The two 8 x 32 rows were
measured on the final source `f6e87fc`; the other rows on `31128c3`, whose
output path is the same and which differs only in response-stream disposal,
which these handlers do not use. On `31128c3` the 8 x 32 rows were 569,888 and
581,750 requests per second.

## Environment and method

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET 10.0.12; Kestrel from the shared framework (core SHA-256 `62B5AB79...C239`) |
| Base | engine head `4c531fe` built from `git archive` |
| Candidate | final `f6e87fc` (core SHA-256 `2F9FEA07...4ADD`, base core `497EC1A5...822B`, runner `B9F762ED`); earlier rows `31128c3` (core `A5CE47BB...14A6`, base core `35053507...5D32`, runner `92E9001A`) |
| Runner | the same harness build for all three engines within each run |
| CPU sets | server logical CPUs 0-7, client 8-15, set at process creation |
| Schedule | 3 rounds, engine order alternating; 5 s warmup, 15 s measurement, fresh processes per sample |

The base engine ran as the harness baseline on HTTP/2 scenarios, in the same
alternating schedule. These runs used a `--baseline-all-protocols` option written
for this work; the integrated harness provides the same behavior as
`--modern-baseline` (#217). The integration also retains
`--baseline-all-protocols` as an alias; both record the effective capability.

Other agents shared the machine and did not all use the shared lock file. A
watcher logged foreign benchmark and test processes every 10 seconds. A sample
counts as clean only if no foreign process was seen between its start and 30
seconds later. On the final source, Kestrel cleartext and all three TLS 8 x 32
engines have two clean samples, the others three. In the `31128c3` run, stream
and upload have two clean samples per engine and the TLS 8 x 32 scenario ran
three times to get three. All samples, including overlapped ones, are kept with
the raw data, and their medians are within 5% of the clean medians.

## Where the time went

Profiles came from separate runs (`--profile` for allocation and contention
events, dotnet-trace thread-time sampling), never from comparison samples.
Thread-time samples include blocked threads, so the shares below drop samples
whose leaf is a wait (thread-pool idle, completion-port polling, console read,
finalizer wait); they are still sampling estimates, not hardware counters.

At `4c531fe`, of active server thread time on h2c 13 B:

1. Socket sends, about 35%. Every frame was a separate `Stream.WriteAsync`
   under a `SemaphoreSlim`; a small response cost three sends (HEADERS, DATA
   and an empty END_STREAM DATA frame at close).
2. Lock contention, about 32%: the frame write gate and header semaphore, the
   dispatcher's `_sync`, the send flow-control lock, and listener admission.
3. Thread-pool dispatch, about 22%. The server ran about 26 work items per
   request.
4. Allocation: 13.5 KB per request, including a linked
   `CancellationTokenSource` per write, semaphore wait boxes, `HpackField[]`
   growth on both sides and per-response header strings.

HPACK coding and frame parsing were a small share. The single `WebServer`
accept loop was checked as a possible serial limit: it used about 0.2
thread-seconds per second, so it was not saturated.

## Changes

- **Ordered output batching** (`Http2FrameTransport`). Writes queue in order
  behind one flusher. A lone writer flushes inline with no thread hop; writes
  queued behind a flush are written together, up to 64 KiB per transport write.
  A queued write remains cancelable until the flusher takes it; that is its
  commit point. Commit-time work runs on the flusher in wire order: HPACK
  encoding, SETTINGS application with its ACK, and the DATA admission check
  against a reduced window. The separate header semaphore is gone.
- **Adaptive flush window.** When the previous batch held more than one write,
  the next flush runs on the thread pool so sibling streams can join it; with a
  lone writer it reverts to inline flushing.
- **One write per small response.** Final response headers travel with the
  first DATA frame, and the DATA write that completes a declared Content-Length
  carries END_STREAM instead of a separate empty frame at close. Over-length
  writes still fail with `InvalidDataException`. Explicit flushes still send
  headers by themselves, and bodies of unknown length still end at close.
  `MultiplexedResponse` uses this through the optional
  `IMultiplexedHeaderCoalescing` interface; HTTP/3 is unchanged.
- **No per-write token linking.** Queued writes observe the request and stream
  tokens directly, and the response gate and flow-control reservation take
  non-waiting fast paths. A linked source is created only when a writer
  actually waits.
- **Cheaper stream dispatch.** Applications start through a cached work-item
  callback and a running count instead of `Task.Run`, a tracked task set and a
  continuation per stream. Shutdown still waits for every application,
  including its exchange disposal.
- **Header text reuse** (`MultiplexedResponse`, shared with HTTP/3). The Date
  string is formatted once per second, the charset decision is reused for the
  last content type and encoding, lowercase field names are cached (bounded),
  and the decoder and field lists are reused or presized. Wire output and the
  application-visible header collection are unchanged.
- **Non-blocking stream disposal** (`MultiplexedResponse`, shared with HTTP/3).
  Disposing the response stream starts the response close instead of waiting
  for it; see the second defect below.

## Defects found and fixed during validation

### Streams refused under load

The first full comparison of the batched engine had one failed candidate sample
in each of two scenarios (`The request was aborted`). The client's inner
exception was `REFUSED_STREAM`: the server refused new streams although the
client never had more than 32 open, against an advertised limit of 128.

A stream stopped counting against the limit only when its writer resumed after
the batch write. Under load that continuation waited on a busy thread pool
while the peer had already received END_STREAM and opened new streams. The base
engine has the same race but rarely reached it: its samples showed no protocol
exceptions, against 1 to 76 per candidate sample. RFC 9113 section 5.1.2 counts
a stream only while it is open or half-closed from the endpoint's own view, so
the local side now ends when the END_STREAM frame is committed. A deterministic
regression holds the write after commit and checks the registry. After the fix,
the same scenarios logged no protocol exceptions and no failed samples.

The failed run and two diagnostic runs are kept. The harness now records the
whole client exception chain, and `EMBEDIO_BENCH_EXCEPTION_DETAIL=1` makes the
server log the first stack trace for each distinct first-chance exception.

### Workers blocked on response-stream disposal

The first conformance run of `31128c3` reported two HTTP/2 case violations on
both transports. After the max-concurrent-streams case (133 slow streams) and
the rapid-reset case (5,000 opened and reset streams), the follow-up request on
a fresh connection did not complete within five seconds; over TLS even its
handshake timed out. The base engine passed the same cases in the same container.

Stacks captured with dotnet-stack during the stall showed 42 thread-pool
workers blocked in `MultiplexedResponse.Close()`, called from the response
stream's synchronous `Dispose`. `SendStringAsync` reaches it because
`StreamWriter` closes its stream synchronously, even from `DisposeAsync`; the
serializers' `using` blocks and compression wrappers do the same. Each blocked
worker waited for the final write, which under batching needed another worker to
flush and to resume, so the pool grew by about two threads a second while new
connections waited. The blocking was already there; batching made it far more
likely to starve the pool.

`WebServerBase` already closes multiplexed contexts asynchronously because
"Multiplexed FIN writes must not block a worker waiting for I/O". Disposing the
response stream now starts the same shared close without waiting and observes
its failure; the context close that the server and the HTTP/2 dispatch await
returns that task. Writes after disposal still fail with
`ObjectDisposedException`, and the explicit `Close` methods remain synchronous.
A wire regression holds the final write and requires a synchronous dispose to
return; it times out against the previous source. Both conformance cases then
passed, with their documented outcomes.

## Validation

All results are for the final source `f6e87fc` unless noted.

- All 4,480 tests discovered on Windows ran: 4,475 passed, 5 existing platform
  skips. Eleven are new: batching order and bound, cancellation of a queued
  header block without HPACK table change, a rejected write inside a batch,
  terminal batch failure, END_STREAM retirement (headers-only and DATA),
  response coalescing on the wire, over-length after completion, explicit-flush
  visibility and non-blocking stream disposal. The four tests of new batching
  behavior fail against `4c531fe`, the two retirement cases and the disposal
  case fail against the source before their fixes, and the invariant tests pass
  on both. After merging the engine branch at `3a45029`, the combined suite
  discovered 4,508 tests (its floor is 4,497): 4,503 passed, 5 existing skips,
  and 865 HTTP/2 and HTTP/3 cases passed.
- Existing flow-control, SETTINGS-ordering, reset, framing, HPACK, interop,
  drain and HTTP/3 suites pass unchanged (863 HTTP/2 and HTTP/3 cases).
- With the actual .NET Standard 2.0 core, 416 of 422 HTTP/2 cases pass. The six
  failures are TLS cases that need ALPN, which only the .NET 10 asset negotiates;
  the base engine's .NET Standard 2.0 asset fails the same six.
- The pinned conformance container (`test/EmbedIO.Conformance`, seed 20261009,
  four CPUs) ran the HTTP/2 phases of `run-campaigns.sh` unchanged, one phase
  group per container: h2spec 141 of 146 cleartext and 142 of 146 TLS, the
  documented set (two 5.1 closed-stream and two 5.3.1 self-dependency
  expectations on both transports, plus the cleartext invalid-preface
  fallback); HTTP/2 cases 16 conforming and 6 policy on each transport, no
  violations; stateful HTTP/2 fuzz 300 iterations passed with 1,154 streams,
  115 client resets, no server resets, handlers drained, handle growth 14 and
  retained managed growth 1.73 MB.
- Formatting, the analyzer-suppression check and the null-forgiving parser
  guard pass.


## Integration review

The combined Windows suite reported 4,514 cases: 4,509 passed, five expected
skips and zero failures (3m00.759s). Both production targets built cleanly;
analyzer guards and formatting passed. The discovery floor is 4,514.

The measurements above belong to the explicitly recorded agent revisions, not
to every subsequent engine build. Integration onto `3a45029` preserves the
current HTTP/1 and listener fixes and adds three writer safety regressions from
PR #219: canceled queued payload reuse before shared I/O unblocks,
HEADERS/CONTINUATION adjacency, and failure propagation to committed and waiting
writers after a partial batch write.

Review also reproduced an application-cancellation failure during HTTP/2
cleanup. A throwing callback after response completion left the new application
counter nonzero, so connection shutdown timed out although PING still worked.
A separate test reproduced the callback exception escaping through parent-token
connection cancellation. Both cancellation boundaries now log recoverable
callback failures and continue cleanup. Exchange disposal releases its resources,
and dispatcher completion accounting runs in a finally even if cleanup throws.
The three regressions cover completed responses, reset streams and an active
application during connection stop. Before/after evidence is retained locally;
this is a cleanup correction, not a new performance claim.

William explicitly approved nonblocking synchronous response-output stream
disposal for HTTP/2 and HTTP/3. See the
[migration guidance](../compatibility/migration.md#multiplexed-response-stream-disposal-unreleased)
for completion/error timing and awaited alternatives. HTTP/3 shares this
response adapter; its framing transport is not changed by the HTTP/2 output
batching.

## Remaining bottlenecks

- **Per-connection lock contention.** The dispatcher's `_sync` (stream
  completion), `HttpConnection._connectionSync` (per-request owner
  registration), and the send flow-control and stream-registry locks are taken
  several times per request by every stream on a connection. Shrinking them
  needs care because the order of reset, registry and credit updates under
  `_sync` keeps DATA credit accounting exact.
- **Allocation per request,** about 10 KB against Kestrel's 32 B. Most is
  application-model state: `NameValueCollection` request headers, a
  `WebHeaderCollection` per response, the context, request, `Uri`, route and
  close-callback objects, and the per-request link to the server token in
  `MultiplexedContext`.
- **Listener hand-off.** Each request is registered in the shared listener
  queue, dequeued by the `WebServer` loop and started again with `Task.Run`.
- **Upload flow control.** 1 MiB uploads remain window-bound (client CPU about
  37%), as recorded in the load comparison; the receive window was out of scope.

## Limits

These are loopback, closed-loop, 15-second windows on one shared Windows host,
with `HttpClient` as the HTTP/2 client. They say nothing about network latency,
other operating systems or long-run memory. Intermediate measurements from
individual commits used two rounds, and one of their first samples overlapped
another agent's test run; they guided the work and are not the result above.
