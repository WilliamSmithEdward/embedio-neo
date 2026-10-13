# Performance, duplication and correctness audit

This audit starts from PR #182 at
`d2e733009c813ef433ea0d9dfe89dcf5231fb6eb`. It covers allocation costs,
duplicated logic, unused internals, bugs, and code that can be simplified without
changing the engine's ownership rules. Public APIs, listener defaults, target
frameworks and runtime dependency groups are preserved.

## Changes

HTTP/1 field parsing finds the final SP/HTAB-trimmed bounds before allocating the
value. It avoids an intermediate substring and the trim-character array. Name
validation, control-character rejection, framing checks and duplicate-field
handling are unchanged. The regression compares 1,766 whitespace and malformed
values with the prior trimming behavior, including every byte value.

WebSocket message delivery returns its existing raw bytes without decoding a
discarded UTF-8 string. The unused internal text property and its cache are
removed. Frame construction still makes its defensive copy; delivery leases,
disposal and callback ownership remain unchanged. Tests include malformed UTF-8
bytes and verify that first raw access allocates nothing.

Optional query/form binding skips the required-field presence scan. Required
fields retain their existing case-sensitive presence check; optional value
lookup remains case-insensitive. Typed bindings reuse the existing scalar and
array extraction helpers. HTTP/1 and multiplexed requests share charset lookup
and fallback, retaining the no-body guard and malformed-quote behavior.

The unused internal form MIME constant, frame enumerator, and a discarded local
in Set-Cookie parsing are removed.
`TestResponse`, a sealed internal testing helper, loses its no-op finalizer and
write-only closure flag. Its stream disposal and captured response body remain
unchanged. Active finalizers and public/protected extension points are retained.

## TLS callback correction

The two `ServerTlsPolicy` callback filters excluded only
`OutOfMemoryException`. They therefore converted synthetic
`StackOverflowException` and `AccessViolationException` into ordinary handshake
refusals, contrary to the documented recovery policy. Both now use the existing
`ExceptionPolicy.IsRecoverable` predicate.

Eight direct callback cases cover ordinary errors and all three excluded types.
On the unchanged baseline, four pass and four fail; the four failures are the
two missing exception exclusions at each boundary. Ordinary validator failures
still return false. Ordinary selector failures still become
`AuthenticationException` with the original inner exception. Excluded exceptions
propagate as the original instance. These are synthetic exceptions, not actual
resource exhaustion, stack overflow, or invalid memory access; downstream runtime
wrapping and process termination are not claimed.

## Reproducing measurements

