# Response header allocation

Every response built a `WebHeaderCollection` for the fields the engine writes,
whether or not the application ever read `Headers`: the content type and length,
`Server` and `Date`, the framing and connection fields on HTTP/1.1, `Alt-Svc` in
combined mode, and `Vary` and `Content-Encoding` from content negotiation. The
collection, and on HTTP/2 and HTTP/3 the arrays read back out of it to build the
HPACK or QPACK block, were the largest single allocation of a small request on
every protocol (#549). The engine now keeps those fields in a list and builds the
collection only when something reads `Headers`, with the same fields in the same
order.

Against 2.18.0, a small request allocates 36.5 percent less on HTTP/1.1, 21.3
percent less on HTTP/2 and 16.5 percent less on HTTP/3. Server CPU per request falls
11.7 percent on h2c and 13.3 percent on HTTP/2 over TLS, and 4 to 6 percent on
HTTP/1.1 and HTTP/3. No latency percentile up to p99.9 is significantly slower: p50
falls on every scenario, p99 by 6 to 14 percent on HTTP/2 and HTTP/3, and p99.9 by 21
to 27 percent on HTTP/1.1. Requests slower than 20 ms, rarer than one in 10,000,
became more frequent on HTTP/2 and HTTP/3 under the default Server GC; with its
dynamic heap-count adaptation (DATAS) turned off they are no more frequent than on
2.18.0. See [latency](#latency).

| Scenario | 2.18.0 B/req | Final B/req | Change | 2.18.0 CPU us/req | Final CPU us/req | Requests/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| HTTP/1.1, 13 B, 64 connections | 4,847 | 3,080 | -36.5% | 18.03 | 17.03 | +4.2% |
| HTTP/1.1, 1 KiB JSON, 64 connections | 6,374 | 4,416 | -30.7% | 37.31 | 35.59 | +4.2% |
| h2c, 13 B, 8 x 32 | 9,267 | 7,295 | -21.3% | 8.83 | 7.80 | +12.3% |
| HTTP/2 TLS, 13 B, 8 x 32 | 9,265 | 7,291 | -21.3% | 8.21 | 7.12 | +11.4% |
| HTTP/3, 13 B, 8 x 32 | 11,868 | 9,911 | -16.5% | 14.68 | 14.09 | +4.6% |
| h2c inspect, 8 x 32 | 13,879 | 11,910 | -14.2% | 12.32 | 11.04 | +9.7% |
| HTTP/3 inspect, 8 x 32 | 16,695 | 14,727 | -11.8% | 17.97 | 16.86 | +3.8% |

Medians of the kept samples from both harness orientations (see
[method](#environment-and-method)). Every one of the 49 pairs (same run and round)
allocated less, and 47 used less server CPU per request. The JSON route sends a
1 KiB document compressed with Brotli, as `SendDataAsync` does when the client
accepts it, so its head carries `Vary` and `Content-Encoding`.

## What a request allocated

Sampled allocation by type, scaled to the exact bytes per request of the same sample
(one `--profile` round per engine; small objects are over-represented by the
sampling, so these are estimates):

| Type | HTTP/1.1 2.18.0 | HTTP/1.1 final | h2c 2.18.0 | h2c final |
| --- | ---: | ---: | ---: | ---: |
| `Bucket[]` | 1,025 | 66 | 871 | 93 |
| `object[]` | 483 | 73 | 379 | 92 |
| `Hashtable` | 386 | 88 | 295 | 72 |
| `ArrayList` | 267 | 49 | 279 | 63 |
| `NameObjectEntry` | 176 | 27 | 166 | 39 |
| `NameValueCollection` | 126 | 51 | 149 | 78 |
| `WebHeaderCollection` | 66 | 0 | 101 | 0 |
| `string[]` | 54 | 67 | 203 | 0 |
| `HpackField[]` | n/a | n/a | 399 | 267 |
| `Field[]` (the list) | 0 | 262 | 0 | 124 |
| `ResponseHeaderFields` | 0 | 92 | 0 | 49 |

The collection's hashtable, buckets, entries and key list go; what remains of those
types belongs to the request's own header and query collections. On HTTP/2 the
`string[]` arrays `GetValues` returned for each field go too, and the HPACK field
array is allocated at its exact size instead of filling a list and copying it to an
array.

## Changes

All production changes are in the response classes, the HTTP/1.1 head writer and
content negotiation.

- `ResponseHeaderFields` (internal) holds the fields of a response head in write
  order. `Headers` builds the `WebHeaderCollection` from them on first read,
  published once with `Interlocked.CompareExchange`; from then on the collection
  holds the fields, and every engine read and write goes to it.
- Only a write the collection would store unchanged stays in the list: a token
  name without an apostrophe and a nonempty printable-ASCII value with no space at
  either end. Any other write builds the collection first and goes to it, so its
  validation, trimming and exceptions apply as before.
- A field holds one value as a string, or several as a list, so an entry costs 16
  bytes. Each response sizes its list for the fields the engine can write to an
  ordinary head: nine on HTTP/1.1 (content type, `Server`, `Alt-Svc`, `Date`, the
  framing field, `Connection`, `Keep-Alive`, `Vary` and `Content-Encoding`) and seven
  on HTTP/2 and HTTP/3, which send no framing or connection fields. The list grows
  only for fields beyond those, such as a trailer declaration.
- Content negotiation (`OpenResponseStream`, and through it `SendStringAsync`, the
  JSON serializer and controllers) writes `Vary` and `Content-Encoding` through the
  list when the response is one of the engine's own. Any other `IHttpResponse`
  takes them through `Headers`, as before.
- The HTTP/1.1 head is written from the list. An HTTP/2 or HTTP/3 head goes into an
  exact-size HPACK field array when every field has one value that the
  collection's `GetValues` would return whole; any other head goes through the
  collection, as before.
- The load benchmark selects exactly one scenario for a name ending in `$`, so
  `h1-plain-small-c64$` leaves out the `h1-plain-small-c64-close100` control.

Public APIs, defaults, targets and dependencies are unchanged. Nothing is pooled.
Library helpers that write other fields, such as redirects, static files, CORS,
security headers and output caching, still write through `Headers` and build the
collection, as before.

## Same fields, same order

The list is not an approximation of `WebHeaderCollection`: every answer it gives is
the one the collection would give, on .NET and on .NET Framework, where the .NET
Standard 2.0 asset runs.

- A write stays in the list only when the collection would store it unchanged. The
  collection trims, refuses or rewrites other values, and refuses other names,
  differently on each runtime, so any other write builds the collection first and
  goes to it, exceptions included.
- `Set` keeps a field's position and the case of its first name, `Add` appends a
  value, `Remove` drops the field, and reads return the values joined with commas,
  as the collection's `Get` does.
- A read the list would answer differently, `GetValues` for a field the collection
  splits on commas, goes to the collection.
- The multiplexed fast path takes each field's value whole only when the
  collection's `GetValues` would return it whole: under a name it never splits, or
  a value without a comma under any name but `Set-Cookie`, whose value the
  collection drops unless it holds `=`.

`ResponseHeaderFieldsTest` runs 2,000 seeded sequences of 1 to 39 operations, valid
and invalid, over 27 names and 27 values (control characters, folds, non-ASCII,
commas, quotes, empty strings and null among them), against a list and a
`WebHeaderCollection` side by side. The list starts at sizes from 0 to 9, so it also
grows. After every step the count, names and joined values must match; each
operation must return the same result and throw the same exception type; whenever
the list claims the multiplexed fast path, the collection's `GetValues` must return
each value whole; and the collection the list builds must equal the reference. The
.NET Framework smoke runs the same sequences against that runtime's collection.

`ResponseFieldsMatchWhetherOrNotHeadersWasRead` sends eight response shapes over
HTTP/1.1, HTTP/2 and HTTP/3 from a combined listener: a declared length, chunked,
204, a custom field, cookies, JSON, a string and the string compressed. Each is sent
twice, once by a handler that reads `Headers` first; the two must give the same
status, head and body on the wire and the same `Headers` afterwards. A handler that
never touches `Headers` must leave the collection unbuilt, and no response may grow
its list: the compressed string over HTTP/1.1, with `Alt-Svc`, `Vary`,
`Content-Encoding`, chunked framing and keep-alive, fills all nine places.
`HeadAndHeadersMatchWhetherOrNotHeadersWasRead` checks the same for seven HTTP/1.1
status, framing and keep-alive combinations.

Eleven deliberate defects each made the new tests fail, run against the final source:
a replaced field moving to the end, the collection built in reverse, a second value
replacing the first, a different join separator, the fast path taking a
comma-separated value or a `Set-Cookie` value, the multiplexed head losing its first
field or its cookies, either list starting one place short, and the list growing one
place early, which only the compressed HTTP/1.1 head reaches.

## Latency

Client latency, pooled over both orientations with the host-stall samples left out,
with the two-sided Mann-Whitney U test's p value; the last column counts requests
slower than 20 ms per million, median per sample:

| Scenario | p50 ms | p99 ms | p99.9 ms | Over 20 ms per million |
| --- | --- | --- | --- | --- |
| HTTP/1.1, 13 B | 0.278 -> 0.269 (p < 0.001) | 0.614 -> 0.614 (p 0.73) | 1.45 -> 1.06 (p 0.018) | 1.7 -> 1.5 |
| HTTP/1.1, 1 KiB JSON | 0.586 -> 0.563 (p < 0.001) | 1.030 -> 1.088 (p 0.25) | 3.11 -> 2.46 (p 0.14) | 7.1 -> 3.1 |
| h2c, 13 B | 0.512 -> 0.461 (p < 0.001) | 1.741 -> 1.549 (p < 0.001) | 6.40 -> 6.55 (p 0.31) | 23 -> 78 |
| HTTP/2 TLS, 13 B | 0.470 -> 0.422 (p < 0.001) | 1.702 -> 1.466 (p 0.005) | 6.20 -> 6.53 (p 0.48) | 35 -> 49 |
| HTTP/3, 13 B | 0.921 -> 0.896 (p 0.001) | 2.662 -> 2.509 (p 0.028) | 11.93 -> 12.19 (p 0.72) | 98 -> 123 |
| h2c inspect | 0.669 -> 0.614 (p 0.011) | 2.329 -> 2.253 (p 0.14) | 6.50 -> 7.27 (p 0.46) | 20 -> 19 |
| HTTP/3 inspect | 1.101 -> 1.050 (p 0.037) | 3.379 -> 3.008 (p 0.54) | 13.52 -> 12.24 (p 0.46) | 101 -> 154 |

Paired by run and round, p50 was lower in 48 of 49 pairs, and on HTTP/2 every pair
had a lower p99. p99.9 moves by one or two histogram buckets either way, and the
harness role alone moves it by up to a quarter, so its differences above are noise.

The far tail is another matter. On HTTP/2 over cleartext, 9 of the final build's 11
samples had more requests slower than 20 ms than the median 2.18.0 sample; on TLS and
HTTP/3 the excess is smaller. These requests come in bursts: one stall delays every
request in flight, 256 here, so 78 per million is about two stalls of 20 ms or more
per 15-second window, against one every other window for 2.18.0. It is not more
garbage collection. On h2c the final build ran 153 gen0, 71 gen1 and 0.8 gen2
collections per second against 173, 100 and 1.7, and paused 89 ms per second against
114; on TLS, 140, 64 and 0.5 against 158, 90 and 1.3, pausing 85 against 106 ms per
second.

It is how Server GC's dynamic adaptation to application size (DATAS, on by default
since .NET 9) responds to the lower allocation rate. With it turned off for every
process (`DOTNET_GCDynamicAdaptationMode=0`), three rounds per orientation:

| HTTP/2, DATAS off | Requests/s | CPU us/req | p99 ms | Over 20 ms per million | Over 40 ms per million |
| --- | --- | --- | --- | --- | --- |
| h2c, 2.18.0 -> final | 480,007 -> 534,496 | 8.11 -> 7.10 | 1.427 -> 1.177 | 46 -> 18 | 0 -> 0 |
| HTTP/2 TLS, 2.18.0 -> final | 504,475 -> 565,204 | 7.49 -> 6.68 | 1.389 -> 1.273 | 55 -> 38 | 17 -> 0.1 |

The final build's far tail is then no heavier than 2.18.0's, and both versions serve
12 to 17 percent more requests per second than with DATAS on. The single slowest
request per window on HTTP/2 over TLS stayed higher (median 47.5 against 33.1 ms) but
not significantly (p 0.14). The heap-count changes themselves were not traced.
Applications that care more about the far tail than about Server GC's memory
footprint can turn DATAS off with `System.GC.DynamicAdaptationMode` set to 0 in their
runtime configuration.

## Remaining contributors

Kestrel from the ASP.NET Core 10.0.13 shared framework, on the same scenarios and CPU
sets (two rounds each):

| Scenario | Final B/req | Kestrel B/req | Final CPU us/req | Kestrel CPU us/req | Final GC pause ms/s | Kestrel GC pause ms/s | Work items per request | Lock contentions per 100,000 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| HTTP/1.1, 13 B | 3,079 | 32 | 17.05 | 15.26 | 28.5 | 0.8 | 1.99 / 2.06 | 0.3 / 0.1 |
| HTTP/1.1, 1 KiB JSON | 4,414 | 1,257 | 35.35 | 22.95 | 21.7 | 20.3 | 1.99 / 2.06 | 0.0 / 0.4 |
| h2c, 13 B | 7,309 | 32 | 7.77 | 2.94 | 89.0 | 5.4 | 3.07 / 1.27 | 16.5 / 1.7 |
| HTTP/2 TLS, 13 B | 7,298 | 39 | 7.02 | 4.21 | 84.5 | 5.2 | 3.09 / 1.25 | 22.9 / 2.4 |
| HTTP/3, 13 B | 10,016 | 2,610 | 13.79 | 15.85 | 55.3 | 23.5 | 6.15 / 5.62 | 55.9 / 88.3 |

The last two columns give the final build first. Kestrel's HTTP/3 runs its MsQuic
threads outside the server's CPU set, so its CPU per request is not a same-CPU
comparison. On HTTP/2 the remaining gap has three measured parts: about 7.3 KB
allocated per request, which costs about 9 percent of wall time in GC pauses against
0.5 percent for Kestrel; 3.1 thread-pool work items per request against 1.3; and
about ten times Kestrel's lock contention.

What a small request still allocates, from the same profile (estimates, as above):

- HTTP/1.1, 3.1 KB: strings (about 0.9 KB), the request, context and response
  objects, the field list, the request's header collection, the route match, `Uri`
  parsing and the response stream. The profile records no async state machine on
  this route: its awaits complete without suspending.
- h2c, 7.3 KB: an async state machine for each layer a request passes through
  (`WebServerBase.HandleContextCoreAsync`, the HTTP/2 dispatch, `SendHeadersAndWrite`,
  the module collection, `SendDataAsync`, `MultiplexedResponse.WriteAsync` and more),
  about 1.6 KB, plus 0.3 KB for the benchmark handler's own; tasks, about 0.55 KB;
  cancellation registrations (`CallbackNode`), about 0.3 KB; strings, about 0.5 KB;
  the request and response HPACK arrays; the context, request and response objects;
  and `Uri` parsing.
- HTTP/3, 10.0 KB: the same application model, with about 2.3 KB of engine state
  machines, plus `byte[]` buffers (about 0.7 KB) and the QUIC stream object.

## Validation

- Discovery reports 7,515 cases, the 7,504 before this change plus the 11 new ones;
  the CI floor and `CONTRIBUTING.md` say 7,515.
- Full Windows suite on the branch merged with `main` (`71ae5271`): 7,515 cases,
  7,510 passed, 4 skipped for the platform, 1 failed.
  `ListenerBoundaryRegressionTest.AcceptBurstsAndResetsAllowShutdownAndRestart(True,True)`
  could not bind an outgoing client port (`NoBufferSpaceAvailable`, WSAENOBUFS)
  partway through the suite; its class's 62 cases then passed three runs in a row.
- After the last formatting pass, which changed layout and two expressions with the
  same results: the solution builds with no warnings, and 901 cases selected by the
  response, cookie, compression, negotiation and HTTP/2 filters pass, the 11 new ones
  among them, with one platform skip.
- The .NET Framework smoke passes 2,569 assertions on CLR 4.0.30319.42000 (Framework
  release 534296) with the .NET Standard 2.0 asset, including the 2,000-sequence
  comparison and three HTTP/1 response shapes.
- The source-quality gate (prettyCSharp 1.1.10 on 586 files and every analyzer at
  hidden severity), both suppression guards, the whitespace checks for the solution,
  `test` and `tools`, and `git diff --check` pass.

## Environment and method

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET 10.0.13; Server GC, concurrent, the same for every engine |
| 2.18.0 | `168d4752` (v2.18.0), core SHA-256 `8163A292...D21F` |
| Final | `8dcf7150` plus this change, core SHA-256 `AA26BE18...77BC` |
| Runner | one harness build, SHA-256 `DFDF0A5D...2E54`; the 2.18.0 copy differs only in `EmbedIO.dll` |
| Kestrel | ASP.NET Core 10.0.13 shared framework |
| CPU sets | server logical CPUs 0-3, client 4-11, set at process creation |
| Schedule | `--modern-baseline`; 5 s warmup, 15 s measurement, 2 s idle; fresh server and client processes per sample; engine order alternating by round |

The harness gives its two engines different roles, and the role alone moves tail
latency by several percent. Every comparison therefore ran twice, once launched
from the final build's runner (final core in the candidate role) and once from the
2.18.0 runner with the roles swapped, and samples are classified by the SHA-256 of
the `EmbedIO.dll` their server loaded. Each orientation ran six rounds of the five
small scenarios and three of the inspect scenarios.

The machine was shared with other agents' sessions, which were idle or light. Each
run took the shared benchmark lock after 60 seconds with no foreign test or load
process, and a guard watched for foreign load throughout. Samples whose background
CPU (machine busy time minus server and client) exceeded the larger of 1.5 times
and 4 seconds above the scenario's median were left out: 24 of 144. Two more failed
in their client with the Windows socket error `NoBufferSpaceAvailable` (WSAENOBUFS)
on an HTTP/1.1 connection; the same error hit a 2.18.0 sample in an earlier run of
this investigation, so it is the client host's, not the engine's. Each response was
validated, and no other sample failed.

Two host events on this machine coincided with single stalls of 400 to 900 ms in
samples of both cores: a Kernel-Power session transition at 21:51:19Z and Software
Protection service activity from 21:56:25Z to 21:56:55Z. Those samples count in the
first table, where leaving them out moves no ratio by more than two points; the
[latency](#latency) comparison leaves them out.

The DATAS comparison ran three rounds per orientation of the two HTTP/2 scenarios
with `DOTNET_GCDynamicAdaptationMode=0` set for every process, recorded in each run's
environment, on the same runners and CPU sets.

## Limits

- Loopback on one shared Windows host with `HttpClient` as the client; no Linux or
  macOS measurement. Closed-loop load; overload is not characterized.
- The final build was measured from `8dcf7150` with this change uncommitted. Later
  edits corrected a code comment and applied the pinned formatter, which changed
  layout and rewrote two expressions to equivalent ones. Merging main before the pull
  request adds TLS session resumption, which changes HTTP/3 handshakes but not the
  per-request path measured here.
- `--profile` amounts are sampled and biased toward small objects; totals are exact.
- p99.9 and the maximum are histogram-bucketed and come from one 15-second window
  each; differences of one or two buckets are within noise.
- Core hashes depend on the build directory and the embedded commit; they identify
  what ran, not a reproducible build.

Evidence (samples, logs, guard journals, profiles and the scoring scripts' output)
is kept under the ignored `TestResults/load-benchmark` and `TestResults/issue-549`
directories of the working tree that produced it.
