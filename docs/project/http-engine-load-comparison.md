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

## Post-integration small-request checkpoint

This checkpoint measures frozen candidate `ae80537fa6c5b3d85235f4171e619f29374aacfc`
against baseline `1445c238afea7d5a237088e8f19c7a4e84eb43f1` and Kestrel
10.0.12. The candidate production source is byte-identical to engine integration
`e52f829` and `6ca91d4`; it predates PR #210's higher-minor HTTP/1 processing.
It is not a measurement of every subsequent engine head.

The Windows host, CPU partition and runtime match the earlier campaign. Each
sample used fresh server/client processes, five seconds of warmup and fifteen
seconds of measurement, with two alternating rounds. The baseline loads the
same harness with only the core assembly replaced. Responses are byte-validated.
No failures were retried and no OS socket limits were changed.

Artifact root: `TestResults/load-candidate-ae80537`. Preparation manifests record
runner SHA-256 `4bab11108325ff746253affbcc6ddfd0074e3ca949e323bf16db2b98b2b9c7b7`,
candidate core `025e7e48d36d711c6183b880c699492f38d63d3345fe38f5dc95502e8cb8e915`,
and baseline core `a7eb20216af2ff7efbacb9c38537902340da62921791b626dc4d7343b40c17fc`.
Raw samples, environment, full histograms and summaries are retained under
`results-small-protocols` in the owned profiling worktree.

There were **36 samples, 35 valid and one failed**. Kestrel's second close-after-100
sample failed with a client SocketException reporting insufficient socket buffer
or queue space. The exact cause is not established. Its remaining single sample
must not be treated as a two-round comparison. All recorded post-run open server
socket counts were zero. These short loopback runs do not establish soak behavior
or a universal performance ranking.
| Scenario | Engine | Valid/total | Requests/s median (min-max) | p50 ms | p99 ms | Server CPU us/req | Server B/req | Server CPU util | Client CPU util | Open server sockets after (max) | Handle growth (max) | Retained heap KB (max) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| h1-plain-small-c64 | candidate | 2/2 | 383,201 (382,549-383,853) | 0.113 | 0.947 | 20.2 | 7,914 | 97 % | 89 % | 0 | 7 | 95 |
| h1-plain-small-c64 | baseline | 2/2 | 325,637 (323,970-327,303) | 0.117 | 0.979 | 22.9 | 10,331 | 93 % | 85 % | 0 | 28 | 99 |
| h1-plain-small-c64 | kestrel | 2/2 | 478,678 (478,540-478,816) | 0.128 | 0.301 | 16.1 | 32 | 96 % | 94 % | 0 | 9 | 118 |
| h1-plain-pipe16-c16 | candidate | 2/2 | 479,717 (479,083-480,350) | 0.277 | 0.778 | 16.2 | 7,550 | 97 % | 71 % | 0 | 7 | 91 |
| h1-plain-pipe16-c16 | baseline | 2/2 | 419,567 (419,554-419,579) | 0.285 | 0.896 | 17.9 | 10,360 | 94 % | 71 % | 0 | 14 | 863 |
| h1-plain-pipe16-c16 | kestrel | 2/2 | 4,174,176 (4,146,251-4,202,100) | 0.056 | 0.161 | 1.8 | 32 | 95 % | 88 % | 0 | 10 | 119 |
| h1-plain-small-c64-close100 | candidate | 2/2 | 336,128 (335,929-336,326) | 0.117 | 1.082 | 22.1 | 8,011 | 93 % | 84 % | 0 | 6 | 115 |
| h1-plain-small-c64-close100 | baseline | 2/2 | 324,710 (321,677-327,744) | 0.116 | 1.011 | 22.7 | 10,377 | 92 % | 82 % | 0 | 22 | 111 |
| h1-plain-small-c64-close100 | kestrel | 1/2 | 400,278 (400,278-400,278) | 0.138 | 0.454 | 17.6 | 126 | 88 % | 88 % | 0 | 9 | 190 |
| h1-tls-small-c64 | candidate | 2/2 | 363,864 (362,261-365,467) | 0.149 | 0.864 | 21.1 | 8,045 | 96 % | 90 % | 0 | 16 | 108 |
| h1-tls-small-c64 | baseline | 2/2 | 165,650 (157,921-173,378) | 0.288 | 1.318 | 34.9 | 10,601 | 72 % | 56 % | 0 | 15 | 122 |
| h1-tls-small-c64 | kestrel | 2/2 | 440,186 (439,885-440,487) | 0.135 | 0.490 | 17.4 | 32 | 96 % | 94 % | 0 | 5 | 227 |
| h2-plain-small-c8x32 | candidate | 2/2 | 301,603 (301,217-301,988) | 0.627 | 3.635 | 25.6 | 13,508 | 97 % | 78 % | 0 | 4 | 110 |
| h2-plain-small-c8x32 | kestrel | 2/2 | 1,707,514 (1,674,543-1,740,484) | 0.136 | 0.522 | 3.9 | 32 | 84 % | 88 % | 0 | 9 | 118 |
| h2-tls-small-c8x32 | candidate | 2/2 | 245,299 (239,591-251,007) | 0.736 | 5.478 | 30.1 | 14,102 | 92 % | 74 % | 0 | 4 | 179 |
| h2-tls-small-c8x32 | kestrel | 2/2 | 1,350,466 (1,333,238-1,367,693) | 0.165 | 0.701 | 5.4 | 42 | 90 % | 91 % | 0 | 4 | 224 |
| h3-small-c8x32 | candidate | 2/2 | 165,032 (156,120-173,944) | 0.947 | 15.155 | 42.4 | 16,341 | 87 % | 55 % | 0 | 31 | 123 |
| h3-small-c8x32 | kestrel | 2/2 | 307,163 (288,918-325,408) | 0.560 | 10.547 | 21.1 | 2,536 | 81 % | 68 % | 0 | 37 | 266 |

