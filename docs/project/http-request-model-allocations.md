# Multiplexed request-model allocation

HTTP/2 and HTTP/3 requests share one application model, `MultiplexedContext` and
`MultiplexedRequest`. This change removes about 510 to 590 bytes of server
allocation per request: 5.2 to 5.9% on small HTTP/2 requests, 4.1% on small
HTTP/3 requests and 2.9 to 4.1% on a route that reads the query, headers, cookies
and referrer. Throughput, CPU per request and latency did not change beyond
run-to-run noise. Public behavior is unchanged; 34 new regression cases pin it on
both protocols.

| Scenario (connections x streams) | Base B/req | Candidate B/req | Change |
| --- | ---: | ---: | ---: |
| h2c small, 8 x 32 | 10,167 | 9,633 | -533 (-5.2%) |
| h2c small, 64 x 1 | 8,776 | 8,259 | -517 (-5.9%) |
| h2 TLS small, 8 x 32 | 10,196 | 9,644 | -551 (-5.4%) |
| h2 TLS small, 64 x 1 | 8,789 | 8,277 | -512 (-5.8%) |
| HTTP/3 small, 8 x 32 | 12,512 | 11,996 | -516 (-4.1%) |
| HTTP/3 small, 64 x 1 | 12,775 | 12,251 | -524 (-4.1%) |
| h2c inspect, 8 x 32 | 15,347 | 14,800 | -548 (-3.6%) |
| h2c inspect, 64 x 1 | 13,908 | 13,337 | -571 (-4.1%) |
| h2 TLS inspect, 8 x 32 | 15,364 | 14,815 | -549 (-3.6%) |
| h2 TLS inspect, 64 x 1 | 13,927 | 13,378 | -549 (-3.9%) |
| HTTP/3 inspect, 8 x 32 | 17,712 | 17,120 | -592 (-3.3%) |
| HTTP/3 inspect, 64 x 1 | 18,019 | 17,501 | -518 (-2.9%) |

