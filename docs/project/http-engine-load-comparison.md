# Managed engine load comparison (October 2026)

Separate-process measurements of the PR #182 managed engine (`candidate`), the
current `main` core (`baseline`) and ASP.NET Core Kestrel, for program #181. They
locate bottlenecks; they are not a public benchmark or a ranking. The harness,
its method and its limits are in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md).

## Environment and provenance

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET 10.0.12; Kestrel and System.Net.Quic 10.0.12 (`95017c7`) from the shared framework; msquic from the runtime |
| Candidate | PR #182 head `9e1fe09` plus harness commit `b184f47` (core SHA-256 `70C692FD...8647`) |
| Baseline | main `1445c23`, built from `git archive` (core SHA-256 `4EB1B475...84F7`) |
| CPU sets | server logical CPUs 0-7, client 8-15 (disjoint physical cores), set at process creation |
| GC | Server GC, concurrent, for every engine |
| Schedule | 3 rounds, engine order alternating; 5 s warmup, 15 s measurement, 2 s idle before each resource snapshot |
| Raw data | `TestResults/load-benchmark/sustained-1` in the benchmark worktree: per-sample JSON with full latency histograms, `environment.json`, `summary.json`/`summary.md` |

Other development sessions shared this machine. The two sessions with local
load agreed to pause for the 75-minute sustained run. Each sample records
machine busy CPU minus server and client CPU: median 1.1 busy logical CPUs, p90
6.1, maximum 10.5. That figure also contains kernel loopback networking that
Windows does not charge to either process, so it overstates other workloads.
Treat differences under about 10% as noise unless they repeat in every round.

## Results

Medians over valid samples; ranges and every sample are in the raw data.
"B/req" is server allocation per request. Utilization is CPU time over the CPUs
the process was given. When client utilization is near 100% the client, not the
server, limits throughput.