The candidate improves on the pinned baseline in these rows, but the extreme
performance objective remains unmet. Kestrel is approximately 8.7 times faster
on pipelining, 5.7 times on plaintext HTTP/2, 5.5 times on TLS HTTP/2 and 1.9 times
on small HTTP/3 responses. Allocation remains about 7.5-8.0 KB per small HTTP/1
request and 13.5-14.1 KB per small HTTP/2 request.

A separate one-round profiling campaign (`profile-pipe-h2`) ran candidate and
Kestrel for plaintext pipelining and HTTP/2: four valid samples, zero failures.
It preserves nettrace files, inclusive/exclusive reports and candidate Speedscope
exports. Candidate throughput was 449,472 pipelined requests/s and 282,702 HTTP/2
requests/s; Kestrel achieved 4,119,626 and 1,587,553 respectively. Profiling samples
are separate from the sustained medians above.

Socket sends, thread-pool continuations and Monitor contention appear prominently.
Caller attribution identifies HTTP/1 connection read/completion and AsyncWriteGate,
and HTTP/2 application dispatch and frame transport, among lock callers. The
reports contain sleeping threads, and the attribution tool's idle classification
is heuristic: its percentages are sampled thread time, not a measured fraction
of process CPU. This evidence does not justify assigning all contention to the
listener admission lock or claim that one small change will remove the gap.
The next optimization requires focused attribution and before/after measurements
while preserving framing, cancellation, explicit flush and graceful drain.

## HTTP/1 chunk-write prototype

The bounded chunk-write prototype serializes the size line, payload slice and
trailing CRLF into one awaited transport write for subsequent asynchronous chunks
up to 64 KiB. The first response head and larger chunks retain the existing path.
It does not defer writes across application calls or change HTTP framing, public
APIs, defaults, target frameworks or production dependencies. The local pooled
buffer is returned and cleared on success, cancellation and transport failure.

A preliminary two-round, fresh-process comparison used the identical frozen
load harness and swapped only the core DLL. The candidate was base `4c531fe`
plus the preserved ResponseStream patch (SHA-256
`C7739392449C0DF1797081E5B4BBAA33E8EC74D1135DAB61E57AED62DC5A6226`);
the control was the frozen `ae80537` assembly from the checkpoint above.
Both rounds byte-validated 1 MiB responses flushed every 16 KiB over 16 connections.
All four samples completed without failures or retained open server sockets.
Artifacts: `TestResults/chunk-batch-load/streaming-comparison`, with source patch,
binary hashes, environment and per-sample JSON retained in the owned worktree.

Candidate server CPU was 913 and 943 us/response; control was 2,189 and 2,203.
Candidate throughput was 8,132 and 7,370 responses/s; control was 1,549 and 2,916.
Allocation stayed approximately 31.6 KB/response on both. Competing host activity
was substantial in the control windows, so these throughput numbers do not prove
a clean speedup ratio. Further isolated comparisons and platform validation remain
necessary; these measurements are not a release or general performance claim.

