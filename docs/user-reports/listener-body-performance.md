# HTTP listener body and header allocations

The unreleased [modern engine increment](../project/http-engine.md) supersedes
this report's managed chunked-input and synchronous-read limitations, and tightens
ambiguous framing. Measurements below describe the earlier source; see the
[migration notes](../compatibility/migration.md#managed-http-framing-unreleased).

Measurements start at main commit `e82d10f`. The change was subsequently rebased
onto `e23ee0d`, preserving the independently merged charset/static-file work and
its 43 new regression cases. It preserves public APIs, supported
frameworks, dependencies, request framing, cancellation, timeout and connection
reuse policies. It follows the [listener boundary audit](listener-boundaries.md).

## Changes

A fully consumed fixed-length RequestStream already returns EOF without touching
its transport. FlushInput now recognizes that state before allocating a drain
buffer. For unread or partially consumed bodies, it rents temporary storage while
keeping the existing read count of at most 2048 bytes. Finally returns and clears
the array, including read-error and disposal paths. Existing success/failure
results and subsequent-request boundaries are preserved. The transport still
belongs to the connection, not the request stream.

Response headers keep their existing name snapshot, ordering, Set-Cookie handling,
status line, UTF-8 bytes, preamble offset and growable buffer. The serializer writes
encoded text directly into its MemoryStream backing array, eliminating the
intermediate encoded byte array and its copy. A direct conditional replaces the
LINQ filter without replacing the header-name snapshot with live enumeration.
That avoids introducing a new enumeration contract for existing consumers.

## Measured allocations

The test-only `--listener-allocations` helper exercises actual FlushInput and
WriteHeaders delegates. Fixture setup is excluded. The unread-body fixture resets
source position and remaining length between operations; that reset is included.
Every serialized header is compared with exact expected wire bytes. Five rounds of
10,000 operations follow 2,000 warmups. These are operation measurements, not whole
HTTP requests. Windows x64 / .NET 10.0.11 results against exact baseline `e82d10f`:

| Operation | Baseline bytes/operation | Candidate bytes/operation |
| --- | ---: | ---: |
| Drain already consumed 4 KB body | 2072 | 0 |
| Drain unread 4 KB body, warm pool | 2072 | 0 |
| Small header fixture | 1056 | 728 |
| 1024-character header value | 9136 | 7984 |
| 16384-character header value | 115176 | 98664 |

The pool must warm before zero-allocation drain measurements. It can retain a
small reusable buffer; this is reduced allocation churn, not a claim that all
memory retention disappears. The cleared return prevents request bytes from
remaining in shared temporary storage. Timing is informational: consumed-body
checks became cheaper, but pool clearing has a cost and small-operation timings
vary. Allocation budgets gate desktop CI rather than timing thresholds.

## End-to-end POST workload

The existing GET benchmark remains the default. Optional POST workload settings:

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release -- --listener-http --request-body-bytes 65536 --body-consumption full --connection-policy keep-alive --concurrency 16 --requests 40 --rounds 5
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --request-body-bytes 65536 --body-consumption none --concurrency 16 --requests 8 --rounds 5
```

Each consumed byte is validated. Response bytes/status and terminal resources are
also verified. Partial consumes half the body; none leaves it to the normal
keep-alive drain. Allocation and memory figures include both the loopback client
and server, including the server's benchmark read buffer and client request objects.

Full-body consumption, median of five rounds, 640 requests per round:

| Protocol | Baseline process bytes/request | Candidate process bytes/request |
| --- | ---: | ---: |
| HTTP | 33422 | 31029 |
| HTTPS | 36783 | 34403 |

Unread bodies, median of three per-run medians (five rounds each), 128 requests per round:

| Protocol | Baseline process bytes/request | Candidate process bytes/request |
| --- | ---: | ---: |
| HTTP | 23554 | 21179 |
| HTTPS | 25219 | 23012 |

Timing varied between repeats. HTTP unread-body medians ranged roughly 78,700 to
80,100 requests/s in baseline and 66,600 to 81,900 in candidate; HTTPS varied more.
No HTTP throughput speedup is claimed. The verified win is lower allocation per
operation and lower allocation in these POST workloads.

An initial larger unread-body baseline run crossed the existing 100-reuse
connection lifetime and failed with a TLS reset while a body could still be sent.
It is excluded from measurements. Unread/partial benchmark workloads therefore
require keep-alive and fewer than 100 requests per worker including warmups. The
native pool chooses actual connections; a reset still fails the run rather than
being retried or hidden. Forced-close behavior is preserved, and applications
needing predictable early responses can consume the body before closing.

## Async-read compatibility finding

RequestStream inherits Stream.ReadAsync and BeginRead/EndRead. The
[pinned .NET implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/IO/Stream.cs)
checks cancellation at task submission and schedules synchronous Read while
sharing coordination with APM operations. A replacement that forwards cancellable
native reads or creates an independent async lock would change that behavior.
This pass deliberately preserves the inherited path. A future async-read change
needs parity for mixed APM/task operations, cancellation, validation and disposal;
it is not automatically authorized merely because it appears faster.

## Validation

26 new cases cover exact UTF-8 header bytes and buffer growth, fully/partially/unread
body boundaries, source errors, disposed getter behavior and temporary storage
clearing. Six real HTTP/HTTPS cases send eight sequential 64 KB POSTs with full,
partial or no application consumption. Existing framing, response ownership,
large response/header, keep-alive, cancellation, shutdown and platform tests remain
part of the full suite.

Desktop CI adds allocation budgets and bounded POST cleanup verification: four
HTTP/HTTPS keep-alive/churn workloads for full consumption, and two keep-alive
workloads for each partial/unread mode. Logs and baseline scratch remain under
ignored TestResults/listener-body-performance. No release or version tag is
requested by this audit.