| Scenario | Engine | Valid | Requests/s | p50 ms | p99 ms | Server us CPU/req | Server B/req | Server CPU | Client CPU |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| HTTP/1.1 13 B GET, 64 conn | candidate | 3/3 | 311,694 | 0.126 | 1.062 | 23.0 | 8,163 | 91% | 81% |
| | baseline | 3/3 | 326,535 | 0.118 | 0.986 | 23.0 | 10,370 | 94% | 81% |
| | Kestrel | 3/3 | 464,260 | 0.133 | 0.262 | 16.4 | 32 | 95% | 92% |
| HTTP/1.1 13 B GET, 256 conn | candidate | 2/3 | 275,485 | 0.570 | 4.915 | 25.5 | 8,025 | 88% | 72% |
| | baseline | 2/3 | 300,617 | 0.425 | 4.941 | 24.8 | 10,258 | 93% | 80% |
| | Kestrel | 3/3 | 449,620 | 0.531 | 1.113 | 16.8 | 32 | 94% | 87% |
| HTTP/1.1 pipelined x16, 16 conn | candidate | 3/3 | 402,608 | 0.281 | 1.254 | 18.1 | 7,918 | 91% | 69% |
| | baseline | 3/3 | 410,905 | 0.285 | 0.998 | 18.2 | 10,365 | 94% | 69% |
| | Kestrel | 3/3 | 4,176,144 | 0.056 | 0.133 | 1.8 | 32 | 95% | 88% |
| HTTP/1.1 1 MiB response, 16 conn | candidate | 3/3 | 19,355 | 0.749 | 2.074 | 383 | 24,660 | 93% | 48% |
| | baseline | 3/3 | 19,226 | 0.761 | 1.920 | 384 | 26,911 | 92% | 48% |
| | Kestrel | 3/3 | 21,739 | 0.518 | 9.421 | 271 | 853 | 73% | 45% |
| HTTP/1.1 1 MiB upload, 16 conn | candidate | 1/3 | 20,792 | 0.710 | 1.523 | 322 | 10,166 | 84% | 75% |
| | baseline | 2/3 | 19,445 | 0.755 | 1.997 | 366 | 28,082 | 89% | 63% |
| | Kestrel | 3/3 | 10,946 | 1.421 | 2.201 | 573 | 389 | 80% | 43% |
| HTTP/1.1 1 MiB flushed per 16 KiB | candidate | 3/3 | 3,511 | 3.482 | 28.672 | 2,211 | 31,853 | 97% | 87% |
| | baseline | 3/3 | 3,504 | 4.250 | 11.571 | 2,189 | 34,184 | 96% | 86% |
| | Kestrel | 3/3 | 15,120 | 0.986 | 2.150 | 490 | 6,650 | 92% | 51% |
| HTTPS 13 B GET, 64 conn | candidate | 3/3 | 174,193 | 0.278 | 1.229 | 33.8 | 8,397 | 73% | 57% |
| | baseline | 3/3 | 168,670 | 0.288 | 1.203 | 34.7 | 10,592 | 74% | 56% |
| | Kestrel | 3/3 | 436,015 | 0.141 | 0.345 | 17.5 | 32 | 94% | 93% |
| HTTPS handshake per request, 32 conn | candidate | 3/3 | 3,431 | 0.227 | 3.430 | 597 | 21,137 | 26% | 36% |
| | baseline | 2/3 | 3,287 | 0.206 | 3.481 | 642 | 23,084 | 26% | 35% |
| | Kestrel | 3/3 | 3,315 | 0.224 | 3.891 | 560 | 12,939 | 23% | 35% |
| HTTPS 1 MiB response | candidate | 3/3 | 7,721 | 1.600 | 8.294 | 972 | 24,799 | 94% | 82% |
| | baseline | 3/3 | 7,897 | 1.894 | 5.632 | 968 | 27,047 | 96% | 88% |
| | Kestrel | 3/3 | 9,077 | 1.715 | 3.533 | 860 | 7,498 | 97% | 65% |
| HTTPS 1 MiB upload | candidate | 3/3 | 8,026 | 1.894 | 3.891 | 878 | 18,755 | 88% | 96% |
| | baseline | 3/3 | 9,009 | 1.664 | 3.123 | 576 | 28,264 | 62% | 94% |
| | Kestrel | 3/3 | 8,272 | 1.894 | 3.225 | 903 | 710 | 94% | 97% |
| h2c 13 B, 8 conn x 32 streams | candidate | 3/3 | 278,207 | 0.608 | 4.352 | 27.9 | 13,637 | 97% | 78% |
| | Kestrel | 3/3 | 1,721,017 | 0.136 | 0.525 | 3.9 | 32 | 85% | 89% |
| h2 TLS 13 B, 8 conn x 32 streams | candidate | 3/3 | 241,039 | 0.723 | 5.632 | 30.8 | 14,309 | 93% | 75% |
| | Kestrel | 3/3 | 1,306,372 | 0.177 | 0.570 | 5.2 | 42 | 85% | 88% |
| h2 TLS 13 B, 64 conn x 1 stream | candidate | 3/3 | 173,107 | 0.333 | 1.690 | 44.1 | 11,364 | 95% | 88% |
| | Kestrel | 3/3 | 374,209 | 0.157 | 0.473 | 19.8 | 286 | 91% | 91% |
| h2 TLS 1 MiB response, 4 x 4 | candidate | 3/3 | 3,940 | 4.045 | 5.888 | 1,873 | 1,139,473 | 93% | 86% |
| | Kestrel | 3/3 | 5,928 | 2.662 | 3.891 | 1,271 | 7,935 | 95% | 91% |
| h2 TLS 1 MiB upload, 4 x 4 | candidate | 3/3 | 922 | 14.131 | 53.248 | 2,877 | 1,179,677 | 33% | 33% |
| | Kestrel | 3/3 | 2,869 | 5.478 | 13.517 | 2,634 | 9,155 | 95% | 72% |
| h2 TLS 1 MiB flushed per 16 KiB, 4 x 4 | candidate | 3/3 | 3,744 | 4.250 | 6.195 | 1,962 | 1,176,263 | 92% | 84% |
| | Kestrel | 3/3 | 4,565 | 3.430 | 5.222 | 1,627 | 31,496 | 94% | 89% |
| HTTP/3 13 B, 8 conn x 32 streams | candidate | 3/3 | 146,065 | 1.037 | 15.770 | 47.0 | 16,367 | 86% | 55% |
| | Kestrel | 3/3 | 324,029 | 0.544 | 10.445 | 20.7 | 2,541 | 84% | 69% |
| HTTP/3 handshake per request, 16 | candidate | 3/3 | 1,794 | 7.373 | 24.576 | 1,183 | 128,904 | 27% | 47% |
| | Kestrel | 2/3 | 1,937 | 6.758 | 23.757 | 987 | 134,197 | 24% | 49% |
| HTTP/3 1 MiB response, 4 x 4 | candidate | 3/3 | 1,908 | 8.397 | 12.083 | 2,425 | 18,381 | 58% | 50% |
| | Kestrel | 3/3 | 1,276 | 11.981 | 27.034 | 4,822 | 1,189,165 | 75% | 59% |
| HTTP/3 1 MiB upload, 4 x 4 | candidate | 3/3 | 1,770 | 9.011 | 13.005 | 2,310 | 85,982 | 51% | 56% |
| | Kestrel | 3/3 | 1,433 | 9.626 | 22.118 | 3,069 | 16,753 | 54% | 42% |
| HTTP/3 1 MiB flushed per 16 KiB, 4 x 4 | candidate | 3/3 | 1,665 | 9.626 | 15.974 | 3,275 | 49,072 | 69% | 52% |
| | Kestrel | 3/3 | 1,324 | 11.776 | 25.395 | 4,638 | 8,455 | 77% | 65% |