The separate write-gate completed-task experiment reduced streaming allocation
from approximately 31.6 KB to 27.1 KB/response but showed inconsistent throughput
and no small-response allocation improvement. It is preserved as a development
patch and is not included in the chunk-write prototype.

Independent HTTP/1 validation of pushed source `ab0301d61b79a05afac9c1d1d740d78b879504f6`
ran in the pinned Linux container with four CPUs and a 6 GiB memory limit, SDK
10.0.401 and runtime 10.0.12. The loaded core SHA-256 was
`82eba815932076736331df1d073f955a90884afbeb4063e5efd38ba10b69ac61`.
The independent model reported 45 conforms, 12 permitted policy choices and no
violations/errors. Seed 20261009 passed 2,000 stateful iterations: 5,029 valid
requests, 473 invalid requests and 211 aborts. Handlers drained to zero; handles
grew by two and managed memory by 2,097,832 bytes. This was the tooling's `self`
mode, so client and server share a process; retained-memory figures cannot be
assigned entirely to the engine. This short campaign is not soak evidence.
Artifacts are under `TestResults/chunk-batch-conformance-linux`.

The full local Windows suite discovered 4,475 cases: 4,469 passed, five expected
skips and one failure in unchanged permanent-ban file replacement. That failure
also reproduces with the frozen pre-change engine. Its Windows HRESULT is
`0x80070497`; the underlying cause remains unconfirmed. No production persistence
change, assertion weakening or quarantine is part of this prototype.

## HTTP/1 unused collection allocation checkpoint

The candidate defers creation of empty query collections and unused context item
containers. Nonempty queries are still decoded before admission. First access
publishes one stable mutable collection; concurrent readers receive the same
instance, and retained references remain request-local. No public signature,
query interpretation, default, target or production dependency changes.

The comparison used base `0be7629` plus the preserved two-file production patch,
core SHA-256 `80461A69BB8866DD081FD237C8310A580BC43A3B2C9CD7D6945484D72D1F7BE8`.
The control is the frozen `ae80537` core used above. Both load the identical
harness, with only EmbedIO.dll swapped. New strict-limit and chunk behavior is
inactive for this fixed-length, valid HTTP/1.1 pipeline workload. Two alternating
rounds used fresh processes, five-second warmup, fifteen-second measurement and
disjoint server/client CPU sets. All four samples byte-validated every response
and completed without failures; no samples were retried.

| Metric, pipelined x16 over 16 connections | Candidate | Frozen control |
| --- | --- | --- |
| Median requests/s | 475,348 | 472,172 |
| Server CPU us/request | 16.3 | 16.4 |
| Allocation B/request | 7,203 | 7,548 |
| p99 ms | 0.774 | 0.778 |
| Maximum post-run open server sockets | 0 | 0 |
| Maximum retained heap KiB | 92 | 107 |

Allocation decreases about 4.6%; throughput and CPU are within noise. This is an
allocation improvement, not a solution to the roughly ninefold pipelining gap.
Short loopback samples do not prove soak or every query-heavy workload. Artifacts,
source patch and binary hashes are under `TestResults/context-allocation-load`.

The first local full suite discovered 4,495 cases: 4,488 passed, five expected
skips and two failures with Windows socket error 10055. One failed in the fixture's
bare free-port probe before constructing EmbedIO; the other failed at WebSocket
connection establishment. All 37 affected-family cases passed afterward. A
standalone .NET 10.0.12 control loading no EmbedIO assembly reproduced the error:
one failure in 18,000 TCP bind/close operations, immediately after port 65533,
followed by allocation at port 49252. This is evidence of a native/runtime port
allocation exposure on this host, not an established OS root cause or a product
fix. A preliminary 512-bind control had no failures. No OS limits, retries inside
fixtures, assertions or quarantine rules were changed. Source and logs are
retained under `TestResults/context-allocation-socket-probe`; original suite/TRX
and affected-family results remain beside the allocation campaign.
A subsequent unchanged-source full-suite confirmation passes all 4,495 cases (4,490 passed/five expected skips). This does not repair or erase the original native allocator failures.

## HTTP/1 first-body buffer reservation experiment

The response-header MemoryStream previously allocated exactly the header size,
then grew when ResponseStream appended the bounded first-body prefix. The
candidate reserves that prefix, including a chunk-size line when applicable,
after deciding response framing. It preserves the 16 KiB first-write boundary,
large-header behavior, wire bytes, immediate writes and cancellation. No public
API, default, target or dependency changes are involved.

