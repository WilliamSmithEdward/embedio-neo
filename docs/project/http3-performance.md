# HTTP/3 request path performance (October 2026)

Profiling-led changes to the managed HTTP/3 engine for program #181, measured
against the engine revision they start from and against Kestrel. Like the
[HTTP/2 work](http2-performance.md) and the
[managed engine load comparison](http-engine-load-comparison.md), these are
loopback development measurements that locate costs, not a public benchmark.
The harness and its limits are described in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md).

## Result

On 13-byte responses with 8 connections and 32 streams each, the engine serves
37% more requests per second with 32% less server CPU and 21% fewer allocated
bytes per request. Kestrel still uses about 1.4x less CPU and 4.9x fewer
allocated bytes per request, and its tail latency is lower.

| Scenario | Base `53b5640` | Candidate `b8759d3` | Kestrel 10.0.12 |
| --- | --- | --- | --- |
| 13 B, 8 x 32 (4 rounds) | 170,217 req/s, 41.8 us, 15,644 B, p99 14.85 ms | 232,964 req/s, 28.4 us, 12,418 B, p99 12.24 ms | 329,899 req/s, 20.9 us, 2,536 B, p99 8.29 ms |
| QUIC handshake per request, 16 concurrent | 1,906 req/s, 1,210 us, 128,323 B | 2,020 req/s, 1,070 us, 119,545 B | not run |
| 1 MiB response, 4 x 4 | 1,753 req/s, 2,595 us, 17,516 B | 1,739 req/s, 2,598 us, 14,821 B | not run |
| 1 MiB flushed per 16 KiB, 4 x 4 | 1,538 req/s, 3,580 us, p99 18.84 ms | 1,651 req/s, 3,200 us, p99 12.19 ms | not run |
| 1 MiB upload, 4 x 4 | 1,238 req/s, 2,756 us, p99 24.47 ms | 1,607 req/s, 2,533 us, p99 12.70 ms | not run |

Figures are medians of accepted samples: requests per second, server CPU
microseconds per request, server bytes allocated per request, client-observed
p99. The 13 B row has four samples per engine; the other rows two per engine.
Every sample validated every response byte; no accepted sample failed. The
1 MiB response row is unchanged within noise. Server thread-pool work items
fell from about 14.6 to 9.1 per small request (Kestrel 5.7), and from 145 to 74
per flushed 1 MiB response.

First-chance exceptions did not change and are not per request: about 72 to 84
`QuicException`, 21 to 25 `TaskCanceledException`, 32 `OperationCanceledException`
and 8 `ChannelClosedException` in each 15-second small-response window of
about 2.5 to 3.5 million requests, on both revisions. The earlier observation of
roughly one `TaskCanceledException` per request no longer reproduces. Retained
managed heap, handles and threads after idle and a forced collection stayed in
the same small ranges for both revisions, and no server sockets remained.