One request per connection (connection setup cost). Several of these samples
exhausted host TCP resources (see failures), so some medians rest on one or two
samples. They were measured with 15 s windows before the churn caps were added and
have not been rerun.

| Scenario | Engine | Valid | Requests/s | p99 ms | Server us CPU/req | Server B/req |
| --- | --- | --- | --- | --- | --- | --- |
| HTTP/1.1 13 B, 64 concurrent | candidate | 2/3 | 28,351 | 3.162 | 101 | 18,736 |
| | baseline | 1/3 | 25,773 | 3.328 | 103 | 20,801 |
| | Kestrel | 3/3 | 26,205 | 3.584 | 117 | 8,939 |
| h2 TLS 13 B, 32 concurrent | candidate | 1/3 | 2,509 | 19.046 | 929 | 33,177 |
| | Kestrel | 1/3 | 2,368 | 21.299 | 715 | 36,627 |

Connection setup is on par across engines for HTTP/1.1, HTTPS and HTTP/3; HTTP/2
over TLS uses 30% more server CPU per connection than Kestrel in its single valid
sample.

### Reading the comparison

- Against `main`, the candidate keeps HTTP/1.1 throughput, latency and CPU
  within run-to-run noise in every HTTP/1.1 scenario. It allocates 18-22% less
  per small request (8.0-8.4 KB versus 10.3-10.6 KB) and 64% less per 1 MiB upload.
  No HTTP/1.1 regression repeats across rounds. The 256-connection and upload
  medians rest on one or two valid samples.
- Against Kestrel, the candidate is slower in every non-QUIC scenario except
  HTTP/1.1 uploads. The gap is largest for multiplexed and pipelined small
  requests: 5.4-6.2x for HTTP/2 with 32 streams per connection and about 10x
  for 16-deep pipelining. For ordinary small HTTP/1.1 requests most of the gap is
  the 100-request connection cap (see the equal-lifetime controls below). Server
  CPU per small request is 1.4-1.9x Kestrel's on
  HTTP/1.1, and on HTTP/2 6-7x with 32 streams per connection (2.2x with one
  stream per connection). Kestrel allocates 32-42 bytes per small request;
  the candidate allocates 8-16 KB.