Measured source is `00102bdfda311170d7bed8c132ddee9eacbe4638` plus the recorded
production patch. Frozen runner SHA-256 is
`4bab11108325ff746253affbcc6ddfd0074e3ca949e323bf16db2b98b2b9c7b7`.
Candidate core SHA-256 is
`04532e6dac74055b721d7d1de6303b3175394f1f03e784b3a55d7185d8e39233`;
control is the prior collection candidate,
`80461a69bb8866dd081fd237c8310a580bc43a3b2c9cd7d6945484d72d1f7be8`.
Only the core DLL is swapped. Windows .NET 10.0.12 / Ryzen 9800X3D,
fresh processes, alternating order, disjoint CPU sets 0-7 / 8-15,
5 s warmup and 15 s measurement. Each campaign has four valid samples,
no failures/retries and zero open server sockets after settlement.

| Campaign / sample | Requests/s | p99 ms | CPU us/request | Bytes/request |
| --- | --- | --- | --- | --- |
| Initial candidate r1 | 284371 | 5.018 | 15.8 | 5726 |
| Initial control r1 | 458400 | 0.960 | 16.5 | 7216 |
| Initial control r2 | 476334 | 0.819 | 16.3 | 7201 |
| Initial candidate r2 | 473485 | 0.909 | 16.2 | 6737 |
| Follow-up candidate r1 | 313109 | 3.994 | 17.2 | 6788 |
| Follow-up control r1 | 324569 | 3.737 | 16.2 | 5831 |
| Follow-up control r2 | 475243 | 0.761 | 16.4 | 7202 |
| Follow-up candidate r2 | 479424 | 0.755 | 16.3 | 6732 |

The initial slow candidate window recorded 124.109 CPU seconds outside the
server/client, versus 11.094-22.859 in the other initial windows. Both first
follow-up windows also recorded substantial competing CPU. This supports host
competition as a contributor; it does not prove sole causation. All samples
remain evidence, including the poor tails and the follow-up allocation reversal.
Do not infer an overall throughput gain or a universal allocation reduction.

An isolated serializer comparison on the same candidate calls the real header
writer with either no reservation or a 13-byte first-body hint, then appends and
validates identical body bytes. Header-only budget rows are unchanged.

| X-Text padding | Growth B/operation | Reserved B/operation |
| --- | --- | --- |
| 0 | 424 | 160 |
| 1024 | 3352 | 1184 |
| 16384 | 49432 | 49432 |

The last row intentionally has no reserved body prefix because its headers exceed
the existing first-write bound; the direct harness then appends bytes beyond that
bound to check growth. It is not a production transport measurement. These rows
prove a local allocation saving for fitting headers, not server throughput.
The listener-specific `--verify-listener-allocations` gate passes. An earlier
command used the general verification flag and produced measurements without
enforcing this gate; its log is retained separately.

Validation: 115 focused cases, both target builds, parser/suppression guards and
changed-source formatting. Corrected full Windows suite: 4495 total,
4490 passed, five expected skips, zero failures. The initial full run had eight
TargetParameterCountException failures in private header-writer reflection calls;
the callers were updated to supply the new internal argument without changing
wire assertions. That failed run remains recorded. Independent campaign and
exact-final-head hosted checks are still required before integration.

Artifacts remain under ignored `TestResults/response-prefix-load`,
`response-prefix-isolated-allocations.log`, and `response-prefix-corrected-full`.
## Rejected request-dispatch scheduling experiment

A prototype replaced the per-request Task.Run wrapper with a cached normal
ThreadPool callback and one request state. Six preservation cases exercised
ambient-state isolation across awaits and independent acceptance under synchronous
and suspended handlers on both backends. The 45-case focused set and both target
builds passed. This was an experiment, not an established fix.

The source was PR216 head `4aca4b5c2eabb99c58bfdeaf13df331bf9310472` plus the
recorded WebServer patch. Candidate core SHA-256
`6e62594a8e2dc558bf028a5f2e36b5eb302d2dd0d127ec65547abb23c17c6522`;
control capacity candidate
`04532e6dac74055b721d7d1de6303b3175394f1f03e784b3a55d7185d8e39233`.
The first frozen-harness run returned four paired pipeline samples and two
candidate-only HTTP/2 samples: its historical-main baseline filter excludes
HTTP/2. Those two samples cannot demonstrate a before/after effect.

