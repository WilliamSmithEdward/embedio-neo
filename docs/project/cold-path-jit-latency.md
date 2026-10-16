# Cold-path JIT latency follow-up

The header/logging extraction was **deferred and removed from the proposed
production change** after mixed warm results. Only the requested listener
branding is retained in production source. This report and its evidence describe
the historical experimental binaries; they do not establish a speedup for
current main. The test-only runner and wire regressions remain available.

The experiment compares main
`c04e15d077a0b754234f5c9547dabc60d9bb17d7` with internal separation of
cookie/large-header encoding and optional request-log formatting. Public APIs,
header validation, cookie lines, transport batching, request cleanup, and log
attribution are preserved. The requested listener display-name update to
`EmbedIO-Neo HTTP Listener` is included in the measured candidate. Copyright
and upstream license attribution remain intact.

No application handlers run as warmup before the first server request, and no
request/response values are cached by this change. The first-endpoint phase
deliberately sends 20 requests to a distinct control endpoint, as a separate
measurement rather than an implicit server warmup feature.

## Attribution and method

An instrumented fresh JSON-server trace identified about 2.76 ms of JIT work in
the request-handling state machine and 1.43 ms in HTTP/1 header encoding. These
single trace observations identify investigation targets; the comparisons below
use uninstrumented processes. The optional log formatting remains behind the
same level/observer checks. Error handling, admission, cancellation and teardown
remain in their original order.

Windows 10.0.26300, .NET 10.0.13 x64, Ryzen 7 9800X3D, Release, default tiered
compilation, unencrypted loopback HTTP/1.1. Each profile uses one identical runner
with only `EmbedIO.dll` swapped. A/B then B/A blocks retain all samples and
abort on the first error without retry. Both heavy slots and the network lock
were held for every measurement; builds, tests and tracing were serialized.

Tracing Off has 14 samples per cell (280 measured processes); Information has
six per cell (120 processes). Specialized endpoints use 14 per cell (112
processes). Each profile also has four disposable client-warmup servers.
Readiness is stdout without an HTTP request. Every response's status, protocol
and complete body is checked; cookie lines and the large field are also checked
in their dedicated scenarios.

## First complete server response

Tracing Off:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| controller | 32.957 | 31.522 | 32.217-39.404 | 30.741-34.733 |
| json | 46.193 | 44.517 | 44.864-49.644 | 43.390-50.081 |
| literal | 33.100 | 31.862 | 32.397-34.331 | 31.299-33.990 |
| literal-100 | 33.124 | 32.278 | 32.186-33.984 | 31.253-37.125 |
| parameter | 34.104 | 32.765 | 33.605-36.171 | 32.276-36.497 |

Plain first-request medians improve by roughly 0.85-1.44 ms, with the JSON
scenario about 1.68 ms lower. This is a modest improvement in this Windows
configuration, not a general JSON initialization improvement.

Informational logging enabled:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| controller | 32.992 | 31.321 | 32.682-33.335 | 30.962-32.255 |
| json | 45.091 | 45.285 | 44.581-46.611 | 43.683-46.337 |
| literal | 33.197 | 31.301 | 32.496-40.040 | 31.021-32.312 |
| literal-100 | 32.669 | 31.067 | 32.331-37.458 | 30.906-31.384 |
| parameter | 33.700 | 33.383 | 33.289-34.254 | 31.939-44.846 |

Plain literal/controller medians are about 1.6-1.9 ms lower in this smaller
profile; parameterized and JSON results are mixed. Logging configuration and
sample spread matter, so the Off results should not be generalized to every
application configuration.

## Process launch to first complete response

Tracing Off:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| controller | 124.386 | 124.393 | 121.945-135.968 | 122.100-131.879 |
| json | 127.681 | 126.475 | 124.641-132.327 | 123.429-142.382 |
| literal | 113.684 | 114.433 | 111.477-119.973 | 111.426-136.456 |
| literal-100 | 113.673 | 115.643 | 111.353-122.734 | 111.767-123.143 |
| parameter | 116.001 | 115.734 | 114.569-122.453 | 114.217-123.318 |

Informational logging enabled:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| controller | 126.835 | 127.082 | 125.047-128.811 | 124.049-129.001 |
| json | 128.979 | 131.131 | 126.793-132.145 | 126.073-132.958 |
| literal | 117.636 | 115.780 | 115.622-128.054 | 113.265-118.230 |
| literal-100 | 116.388 | 114.941 | 114.952-123.224 | 114.007-118.412 |
| parameter | 119.237 | 117.255 | 115.612-122.286 | 115.410-129.517 |

Total launch measurements are mixed. This pass establishes no general reduction
in process-launch-to-response latency; runtime launch, configuration, listening,
console readiness and scheduling remain visible in these totals.

## First endpoint after control requests

Tracing Off:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| controller | 0.238 | 0.238 | 0.210-0.418 | 0.212-0.273 |
| json | 12.527 | 12.604 | 12.201-14.002 | 12.403-15.177 |
| literal | 0.311 | 0.319 | 0.266-0.385 | 0.277-0.351 |
| literal-100 | 0.316 | 0.321 | 0.301-0.377 | 0.304-0.778 |
| parameter | 1.405 | 1.391 | 1.371-1.517 | 1.355-1.566 |

JSON initialization remains around 12.5-12.6 ms in this phase. The small
differences in warmed-engine endpoints are not claimed as improvements.

## Specialized endpoint check

The cookie case writes both a structured cookie and an independent raw
Set-Cookie line. The large-header case writes a 17,000-character field, beyond
the stack buffer's capacity. These cases specifically expose work that the
candidate defers until it is needed.

