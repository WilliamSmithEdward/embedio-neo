# Listener chunk framing and charset allocations

This audit starts at main commit `d3a847f`, following the
[body/header allocation work](listener-body-performance.md). It changes only
internal formatting and extraction, preserving APIs, frameworks, dependencies,
wire bytes, cancellation, error handling, request policies and routing/TLS defaults.

## Retained changes

Chunk prefixes are encoded directly as lowercase hexadecimal ASCII bytes followed
by CRLF. The final flag adds the same second CRLF as before. The result is still a
new array per call. The implementation also preserves the old unsigned hexadecimal
representation of negative integers, although production writes reject negative
counts before reaching this private formatter. No shared mutable byte array or
new socket option is introduced.

Charset extraction scans semicolon-delimited segments without building a split
array or LINQ iterators. It retains Substring/Trim, the exact OrdinalIgnoreCase
prefix check and the existing GetAttributeValue/Unquote helper. The first matching
segment still determines the result, even when it has no usable value; single
quotes, unusual parameter names, semicolons inside quotes and malformed quoting
are not normalized. Existing exception types/parameter names are preserved.

## Measurements

Actual formatter and extractor delegates were measured on Windows x64 / .NET
10.0.11, against exact baseline `d3a847f`. Setup is excluded; five rounds of 50,000
calls follow 10,000 warmups. Timing includes output validation and is informational.

| Operation | Baseline B/call | Candidate B/call |
| --- | ---: | ---: |
| Final zero chunk | 64 | 32 |
| Chunk length 15 or 256 | 64 | 32 |
| Chunk length 16384 | 72 | 32 |
| Content type without charset | 200 | 0 |
| `text/plain; charset=utf-8` | 392 | 184 |
| Boundary plus quoted UTF-8 charset | 552 | 336 |

Initial operation medians also decreased (roughly 86 to 35 ns for the final chunk,
196 to 91 ns for UTF-8 extraction). These are focused operation results, not claims
about whole HTTP throughput. Allocation budgets, rather than timing, gate CI.

The test-only HTTP benchmark adds optional `--chunk-bytes`, preserving its existing
fixed-length default. A 64 KB response in 1 KB writes performs 64 body chunks plus
a final terminator. Three runs of five rounds, four workers and 12 requests per
worker/round gave median per-run process allocation values:

| Protocol | Baseline B/request | Candidate B/request |
| --- | ---: | ---: |
| HTTP | 171151 | 168928 |
| HTTPS | 169830 | 168180 |

These figures include the loopback client and server. Timing varied: HTTP medians
ranged 2011-2063 requests/s in baseline versus 1926-2032 in candidate; HTTPS varied
more. No whole-server throughput improvement is established by these runs. The
repeatable win is lower formatter/extractor allocation, with lower process
allocation in this chunked workload. Exact raw chunk framing is tested separately
from HttpClient's decoded response bytes.

## Rejected and deferred candidates

Enumerating named-prefix dictionary entries directly avoided one successful-route
hash lookup, but its 128-prefix probe increased from about 826 to 882 ns with the
same 88 B allocation (including reflection). That candidate was reverted; route
selection and dictionary enumeration remain unchanged. Its measurements stay in
ignored scratch results rather than becoming a new production optimization.

Request header policy, header deadlines, async-read/APM coordination, socket
options, parser fast paths and connection lifetime defaults are unchanged. Prior
compatibility findings remain documented in the linked audits. This pass does not
reconsider those policies merely to improve a benchmark.

## Parity and regression validation

An isolated comparison loads exact baseline and candidate assemblies. Across
English, Turkish, Arabic and French cultures, 40,000 random signed-integer chunk
snapshots and 40,000 generated charset inputs matched, including returned values
and exception types/parameter names. These are deterministic comparisons, not a
claim of exhaustive proof over every possible environment.

42 permanent cases cover integer/hexadecimal boundaries, final/non-final chunks,
three cultures, null/empty/duplicate/malformed charset inputs, Unicode whitespace,
legacy exceptions and complete multi-write chunk bodies with synchronous and async
writes. Existing real HTTP/HTTPS, content encoding, large response, shutdown,
resource lifetime and native-platform suites remain required. Desktop CI adds the
focused allocation budgets and bounded chunked HTTP/HTTPS keep-alive/churn cleanup
verification.

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release -- --verify-listener-wire
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --chunk-bytes 1024 --payload-bytes 65536 --connection-policy keep-alive --concurrency 4 --requests 12 --rounds 5
```

Logs, baseline copies and differential tools remain under ignored
TestResults/listener-wire-performance. No release or version tag is requested.