An explicit test-only `--modern-baseline` flag now permits paired comparisons
against a recorded modern engine revision; its value is captured in environment
metadata. It leaves historical-main filtering as the default and retains all byte,
error, resource and process-exit checks. The corrected comparison uses identical
new runner copies, swapping only the core DLL. Exact runner hashes and complete
source patches are in ignored `TestResults/dispatch-scheduling-load`.

The paired HTTP/2 small-response run, Windows .NET10.0.12 / Ryzen9800X3D,
disjoint CPU sets, fresh processes, two alternating rounds and 5s warmup /15s
measurement, produced four valid samples with no failures/retries and zero open
server sockets after settlement:

| Engine | Requests/s median (range) | CPU us/request | Bytes/request | p99 ms median |
| --- | --- | --- | --- | --- |
| Scheduling prototype | 231964 (216216-247712) | 27.4 | 13272 | 4.480 |
| Capacity control | 233639 (217829-249448) | 27.3 | 13267 | 6.886 |

These data do not establish a worthwhile throughput, CPU or allocation gain.
Pipeline samples showed lower allocation but mixed paired throughput and substantial
host competition. Tail differences from two rounds do not establish a general
latency gain. The prototype production code and new tests were preserved under
ignored evidence and restored out of the working source. No default, API,
dependency or discovery floor changed. The broader performance target remains
unmet; connection write serialization/batching needs further investigation.
## Bounded shared HTTP/2 output experiment

A connection-owned writer groups already queued complete operations into at most
64 KiB and 16 operations per transport write. It adds no timer or delay to collect
work. An uncontended write uses a leading path without allocating queue state.
Header-block frames stay adjacent, queued request cancellation removes the pending
operation before its borrowed payload can be read, and committed operations use
connection cancellation. Each caller still awaits its transport completion.
SETTINGS adjustment and ACK ordering remain part of the wire commitment; revoked
DATA reservations are returned through the existing flow controller. A legal
operation exceeding the coalescing bound retains the previous per-frame path.

The initial always-queued prototype passed controlled semantics but regressed
small responses. Its evidence remains separate. The refinement adds the leading
path and hands pending writers to a cached normal thread-pool callback, so a
finished leader need not wait for siblings. No public API, default, target or
production dependency changes are involved.

Measured base is `e435e334f75a2663f3b397143035fddc11f2d71b` plus the recorded
transport patch and writer source. Both rounds use identical frozen runner
SHA-256 `e2c96d34444d8630b24b93d1a0fc2288b887897bc69db51827997080098b5a15`.
Initial core: `22c35d1b93b010f5c9d31d19e91e8bc9c8f4f23558278d6c2ca3de509ac8c865`.
Refined core: `a66ec4c5db6603d11160d3802d6819130377fd86d559384b1c73cf80822eb9ee`.
Control: `04532e6dac74055b721d7d1de6303b3175394f1f03e784b3a55d7185d8e39233`,
the pre-batching capacity candidate. Only the core DLL is swapped; the explicit
modern-baseline capability is recorded. Source/binary hashes are preserved with
all samples. The later immutable validation snapshot is `3795c5a`, after
formatting and integration reconciliation; measured hashes do not claim that
snapshot's metadata-bearing binary was benchmarked.

Windows .NET 10.0.12 / Ryzen 9800X3D, loopback, fresh processes, disjoint CPU sets
0-7 / 8-15, two alternating rounds, 5 s warmup / 15 s measurement. Each campaign
produced eight valid samples, zero failures/retries and zero open server sockets
after settlement. Values below are medians of two samples per row.

| Version/workload | Candidate/control requests/s | CPU us/request | B/request | p99 ms |
| --- | --- | --- | --- | --- |
| Initial, small h2c, 8 connections x 32 streams | 293096 / 306480 | 26.1 / 25.4 | 13704 / 13406 | 4.096 / 3.712 |
| Initial, TLS 1 MiB, 4 connections x 4 streams | 5457 / 4278 | 1326.6 / 1682.9 | 89037 / 96055 | 4.761 / 6.042 |
| Refined, small h2c, 8 connections x 32 streams | 304679 / 300139 | 25.1 / 25.6 | 12922 / 13426 | 3.865 / 3.917 |
| Refined, TLS 1 MiB, 4 connections x 4 streams | 5833 / 4296 | 1275.9 / 1675.4 | 92839 / 96694 | 4.122 / 6.605 |