First complete server response, tracing Off:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| cookies | 34.874 | 34.039 | 34.123-41.346 | 32.831-41.195 |
| large-header | 34.412 | 32.899 | 33.430-34.820 | 32.264-35.708 |

First endpoint after control requests, tracing Off:

| Scenario | Before median ms | After median ms | Before min-max ms | After min-max ms |
|---|---:|---:|---:|---:|
| cookies | 1.141 | 1.274 | 1.084-1.312 | 1.220-1.369 |
| large-header | 0.468 | 0.551 | 0.439-0.716 | 0.512-0.841 |

Both specialized first-server-request medians improve. After control warmup,
however, first cookie use rises from 1.141 to 1.274 ms (0.133 ms) and first
large-header use from 0.468 to 0.551 ms (0.083 ms). This is the cost of compiling
the extracted helpers on demand. The candidate trades a smaller common
first-request cost for these small deferred costs; it is not a uniform
first-endpoint improvement. Applications prioritizing those specialized paths
should evaluate this tradeoff with their own request sequence.

## Warm-latency follow-up and merge decision

William's priority is warm steady-state and tail latency when a cold/warm
tradeoff is necessary, while aiming to improve both. The candidate was deferred
and its helper changes removed from this PR: the following warm evidence establishes neither a consistent warm improvement
nor a sufficiently strong non-regression conclusion.

The sustained comparison used the same baseline and candidate binaries as the
cold comparison, on the same Windows/.NET host. Four rounds alternated engine
order, with five seconds of warmup and 15 seconds of measurement per sample.
The server used CPUs 0-3 and the client CPUs 4-7. Both heavy slots and the network
lock were held throughout; builds and fuzz campaigns ran afterward.

All scenarios used **eight actual connections**, overriding their historical
names. This is paced light load at 64 aggregate requests/second (about 65 for
16-request pipeline batches), not saturation or a throughput comparison. Prefix
scenario selection also included the close-after-100-requests control. All 32
samples completed without client errors, unanswered pipeline requests or forced
process termination. Each ordinary sample contains about 960 measured requests;
tail estimates, especially correlated pipeline batches, have limited precision.

Values below are medians of four per-sample percentiles, not pooled percentiles.
The evidence retains every sample and its p50/p95/p99 range.

| Scenario | p50 before/after ms | p95 before/after ms | p99 before/after ms | p99 before range ms | p99 after range ms |
|---|---:|---:|---:|---:|---:|
| Small keep-alive | 0.1831 / 0.1839 | 0.2287 / 0.2271 | 0.2495 / 0.2463 | 0.2431-0.2559 | 0.2399-0.5439 |
| Pipeline 16 | 0.3983 / 0.3839 | 0.6399 / 0.6271 | 0.7295 / 0.7647 | 0.7103-0.7743 | 0.7039-0.7935 |
| Small close-100 control | 0.1831 / 0.1831 | 0.2271 / 0.2255 | 0.2495 / 0.2527 | 0.2431-0.2623 | 0.2463-0.2719 |
| JSON 1 KiB | 0.2319 / 0.2399 | 0.2879 / 0.2879 | 0.3407 / 0.3407 | 0.3071-0.9599 | 0.3295-0.4223 |

Small-response medians are close. Pipeline median p99 rises by 0.0352 ms
(about 4.8%), while its p50/p95 decrease; JSON p50 rises by 0.0080 ms
(about 3.4%), with unchanged median p95/p99. The individual rounds vary, and the
candidate has a small-response p99 outlier. These observations must not be
reported as a warm win or dismissed solely because cold response medians improved.
Longer representative-load measurements are needed before accepting the tradeoff.

A separate socket-free counting-transport ABBA check ran with tiered compilation
disabled, five internal rounds per sample. Warm first fixed 13-byte writes were
1,830-1,870 ns before and 1,836-1,878 ns after; first chunked writes were
1,873-1,889 ns before and 1,805-1,851 ns after. Managed bytes and transport-write
counts match for corresponding rows. This short component check does not
establish end-to-end tail behavior or settle the mixed network result.

An earlier connection-churning driver failed with Windows socket error 10055
before producing valid comparison samples. Its failure logs are retained; it
does not demonstrate a production defect or contribute timing results here.

The [warm evidence](evidence/warm-response-latency-20261016.json) records all
rounds, component rows, binary identities and raw-file hashes. The response
pipeline fuzz expansion in PR #469 also passed 128 generated cases per protocol
against this candidate core, plus its six negative oracle checks; correctness
validation does not supply performance evidence.

## Validation and limits

Existing response-write regressions now exercise common, boundary-sized and large
fields with both cookie paths, through synchronous and asynchronous output.
Existing cold-start cases verify the listener's display name. Log attribution,
direct dispatch, malformed fields, cancellation and complete request cleanup
remain covered by the regression suite. Exact test and CI outcomes are recorded
in the PR; these timing tables are not CI timing thresholds.

The 100-request warm windows remain scheduling/JIT/keep-alive sensitive and do
not establish throughput or p99/p99.9 behavior. Fresh process does not mean cold
OS/filesystem caches. These results do not establish TLS, HTTP/2, HTTP/3,
Linux/macOS or Native AOT performance. Source-generated JSON/controller and
deployment-mode comparisons remain useful next measurements.

The [evidence summary](evidence/cold-path-jit-latency-20261016.json) retains
medians, full min/max ranges, binary/source hashes, logging settings, process
order and raw-file hashes. Raw JSONL and diagnostic traces are under ignored
`TestResults/latency-next`. The [runner instructions](../../test/EmbedIO.Performance/README.md#first-endpoint-latency)
describe scenario selection and logging controls. The earlier
[literal-route report](first-endpoint-latency.md) is a separate comparison.
