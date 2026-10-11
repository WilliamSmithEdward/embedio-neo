# TCP drain and replacement investigation

Issue [#279](https://github.com/WilliamSmithEdward/embedio-neo/issues/279)
tracks two approximately 21-second unanswered TCP connects reported by the
[interim endurance campaign](https://github.com/WilliamSmithEdward/embedio-neo/pull/276).
One started during drain/replacement and one about forty seconds after replacement.
The report tested `a4f710574748090ef5bad0871d68f28bcf9f03be` on a busy Windows
Hyper-V guest. Those observations have no isolated reproduction or confirmed cause.

This investigation starts from integration PR #182 at
`b3e55b5a41e68fb252a95f2ba22dafc1a6637cbb`, after the TCP endpoint/admission/accept
actors were independently rewritten. Passing these cases does not establish that
the rewritten actor corrected either historical timeout.

## Bounded diagnostic cases

`TcpDrainReplacementTest` adds nine ordinary regression cases:

- Eight cases each perform sixteen retire/replace cycles on one fixed port. They
  cover IPv4 literals, IPv6 literals, both localhost families, and both wildcard
  spellings over each family. Actual connect targets are explicit loopback addresses;
  request Host headers match the named registration, avoiding DNS-order ambiguity.
- Every retiring generation has a verified accepted request whose handler is held
  open. Sixteen fresh connection attempts are initiated before graceful drain and
  awaited during that disruption window. A refused/reset/aborted connect is allowed
  only in that window. A connect deadline or any other unexpected error fails.
- Drain must preserve the accepted response, close the listening socket, and finish
  its accept worker successfully. Replacement endpoints must be different objects.
  Sixteen independent HTTP/1.1 requests validate the replacement's generation body
  and complete wire response. Another succeeds after disposal of the retained old
  server object. The replacement itself then drains, releases its listener handle,
  and finishes its worker. No client retries occur.
- A separate case replaces a listener, validates it, leaves it without traffic for
  forty seconds, and requires a fresh HTTP/1.1 connection and correct response.

The eight churn cases contain 128 replacement cycles, 2,048 disruption-window
connects and 2,304 validated HTTP exchanges. The quiet case adds three exchanges.
Each run therefore records 4,355 actual connection attempts, without pooling or
application retries. IPv6 cases explicitly skip on a host without IPv6 support.

The tests print UTC timestamps, monotonically measured attempt durations, explicit
remote endpoints, phase/generation, connection outcomes and socket errors. Listener
snapshots include bound addresses, unique endpoint object identities, stopped/closed
state, accept-task status and exceptions before/during/after drain and replacement.
Passing and failed test output is retained in CI TRX artifacts. An unexpected failure
stays failed; cleanup releases owned objects without retrying the attempt.

## Reproduction

From a locked restore and Release build:

```powershell
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter FullyQualifiedName~TcpDrainReplacementTest --report-trx --results-directory TestResults/issue-279 --timeout 2m --minimum-expected-tests 9
```

The ordinary CI matrix already includes these tests. Its discovery floor increases
from 4,625 to 4,634; existing budgets, coverage and gates remain unchanged. This adds
no production code, APIs, defaults, dependencies or operating-system tuning.

## Evidence and remaining work

The initial nine-case Windows campaign passed in 42.5 seconds. The equivalent
Linux campaign passed in 41.9 seconds in the installed SDK image
`sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`.
Final Linux validation, including accept-worker completion and actual bound-address
snapshots, passed all nine cases in 42.0 seconds. Its retained TRX records 2,033
successful disruption connects and 15 resets, all 2,307 validated exchanges, and no
unanswered connect. The final Windows full suite passed 4,634 reported cases (4,625 passed, nine explicit
platform/campaign skips, zero failures) in 4m15s under the existing five-minute
budget. All nine new cases passed; their retained client traces show no unanswered
connect and all 2,307 validated exchanges.

Local evidence is retained under the dedicated worktree's ignored
`TestResults/issue-279`. The first fixture attempt mistakenly sent a localhost Host
header to a literal-host registration; it was interrupted and corrected, not
classified as a listener failure. An initial solution build lacked restore assets
for three additional solution projects; a complete locked restore and rebuild
passed. An interrupted local suite command used an incorrect seven-minute timeout;
its log is retained, and final validation uses the existing five-minute Windows
budget. No failed product attempt is replaced by a successful retry.

The bounded campaigns have not reproduced the historical unanswered connect.
They do not recreate the original eight-hour mixed-load campaign, TLS/QUIC traffic,
Hyper-V scheduling pressure or operating-system backlog saturation. A quiet local
pass cannot distinguish those causes. No admission, endpoint ownership, address
reuse or host-contention cause is asserted, and no speculative production fix is
proposed.

Issue #279 remains open for the original failure: capture a current-engine mixed-load
failure with these listener-generation traces plus client socket timestamps/errors,
TCP/backlog evidence and host scheduling/resource evidence. If it recurs, identify
the mechanism and add its deterministic regression before changing production
behavior. A bounded clean run is useful coverage, not proof that intermittent
connect timeouts have been eliminated.

## Longer mixed-load campaign

The owner authorized a longer current-engine investigation after the bounded
regressions did not reproduce the reported timeouts. The test-only staging script
`scripts/prepare_tcp_endurance.py` copies the existing PR #276 harness from pinned
commit `d6097ea7a28f40266171e77537e10f8160c1bda9` and applies the committed
`test/EmbedIO.LoadBenchmark/tcp-endurance-diagnostics.patch`. It references the
checkout's core without changing production code or modifying the original harness
branch. A new output label is required; previous evidence is never overwritten.

```powershell
git fetch origin d6097ea7a28f40266171e77537e10f8160c1bda9
python scripts/prepare_tcp_endurance.py tcp-investigation
dotnet restore TestResults/tcp-investigation/EnduranceHarness/EmbedIO.LoadBenchmark.csproj --locked-mode
dotnet build TestResults/tcp-investigation/EnduranceHarness/EmbedIO.LoadBenchmark.csproj -c Release --no-restore
dotnet TestResults/tcp-investigation/EnduranceHarness/bin/Release/net10.0/EmbedIO.LoadBenchmark.dll endurance --output TestResults/tcp-investigation/campaign --plan "idle:10,load:steady:120,drain:mixed:90:16,load:steady:120,idle:40,load:health:10" --settle 10 --rate-scale 0.25 --interval-seconds 10 --revision <tested-checkout-commit>
```

This deliberately exercises HTTP/1.1, HTTP/1.1 over TLS, HTTP/2 cleartext/TLS and
HTTP/3 concurrently, plus cancellations, resets, slow readers/writers, partial
heads, idle connections and WebSocket traffic. Each cycle replaces listeners while
the client workload is still running, then validates healthy subsequent exchanges.
The harness preserves its original request limits and drain/operation deadlines.
Separate failed exchanges are retained; continuing the workload is not an automatic
retry of a failed exchange. It does not infer resolution from a green aggregate.

Extra JSON records in the client stderr artifacts pair every direct HTTP/1 TCP
connect start/end, including exact destination port, local endpoint on success,
UTC timestamp, monotonic duration, TLS/disruption state and full failure stack.
Every workload error is recorded, including errors that the original harness
categorizes as occurring during intentional disruption. A TCP timeout must be
investigated even in that category. Server stderr contains listener-generation,
endpoint identity, bound address, pending-session count, accept-worker state,
thread-pool availability and stop/drain/dispose snapshots. The normal five-second
resource samples retain per-port TCP states and host CPU/resource evidence.

Instrumentation deliberately retains endpoint objects to inspect their completed
workers after retirement. This makes the campaign unsuitable for attributing
retained-memory growth or drawing allocation/throughput conclusions. Timing may also
be affected by tracing and other activity on the shared host. Server stderr is
collected when the server exits; resource samples and completed client reports are
available during the run. This is runtime/ownership evidence, not a TCP packet capture.

The local smoke campaign passed one twenty-second mixed-load drain cycle plus
post-phase health checks. It recorded 316 direct TCP connection completions, including
173 refusals during deliberate disruption and no timeout; its maximum recorded
connect duration was 2.06 seconds. The completed longer-campaign results are recorded below; the historical cause
remains unconfirmed.

### Integration reconciliation

While the longer campaign ran, the integration branch advanced to
`3f86edf004f246961968e0d7e936a42593ef4ea0` with separate idle-closure and teardown
accounting work. The TCP endpoint/admission/accept actor source is unchanged.
Both documentation entries are preserved and both discovery guards combine the new
base's 4,639 cases with these nine cases, for a 4,648 minimum. The running campaign
keeps its originally recorded binary, hashes and source identity. Reconciliation
does not replace its evidence with results from an untested binary.

### Completed longer-campaign results

[Committed evidence](evidence/tcp-drain-replacement-279.json) records the exact
binaries, plan, runtime and limitations. All six plan steps passed over approximately
38 minutes on Windows 10.0.26300 / .NET 10.0.12, Ryzen 7 9800X3D with sixteen logical
processors. This is a different host/OS build from the historical Hyper-V report,
and paced rates were reduced with `--rate-scale 0.25`.

- Sixteen mixed-load drain/replacement cycles and subsequent health checks passed;
  the process used seventeen listener generations.
- 9,862,848 validated exchanges completed across 37 client phases, with zero
  reported unexpected client errors and all workers finishing within the original
  shutdown bound. No stale-generation requests occurred.
- All 17,560 direct HTTP/1 TCP connects have paired start/end evidence: 14,668
  succeeded; 2,892 were refused during deliberate disruption. There were zero TCP
  connect timeouts, including inside those disruption windows.
- The longest connect took 2,589 ms and ended in refusal during deliberate
  disruption. No refusal occurred outside that window. All 6,116 captured workload
  interruption exceptions remain available; they are not described as absent.
- All 68 recorded endpoint objects ended with closed listening handles and
  successfully completed accept workers. There were no captured endpoint faults,
  no incomplete connect traces, and the server exited cleanly without forced killing.
- The final steady-load phase, forty-second traffic-free endpoint interval and
  subsequent health traffic also passed. Binary hashes were verified unchanged
  after completion.

Use the independent summarizer after the server exits:

```powershell
python scripts/summarize_tcp_endurance.py TestResults/tcp-investigation/campaign --output TestResults/tcp-investigation/summary.json
```

It fails for TCP timeouts even when the original harness labels them as intentional
shutdown disruption, incomplete connect evidence, endpoint faults/open handles,
worker-bound violations, failed plan steps, an incomplete server exit, or reported
client errors. Summary outputs use exclusive creation so earlier evidence is not
overwritten. Controlled hidden-timeout, incomplete-trace, worker-bound and unfinished
campaign inputs are retained separately from actual runtime results.

This materially extends coverage beyond the bounded regressions, but does not
reproduce or explain the old two approximately 21-second timeouts. No production
fix is inferred. The original environment, eight-hour duration, full-rate load and
TCP packet evidence remain unvalidated; issue #279 remains open for that concrete
reproduction/attribution gap.

### Owner-approved CI scheduling correction

CI run 38112614865 on reconciliation head `8a6274b` hit its unchanged five-minute
Windows limit with 4,322 results and zero reported assertion failures. Its original
log is preserved. The new forty-second quiet case contributed serialized elapsed
work; the discovery gate correctly rejected the incomplete suite.

William explicitly approved running that case concurrently on copied binaries.
`scripts/run_tcp_regression_ci.py` keeps the main suite's platform deadline and
coverage, requires 4,647 main cases plus the separately required quiet case, runs
coverage against separate assembly copies, and retains separate TRX/coverage/log
artifacts. The quiet case uses an ephemeral port so it cannot reuse the other
process's sequential test-port allocation. Its assertions and per-operation
limits remain unchanged. A quiet-case failure returns a non-quarantinable error;
the existing MsQuic classifier still examines only the main TRX and cannot hide
that failure. No test, coverage run or platform is dropped and no deadline is
increased. Final local and hosted results are recorded in the PR.

The approved split passed locally with coverage on both processes: main TRX reports
4,647 cases (4,638 passed, nine explicit skips, zero failures); quiet TRX reports
one passed case, zero skips/failures. Both coverage artifacts exist, and main,
copied and source core DLL bytes match after collector restoration. Eight synthetic
summary faults are rejected, and the successful control passes. Five driver exit
controls prove that quiet failure cannot enter the main-only quarantine path.
Source/parser/format guards, YAML parsing, pinned offline zizmor and diff checks
pass. The earlier failed hosted run remains recorded; fresh exact-head hosted
checks are required before acceptance.