- HTTP/3 bulk transfers are the exception: the candidate leads Kestrel on
  1 MiB responses, uploads and flushed streams, with lower tail latency and about
  half the CPU per request. Kestrel's HTTP/3 response path allocates about 1.1 MiB
  per 1 MiB response in this test.
- HTTP/1.1 1 MiB uploads are faster on both EmbedIO cores than on Kestrel
  (about 20,000 versus 11,000 requests/s). That is not established as an engine
  property: the handler's pattern check runs on both, and the cause was not profiled.

### Equal connection lifetime controls

The managed listener closes each HTTP/1.1 connection after 100 requests; Kestrel
does not. In the `-close100` controls the client closes every connection after 100
requests, so all engines pay the same reconnect cost and the server closes first.
Three rounds, same settings; raw data in `TestResults/load-benchmark/upload-control-1`.
This run used the harness revision that added the control scenarios and churn
caps; the candidate and baseline core sources are the same as above.

| Scenario | Engine | Valid | Requests/s | p99 ms | Server us CPU/req | Server B/req |
| --- | --- | --- | --- | --- | --- | --- |
| 13 B GET, 64 conn, close after 100 | candidate | 3/3 | 322,977 | 0.986 | 22.5 | 8,134 |
| | baseline | 3/3 | 330,152 | 0.960 | 23.1 | 10,372 |
| | Kestrel | 3/3 | 358,003 | 0.480 | 18.0 | 126 |
| 1 MiB upload, 16 conn, close after 100 | candidate | 3/3 | 20,564 | 1.523 | 309 | 10,283 |
| | baseline | 2/3 | 19,787 | 1.626 | 371 | 28,082 |
| | Kestrel | 3/3 | 11,272 | 2.253 | 582 | 1,030 |
| 1 MiB upload, 16 conn (repeat) | candidate | 3/3 | 21,089 | 1.421 | 323 | 10,161 |
| | baseline | 3/3 | 19,642 | 1.690 | 379 | 28,074 |
| | Kestrel | 3/3 | 11,428 | 1.946 | 573 | 397 |

With the same lifetime, Kestrel drops from 464,260 to 358,003 small requests/s and
Kestrel's lead over the candidate narrows from 1.49x to 1.11x (CPU per request from
1.4x to 1.25x).
Most of Kestrel's small-request HTTP/1.1 advantage in the main table therefore comes
from connection reuse, not per-request processing. The candidate's upload advantage
over Kestrel repeats with equal lifetimes.

## Failures

No sample was retried; each failure keeps its error, client counters and a
machine-wide TCP state census in its JSON file. 14 of 171 samples failed.

- Host TCP exhaustion (10 samples). HTTP/1.1 churn (about 25,000 connections per
  second, server closes first) accumulated 38,000-63,000 server-side TIME_WAIT
  entries; Windows then failed new sockets with WSAENOBUFS. HTTP/2 churn
  (HttpClient closes first) used up the 16,384-port client range. EmbedIO's
  256-connection keep-alive samples failed the same way because its listener closes
  each connection after 100 requests, so 13,000-28,000 connections were opened in a
  sample; Kestrel opened 256. OS network limits were not changed. Churn windows are
  now capped and the orchestrator also waits for machine-wide TIME_WAIT to drain.
- HTTP/1.1 1 MiB upload connect timeouts (3 samples, EmbedIO only: candidate twice,
  main once; Kestrel 0 of 3). A new connection's connect attempt went unanswered for
  about 20 seconds after 850-1,700 reconnects, with about 11,000 TIME_WAIT entries
  and a 500-entry listen backlog. The control run added one more (main, client-forced
  lifetime). Across both runs 4 of 18 EmbedIO upload samples failed and 0 of 9
  Kestrel samples, 6 of them with the same connection lifetime. That count does not
  establish an engine defect (0 of 9 is about 24% likely at the pooled 4-in-27 rate) and the
  cause is not identified. It is inherited from main, not introduced by PR #182.
