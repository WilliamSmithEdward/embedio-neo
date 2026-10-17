# Stateful application-surface fuzzing

These test-only campaigns are included in the 2.3.0 source tree. They extend
[response-pipeline fuzzing](response-pipeline-fuzzing.md) using the existing
[state-machine runner](multi-connection-fuzzing.md), without changing production
APIs, defaults, targets or dependency groups. The fuzz executable now references
the repository's controller generator as a build-only analyzer.

## Run and reproduce

Use the pinned SDK and locked dependencies:

```sh
dotnet restore test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj --locked-mode
dotnet build test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj -c Release --no-restore
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --header-policy-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 forwarded-policy
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 cors-policy
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 conditional-ranges
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --extended-state-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 controller-parity
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 permanent-bans
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 server-lifecycle
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 file-mutation
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 request-codings
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 multipart-lifetime
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/request-codings-empty-chain.json
```

Each trace has three actors and at most 48 generated commands. Mandatory safety
sequences survive shrinking to an empty command trace. Failures retain exact
commands, invariant identity, transition counts and binary/runtime provenance.
Unexpected errors and deadlines are failures. No failed sample is silently retried.

## Campaigns and independent expectations

| Scenario | Surface and oracle |
|---|---|
| `forwarded-policy` | In-process requests alternate trusted, untrusted, standard-only, legacy-only and unprocessed routes. Known proxy chains check one/two/three-hop limits, nearest-proxy alignment of unequal lists, ignored sources, conflicting/malformed/oversized headers, IPv6 authorities, host rejection and unchanged physical connection values. |
| `cors-policy` | In-process requests alternate allowed, denied, missing and lookalike origins across restricted and wildcard modules. The oracle checks ordinary/preflight dispatch, configured versus echoed headers/methods, Vary preservation, absent credential/exposure grants and no per-request state leakage. It preserves legacy all-wildcard and case matching behavior; this is server-output validation, not browser CORS enforcement. |
| `conditional-ranges` | Generated GET/HEAD/QUERY/POST/OPTIONS calls combine strong/weak/wildcard/list validators, existence, date-precondition precedence and If-Range with single/suffix/open/invalid/unsupported/multiple ranges. A BigInteger interval model independently calculates clipping, unsatisfiable ranges, zero suffixes and numbers beyond long.MaxValue. Helper results are reported in a diagnostic response field; actual 304/206 framing remains covered by the response campaign. QUERY fixtures include their required media type. |
| `controller-parity` | Six requests enter application gates before release, split between reflection and actual source-generated registration. Three corresponding pairs use distinct signed values and request identities. Independent expected status/body checks complement parity of hook, invocation and awaited-release counts. Covers route/query/form/JSON binding, invalid input, asynchronous returns, HTTP/application errors, role denial and authorization before activation. |
| `permanent-bans` | A separate set model checks permanent/temporary capacity, promotion, removal, detached snapshots and reopen behavior. The actual JSON store is read independently. Gated worker batches add/remove distinct keys concurrently. A destination directory forces failed saves; neither confirmed memory nor saved disk bytes may change, and staging files must be removed. A second writer must fail while the lease is held. |
| `server-lifecycle` | Three real listeners start with held HTTP/1/HTTP/2 requests. Commands explicitly create an owner/request, complete it, cancel the caller, dispose, race cancellation with disposal, or check a healthy follow-up. Operations on retired owners do not implicitly start another listener. Entered handlers must observe cancellation, exit, and close their context once. Completed response identities must match their owner/generation. |
| `file-mutation` | Three gated identity/gzip/deflate requests overlap against a mutable filesystem provider and owned file cache. Every context closes before the next mutation. Same-size overwrites, changing sizes, deletion/recreation and unchanged generations check current bytes, bounded coding output, validator freshness, 304 bodylessness and rejection of deleted resources. Expected bytes and generation changes are generated outside the file/cache implementation. |
| `request-codings` | Three real HTTP/1 requests are held at application entry before reads begin. BCL encoders produce identity, gzip, deflate, Brotli and coding chains. Bounded reads with varied buffer sizes must produce the independently expected SHA-256, reject unsupported/truncated coding with 400 and decoded overflow with 413, accept the exact limit and preserve healthy subsequent requests. Empty gzip/deflate use complete canonical members because a zero-byte BCL write can emit no envelope. |
| `multipart-lifetime` | Three parsers first overlap in actual pending source reads. Known binary/field payloads exercise fragmentation, unread/disposed-part draining, limits during draining, missing closing delimiters, concurrent-operation rejection, and cancellation by operation token, lifetime token or reader disposal during a held body read. Disposing a public reader must preserve its borrowed source. |