## Environment and method

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET 10.0.12; MsQuic 2.5.10 (`msquic.dll` SHA-256 `B7158D42...57E8`); System.Net.Quic `AF5B52CD...BE73` |
| Kestrel | shared framework 10.0.12 (core `62B5AB79...C239`, QUIC transport `6A6919A8...91DE`) |
| Base | engine head `53b5640` (tree `f00150e9`), core SHA-256 `C326A605DC375027A83852F9DEAE61163882F7BBC55D6F063A3EDC2B7D35E37C` |
| Candidate | `b8759d3` (tree `ed66381b`), core SHA-256 `7C9BFA9B96D1A84EA1EBF0372C258DE71E20F517E4F423B7C4D7DA7A95A7C2D5` |
| Runner | one harness build for all engines, SHA-256 `68DD9356D59A20AFC276C6A3E053CC62CED7FDF7627A8E4BC88973DD5F81EEBC`; the base copy differs only in `EmbedIO.dll` |
| CPU sets | server logical CPUs 0-7, client 8-15, set at process creation |
| Schedule | `--modern-baseline` (#217); engine order alternating by round; 5 s warmup, 15 s measurement, fresh processes per sample, no retries |

Core builds use `ContinuousIntegrationBuild` from `git archive` snapshots
(`scripts/prepare_load_benchmark.py`). The `environment.json` of each run records
the revisions, every hash above, the power scheme and the CPU sets.

The machine was shared with other agents. A run started only after 120 seconds
with no foreign test or load process, under a lock file created only if absent.
A watchdog checked every 10 seconds for foreign `EmbedIO.Tests`, load-benchmark,
`dotnet test` or conformance processes and for loss of the lock, and aborted the
whole attempt if either appeared. The accepted runs above completed without an
abort. Earlier attempts are kept and are not evidence:

- A first comparison produced no base samples, because the harness of that time
  filtered its baseline engine out of HTTP/3 scenarios.
- Two alternating comparisons were overlapped by another agent's full test suite
  (one after a lock overwrite). Their directories are marked `INVALID`; an
  aborted third is marked `ABORTED`.
- An orphaned run of a superseded revision, left by a stopped wrapper shell, and
  one watchdog false positive on the script's own process are marked `INVALID`.

## Where the time went

Profiles came from separate runs (dotnet-trace thread-time sampling and runtime
allocation and exception events with call stacks), never from comparison samples.
Thread-time samples include blocked threads; the shares are sampling estimates.
At engine `4c531fe`, on the 13 B, 8 x 32 scenario:

1. Allocation, about 16.3 KB per request, spread thinly. HTTP/3-specific sources
   included two watcher state machines, a `WhenAny`, a completion source and a
   linked registration per request for direction monitoring; `Task.Run` closures
   and wrapper tasks for the stream worker and application dispatch; a fresh
   `MemoryStream` and growth per QPACK section; two `QuicStream.WriteAsync`
   submissions (each registering its cancellation token) per frame; and `Task`
   results from one-byte frame-header reads. Shared code
   (`MultiplexedResponse.BuildHeaders`, request and response collections, the
   `WebServer` pipeline) accounted for most of the remainder.
2. Lock contention, about 32% of active thread time. About half was inside
   System.Net.Quic event callbacks (stream shutdown, receive, peer stream start,
   including a resource-string lookup when each stream completes); that is runtime
   behavior shared with Kestrel. The connection's single `_sync` gate (admission,
   QPACK decoding, response encoding and a Stream Cancellation for every finished
   stream), the per-request linked `CancellationTokenSource` registering on the
   shared connection token, and the priority state accounted for most of the rest.
3. Protocol work on every bodiless request. A request whose body the application
   never read had its read direction aborted (STOP_SENDING with H3_NO_ERROR), and
   every finished stream emitted a QPACK Stream Cancellation on the decoder stream,
   which woke the feedback writer for a transport write.

## Changes

All changes are inside `src/EmbedIO/Net/Internal/Http3/`. Public APIs, defaults,
targets and dependency groups are unchanged.

- **Request scopes owned by the connection.** Each request stream gets its own
  `CancellationTokenSource`, created at admission and tracked with the stream.
  One registration on the connection token cancels all active request sources.
  Request sources are not linked to the connection token and are never disposed,
  so a late callback can only repeat a cancellation.
- **Direction monitoring.** One continuation per QUIC direction replaces the two
  watcher tasks. FIN is still a normal half-close; a `QuicException` fault (reset,
  STOP_SENDING, connection loss) cancels the request at once, including during
  early upload cancellation and while QPACK is blocked. Any other completed fault
  is still reported to the stream owner when the request ends.
- **Fewer thread-pool hops and allocations.** Stream workers and isolated
  application dispatch yield instead of allocating `Task.Run` closures. The
  listener's own dispatcher, which only builds and queues a context, runs on the
  stream worker. Arbitrary dispatchers given to the connection keep their
  isolation. The exchange's owner is the request scope, replacing three captured
  delegates.
- **Separate gates.** QPACK decoding and response encoding have their own gates;
  `_sync` keeps admission, workers and drain state. Nested acquisition is only
  decoder gate then decoder internals, and a decode failure fails the connection
  after releasing the gate, so request callbacks never run under it.
- **QPACK feedback only when needed.** The feedback writer is woken only when the
  decoder produced an instruction. A request read to its FIN emits no Stream
  Cancellation (RFC 9204 Section 4.4.2 covers reset or abandoned input); abandoned
  and reset input still does.
- **Consume an already received end of input.** After the response, if the
  transport already holds the request's FIN, it is consumed without waiting,
  avoiding STOP_SENDING and Stream Cancellation. Unread DATA, trailers, errors or
  input that has not arrived still abandon the read direction as before.