The refined transfer workload shows about 36% more throughput and 24% less CPU
per request. Small throughput remains roughly flat, with about 3.8% less
allocation. This is a short loopback comparison with two rounds, not a general
ranking or sustained-load acceptance. No initial sample is discarded. Refined
retained managed-heap maxima are 87/87 KiB for small and 107/124 KiB for large
(candidate/control); short settlement does not prove a long-term resource bound.
The much larger small-response gap against Kestrel remains open.

Validation: eight new controlled cases exercise operation/byte bounds, transport
completion, canceled queued payload reuse, header adjacency and shared partial-write
failure. Four batching-count cases fail on the frozen control; the other four
preservation cases pass. The combined HTTP/2/admission set passes 437 cases.
Both targets, parser/suppression guards and formatting pass. Full Windows suite:
4505 total, 4500 passed, five expected skips, zero failures. The retained Windows
Framework smoke passes 531 assertions loading .NET Standard, registry release
533509 / CLR 4.0.30319.42000; this does not prove every legacy/TLS/platform case.
Independent peer campaigns and final hosted checks are still required before
integration. Discovery guards increase by eight to 4505.

Artifacts: ignored `TestResults/http2-batch-load`, `http2-batch-fast-load`,
`http2-batch-fast-full`, `http2-batch-legacy.json` and `http2-batch-peer-campaign`.
### Completed independent and broader candidate checks

The pinned Linux campaign on snapshot `3795c5a` completed successfully. Cleartext
and TLS each passed 16 requirement cases and recorded six permitted policy choices,
including 300 reset-during-response trials, 40 shrinking-window trials and 60
paired SETTINGS ordering trials per transport. Hyper-h2 4.4.1 / Python 3.12.3,
SDK 10.0.401 / runtime 10.0.12, four CPUs and 6 GiB. Every phase loaded core
SHA-256 `628136c7c5f1d29d411bf7ba89ce2e1701f3dd3f9494002551bd7109742c2875`.
Later documentation commits do not change the production source.

| Stateful seed 20261009 | Connections | Streams | Client/server resets | Settled handles growth | Managed growth bytes |
| --- | --- | --- | --- | --- | --- |
| Cleartext | 300 | 1235 | 114 / 0 | 13 | 1990488 |
| TLS | 300 | 1269 | 191 / 0 | 14 | 2597128 |

Both runs settle active handlers and pending thread-pool work at zero. These
bounded campaigns are not a long soak or an engine-only memory attribution.
A separate held-output probe canceled 5000 queued 1 KiB inputs. After 50 ms
callback settlement and collection, all five rounds had zero reachable input
owners and zero queued operations; two healthy controls subsequently emitted
34 bytes. An initial immediate-GC probe saw one reachable input with an empty
queue. Its failed record is retained; the transient root was not traced.

The broader Windows comparison used the same refined/control hashes, frozen
runner and process/CPU/sample method above. Twelve valid samples, no failures or
retries, zero open server sockets after settlement:

| TLS workload | Candidate/control requests/s | CPU us/request | B/request | p99 ms |
| --- | --- | --- | --- | --- |
| Small, 8 connections x 32 streams | 259509 / 248583 | 28.4 / 30.3 | 13202 / 14214 | 5.350 / 5.222 |
| 1 MiB upload, 4 connections x 4 streams | 973 / 868 | 2843.1 / 2947.4 | 136745 / 136563 | 49.152 / 58.163 |
| 1 MiB stream, flushed every 16 KiB, 4 x 4 | 5612 / 4202 | 1315.2 / 1759.2 | 122688 / 132695 | 4.608 / 5.350 |

Upload allocation is effectively unchanged, and small TLS p99 varies by round
(one improves, one worsens), so no general tail improvement is claimed. Both
upload and streaming throughput improve in these paired samples. Upload CPU
utilization remains low and its throughput limitation is still actionable.
Retained managed-heap maxima candidate/control: 194/151 KiB small, 71/95 KiB
upload and 183/161 KiB streaming. Preserve those increases; short settlement
does not prove a universal retained-memory improvement.

Additional artifacts: ignored `TestResults/http2-batch-broader-load` and
`http2-batch-resource-probe`, including the first failed diagnostic. Final
repository checks must be green on the head containing this report before merge.
Broader protocol/platform/resource/performance acceptance for program #181 remains
incomplete after this incremental change.