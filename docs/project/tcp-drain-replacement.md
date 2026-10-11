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
