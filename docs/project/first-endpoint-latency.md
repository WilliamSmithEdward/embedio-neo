# First-endpoint latency

This first latency pass compares the 2.2.0 source at
`0dbf5eec4226942596a55062dbf8adb6331d72c6` with a focused literal-route
change. It removes regex construction and first-match compilation from
case-sensitive routes without parameters. Parameterized and explicitly
case-insensitive routes retain their regex path. No response cache, application
handler warmup, public API, dependency, default or target changes are involved.

## Measurement

Windows 10.0.26300, .NET 10.0.13, x64, Ryzen 7 9800X3D (8 cores / 16 threads),
Release build, default tiered compilation. The same runner binary is used with
only `EmbedIO.dll` replaced. A/B then B/A blocks each run seven rounds: 14
fresh server processes per scenario/phase/variant, 280 measured server processes
and four disposable client-warmup servers. All responses passed status, protocol
and complete-body validation; no errors, retries or discarded measured samples.

The parent reserves a loopback port, releases it and launches the child. A bind
race is possible and fails the run rather than retrying. The child reports
readiness on stdout without an HTTP probe or initializing JSON serialization.
The parent measures the first request immediately, or sends 20 requests to a
different control endpoint before first using the target. Each phase then records
100 requests to that target. Every sample uses its own server. The literal-100
case has 99 preceding target routes plus a separate control route.

## Results

Medians in milliseconds; 14 samples in every cell. First server request includes
connection setup and the entire server request pipeline. First endpoint use
follows the distinct control endpoint on the established HTTP/1.1 connection.

| Scenario | First server request, before | After | First endpoint use, before | After |
|---|---:|---:|---:|---:|
| literal | 33.806 | 33.262 | 0.806 | 0.318 |
| literal-100 | 39.926 | 33.617 | 7.322 | 0.359 |
| parameter | 34.623 | 34.515 | 1.383 | 1.419 |
| controller | 33.686 | 33.057 | 0.723 | 0.244 |
| json | 46.206 | 46.141 | 13.111 | 12.576 |

The largest gain is first use of the last of 100 literal routes: 7.32 to 0.36 ms
(95% lower). Its first server request falls from 39.93 to 33.62 ms (16% lower).
The parameterized control is 1.38 to 1.42 ms after control warmup; there is no
claimed parameterized-route improvement. JSON remains approximately 13 ms on
first endpoint use and 46 ms on the first server request.

First-endpoint complete-response ranges, including every sample:

| Scenario | Before min–max ms | After min–max ms |
|---|---:|---:|
| literal | 0.784–0.898 | 0.287–0.380 |
| literal-100 | 6.996–7.898 | 0.312–0.460 |
| parameter | 1.344–1.486 | 1.375–1.548 |
| controller | 0.712–0.780 | 0.206–0.283 |
| json | 12.907–13.926 | 12.163–13.029 |

The warmed literal matcher microbenchmark (`--verify-allocations`,
`DOTNET_TieredCompilation=0`) falls from 47.1 ns / 320 B to 6.9 ns / 48 B per
match. Parameter matching remains 848 B per match (222.9 versus 214.5 ns).
All existing hot-path allocation ceilings passed on both variants. These are
matcher measurements, not end-to-end throughput claims.

The short HTTP warm windows do not establish a steady-state latency improvement:
candidate medians are around 48–70 microseconds and some are higher than the
baseline. With only 100 requests, the windows include scheduling, tiered JIT and
normal keep-alive rollover. Use sustained controlled-load campaigns to assess
p99/p99.9 and distinguish regressions from noise before making broader claims.

## Compatibility and validation

Literal matching preserves case sensitivity, route normalization, escaped opening
braces, regex metacharacters, Unicode, null paths and the regex `$` behavior that
allows one final LF. Returned matches retain the original parameter-name list.
If a consumer mutates that legacy concrete list, the existing regex behavior is
available through a thread-safe lazy fallback. There is no eager application
handler execution and no cached request data.

Three added regression tests compare 264 fixed/generated route templates against
the legacy regex semantics, exercise legacy list mutation, and run 1,000 concurrent
matches. All 284 focused startup, routing and authorization cases passed locally.
FsCheck passed 10,000 cases for each of six properties, including new differential
literal-route checks. The full suite and cross-platform PR checks are reported
separately; these Windows timing results do not establish Linux/macOS performance.

## Reproduce and next targets

See the [runner instructions](../../test/EmbedIO.Performance/README.md#first-endpoint-latency).
The [measurement summary](evidence/first-endpoint-latency-20261016.json) records
all medians/ranges, core and runner hashes, source hashes, process order and raw
JSONL hashes. Raw local JSONL files are retained under ignored
`TestResults/cold-endpoint-latency`; builds and measurements were serialized while
holding the repository benchmark lock and both heavy-work slots.

Next measurements should attribute the roughly 33 ms first-request pipeline and
JSON metadata/serializer initialization, compare generated controllers and JSON
metadata with reflection, then separate TCP/TLS and HTTP/2/HTTP/3 handshake costs.
Process launch to readiness and process launch to a complete first response must
remain visible so moving work into startup cannot masquerade as a reduction.
Native AOT / ReadyToRun comparisons and static-file first use need their own
measurements. Existing hot allocation gates remain regression controls.

Cold here means a fresh server process. Filesystem caches, OS caches and CPU power
state are uncontrolled. Header timing records complete response headers, not the
first received byte. This pass uses unencrypted loopback HTTP/1.1 and includes
client HTTP-stack overhead; it is not a network, TLS, production-tail or
cross-platform latency guarantee.
