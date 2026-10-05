# Repository performance and code audit

This audit examines allocation costs, cache memory limits, dead code and duplicated
work while preserving the public API and existing HTTP/WebSocket contracts.
The baseline is commit `795e41f`. Changes are internal; package identities, target
frameworks, listener defaults and dependency versions are unchanged.

## Scope and findings

A repository-wide candidate scan covered the production projects, test fixtures,
CI and maintenance scripts. Manual review and execution concentrated on the
following areas. This is a focused audit, not proof that every workload is optimal.

| Area | Review and outcome |
| --- | --- |
| HTTP listener and streams | Remove copies of internally owned `MemoryStream` buffers during header parsing, request-body handoff and response writes. Keep logical lengths and preamble offsets. Verify fragmented input, unused capacity, bodies and chunk framing. |
| Routing and Web API | Replace allocating LINQ group extraction with indexed extraction. Return exact cached route matches before rebuilding their regex patterns. Keep normalization, parameter order, decoding, invalid-route errors and cache clearing. Compiled controller handlers and existing async binding contracts remain intact. |
| Static files and compression | Repair LRU initialization, replacement links, content size accounting after eviction/invalidation, and cleaner ownership. Reuse one removal implementation. Complete the gzip-to-deflate copy synchronously before finalization. Replace mapping-cache update closures with dictionary assignment. |
| WebSockets | Serialize frames into one final buffer and read small frame components directly into their result buffer. Preserve short reads and EOF. Remove unreachable compression machinery: the internal compression setting was permanently `None`, and compressed peer frames were already rejected. Preserve fragmentation, rejection and independent event payload arrays. |
| Diagnostics and security | Skip trace argument formatting when its level is disabled, after security observers run. Avoid constructing a discarded `ConcurrentBag` on every existing-IP hit. Preserve message observers, thresholds, shared history and regex timeout behavior. |
| Sessions, authentication, CORS, utilities and serialization | Remove the unused private session-ID helper. Retain public/protected extension points, session cookie behavior, JSON options, header parsing and validation. A URL normalization fast path was evaluated and discarded after timing regressions for repeated slashes. |
| CLI, JsonServer, dependency injection and testing helpers | Review allocation, reflection and lifecycle candidates. Keep ordered plugin discovery, request-scope ownership, testing response snapshots and JsonServer's serialized persistence contract. No speculative architecture or dependency replacement. |
| Tests, build, docs and maintenance scripts | Keep benchmark code outside shipped packages and the normal solution. Add allocation budgets to existing desktop CI, update test discovery floors, preserve pinned actions and dependency locks, and store local logs under ignored `TestResults`. |

The static-file cache previously had no oldest-entry pointer after insertion, so
its configured size purge could not evict entries. Concurrent replacement also
left incorrect links and inflated accounting. Late content writes could add size
to removed entries; removing a section twice could stop the cleaner for a live
section. Section membership and content accounting now change under the section
lock, with lock order section then item. Compression remains outside those locks.

The cache still uses its existing approximate object-size accounting and periodic
purge policy. `MaxSizeKb` is not an instantaneous hard limit, and active requests
may temporarily retain an evicted representation. Defaults and compression
thresholds are preserved.

## Reproduce the measurements

The dependency-free runner uses the repository SDK selection and project reference:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --verify-allocations
```

Omit `--verify-allocations` when measuring an older baseline. To compare revisions,
use the same runner source, SDK, runtime and machine in separate checkouts, with
`ProjectReference` pointing to each checkout's core. Build both in Release.
The runner warms each operation 20,000 times and reports the median of seven
100,000-operation rounds (10,000 for the 64 KiB serializer). Reflection resolves
internal delegates before measurement. Results count bytes allocated on the
current thread; these operations use synchronous in-memory input.

On Windows x64 with SDK 10.0.400 and .NET 10.0.11:

| Operation | Before, B/op | After, B/op | CI allocation ceiling, B/op |
| --- | ---: | ---: | ---: |
| Parse an already cached route | 1119 | 32 | 64 |
| Match two route parameters | 952 | 848 | 900 |
| Match a literal route | 400 | 320 | 360 |
| Read a two-byte WebSocket component | 480 | 104 | 256 |
| Serialize an empty WebSocket frame | 408 | 32 | 128 |
| Serialize a 125-byte WebSocket frame | 528 | 152 | 253 |
| Serialize a 126-byte WebSocket frame | 536 | 160 | 254 |
| Serialize a 65,536-byte WebSocket frame | 131592 | 65576 | 65664 |
| Write a disabled diagnostic message | 40 | 0 | 0 |

The unmodified 16 KiB read and URL normalization operations remain in the runner
as controls. Large reads retain incremental buffering to avoid allocating the
full peer-declared length before data arrives. Timings varied between runs;
allocation ceilings are enforced in CI, while elapsed times are informational.
These are microbenchmarks, not measurements of server throughput or p95/p99 HTTP
latency. Network, TLS, compression and application handler costs are not covered
by those timing claims.

## Validation

Fifty new cases exercise cache eviction, replacement, detached writes,
concurrency, shared-cache purge, cleaner ownership, compression conversions,
WebSocket length boundaries and reassembly, payload isolation, compressed-frame
rejection, route caching, short reads, fragmented HTTP input and response framing.
Eight cache regressions failed on the original snapshot; all new cases pass on
the audit changes. Compression conversion cases passed on the original snapshot
too; completing its discarded async copy is a lifecycle correction, not a claim
that content corruption was reproduced.

Local validation also passed 191 targeted cases, the existing 100,000-mutation
URL/query fuzz run with seed 1729, both library target builds, allocation budgets,
and public/protected API signature comparisons for `netstandard2.0` and `net10.0`.
The full local Windows run was interrupted because the separate test executable
triggered firewall prompts. Its partial results are not a complete-suite pass.
Use the required Windows/Linux/macOS and platform CI gates for complete validation.
No firewall settings should be broadened to run these examples.

Public and protected APIs, supported frameworks and dependency versions are
preserved. Removing internal compression code does not add WebSocket extension
negotiation or change compressed-frame rejection. Broader controller binding,
rate-limiter history ownership, cache policy or persistence redesigns need their
own workload measurements and compatibility review.