- One Kestrel HTTP/3 churn sample timed out in the orchestrator after its 15 s
  measurement completed (26,858 requests). The cause was not investigated further.

## Bottlenecks, with evidence

Evidence comes from separate profiling runs, never from the comparison samples
above: in-process runtime events (`--profile`: sampled allocation by type,
exceptions, contention) and dotnet-trace 10.0.745401 thread-time sampling of the
server during the measurement window. `scripts/attribute_trace_frames.py`
attributes a hot frame to its nearest EmbedIO callers. Percentages are shares of
non-idle server thread time.

1. Listener-wide admission lock. `Monitor.Enter_Slowpath` is 16% of HTTP/1.1,
   12% of HTTP/2 and 26% of HTTP/3 server time. Every request on every protocol
   takes `HttpListener._lifecycleSync` in `RegisterContext` or
   `RegisterMultiplexedContext`, and again (twice) in `GetContextAsync`, then
   passes through one `SemaphoreSlim` and a `ConcurrentDictionary` queue before the
   `WebServer` loop dispatches it. Attribution: HTTP/1.1 contention is reached from
   `HttpConnection.OnReadInternal`, where admission calls `RegisterContext` (69%)
   and from `GetContextAsync` (14%); HTTP/2 from `RegisterMultiplexedContext` (14%)
   and `GetContextAsync` (7%). The trace does not name the lock object; the
   per-connection `_connectionSync` is the other lock on that path.
2. HTTP/2 frame write and read copies. Each 16 KiB DATA write allocates a new
   payload array and a one-element `Http2Frame[]` (`Http2Exchange.WriteAsync`),
   then `Http2FrameTransport.WriteAsync` copies it into a pooled buffer and issues
   one stream write per frame under a `SemaphoreSlim`. Each inbound frame allocates
   its payload (`Http2FrameTransport.ReadAsync`), and the array `ReadAsync` overload
   returns a `Task<int>` per read. Result: 1.14-1.18 MB allocated per 1 MiB
   response, upload or stream, about 10% of sampled allocation in `Http2Frame[]`
   for large responses and about 133 KB of `Task<int>` per upload. HTTP/2 also
   contends on the frame write gate (about 20% of its lock time).
3. HTTP/2 upload flow control. The candidate advertises no SETTINGS_INITIAL_WINDOW_SIZE,
   so each stream receives the 65,535-byte default, and unread request bytes are
   capped at 65,535 per stream. 1 MiB uploads run at 922 requests/s with both server
   and client CPU at 33%: the transfer is waiting on window updates, not on CPU.
   Kestrel 10.0.12 defaults to 768 KiB per stream and 1 MiB per connection
   (read from its `Http2Limits`) and reaches
   2,869 requests/s at 95% server CPU.
4. HTTP/3 cancellation on the normal path. About one first-chance
   `TaskCanceledException` per request (374,784 for 375,175 requests). After each
   request, `Http3QuicConnection.ProcessRequestAsync` calls
   `CancelRequests(requestStop)`, which completes the
   `WatchRequestDirectionAsync` watchers by throwing out of
   `completion.WaitAsync(requestStop.Token)`. Exception dispatch and
   `CancellationTokenSource` callbacks appear in the HTTP/3 trace, and
   `CancelDecode` and `WatchRequestDirectionAsync` together account for 15% of its
   lock time.
5. HTTP/1.1 per-request header and context allocation. About 8 KB per small
   request: strings 1.7 KB, `Hashtable` buckets and `ArrayList`s about 1.7 KB, and
   `NameValueCollection`/`WebHeaderCollection` objects, plus `HttpListenerContext`,
   request, response, `RouteMatch` and `Uri` state. In the profiling run, gen 0/1/2
   collections ran at about 100/33/6 per second at 280,000 requests/s; Kestrel
   allocates 32 bytes per request.
