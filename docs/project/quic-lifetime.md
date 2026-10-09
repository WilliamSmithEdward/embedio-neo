# QUIC listener disposal, rebind and workaround retirement

This guide records the QUIC lifetime investigation for [program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181),
[draft PR #200](https://github.com/WilliamSmithEdward/embedio-neo/pull/200), and the
HTTP-engine integration in [draft PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182).
[Issue #202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202) tracks the
remaining upstream-reporting, native dependency and retirement questions. The
candidate is unreleased. This document does not claim integration on main or
completion of the HTTP engine.

## Two native lifetime behaviors

A completed managed `QuicListener.DisposeAsync` does not, in the tested versions,
guarantee that another listener can immediately bind the same endpoint.

On Darwin, .NET 10.0.12 waits for the listener STOP_COMPLETE event. MsQuic 2.6.2
(`819ab74f`) releases the binding, but the kqueue datapath queues socket-context
shutdown to its partition worker. The descriptor closes later. In twelve traced
failures across Apple Silicon and Intel, the replacement bind began before the
old descriptor's close completed. Closure occurred 25–230 microseconds after
managed disposal returned. The inspected MsQuic main revision `931fdf77` retained
the same kqueue datapath implementation; this is a recorded investigation result,
not a claim about every future upstream revision.

A separate lifetime applies to accepted connections: they share the listener's
native binding, which can survive listener disposal until native connection
cleanup (`QuicConnFree`). The agent's Windows contention campaign recorded four
serve/stop/restart failures in 7,040 restarts on .NET 10.0.12, which bundles
MsQuic 2.5.10. One failure used port 20423, outside the UDP dynamic range, so that
observation cannot be explained solely by ephemeral-port reuse. The pinned Linux
campaign used MsQuic 2.6.2 and did not reproduce this behavior; absence in that
campaign does not establish immunity.

The Darwin listener-only ordering and the accepted-connection binding lifetime
must be evaluated separately. Correcting one does not prove the other corrected.
The investigation's Kestrel and WatsonWebserver reference review found no matching
retry. No production implementation code was copied from those projects.

## Managed retry and its bounds

The all-platform candidate is PR #200 head `45b4097`, based on engine `24cb39d`.
`Http3Listener` records the configured endpoint when `QuicListener.DisposeAsync`
returns. Internal `QuicEndpointReleases` uses a monotonic clock and retains recent
release timestamps. Recording another release prunes timestamps older than the
one-second window.

A failed bind is retried only when its error is `AddressAlreadyInUse` and this
process recorded releasing that same endpoint within one second. Attempts sleep
for one millisecond. Unrecorded endpoints and other socket errors propagate
immediately. A genuine persistent conflict still fails when the release window
expires; the last socket error is propagated. Re-recording an endpoint starts a
new window. There is no blanket retry of arbitrary listener-start failures and
no change to package dependencies or public APIs.

This is a synchronous startup workaround. It neither guarantees native disposal
ordering nor prevents other processes from binding the endpoint. Review of
endpoint identity, timestamp retention, concurrent release behavior and startup
bounds remains part of owner integration. The final source must be revalidated
when integrated with other listener changes.

## Evidence and limits

| Campaign | Result | Limit |
| --- | --- | --- |
| Darwin unpatched control, before managed retry | EmbedIO restart failed 2/20 runs | Pinned runtime/native version and host |
| Darwin unpatched control, with managed retry | EmbedIO restart passed 20/20 runs | Full experiment also had an unrelated WebSocket failure |
| Windows contention, final agent candidate | No failures in 7,680 restarts | Agent-reported campaign, not a general reliability guarantee |
| Test-only native close patch, both Macs | No failures in 80 paired runs and forty 4,096-cycle probes | Native patch is not shipped |
| Owner combined retry/admission focused Windows set | 23 cases, zero failures | Full combined Windows validation also passed |

The before and after experiments are [37981961893](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37981961893)
and [37982623556](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37982623556).
The latter's owned restart cases passed, but its full suite failed on
`OriginalDelayedLargeReplyPatternSurvivesConcurrentBroadcasts(Microsoft,1)`.
A previous experiment observed the same fixture family with `(Microsoft,2)`.
The cause is unconfirmed and is not attributed to QUIC.

The final-head [experiment 37985906512](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37985906512)
at `45b4097` completed with failure. Its native socket cleanup comparison and
three desktop regression jobs passed. The macOS compatibility job failed before
allocation checks when the upstream `dotnet list package --vulnerable` subprocess
returned exit 137. The cause of that termination is unconfirmed; it remains a
failed check. Final-head [PR CI 37985912366](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37985912366)
completed with overall SUCCESS and every check terminal success or intentional skip. The separate experiment remains failed. A green individual job does
not establish overall acceptance. Inspect every check on the exact integration
head before merge.

Owner integration reports 4,388 Windows cases (4,383 passed, five expected local/platform skips, zero failed), and 99 focused Linux HTTP/3/retry/admission cases, all passed. Both targets build without warnings/errors; analyzer guards, changed-source formatting and all four resource budgets pass. The narrowed classifier was checked against 41 real retained TRX files: ten contain known raw bind failures, while the unrelated WebSocket failure remains excluded. Four counterexamples with a different method, owned restart method, wrong stage or wrong socket error are also excluded. Combined-source macOS CI, native experiment and final admission load comparison remain pending.

Raw TRX files, native traces, command logs, binary hashes and control/candidate
results are retained in ignored TestResults artifacts. Historical engine-log
sections describe earlier candidates and decisions; this guide records the
current all-platform policy without rewriting the original failure evidence.

## Narrow owner-approved quarantine

William approved removing Intel macOS CI, extending the managed retry to all
platforms, and quarantining the identified raw MsQuic bind failure on every OS.
Intel runtime support has not been removed. The native patch experiment now runs
on Apple Silicon; older Intel evidence remains part of the investigation.

The regression wrapper may tolerate test exit code 2 only when every failed
result is the raw runtime test
`QuicRuntimeRebindTest.DisposedRuntimeListenerRebindsSameEndpoint`, its captured
output identifies `stage=bind,`, and its error contains
`SocketErrorCode: AddressAlreadyInUse`. The owner integration additionally matches
the exact test method and checks the combined discovery floor of 4,388 before
allowing that exception. The shared PowerShell classifier rejects ambiguous
multiple-TRX directories and prohibits XML DTDs.

The original failed TRX is preserved and a warning reports the exception. This
policy is an explicit owner-approved exception, not evidence that the native
runtime defect is fixed. EmbedIO's own stop/serve/restart tests, missing tests,
other raw-runtime failures, WebSocket timeouts, compatibility failures, crashes
and other failed checks remain failures. The candidate native experiment full
suite remains a strict check. No production native library is patched.

## Remaining decisions and retirement gates

Issue #202 keeps the following work visible:

- Draft an upstream report separating Darwin deferred descriptor closure from
  accepted-connection binding lifetime. Establish which layer owns the expected
  disposal contract. Posting externally requires William's approval.
- Record the native dependency policy. The current candidate uses stock native
  dependencies; shipping patched MsQuic remains a separate decision, with the
  investigation recommending against it.
- Track exact runtime and MsQuic versions that address both lifetime behaviors.
  A new major version or prerelease does not by itself satisfy retirement.
- Test the original raw immediate-rebind cases and EmbedIO stop, serve, restart
  and graceful-drain cases without retry or quarantine, under contention on all
  supported platforms. Preserve failing controls, use matching source/runtime
  builds and include accepted connections as well as idle listeners.
- Remove retry and quarantine only after required dependencies pass those gates.
  Recheck resource cleanup and ordinary conflicting-endpoint behavior afterward.

No upstream issue has been posted, no patched native dependency is shipping, and
no release is authorized by this investigation. Other protocol conformance,
performance and default-engine work remains in program #181.
## Subsequent owner checkpoint

The owner reconciled the candidate with engine c25fd05 and the explicitly approved
HTTP/1 keep-alive cap removal. The combined floor is now 4,400, including its
independent quarantine discovery check. Full Windows reports 4,395 passed/five
expected local skips, zero failed; focused Linux reports 230 passed. Exact-head
CI remains required. Prior validation tables above retain their original source
revisions and must not be read as testing this later checkpoint.

The exact-0dc187d native experiment 37987974756 has completed. Seven of twenty
unpatched runs contained only the established raw bind defect; patched runs had
no failures in twenty runs and sanitizer exit zero. The patched full macOS suite
passed 4,357 cases with 31 skips. Overall workflow failure is retained because
Windows saw the coding-chain 503 and a TcpAndQuicSharePortAndStopIndependently TLS
UserCanceled failure. The latter requires QUIC/handshake triage and is not within
the raw-bind quarantine. No cause or correction for either is claimed here.