Server bytes allocated per completed request in the 15-second window, median of
three rounds, final source `509a3b8` against `90e84f1`. Every paired round (same
round, alternating engine order) was lower, by 2.7 to 6.1%. Small requests use the
13-byte plaintext route; inspect requests use the route described under
[method](#environment-and-method).

## What a request allocated

Profiles came from separate `--profile` runs (sampled allocation by type over the
measurement window), never from comparison samples, plus an in-process probe that
counts exact bytes per step. At engine head `90e84f1` a small h2c request allocated
about 10.2 KB. The request model's share, measured by the probe with an exchange
that allocates nothing (median of six alternating rounds of 200,000 requests):

| Step | Plaintext base | Plaintext final | Inspect base | Inspect final |
| --- | ---: | ---: | ---: | ---: |
| Construct context and request | 1,896 | 1,224 | 3,600 | 3,056 |
| Server token link (`CancellationToken` setter) | 80 | 80 | 80 | 80 |
| Route `Url.AbsolutePath` | 96 | 248 | 88 | 88 |
| MIME provider push, debug-line reads | 104 | 104 | 168 | 168 |
| Route reads (query, headers, cookies, referrer) | 0 | 0 | 1,091 | 1,091 |
| Close (mostly `MultiplexedResponse`) | 2,190 | 2,193 | 2,000 | 2,000 |
| Total bytes | 4,366 | 3,849 | 7,027 | 6,483 |

Per-request time in the probe was the same within its noise (about 2.8 to 2.9 us
for both cores and both routes).

Construction was the largest avoidable cost:

1. An empty `QueryString`. `NameValueCollection` allocates an `ArrayList`, a
   `Hashtable` and its bucket array even when empty, about 290 bytes, for every
   request without a query.
2. Query parsing split the query into an array of part strings before decoding each
   name and value.
3. Wrappers: `Lazy<IDictionary>` with its `LazyHelper` (about 70 bytes), although
   the completion extension fills `Items` on every request anyway; an eager callback
   `Stack`; and a `TimeKeeper` object holding one number.
4. The context ID: `Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 22)`
   allocated a byte array and two strings. `WebServer` reads the ID for every request
   as the listener queue key, so deferring it would save nothing.

On plaintext requests about 150 bytes of URI component parsing moves from
construction to routing, because the constructor no longer reads `Url.Query` when
the target has no query.

## Changes

All production changes are in `MultiplexedContext.cs` and `MultiplexedRequest.cs`.

- **Empty query collection on first read.** A request whose target has no `?` gets
  its empty `QueryString` when it is first read, published once with
  `Interlocked.CompareExchange`, as the HTTP/1 request already does. Concurrent first
  readers may each create one; all get the published instance.
- **Query parsing without `Split`.** A present query is still parsed at construction.
  The parser walks it with index arithmetic and produces the same pairs: an empty
  part adds a null key with an empty value, each part splits at its first `=`, and
  every name and value is URL-decoded.
- **Context storage on first use.** `Items` is a field published the same way. Close
  callbacks get their `Stack` on the first `OnClose`, under the existing lock; a
  context closed without registrations needs none.
- **Age as a timestamp.** The context keeps its `Stopwatch` start timestamp instead of
  a `TimeKeeper` object; `Age` is still whole elapsed milliseconds.
- **ID without intermediate buffers.** On the .NET 10 asset the ID is the first 22
  base64 characters of a new GUID's bytes, written to stack buffers, so only the
  result string is allocated. The format is unchanged; the .NET Standard 2.0 asset
  still calls `UniqueIdGenerator.GetNext`.

Public APIs, defaults, targets and dependencies are unchanged. No object is pooled.

## Deferred parsing raised tail latency

The first candidate (`8c22885`) also deferred the parse of a present query and of the
referrer to the handler's first read. That saved about 110 more bytes on the inspect
route, but with 8 connections x 32 streams it moved latency:

| h2c / h2 TLS inspect, 8 x 32, 5 rounds | Paired round ratios, candidate / base |
| --- | --- |
| p99 latency | 1.09 to 1.16 / 1.07 to 1.14, every round higher |
| p50 latency | 0.90 to 0.98 / 0.95 to 0.96, every round lower |
| Server CPU per request | 1.005 to 1.05 / 1.03 to 1.06 |
| Client CPU | 59 to 64% against 55 to 57% / 63 to 68% against 58 to 60% |

An A/A control (the base core against an identical copy, three rounds) showed no
consistent difference in any of these, with client CPU 53 to 60% for both. The
64 x 1 inspect scenarios did not show the effect. A hybrid that parsed a present query
and referrer at construction again, keeping every other change, removed the shift:
five rounds against the base showed p50, p99, server CPU and client CPU within noise,
with allocation still 3.0 to 3.8% lower. The final source is that hybrid, with the
referrer code restored to the original.

The cause was not traced further. Both versions do the same parsing work; the
deferred one does it inside the application handler, just before the response
writes, instead of in the per-stream dispatch before the context is queued. HTTP/2
output batching adapts to how writes from sibling streams arrive, so a plausible
explanation is a change in batching, which would match lower p50, higher p99 and more
client CPU. This is an inference, not a measured mechanism.

## Compatibility validation

`MultiplexedRequestModelTest` adds 34 cases, each run through the public `WebServer`
over h2c and over HTTP/3:

- Query parsing compared with the original split-based rules for nine targets,
  including repeated keys, empty parts, keys without values, empty keys, encoded
  delimiters, a trailing `&`, keys differing only in case and UTF-8 escapes.
- Sixteen concurrent first readers of `Items`, `QueryString` and `UrlReferrer` get the
  same instances, with and without a query; the collections stay mutable and
  request-local across two requests.
- The referrer for an absolute, a relative, an invalid and a missing header keeps the
  value it had at request start after the header is changed.
- Twenty-four concurrent requests get distinct IDs in the original format, and `Age`
  advances.
- Sixteen close callbacks registered concurrently run once in reverse order, a
  request without callbacks closes normally, and registering after close throws.

The 34 cases pass against the base core (`90e84f1`) and the final candidate, so they
pin existing behavior. On the candidate's .NET Standard 2.0 asset the 17 HTTP/2 cases
pass and the 17 HTTP/3 cases are skipped (that asset has no QUIC). With the existing
adapter, listener, context, cancellation and query-policy fixtures, 305 focused cases
pass on Windows. Both library targets build with analyzers as errors; whitespace
formatting and the suppression check pass.

## Environment and method

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GB, Windows 10.0.26300, Ultimate Performance power scheme |
| Runtime | .NET 10.0.12; `msquic.dll` SHA-256 `B7158D42...57E8`; System.Net.Quic `AF5B52CD...BE73` |
| Base | `90e84f1` (codex/managed-http-engine), core SHA-256 `9D394293...1D73` |
| Final candidate | `509a3b8`, core SHA-256 `4C1901BA...269C` |
| Runner | one harness build for both engines, SHA-256 `85E013C7...F2CB`; the base copy differs only in `EmbedIO.dll` |
| CPU sets | server logical CPUs 0-7, client 8-15, set at process creation |
| Schedule | `--modern-baseline`; 3 rounds (5 for the latency follow-up), engine order alternating by round; 5 s warmup, 15 s measurement, fresh processes per sample, no retries |

The harness gains `-inspect-` scenarios (see the
[load benchmark README](../../test/EmbedIO.LoadBenchmark/README.md)) whose route reads
the query, a repeated query key, five headers, two cookies, the referrer, body framing
and the endpoints, and answers 400 naming the first mismatch, so a wrong property
value fails the sample. It also gains h2c and HTTP/3 64 x 1 small scenarios. HTTP/2
cleartext and TLS and HTTP/3 each ran with 8 x 32 and 64 x 1 for both routes. Every
response byte was validated and no sample failed.

The machine was shared with other agents.

- The first candidate's comparison, the five-round latency follow-up, the A/A control
  and the hybrid run took the shared lock file only when absent, after 20 seconds
  with no foreign test or load process. A watchdog checked every 10 seconds for
  foreign tests or load and for loss of the lock. None of those attempts was aborted.
  Background CPU per sample (machine busy time minus server and client) was 8 to 22
  CPU-seconds per 15-second window.
- The final-source comparison ran without the lock at the owner's request, while
  other agents ran tests and load. Background CPU ranged from 8 to 68 CPU-seconds per
  window, and its throughput and latency samples swing by up to 25% and 1.9x in single
  rounds. Its allocation results agree with the locked runs; its throughput and
  latency figures are not used as evidence. The latency conclusion for the final code
  rests on the locked hybrid run, which does the same work at the same points.

The in-process probe constructs `MultiplexedContext` through a compiled constructor
call with an emitted exchange whose properties read fields and whose operations
return completed tasks. It then performs the reads `WebServer` and a route make, step
by step, measuring `GC.GetAllocatedBytesForCurrentThread`.

## Remaining contributors

Outside the two files changed here, in the order of their share of a small request:

1. Response construction and close: `MultiplexedResponse` with its
   `WebHeaderCollection` and header serialization (`HpackField[]`, header strings).
2. Transport and dispatch state machines and tasks per stream.
3. The `NameValueCollection` request headers built by the transport.
4. `WebServer` dispatch: its Debug line is formatted for every request whether or not
   Debug logging is enabled (`RemoteEndPoint`, `Url.PathAndQuery`, `UserAgent` and
   the interpolated string), and the completion extension creates the `Items`
   dictionary for every request.
5. The linked `CancellationTokenSource` per request, about 380 bytes in the load
   profile with its registrations. Removing it needs a cancellation design shared by
   `WebServer` and the transports; the context cannot drop it alone without changing
   when application tokens observe server shutdown.

## Observation outside this change

Over HTTP/3, a GET without `content-length` reports `HasEntityBody` true and
`ContentLength64` -1, because `Http3QuicExchange.InitialBodyComplete` is true only for
an explicit `content-length: 0`. HTTP/2 reports false and 0 for the same request. This
predates the change, which does not touch it. The inspect route therefore does not
require `HasEntityBody` false over HTTP/3.

## Limits

- Loopback on one shared Windows host with `HttpClient` as the client; no Linux or
  macOS measurement. Closed-loop load; overload is not characterized.
- HTTP/3 uses System.Net.Quic. No native QUIC provider was measured.
- Three rounds per scenario (five for the latency follow-up). Throughput and latency
  differences inside the run-to-run spread are reported as noise, not as gains or
  losses; a few unlocked final rounds show consistent differences in both directions
  that coincide with background load and are not claimed.
- `--profile` allocation figures are sampled. The probe isolates the request model
  from transport and response costs and is not end-to-end.
- Core hashes depend on the build directory and the embedded commit; the recorded
  hashes identify what ran, not a reproducible build.

Evidence (samples, logs, probes, profiles, guard journals and summaries) is kept under
the ignored `TestResults/mra` directory of the working tree that produced it.

## Integration fixture review

The review branch reconciles these changes with engine `1b70377` and raises
the discovery floor from 4,984 to 5,018 for the 34 ordinary cases. The rooted
referrer fixture now preserves the existing Windows/Unix `Uri.TryCreate`
difference: `/relative/page` is an absolute file URI on Unix. The HTTP/3
fixture provisions an exportable key, consistent with existing QUIC fixtures.
No production referrer semantics or trust policy changes.

Before reconciliation, the corrected fixture passes 34/34 cases with zero
skips on Windows and pinned Linux. The original fixture fails the two Unix
rooted-referrer cases; its macOS TLS failures require hosted verification of
the corrected key provision. The original PR #260 Linux run also terminated
with an unhandled Microsoft-listener `FormatHeaders` null-reference callback.
The separate investigation in [draft PR #266](https://github.com/WilliamSmithEdward/embedio-neo/pull/266)
identifies a corrupted header collection on an unbound 404 response in the
runtime's Unix managed `HttpListener`, with retained exceptions from listener
shutdown and `SendError`. Its upgraded responses are intact. These paths do
not involve the changed multiplexed request-model files. No runtime correction,
new quarantine or removal of the upgrade-cancellation test is made here;
passing follow-ups do not establish repair of that runtime defect. The separate
glibc double-free remains unattributed. Earlier performance results are for their
named revisions and do not measure the reconciled review head.

The review is now reconciled with verified engine `972ba33`, retaining its
HTTP/2 shutdown correction and UTF8 JSON path. The prior measured 5,018-case
minimum remains until the new combined discovery is actually reported. Fresh
combined-source validation and every exact-head check are still required before
integration.

The current review is reconciled with engine `d10dedc`, including the approved
Microsoft-backend removal and the WebSocket initialization correction. Its
full Windows coverage run reports 4,521 cases (4,519 passed, two existing skips,
zero failures, 3m13s). This verifies the new floor as the current 4,487 cases
plus these 34 ordinary regressions. Both library targets and the solution build
with zero warnings/errors; formatting and source guards pass. The retired
Microsoft path is no longer part of this combined source, while the historical
glibc heap-abort cause remains unconfirmed. Historical allocation measurements
above do not measure this refreshed head. Fresh exact-head hosted checks remain
required before integration.