Use the identical benchmark source against the baseline and changed core, with
the same Release configuration, SDK and runtime:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet build test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --audit-allocations
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --engine-parser
```

`--verify-audit-allocations` also enforces 48 bytes per fresh text event and
64 bytes per optional lookup. The event workload uses a preallocated 4 KiB
payload and includes construction, first raw access and disposal. The binding
workload uses a prebuilt collection with 100 unrelated keys. Both validate a
checksum; reflection and delegate compilation occur before measurement.
The existing parser workload includes context construction and pipeline handoff,
with no socket traffic or URI finalization. These microbenchmarks do not establish
whole-server throughput or latency improvements.

Windows x64, SDK 10.0.401, .NET 10.0.12, compared against the exact base above:

| Workload | Baseline bytes/op | Changed bytes/op |
| --- | ---: | ---: |
| Fresh 4 KiB text message, raw access and disposal | 8,264 | 40 |
| Optional scalar binding after 100 unrelated keys | 112 | 32 |
| HTTP/1 parser/context construction, two request headers | 3,144 | 2,752 |

The parser result is per request and agrees across pipeline batches of 1, 16 and
64. Timing varied across parser batches, so the retained claim is allocation
reduction. Raw logs, exact baseline source, failed attempts and the rejected
experiment remain under ignored `TestResults/audit` in the audit worktree.

An HPACK static-table dictionary experiment was rejected. Its exact wire hash
matched the original across the measured cases, but it added roughly 12 KiB of
one-time allocation and produced mixed timing results, including slower
status-only/static cases. The original lookup remains in production.

## Initial audit validation checkpoint

The initial 25-case audit source passed both library target builds with zero warnings/errors,
111 focused cases, and the full Windows suite: 5,692 passed, 21 skipped, zero
failures (5,713 reported). The suite completed in 5m07s under its unchanged
eight-minute budget. Twenty-five cases were added; the CI discovery floor is
increased from 5,688 to 5,713.

New and existing hot-path, listener-queue and cold-start allocation guards pass,
as do the SDK parser, suppression and changed-file formatting checks. The
approved shared test runner was restored byte-for-byte after validation; no
firewall rules or process environment settings were changed. The prior run with
the rejected HPACK experiment is retained separately from final-source results.
Initial fixture compilation/formatting failures remain in their original logs.

These results are local Windows evidence. This checkpoint does not claim fresh
Linux, macOS or MAUI validation, a merge into the engine PR, or a release.

## Expanded repository audit checkpoint (2026-10-13)

The audit subsequently expanded beyond the initial changes above. Run12 of the
expanded focused validation completed locked restore, formatting and the Release
solution build with zero warnings or errors. Its focused selection passed all
166 cases with zero failures or skips. The selection includes the new query,
charset, trailer, testing-adapter, CORS, file-range, IP-range, routing, cleanup,
OpenAPI and telemetry regressions, together with existing generator/OpenAPI tests.
The exact selection, source hashes and logs are retained under ignored
`TestResults/audit/run-12-expanded-focused`.

The earlier 5,713-case full-suite result belongs to the initial audit source;
it is not final-source validation of these later edits. The expanded source still
needs its final full-suite, resource and applicable platform checks. Neither the
focused result nor an allocation reduction establishes whole-engine performance
acceptance, completion of program #181, integration, or release. The rejected
HPACK experiment remains excluded, and the owner-deferred native QUIC/provider
and WebTransport investigation is not reopened by this audit.
## Expanded changes and evidence

The expanded pass covers all five requested categories: performance, duplication,
dead code, bugs and code complexity. File-level review methods and finding
outcomes are retained under `TestResults/audit/full-scan-*.json`. A structural
inventory or a passing test does not establish complete semantic review. Review
coverage is still being reconciled, and this checkpoint does not claim the full
repository audit is complete.

Shared query parsing now serves HTTP/1 and multiplexed requests while preserving
collection creation timing. Cookie formatting is shared by the core and testing
adapter; the adapter also preserves content headers, charset, request cookies and
separate response cookie fields. Compression uses a common transcode pipeline
with the existing identity, invalid-enum and target-specific behavior. Charset
and chunked-trailer parsing use segment bounds instead of temporary trimming
allocations. The measured allocation changes below use the same Windows SDK and
runtime as the initial measurements and the exact original baseline:

| Additional workload | Baseline bytes/op | Changed bytes/op |
| --- | ---: | ---: |
| Charset parameter present | 456 | 80 |
| Charset parameter absent | 320 | 0 |
| Two chunked trailers | 1,112 | 952 |

These are allocation measurements, not whole-server timing or throughput claims.
The charset parity fixture compares 3,073 inputs. IP range enumeration validates
input eagerly but produces the Cartesian result lazily; its candidate-only large
range case must not be run against the original eager baseline.

Correctness fixes cover legal short file reads and premature EOF, filesystem
containment at directory boundaries, wildcard CORS configuration, session
regeneration before first access, MIME wildcard matching, null Q-value lookup,
static attributed handlers, duplicate WebSocket batch registration, IPv6/wildcard
HTTPS authority parsing and telemetry cleanup when an observer throws. Generator
hint names preserve distinct escaped identifiers. OpenAPI merges compatible
query declarations and equivalent path shapes while reporting genuine conflicts.
Accepted-context dispatch always enters the cleanup owner even when cancellation
happens between acceptance and task scheduling.

Private, unconsumed fixture state and helpers are removed where callsite review
confirms they contribute no assertion or diagnostic output. Test files use owned
temporary directories and dispose clients. Streaming body checks keep their
existing failure assertions and deadlines through body reads. Tooling rejects
invalid campaign evidence, failed inventory builds, unknown campaign selections
and incomplete readiness. Process output and process exit share bounded waits;
timeouts clean up only the process tree started by the fixture. Merge helpers
leave dependency PRs open for review of all checks instead of automatically
arming merge after an aggregate gate.

Documentation corrections distinguish current APIs from historical 1.x listener
results, restored MAUI gates and prior measurements. Changed runnable examples
require their own compile/execution receipts; prose review does not supply them.

## Integrated validation checkpoint (2026-10-13)

The audit is rebased onto accepted engine `899261a3`, after PRs 376, 377 and 378
were merged with reviewed checks and artifacts. Run13 built the exact original
`d2e73300` source with selected new fixtures: 85 cases reported 31 passes and 54
failures. These are failing cases, not 54 distinct bugs. The old eager-IP large
range case was excluded to avoid exhausting memory. Run15 passed all 57 selected
compression, listing and accepted-context cancellation cases after the dispatch
cleanup correction.

Run16 built the integrated solution and allocation project without warnings or
errors and passed the parser guard. Its full Windows suite reported 5,902 cases:
5,880 passed, 21 skipped and one failed, in 5m06s. The failure is an initial
client `ConnectAsync` in the proxy-disconnection fixture with Windows socket
error 10048, AddressAlreadyInUse. Its cause is not established by that receipt.
The failed result and exact source hashes are retained; the approved runner was
restored byte-for-byte and the owned machine lock released. Later audit fixes
are not validated by this run, and a complete final-source pass remains required.

Native QUIC/provider and WebTransport investigation remains explicitly deferred.
Historical native failures, the retained HTTP/2 missing-response observation and
broader engine acceptance gaps remain separate, source-qualified records. This
audit checkpoint does not authorize a release or complete program 181.

Additional tooling regression evidence: run18 builds the original and corrected
host-metrics command with the identical synthetic peer harness. The baseline
passes normal output, pipe pressure and missing-executable controls, but fails
both stalls: open stdout exceeds the outer watchdog; closed stdout returns with
the child still running. The candidate passes all five controls and both stall
children exit after the unchanged ten-second deadline. Identity-verified cleanup
terminates baseline peers; the owned machine lock is released. Run17's earlier
harness analyzer errors are retained. The pure Python script suite subsequently
passed all 120 cases. Later benchmark, conformance and fixture changes still
require combined validation; these receipts do not certify them.

## Final local audit validation (2026-10-13)

The current source adds 219 cases to accepted engine `899261a3`, for a deliberate
5,924-case discovery minimum. Run21 built the solution, performance project and
standalone conformance, load benchmark, AppContainer, AOT and legacy HTTP-engine
projects with zero warnings or errors. All 665 selected cases passed, together
with the exception/endurance/teardown accounting controls and allocation budgets.
Its last Python guard incorrectly classified logical negation in a target-framework
conditional as nullable suppression. The corrected guard accepts conditional
expressions while retaining rejection inside their branches; all six guard tests
pass. The independent SDK parser remained green.

Run22 rechecked exact compiled source hashes and completed the full Windows suite:
5,903 passed, 21 expected skips, zero failures (5,924 reported), in 5m03s. Hot-path,
audit, queue and cold-start allocation checks, formatting and both analyzer guards
passed. The approved test runner was restored byte-for-byte and the owned lock
released. Run16's initial proxy-connect socket failure remains retained; later
passing checks do not establish its cause or claim a production repair.

The source inventory covers all 1,334 tracked and nonignored new files with current
review evidence and no ordinary source-review backlog. Forty-five native/provider
bodies remain explicitly deferred, with structural or partial review labeled as
such. The inventory is not native runtime acceptance. The nine offline HTTP/2
campaign-oracle tests pass in an isolated environment using the three existing
hash-pinned dependencies, and three fake-process comparison cleanup controls pass.
Changed example execution, hosted platform validation and final PR checks remain
separate gates; this local checkpoint does not complete v2 release readiness.

Current scanner-pin reconciliation uses main's YARA-X 1.21.0 and Forge 20261011
archives, verified against their SHA-256 pins. A raw Git-blob export of all 1,334
committed source files was scanned; this avoids Windows archive line-ending
conversion. Three case-anomaly acceptances became stale and were removed. The
new RequestBindingCleanupTest reflection match has a complete written review;
its exact-rule/path alert-dismissal exception remains pending. Scanner process
success is not a claim that all findings or merge requirements are satisfied.
The final pure Python tooling suite passes all 122 cases.

V2 consumer and drain follow-up: run23 builds the solution without warnings or
errors and all 20 admission/drain cases pass with actual HTTP/2 context completion
assertions separate from client delivery. Run24 reports 5,903 passes, 21 expected
skips and zero failures; allocation budgets, formatting and both guards pass,
with the approved runner restored. These passes do not establish the cause of
the retained PR352 missing response. All nine published-1.0.3 API comparisons
were refreshed against run23 binaries and their full deltas exactly match the
reviewed migration rows. Eight documentation consumer builds pass without
warnings/errors. Adapted in-process controls pass 29 MIME/pre-request assertions,
buffer success/overflow on both published/current assets, and local certificate
publication checks. The corrected MIME program explicitly targets the v2
nullable contract; its published-1.0.3 compilation failure is retained. Wire
commitment, exact TLS-program execution, hosted checks and packaging remain
separate gates. The binding fixture, its quoted review text and the older HTTP/2
fixture still require their exact matching-alert exceptions; none is inferred.

Version preparation and package validation: the candidate uses 2.0.0 metadata,
with nine own-package lock edges updated and external dependencies unchanged.
Run25 reports 5,903 passes, 21 expected skips and zero failures (5,924 total);
allocation checks, formatting and both guards pass, and the approved runner is
restored. Five local NuGet packages pass structure, metadata, notices and exact
build-asset verification. Both target-profile consumers compile with the packaged
generator, and the modern in-process consumer runs successfully. An independent
34-check review verifies package selections, imports, analyzer bytes and copied
runtime assemblies against these exact local packages. These receipts describe
the dirty local candidate; final committed and hosted artifacts require fresh
verification, including protection against cached packages of the same version.

The load harness now probes native QUIC identity only for HTTP/3, avoiding an
unneeded native runtime initialization during HTTP/1.1 and HTTP/2 diagnostics.
A bounded network-isolated Linux HTTP/2 control completes 7,798 byte-validated
responses with two connections and eight streams per connection. Histogram
counts, buckets and recomputed percentiles agree, exception census is empty,
and all four server connections close with no active or invalid ledger entries.
This validates the corrected harness; it is not a comparative performance or
endurance result and does not repair the retained Windows drain failure.

The current pinned YARA scanner completes a full scan of all five local 2.0.0
packages and their extracted assets. It finds only the two DI assembly debug
filename matches. Printed offsets agree with independent PE CodeView parsing.
The version-specific review is being refreshed; ClamAV, exact acceptance policy,
matching-alert requirements and final hosted scans remain separate gates.
A subsequent pinned ClamAV 1.5.4 scan updates to daily database 28152 on
2026-10-13 and scans all five archives and their extracted assets: 70 files,
zero infections, no scanner errors. This local result does not replace final
committed-source and hosted artifact scans.
## Finite TCP endurance validation (2026-10-13)

The explicit TCP-only EmbedIO profile completed a retained-process Linux campaign
on SDK 10.0.401/runtime 10.0.12, limited to two CPUs and 4 GiB. All 17 plan steps,
28 exact load/health reports and 18 forced settled snapshots passed their
correctness checks. Three steady/fault/WebSocket/mixed cycles were followed by
two idle listener restarts, a final steady phase and idle. The server observed
1,600,313 requests; every settled snapshot had zero in-flight requests, active
WebSockets and stale-generation work. All client workers drained, all health
checks passed, and the server exited without forced termination. Source and
binary readbacks passed, the owned container was removed and its lock released.

After the first two warmup snapshots, generation-one retained managed bytes
ranged from 2,677,232 to 3,623,528; handles levelled at 130. Private bytes rose
by 40,497,152 between the first and last retained observations in that generation,
and GC committed bytes also varied. These resource readings require analysis;
a correctness pass does not establish absence of indefinite retention. Three
idle-hold observations ended without a closure outcome and remain censored,
with no timeout-behavior claim. HTTP/3, deferred native-provider experiments,
under-load drain reproduction and other platforms were excluded from this proof.

The tested core SHA-256 was
`dfa51e214066e63f058f4457ab87c53895c897c2402652092a1e6deb7846f32b`.
This is a working-tree build identity, not a final release artifact hash.
## Conformance and load-harness follow-up (2026-10-13)

HTTP/2 and HTTP/3 campaigns now share one bounded resource-observation helper.
A configured observation endpoint must return successful HTTP framing and the
expected nonnegative integer counters; unavailable, malformed and unsettled
observations fail the campaign instead of becoming fabricated zero counters.
The HTTP/3 baseline controls captured 22 failed subtests and two errors across
seven test methods, including previously ignored observation failures. The
corrected seven-method suite, nine shared framing controls and existing HTTP/2
controls pass. Protocol case bodies and stateful fuzz loops remain unchanged.
These offline controls do not replace live public HTTP/3 validation on final
source, and deferred native-provider investigation remains separate.

The load client now records a worker failure immediately and cancels its sibling
workers before awaiting their combined completion. Caller cancellation is
reported even when workers never enter the request loop; a later cancellation
cannot replace the first recorded workload error. Two strict builds completed
without warnings or errors. The original header-failure control exceeded its
fixture deadline, while all six corrected controls pass, covering blocked
siblings, body mismatch, active and pre-canceled callers, first-error retention,
and validated request/latency accounting. The baseline process stopped at its
first timeout, so it did not independently execute the caller-cancellation
negative controls. That finding is source-confirmed and candidate-tested.

The ordinary comparative matrix remains failed: its Kestrel cleartext HTTP/2
64 KiB control did not finish within the unchanged deadline. A separately
instrumented control reproduced the stall. Event counters alone do not identify
its cause, and the load-client fixes do not establish a repair. Failed receipts
remain retained. A passive diagnostic is being reviewed; no performance ranking
or completed comparative gate is claimed from diagnostic instrumentation.

## Application comparisons (2026-10-13)

The full application matrix completed all 84 samples: 14 JSON, static-file and
WebSocket workloads, both exact parent `899261a3` and current cores, in three
rounds. All response-byte, status, protocol, histogram, connection-lifetime and
clean-shutdown checks passed. Both strict builds had zero warnings or errors;
source, common-client, dependency and binary identity checks passed, and the
owned container and machine lock were released. Independent terminal-only
analysis recomputed the accounting, percentiles and per-request metrics.

For WebSocket text messages, median server allocation was 5,068.45 bytes per
validated exchange versus 7,149.81 on the parent, about 29% lower. This scoped
result is not a universal throughput or allocation improvement. First-fill
means 128 distinct application-cache misses, not cold OS or disk caches.

The first-fill HTTP/2 workload needs follow-up: client CPU per request increased
by 76.96%, 32.61% and 88.61% across the three paired rounds, while p99 latency
increased by 160.65%, 22.39% and 188.63%. All 128 responses remained correct in
every sample. Candidate elapsed times ranged from 0.083 to 0.348 seconds and
parent times from 0.082 to 0.100 seconds; route initialization, JIT and file-system
cache state are not independently attributed by this experiment. The retained
comparisons therefore flag investigation, without claiming a source cause or
statistical significance. The separate failed Kestrel control remains unresolved.

Public HTTP/3 follow-up completed on the pinned Linux tool profile using the
ordinary `System.Net.Quic` provider. All eight existing cases passed their
conformance or permitted-policy verdicts, including 200 rounds of four canceled
uploads and 2,000 rapid stream cancellations. The full scale-one stateful
campaign completed 100 connections, 720 streams and 69 client cancellations,
with zero server resets. Configured resource observations passed, handlers
settled to zero and both separate server processes exited normally. All 493
source-input checks and runtime-binary readbacks passed; the owned container
was removed and the machine lock released. This closes the current-source
public HTTP/3 resource follow-up, without extending the explicitly deferred
native application-provider or WebTransport scope or replacing hosted checks.


## File first-fill flow-control diagnostic (2026-10-13)

Eight separate instrumented samples passed: parent and candidate cores, default
and explicit 131,072-byte public client windows, and two rounds with both window
and engine order reversed. Both strict builds and compiled EventSource controls
passed with zero warnings or errors. All 128 responses per sample passed the
body and accounting checks; observer bounds and all four final source, binary
and plan guards passed. The owned container and machine lock were released.

The larger window eliminated observed slow reservation-path calls in all four
samples, but candidate p99 remained 113.05 and 155.30 ms versus parent 47.90 and
68.09 ms. Default-window results changed direction between rounds: candidate
p99 was 45.47 and 87.97 ms versus parent 44.99 and 228.97 ms. Reservation-path
elapsed sums overlap concurrent writes and include cancellation-source setup;
they are neither CPU time nor an isolated suspension measurement. Instrumentation
also changes timing. These observations narrow the investigation without
explaining the original first-fill regression or changing production defaults.
The original performance finding and failed default Kestrel control remain open.


## Framework-only HTTP/2 boundary controls (2026-10-13)

An independently built Kestrel/BCL application reproduced a bounded warmup
completion failure with a 65,536-byte response and the default client window.
Its project has only the Microsoft.AspNetCore.App framework reference: dependency
assets are empty, output libraries contain only the minimal application, and no
EmbedIO assembly was present. The loaded Kestrel 10.0.12 product commit and binary
hash matched the retained benchmark runtime. Compilation passed with zero
warnings or errors; source, binary and plan integrity plus owned cleanup passed.
No WARM report arrived within 30 seconds, and both children required cleanup.

Two separately declared controls passed: an explicit 131,072-byte client window
validated 676,023 measured responses, while a 65,535-byte payload with the default
window validated 670,483. Including warmup, server admission/completion counts
were 848,686 and 839,077 respectively, with zero active requests and normal
process exits. Both strict builds, dependency proofs, final integrity checks and
owned cleanup passed. These are boundary diagnostics, not performance rankings
or substitutes for the failed default comparison.

This establishes that the external completion-failure symptom can occur without
EmbedIO dependencies. It does not establish an identical cause to the original
benchmark stall or a universal runtime defect. The minimal client uses a linked
30-second phase deadline and the server has a 30-second stop-command wait near
the observation deadline; no completed-request or per-stream report was available
for the failed sample. The default comparative gate therefore remains unresolved.
Public Kestrel queue logging in the separate full harness found no queue-overflow
or output-loop error explaining its two stuck responses; observed first-chance
I/O exceptions are not confirmed workload failures.


## Documentation runtime follow-up (2026-10-13)

The unchanged complete certificate-guide program compiled against the current
2.0.0 core with zero warnings or errors. Two distinct trusted, hostname-verified
TLS connections returned the exact HTTP/1.1 hello response and transport EOF;
wrong-hostname and untrusted-certificate controls were rejected. SIGINT produced
a graceful zero exit. All 460 selected source inputs, 13 output hashes and ten
executed plan files were independently checked, and disposable private-key files
were absent after owned cleanup. The unchanged one-hour reload interval was not
exercised; this is not certificate-rotation or HTTP/2/HTTP/3 acceptance.

All three deferred-response guide snippets also passed five real managed HTTP/1
TCP cases in an authored host: unwind commitment, deferred success/failure, and
bounded success/overflow. Four gated report callbacks produced no response byte
during the finite 150 ms pre-release observation. Expected status, Content-Length,
body, transport EOF, replacement-handler counts and subsequent health requests
passed. Compilation had zero warnings or errors; independent verification covered
460 source inputs, 14 outputs and ten plan files. The initial fixture import failure
and a subsequent authored health-route framing mismatch remain retained; corrections
changed only the wrapper, not guide statements or production code. Cancellation,
compression and other protocols are separate evidence scopes.

A standalone synthetic EventPipe gate validated the pinned cached trace parser
against the exact .NET 10.0.12 runtime. It decoded 901 known managed CPU samples,
phase-specific JIT/allocation/GC events and zero reported loss, while rejecting a
missing marker, physical truncation, invalid format and oversized input. Both
strict builds and final source/binary/plan checks passed with owned cleanup. This
establishes compatibility for bounded profiling, not a performance conclusion;
the original first-fill regression and default comparison failure remain open.


## First-fill trace follow-up (2026-10-13)

Four instrumented default-window samples completed with exact 128-response
body/accounting checks, normal process exits and zero reported trace loss.
Both application builds and the adapted parser passed strict compilation;
retained synthetic positive/negative traces passed before any listener ran.
All four final source, marker-input, binary and plan guards passed, with owned
container removal and machine-lock release. The initial parser analyzer failure
was retained and corrected with explicit ordinal comparisons only.

These traces do not establish a performance ranking or source cause. SampleProfiler
records thread samples that include external/native/waiting activity. A separate
strictly compiled offline inspector verified the cached public enum and all nine
read-only ETLX files, reproducing the 901 known synthetic samples and reporting
zero Error-type samples. Measured client Managed-type samples were 45/47 for the
candidate and 20/22 for the parent; most other samples were External. External
samples can include native CPU as well as waiting, and managed classifications
are not exact process-CPU counters. Unresolved frames, omitted method rows,
short measurement windows and instrumentation overhead remain explicit limits.
The original first-fill investigation and failed default comparison remain open.

## Current cache-buffer ownership change and integration status

The ordinary file-cache fill now adopts its private MemoryStream buffer only
when its capacity exactly equals its final length. Spare-capacity buffers still
use ToArray. The provider receives the existing CompressionStream wrapper;
the owner disposes the private stream before publishing the cache entry.
Precompressed variants, public APIs and provider ownership are unchanged.

All 19 new ownership regressions passed on Windows, including retained-wrapper
late writes, compression footers, stale source lengths, cancellation/failure
retry, ranges and disposal ordering. Both library targets and the strict solution
build passed. The identical-source paired allocation control is prepared but
has not run; this is not evidence that the earlier first-fill performance
comparison has been repaired. CI's discovery floor is deliberately raised from
5,924 to 5,943 for these cases. The final combined suite remains required.

The current audit is being integrated into draft PR #182 as one commit. Release
acceptance remains open: the owner-approved explicit extended-CONNECT handoff
guard and its migration notes are not implemented yet; browser HTTP/3 discovery
is prepared but unrun; the first-fill and ordinary-default HTTP/2 performance
comparisons remain unresolved; final exact-source platform, package, security
and CI gates remain required. Scanner reviews are not automatic acceptance of
new findings or a waiver of matching-alert dismissal requirements. No release
or version tag has been published by this audit.

## Approved extended-CONNECT guard, changed-source validation

The owner approved mandatory explicit extended-CONNECT acceptance on 2026-10-13.
The shared H2/H3 response now rejects an unaccepted successful final response
with empty 501 before headers, including coalesced first writes, explicit flush,
empty close/dispose and status edits. Later ordinary writes are discarded.
AcceptTunnelAsync records acceptance before its legitimate flush; actual
WebSocket acceptance records its separate handoff before flushing. The protocol
token alone does not grant acceptance. Application dispatch, explicit errors,
plain CONNECT and H1 remain unchanged. Migration and release notes explicitly
cover direct Request/Response custom extension implementations.

On the retained unchanged core, all 28 new H2 cleartext/TLS guard cases and all
14 H3 guard cases failed on actual 200/201/204 success. On the candidate, all
38 H2 cases and 19 public System.Net.Quic H3 cases passed, including application
400/401/403/404/501 bodies and healthy siblings on the same connection. The
broader selected tunnel/WebSocket suite passed 519 cases with zero skips. Both
library targets and strict solution builds reported zero warnings/errors;
runner restoration and owned-lock cleanup were verified. This does not yet
prove the final combined suite or all hosted/platform gates. The discovery floor
is raised to 6,000 for the 57 new cases.

PR #182 now includes the prior audit commit 5d2a6e80 and content-identical main
reconciliation a7aa1365. Their security/malware failures are retained: the former
re-emits the previously dismissed immutable-ZIP-provider false positive, reviewed
in .github/security/zip-provider-codeql-review.md; the latter reports two new
binding review/test paths, v2 DI binary paths and stale v1 DI paths. The new
narrow malware acceptance/alert-dismissal exceptions remain pending. Neither
failure is canceled or considered a green result.

## macOS coverage budget decision

CI 38421579986 on a7aa1365 passed Windows/Linux, package/allocation checks and
platform smoke jobs, but macOS coverage stopped at the whole-run limit after
5,408 of 5,943 cases (5,388 passed, 20 skipped, zero reported case failures).
Its TRX case durations sum to 462.073 seconds; the remaining fixtures did not
complete. This is incomplete acceptance. William approved raising only macOS
CI coverage to twelve minutes on 2026-10-13. Windows/Linux remain at eight
minutes, with the unchanged 15-minute job bound, per-case deadlines, coverage,
iterations, discovery minimum and failure propagation. No retry or skip was
added. A complete changed-source macOS pass remains required.

The local F2 combined validation reported all 6,000 cases in 5m09s, with 5,978
passed, 21 expected skips and one existing HTTP/1 burst-test socket connection
failure (10055, NoBufferSpaceAvailable). All four unchanged burst/reset cases
then passed in isolation. The original failure and isolated result are retained;
they do not establish a repaired resource defect. A second complete run on the
same production/test source is in progress.

The unchanged second local full run finished successfully in 5m10s: all 6,000
cases reported, 5,979 passed and 21 expected skips. Strict build, analyzer/parser
and suppression guards, formatting, exact runner restoration and owned nonce
release passed. Current compiler-input hashes match the tested source. The
first run's 10055 failure remains recorded; no socket-resource repair is claimed.
Fresh exact-head hosted validation is still required after integration.

## Narrow malware acceptance decisions

William approved the two binding fixture/review ASP paths and the four DI
binary/review God Mode paths on 2026-10-13, waiving only their unavailable
matching-alert dismissals. He separately approved that same narrow unavailable
alert exception for the existing Http2RequestHeadersTest.cs ASP finding.
The DI binary paths change only from 1.0.3 to 2.0.0; the two binding paths are
new separate acceptances. The approved acceptance set matches all eleven
findings in downloaded malware run 38421579803 exactly, with no extra/stale
pair. Its ClamAV result has zero infected files. The unchanged report script
passes on those actual scan artifacts with the updated policy. This recheck is
not a fresh current-head scan or a green hosted check. Original failures remain
retained. No genuine finding, other rule/path, tool error or scan requirement
is suppressed or waived; fresh exact-head validation remains mandatory.