6. No response batching across pipelined requests. Each response is a separate
   socket send (24% of pipelined server time in `DoOperationSendSingleBuffer`), so
   pipelining gives 1.3x over ordinary keep-alive where Kestrel gains 9x.
7. Chunked streaming writes. Each flushed 16 KiB chunk issues three socket writes
   (size line, data, CRLF), allocates the size line and an `AsyncWriteGate` scope
   task; send is 45% of streaming server time. CPU per 1 MiB streamed response is
   4.5x Kestrel's.
8. 100-request keep-alive cap. The managed listener closes each connection after
   100 requests. At 300,000 requests/s that is about 3,000 reconnects per second,
   visible as accept, `HttpConnection` construction and shutdown frames in every
   HTTP/1.1 trace, and as the TIME_WAIT exhaustion failures above. Kestrel has no
   such cap. The equal-lifetime control shows the cap accounts for most of
   Kestrel's small-request HTTP/1.1 lead (1.49x falls to 1.11x).
9. TLS handshake cost is on par: per-handshake CPU is within 7% of Kestrel's
   (`AcceptSecurityContext` and certificate chain building dominate both). Small
   HTTPS requests are slower for the reasons above, not because of TLS.

## Prioritized improvements

Each item names the evidence that should move. None was implemented here;
production changes need coordination on PR #182.

1. Replace per-request listener-lock admission with a lock-free or per-connection
   handoff (for example a `Channel` with the drain and stop checks moved off the hot
   path). Expect lower `Monitor.Enter_Slowpath` share and CPU per request on all
   protocols. Re-measure: HTTP/1.1, HTTP/2 and HTTP/3 small-request rows.
2. HTTP/2 DATA: write frames directly from the caller's buffer with a pooled
   header, coalesce frames per write, and read frame payloads into pooled buffers
   with `ValueTask` reads. Target: allocation per 1 MiB response from 1.14 MB to
   kilobytes; CPU per request toward Kestrel's 1.27 ms.
3. HTTP/2 receive window: advertise a larger initial stream and connection window
   (with a bound on unread bytes per connection) and send WINDOW_UPDATE as data is
   consumed. Target: HTTP/2 upload throughput limited by CPU, not window round trips.
4. HTTP/3: complete the request-direction watchers without throwing (for example a
   completion source signalled on normal exit). Target: zero first-chance exceptions
   per request and less lock time in `CancelDecode`.
5. HTTP/1.1 response batching: let pipelined responses, and chunk size line plus
   data plus CRLF, share one write; drop the per-write gate allocation on the
   uncontended path. Target: pipelined and streamed rows.
6. HTTP/1.1 request representation: keep parsed headers in a compact structure and
   materialize `NameValueCollection` only when the application asks for it. Target:
   B/request and GC counts in the HTTP/1.1 rows.
7. Revisit the 100-request keep-alive cap (raise it or make it configurable,
   preserving the drain/close behavior). The control isolates it as the largest
   single factor in small-request HTTP/1.1 throughput against Kestrel. Behavior is
   visible to clients, so it needs owner approval.
8. Investigate the HTTP/1.1 upload connect timeouts with more repetitions and a
   packet capture to establish whether they are engine-specific.

## Limits

All measurements are loopback on one shared Windows host, closed-loop and short (15 s
windows). They say nothing about network latency, packet loss, Linux or macOS
behaviour, open-loop overload or long-run memory growth. The HTTP/2 and HTTP/3 client
is `HttpClient`; in several Kestrel rows client CPU was 85-93%, so Kestrel may be
faster than shown. Profiling used thread-time sampling, which includes runnable time
on contended locks, not hardware CPU counters. The Speedscope attribution is per
nearest EmbedIO caller and does not identify lock objects.