- **One transport write per small frame.** Frames up to 16 KiB are copied with
  their header into a pooled buffer and submitted once. Final headers and a small
  first body write share one submission (the optional coalescing contract from
  #221), and a write that completes a declared Content-Length carries FIN.
  `QuicStream.WriteAsync` copies caller bytes into native memory before submitting
  them (runtime 10.0.12 `MsQuicBuffers.SetBuffer`), so the buffer returns to the
  pool once the write completes or fails. The used span is cleared first.
- **Reader and QPACK details.** Frame headers are read with as few transport
  reads as their encoding allows, never asking for a byte past the header. Reader
  operations return `ValueTask`. QPACK serialization and decoding reuse per-thread
  scratch storage and allocate only the exact result; the encoder scratch stream is
  cleared after each use, so encoded fields (cookies included) do not linger.

## Validation

- Focused tests: all 494 HTTP/3, QPACK, QUIC and multiplexed-context cases pass at
  `b8759d3`, including request cancellation, multiplexing, framing, QPACK blocked
  streams and feedback, drain, shutdown and resource cases.
- New regressions:
  - `Http3RequestInputEndTest`: over real QUIC, a request read to FIN produces no
    Stream Cancellation while an abandoned body produces one for its own stream.
    Against the base core it fails (it sees a cancellation for the completed
    stream first).
  - Direction watcher cases: FIN, QUIC faults (already completed or later),
    request end without transport completion, a late fault after the request
    ended, an unexpected fault reported to the owner, and 2,000 iterations of a
    fault racing request completion that cancel exactly once.
  - Request-stream probe cases: an available FIN, DATA or reserved frame is read
    without waiting; input that has not arrived is returned as pending.
  - Frame-header read bounds: no read past the header, and the number of
    transport reads per header encoding.
  - Adapter cases for a declared Content-Length written in one call (coalesced
    with FIN) and split across two writes.
- Existing tests that reach internals by reflection were adjusted only where the
  internal shape changed (`ValueTask` results, the decoder gate, array-backed
  memory reads in fixtures); no assertion or timeout was weakened.
- `tools/EmbedIO.AnalyzerGuard` and `dotnet format whitespace` pass; both core
  targets build.
- Conformance campaign (pinned container, MsQuic 2.6.2, aioquic 1.3.0 with
  pylsqpack, h2spec, seed 20261009) on `b8759d3` and on base `53b5640`: HTTP/3
  stateful fuzz passes (100 connections, 676 streams, 95 client cancellations, no
  server resets) and all HTTP/3 cases conform with one policy observation, with
  identical outcomes on both revisions. The overall result is failed on both
  because h2spec reports 141/146 cleartext and 142/146 TLS, with identical failure
  sets on both revisions; these are the known HTTP/2 items in the
  [conformance audit](http-conformance.md), unrelated to this change.

## Remaining bottlenecks

From the candidate's allocation profile and the comparison:

1. Shared request and response objects dominate the remaining 12.4 KB per small
   request: `WebHeaderCollection`, `NameValueCollection` and `Hashtable` storage
   for headers and the empty query collection, the request `Uri`, the context, and
   the `WebServer` dispatch chain with one `Task.Run` per context. These are shared
   with HTTP/2 and HTTP/1 and outside this change.
2. System.Net.Quic itself: per-stream objects, final-state tasks for
   `ReadsClosed` and `WritesClosed`, an exception object created at each stream
   shutdown, and lock contention in its event callbacks. Kestrel pays the same
   runtime costs.
3. Per request, the connection still awaits the application with a cancellable
   `WaitAsync`, and the listener waits for context completion with another, so a
   peer reset unblocks the stream even if the application ignores cancellation.
   Removing either would change cancellation semantics.
4. Thread-pool hops: about 9 work items per small request versus 5.7 for Kestrel,
   mainly the listener channel, the `WebServer` per-context task, response close
   continuations and QUIC completion continuations.
5. Tail latency: p99 is still about 1.5x Kestrel's at this load.

## Limits

- Loopback on one Windows host; no packet loss, real network or NIC offload.
  Closed-loop clients; overload and coordinated omission are not characterized.
- The client is `HttpClient` and used 53% to 63% of its CPUs; it does not saturate
  before the server here, but it is heavier than the server's own work per request.
- Two rounds for the non-small scenarios; small differences there are noise.
- Linux and macOS performance were not measured. Conformance ran on Linux in the
  pinned container; Apple QUIC lifecycle work belongs to its own program item.