All nine campaigns have a negative self-test that corrupts a response/body or persisted
state. It must fail with its named invariant; another exception or timeout does
not count as detecting the injected fault. These tests exercise the checks, not
production fault-injection switches.

## Ownership, ordering and deadlines

Gates establish application/source entry; timing sleeps do not establish overlap.
File generations receive distinct explicit timestamps, so same-size invalidation
does not depend on filesystem timestamp delays. Persistence uses a unique,
application-owned temporary directory and does not edit any real application store.

Lifecycle tests observe server-side handler cancellation and context closure
**before** canceling pending test-client work. This prevents an idempotent GET
from spending time reconnecting to an intentionally ended listener. It cannot
make a server that failed to cancel its handler pass. Client/server/request tasks
are observed during cleanup; gates are released and owned resources disposed.

Policy fixture trials also retained the omitted QUERY media type and corrected
expectations for nearest-proxy list alignment and legacy all-wildcard CORS.
No production behavior was changed to satisfy those expectations.

Initial fixture trials are retained under ignored `TestResults/latency-next`:
an immutable provider was inappropriate for a mutation oracle; lazy empty
compression output produced a malformed fixture; and client reconnect attempts
exhausted the lifecycle case deadline. Corrected fixtures replay the retained
inputs. No production fix or deadline increase is claimed for those trials.

The runner retains its 15-second case deadline and five-second cleanup grace.
The fixtures additionally bound their cleanup waits. The campaigns validate
correctness and lifetime behavior; their runtime is not a latency benchmark.

## CI and coverage map

The pinned Windows/Ubuntu state-machine jobs replay all thirteen application
traces and run nine negative checks. Controller, multipart and the three header
policy campaigns use `MODEL_CASES`
(500 normally, 5,000 scheduled); persistence and the real-listener/file/coding
campaigns use `CONNECTION_CASES` (32 normally, 128 scheduled). HTTP/3 response
coverage remains in the prepared Linux QUIC job. Configuration alone is not
evidence that a particular CI head passed.

| Existing coverage | New complementary coverage |
|---|---|
| Forwarding/CORS/precondition/range regression fixtures | Overlapping generated policy requests with independent output/interval expectations |
| URL/query FsCheck properties, literal-route parity and multipart framing/round trips | Stateful controller binding, multipart drain/cancel/dispose and pending-operation ownership |
| HTTP/1 head, HTTP/2/HTTP/3 and capsule transport mutation; WebSocket session mutations | Application request decoding chains and decoded-size boundaries |
| Shared budgets, rate permits, endpoint routing, tunnel/session models, bearer/JWT identity state | Permanent-ban disk state, failure rollback and cooperating-writer ownership |
| Real HTTP connection/identity isolation and response framing, immutable files, ranges, validators, cookies and trailers | Real-listener stop/disposal and mutable-file cache generations |

This is a gap audit of useful seams, not exhaustive project coverage or additional
coverage-guided AFL++/SharpFuzz instrumentation. These campaigns do not establish
Native AOT or native `System.Net.HttpListener` parity, browser policy enforcement,
arbitrary proxy grammars or live proxy transport, TLS trust/rotation correctness,
process-crash durability, cross-process writer races, arbitrary writes during
an active file read, ZIP mutation, all controller/custom-binding types or all
operating-system schedules. Attribute-specific per-route hook matrices remain in the dedicated regression/AOT
fixtures; this campaign checks the existing controller override and registration paths. Existing focused regression/platform
tests remain necessary for those surfaces.

The 2.4.0 `request-validation` campaign adds concurrent generated/reflection
validation parity with independent model expectations, limits, malformed JSON,
opt-in compatibility and bounded error responses. See [request validation](request-validation.md).

The unreleased `json-streaming` campaign exercises array/NDJSON framing,
empty and Unicode records, per-record budgets, generated/reflection parity,
HEAD suppression and concurrent producer cleanup. It uses independent parsed
value/count expectations and an enumerator ledger. See
[streaming JSON](streaming-json.md) for wire-level regression coverage and
cancellation/producer limitations.
