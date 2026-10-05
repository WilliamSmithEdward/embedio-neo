# Additional performance audit

This pass extends [the first audit](performance-audit.md), using commit `d27bd5c`
as its measurement baseline. It targets remaining allocation costs in content
negotiation, header and token parsing, WebSocket metadata and request-rate history.
Public/protected APIs, target frameworks, defaults and dependency versions remain
unchanged. Existing pinned dependencies already provide the .NET Standard 2.0
`BinaryPrimitives` implementation; no new runtime package is introduced.

## Changes and compatibility

Weighted `QValueList` parsing now scans the existing narrow grammar directly
instead of allocating regex matches, groups and fractional strings. It keeps
case sensitivity, first valid quality-field selection, malformed-field fallback,
Unicode digit arithmetic, extensions, duplicate entries and per-header ordinal
behavior. It also retains the former regex's acceptance of one terminal LF.
This is an implementation optimization, not stricter HTTP validation.

Content negotiation reuses private method/name arrays. Returned quality
dictionaries remain independent and mutable through their existing concrete
dictionary type. Header token matching visits comma-separated values only until
a match, preserving trimming, comparison modes, empty values and existing quoted
comma behavior. Token validation uses an ASCII membership table with the same
accepted characters and errors. Route lookup reuses indexed search and its
standard list/array implementations, preserving ordinal and null-name handling.

WebSocket length decoding uses network-byte-order primitives without temporary
reversed arrays. Close reasons are encoded directly into their final buffer with
unchanged UTF-8 fallback. The obsolete byte-order conversion helper is removed.
Incremental frame reads return an internally owned buffer only when its capacity
is exactly its logical length; other lengths still copy to an exact-length result.
Reads remain incremental and returned data does not alias the source or another
read. Large peer-declared payloads are not allocated in full before data arrives.

The requests-per-second criterion previously took two allocating snapshots of
its `ConcurrentBag` history per check. Purging also replaced bags while writers
could still be adding to them. One baseline concurrency run retained only 825 of
1,000 submitted requests after purge. Private per-address lists now synchronize
append/count and purge operations, with an identity check when a history is
removed while a caller waits. Purging cannot overwrite a newly recorded request.
Expired entries are removed in place; spare capacity is trimmed on purge and
empty addresses are dropped.

The request timestamp is captured before waiting on the history lock. Histories
keep their existing global ownership, local wall-clock timestamps, one-second
threshold and integer-divided one-minute threshold. Clearing an address and
disposing a criterion retain their existing shared reset semantics. Counting is
still linear in retained history length; this does not add per-server isolation
or redesign the separate regex-based criterion.

## Measurements and budgets

Run the same dependency-free runner described in the first audit:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --verify-allocations
```

Omit `--verify-allocations` for the baseline, using identical runner source against
both revisions on the same machine. The runner warms each operation 20,000 times
and reports the median of seven rounds. New operations use 100,000 calls per round,
except the request-history workload, which uses 5,000. Its private 5,000-sample
history is seeded before measurement; each measured public validation adds one
sample and the harness removes it afterward to keep history size constant. That
cleanup is included in both measurements. The history has spare capacity.

Windows x64, SDK 10.0.400, .NET 10.0.11:

| Operation | Before, B/op | After, B/op | CI ceiling, B/op |
| --- | ---: | ---: | ---: |
| Parse three weighted qualities | 2024 | 440 | 512 |
| Negotiate content encoding | 112 | 32 | 64 |
| Look up a route parameter name | 88 | 0 | 0 |
| Match the first header token | 368 | 48 | 64 |
| Match the last header token | 528 | 368 | 400 |
| Validate an RFC token | 32 | 0 | 0 |
| Decode 16-bit or 64-bit frame length | 80 | 0 | 0 |
| Build a close payload with a reason | 216 | 40 | 64 |
| Read a complete 16 KiB frame payload | 49408 | 33000 | 34000 |
| Read a declared 16 KiB payload ending at 4 KiB | 12568 | 8448 | 9000 |
| Read a complete 12,000-byte payload | 45168 | 45168 | 46000 |
| Validate an IP with 5,000 history samples | 80288 | 0 | 0 |

Plain quality parsing remains at 424 B/op. Zero-allocation history checks refer
to this steady-state workload, not new addresses or list growth. Weighted parsing
measured about 411 ns before and 86 ns after; the history workload measured about
16.4 microseconds before and 1.9 after. Timings are informational, and allocation
ceilings run in desktop CI. These results do not establish server throughput,
tail latency, multi-client contention or older-runtime performance.

## Validation

Forty-nine new regression cases cover the original parser as an independent
oracle, 2,000 seeded malformed header samples, every UTF-16 character in quality
fractions and token validation, Unicode/comparison behavior, negotiation ties,
network byte order, UTF-8 fallback, exact-length and short reads, rate thresholds,
shared ownership, address separation, expiry and concurrent purge/write behavior.
The baseline passed the contract cases and failed the purge/write case. All new
cases pass after the changes; that race also passed ten independent stress runs.

Local validation passed 268 targeted cases without opening a listener, both
library target builds, locked restores, public/protected API comparisons, allocation
budgets and the existing 100,000-mutation URL/query harness with seed 1729.
Complete Windows/Linux/macOS and MAUI listener validation uses the required CI
jobs, avoiding local firewall prompts. Local logs and baseline snapshots remain
under ignored `TestResults/perf-second`.
