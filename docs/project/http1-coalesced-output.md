# HTTP/1.1 response completion and held output

This is the first increment of [#577](https://github.com/WilliamSmithEdward/embedio-neo/issues/577).
The change is pending and is absent from the 2.19.0 packages. The wider goal is
lower cold-start and warm latency and higher throughput than Kestrel. Warm
steady-state and tail latency take priority, and Windows is the lead environment.
This increment does not establish that the wider goal has been met.

## Output ownership

While the HTTP/1 connection dispatches a handler for a request without an entity
body inline, an incomplete response can hold up to 64 KiB of encoded output.
Its final write or synchronous completion combines the held head, body chunks
and ending into one transport write
when they fit. A response that reaches its declared length writes through at once.
Informational responses and tunnel/upgrade heads remain immediate.

When the handler returns or suspends, the connection closes the holding window
and sends any remaining output. Subsequent writes pass through. With
`IgnoreWriteExceptions = false`, application writes and flushes always pass
through so their transport exceptions remain observable by the caller.
Requests with entity bodies also retain write-through behavior: an application
can commit response bytes before a subsequent request-body read fails.
If a handler for a request without a body throws after committing output, its accepted
bytes are sent before the connection aborts, without a clean chunk terminator.
Intentional response abort and listener stop also attempt to submit already
accepted bytes if the output gate is immediately available. They close the
transport without waiting for that send, so backpressure cannot extend shutdown.
Transport-error and deadline retirement discard unsent held output. An aborted
connection does not guarantee delivery of its submitted bytes.

A handler can flush and then block synchronously without returning to the
connection. One process-wide timer sends output held across two tick increments,
at a 10 ms interval. A fast inline handler therefore has at least one complete
tick to finish. This is a scheduling interval, not a real-time latency guarantee.
No option or public API is added.

Each connection registers once, on its first eligible output window, and removes
its registration at transport retirement. The timer stops while no output is
held. A response publishes its held output without registering another callback
or queuing a work item. The sender rechecks output age under the existing write
gate; a slow connection has at most one heartbeat sender and does not hold the
shared registration lock during I/O. Pooled held bytes are cleared before return,
including on write failure and connection retirement.

## Why the first promptness mechanism was replaced

The first candidate queued a ThreadPool work item for every response's first held
byte. A controlled transport test on `4fc61c43` confirmed that this sender could
own the write gate across an asynchronous write while synchronous completion
blocked in `SemaphoreSlim.Wait`. Releasing the controlled write allowed completion
to continue. This establishes the contention path; it does not quantify how much
of an end-to-end benchmark regression it caused.

The shared heartbeat delays its intervention until output has survived a full
tick and avoids creating a competing sender for each fast response. Synchronous
completion still serializes behind a heartbeat write that is already in flight
for a long-running handler, preserving wire order.

## Measurements

Final role-alternated warm and separate cold measurements are pending. The first,
fixed-role candidate results remain recorded in #577 and under ignored
`TestResults/http1-perf/results/ending-1-a1`; they are preliminary and are not
evidence for the heartbeat implementation.

The comparison uses explicit main `b30f186436b23b0a924952b9e2a231266666d01f`,
the candidate, and Kestrel on the same .NET 10.0.13 runtime. Server CPUs are 0-3,
client CPUs are 4-11, leaving CPUs 12-15 free. The shared benchmark lock and both
heavy-work slots are required. The process guard rejects foreign benchmark load.
Candidate and baseline runner roles must be reversed before drawing conclusions.

Warm scenarios retain small responses at concurrency 64 and 256, pipeline depth
16, a 13-byte chunked response, compressed 1 KiB JSON, 1 MiB streaming, and
small/chunked TLS responses. Cold measurements launch fresh processes and report listening and
first-request costs separately from established-connection warm percentiles.
Fresh processes do not imply cold OS caches.

## Validation

The 39 new cases and 96 existing compatibility cases pass on Windows (135 total).
Four identical single-write assertions fail on exact main: synchronous responses
use three writes, while asynchronous variants use two asynchronous writes before
completion. The baseline proof adapts only the unavailable holding-window setup;
its main source and selected assertion method are hash-verified unchanged.

The final source-quality scripts pass all 588 source files and all 16 project
scans. Changed source and tests have no informational, warning or error
diagnostics; hidden hints remain report-only. Both library targets build in
Debug and Release, changed-file whitespace and analyzer guards pass, and all four
existing allocation ceiling checks pass. The complete Windows suite is pending.

The cases exercise transport write counts, blocking-handler flush promptness,
strict errors, pipelining and keep-alive order, window closure, tick aging,
registration lifetime, held-buffer cleanup, telemetry, committed errors and
nonblocking shutdown under backpressure. The discovery floor is 7,575.

The broader #577 work, including pipelined response batching, per-request
allocation and a Windows Registered I/O transport, remains separate. A transport
requiring a public option needs William's design approval before implementation.
