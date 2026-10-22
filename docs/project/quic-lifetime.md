# QUIC listener disposal, rebind and workaround retirement

> Historical record. The earlier HTTP/3 providers, and the code, tests and CI jobs
> this record describes, were removed on 2026-10-22; see
> [retiring the earlier HTTP/3 providers](quic-development-and-testing.md#retiring-the-earlier-http3-providers).

This guide records the QUIC lifetime investigation for [program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181),
[draft PR #200](https://github.com/WilliamSmithEdward/embedio-neo/pull/200), and the
HTTP-engine integration in [draft PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182).
[Issue #202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202) tracks the
remaining upstream-reporting, native dependency and retirement questions. The
candidate is unreleased. This document does not claim integration on main or
completion of the HTTP engine.

## Initial bind failure versus drain or rebind

PR #365 source `a1ad06be` failed the macOS regression run
`38319118404`, job `115042718415`, in
`GracefulListenerDrainCanAbortUnfinishedResponses("listener-stop")`.
The actual exception was `AddressAlreadyInUse` (48) from the initial
`QuicListener.ListenAsync` through `SystemBinding.Create` and `Session.Start`.
The fixture waited for handler entry until its existing 30-second token expired,
then its cleanup re-awaited the failed startup task. It never reached the drain
or listener-stop trigger. The selected port and occupied family were not captured;
this is not evidence of a drain/rebind failure or a confirmed natural cause.

The test prefix helper probes and releases only an IPv4 loopback UDP port.
The actual `localhost` resolver also includes IPv6 loopback when `UseIpv6` and
the OS permit it. A successful probe in one UDP namespace does not establish
availability in the other. Test-only inverse IPv4/IPv6 controls demonstrate that
gap with one family deliberately occupied while the other family's probe succeeds.
Actual `localhost` QUIC startup then fails with the original bind exception.
These controlled Linux results do not reproduce or attribute the natural macOS
failure; the provider's literal-family binding strategy and a failing macOS
endpoint trace remain separate evidence requirements.

The drain fixtures observe the actual startup, client and handler-entry tasks,
so an initial failure is reported before waiting for impossible handler entry.
Read-only diagnostics record the prefix, actual resolved endpoints, address-family
configuration, task states and shared retry/recent-release history. They neither
mutate that history nor retry startup. Cleanup uses one shared five-second budget,
reports unfinished owners as unfinished, and preserves all independent failures;
an aggregate retains the original primary exception as its first inner exception.
Critical errors are not treated as recoverable diagnostic or expected client faults.
The existing drain assertions, deadlines and raw-rebind quarantine are unchanged.

On Linux/.NET 10.0.12 with actual stock MsQuic 2.6.2, source
`ecfbbd4498309ab296a5c924c29010657288a459`, based on accepted engine `0773460e`,
passed all eleven focused cases: four namespace/startup controls and seven existing
drain cases, zero failures or skips, in 2.197 seconds. Both controlled initial
conflicts had unrecorded endpoints and zero retry counts before and after startup;
all nine server cases ended with both actual task owners terminal. Frozen source,
package, module and loaded-provider hashes were retained. Both assets, solution
build and parser/suppression/scoped-format gates passed. This is source-qualified
Linux evidence, not Windows/macOS/full-suite or final-head acceptance.

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

On Windows, the same wrapper and classifier also carry a separate
owner-approved exception for the QUIC credential failure in
[issue #398](https://github.com/WilliamSmithEdward/embedio-neo/issues/398); see
[the HTTP engine notes](http-engine.md#windows-quic-credential-quarantine). It
does not widen the rebind exception, and the two are retired independently.

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

The upstream report is posted as microsoft/msquic#6374 (2026-10-11); no patched
native dependency is shipping, and no release is authorized by this investigation. Other protocol conformance,
performance and default-engine work remains in program #181. The sections from
[Ownership of the disposal contract](#ownership-of-the-disposal-contract) onward
record the work completed for #202 on that list: the reviewable draft report, the
recorded dependency policy, version tracking, the executable retirement gate, the
owner review of the retry and classifier, and exact-head outcomes.
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
## Exact a33785f native experiment outcome

[Run 37994255459](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37994255459)
tested a33785f on Apple Silicon. The native comparison passed. Four of twenty
unpatched NUnit control runs contained failures, all classified as the established
raw MsQuic bind defect; no unrelated failures were classified as that defect.
The native probe ledger records six failing control probes and ten passing
candidate probes. Each candidate completed all 4,096 cycles. Candidate native
and AddressSanitizer exit codes are zero. These are test-only patched-MsQuic
results, not evidence that the shipped upstream runtime has been repaired.

The patched full macOS regression reports 4,400 cases, 4,369 passed, 31 skipped,
zero failures. The complete workflow remains failed because the Windows coding
chain test received the HTTP.sys 503 response. The prior shared TCP/QUIC TLS
failure did not occur in this run; its absence does not establish a correction.
No upstream report or native release was published. Retry and quarantine retirement
still requires the runtime evidence and gates listed above.

Artifacts and classification receipts are retained under ignored
TestResults/a337-quic-experiment. The control failures and failed overall workflow
remain part of the evidence.

## Ownership of the disposal contract

The verified mechanics are written up in the
[draft upstream report](quic-upstream-report-draft.md), which is reviewable but
unposted; posting it needs William's approval. In short:

- .NET's `QuicListener.DisposeAsync` (release/10.0) calls `ListenerStop`, waits
  for `STOP_COMPLETE`, disposes the handle (`ListenerClose`), cancels pending
  handshakes and drains its accept queue. Its documentation promises that the
  listener "releases all resources"; neither it nor MsQuic's `ListenerClose.md`
  or `ListenerStop.md` says anything about the bound UDP port.
- MsQuic's `QuicListenerStopAsync` unregisters the listener and releases the
  listener's binding reference before the stop-complete indication, and
  `MsQuicListenerClose` blocks on the stop event. When `DisposeAsync` returns,
  the listener's own reference is gone; whether the port is free depends on the
  two behaviors below, neither of which the managed runtime can observe.
- Behavior A, deferred datapath close: `CxPlatSocketDelete` returns after
  `CxPlatSocketContextUninitialize` queues `ShutdownSqe`; the `close()` runs
  later on the event-queue worker in both `datapath_kqueue.c` and
  `datapath_epoll.c`, at `v2.6.2` and on `main` as inspected on 2026-10-11. The
  kqueue datapath sets neither `SO_REUSEADDR` nor `SO_REUSEPORT`; the epoll
  datapath sets `SO_REUSEPORT` on UDP server sockets when `PartitionCount > 1`,
  which is why multi-core Linux runners never reproduced the failure. Windows
  calls `closesocket` synchronously after its rundown wait.
- Behavior B, connection-held binding: connections reference the binding from
  `QuicBindingCreateConnection` until `QuicConnFree`, which usually runs on a
  worker after `ConnectionClose` returned. A new listener shares a still-listed
  binding without error; the failure window is the interval in
  `QuicLibraryReleaseBinding` between unlinking the binding under the lock and
  the completed socket close in `QuicBindingUninitialize`, during which a new
  listener creates a colliding socket.

Both behaviors are therefore owned by MsQuic. dotnet/runtime can only document
the limit or depend on a fixed native version; EmbedIO can only bridge the window
with the bounded retry. With William's approval the report was posted on
2026-10-11 as [microsoft/msquic#6374](https://github.com/microsoft/msquic/issues/6374);
it asks the maintainers which contract they intend. Until answered, the retry and
the quarantine stay, and the dotnet/runtime documentation note remains unposted.

## Native dependency policy

This is the explicit policy requested by #202; it restates what the candidate
already does and what remains a separate decision.

- Shipped packages use stock native dependencies only. On Windows that is the
  `msquic.dll` bundled with the installed .NET runtime; on Linux and macOS it is
  the `libmsquic` the host installs. Desktop CI pins libmsquic 2.6.2 by package
  hash (Ubuntu 24.04 `.deb`, Homebrew arm64 bottle) so results name an exact
  library; raising those pins is a deliberate change recorded here.
- Patched MsQuic builds are test-only experiments: the patches live under
  `test/EmbedIO.RuntimeCloseProbe`, are compiled only by
  `quic-cleanup-experiment.sh` on a dispatch input, are never installed
  system-wide, never packaged, and never referenced by production projects or
  lock files. Their results show what a native fix would achieve; they are not
  evidence about the shipped runtime.
- Shipping a patched or privately built MsQuic is a separate owner decision. It
  would need William's explicit approval and a provenance, signing, loader and
  security-servicing plan per OS before any work starts. CVE-2026-105794, fixed
  in MsQuic 2.5.11 and 2.6.1, shows why: a private build must track upstream
  security fixes for as long as it ships. The investigation recommends against
  it; the bounded retry stays until the stock dependencies pass the retirement
  gate.
- No production project, package dependency group, target or public API changes
  for this policy.

## Runtime and MsQuic version tracking

Checked on 2026-10-11. "A" and "B" refer to the two behaviors above.

| Platform (CI runner) | Runtime | MsQuic | Source | A | B |
| --- | --- | --- | --- | --- | --- |
| Windows (windows-2025) | .NET 10.0.12 | 2.5.10, bundled, Schannel | runtime | not applicable: synchronous `closesocket` | present: 4 of 7,040 contended restarts |
| Linux (ubuntu-24.04) | .NET 10.0.12 | 2.6.2 `.deb` `9ac90841…` | packages.microsoft.com | present in code, masked by `SO_REUSEPORT` with more than one partition | not reproduced; immunity not claimed |
| macOS (macos-15, arm64) | .NET 10.0.12 | 2.6.2 bottle `c7d7fbda…` | ghcr.io/homebrew | present: twelve traced failures; PR #200 recorded the raw case failing in about one ordinary run in four | present in code |

Upstream status on the same date:

- MsQuic `v2.6.2` (2026-09-30) is the latest release; no later release or
  prerelease exists. `main` retains the deferred kqueue close. No upstream issue
  describes either behavior; discussion #2961 concerns a kqueue wake-up deadlock
  on FreeBSD.
- dotnet/runtime `release/10.0` moved `MicrosoftNativeQuicMsQuicSchannelVersion`
  to 2.5.11 for 10.0.13 (PR #135579, merged 2026-10-11, servicing-approved) only
  because 2.5.10 carries CVE-2026-105794, an OpenSSL/QuicTLS hostname-validation
  defect; the 2.5.11 and 2.6.x changes do not touch socket close ordering or
  binding lifetime. `main` (12.0 alpha) also references 2.5.11. The CI-pinned
  2.6.2 already contains that security fix. .NET 10.0.13 had not shipped on
  2026-10-11.

A new runtime or library version counts toward retirement only after it is
checked, in this order: `datapath_kqueue.c` and `datapath_epoll.c` close the
descriptor inside `CxPlatSocketContextUninitialize` or `CxPlatSocketDelete`
waits for it; `library.c` and `binding.c` no longer leave the unlink-before-close
window; dotnet/runtime's `eng/Versions.props` and release notes name the bundled
Windows version; the pins in `ci.yml`, `diagnostics.yml` and `quic-cleanup-experiment.sh` are
raised with new hashes; and the retirement gate below passes on every platform.
A major version number or a prerelease label is not evidence.

## Retirement gates

The gate is executable and lives in the repository:

- Cases: the raw `QuicRuntimeRebindTest.DisposedRuntimeListenerRebindsSameEndpoint`
  pair, which has no retry, and the four owned cases in
  `QuicLifetimeRetirementGateTest`: an idle listener restarted 64 times
  (behavior A), a served listener restarted 32 times with the client connection
  alternately closed before and after the stop (behavior B), a drained listener
  restarted 16 times after `DrainAsync`, and four servers restarting concurrently
  for 16 served cycles each (contention). Each owned case fails if the restart
  fails or if `QuicEndpointReleases.Shared.Retries` grew, that is, if the
  production retry had to bridge an `AddressAlreadyInUse` failure. The counter is
  the only production change; it is internal and read through reflection.
- Selection: the owned cases skip unless `EMBEDIO_QUIC_RETIREMENT_GATE=1`, so
  the ordinary suite counts them in the 4,743 floor without running them.
  `--filter "TestCategory=QuicLifetimeRetirementGate|FullyQualifiedName~QuicRuntimeRebindTest"`
  with `--minimum-expected-tests 6` selects the six gate cases.
- Platforms and repetitions: the `quic-retirement-gate` input of the manual
  `Diagnostics` workflow (`.github/workflows/diagnostics.yml`) runs
  the selection on ubuntu-24.04, windows-2025 and macos-15 in separate test
  processes (`quic-retirement-gate-repetitions`, default 10), with
  `EMBEDIO_REQUIRE_QUIC=1`, no quarantine and no tolerated exit code. Its
  artifact retains `environment.txt` (runtime and the exact native library:
  `dpkg` version, dylib hash or `msquic.dll` product version),
  `repetitions.tsv`, every TRX and log, and `gate-ledger.jsonl` with one record
  per case (cycles, retries before and after, bridged count, outcome, timing,
  runtime, OS). The job is not part of `CI passed`; the dispatched run's own
  status is the verdict.
- Pass criterion: every repetition exits zero on every platform, twice, on
  identical source: once as the control with the current pins and once with the
  candidate dependency. A control that fails on the stock runtime is the
  expected outcome today and is retained as proof that the gate detects the
  defect; it is not a CI regression. The default ten processes detect the macOS
  ordering reliably, but the recorded Windows rate is about four failures in
  7,040 restarts, so the Windows candidate run needs at least 100 processes
  (about 17,600 owned restarts) with zero bridged binds before behavior B may be
  called absent there.
- Retirement: only after the candidate passes, remove the retry
  (`QuicEndpointReleases` and its two `Http3Listener` call sites), remove the
  rebind branch of the `ci.yml` quarantine and its classifier count (the
  Windows credential exception for issue #398 has its own removal condition),
  keep the raw and owned cases as ordinary strict tests, rerun the gate and the
  full suite on the final
  source, and recheck `FailedMultiEndpointStartupRollsBackEarlierBindings`,
  foreign-process endpoint conflicts and resource cleanup. The idle case covers
  behavior A and the served and contended cases cover behavior B; fixing one
  does not retire the other.

Run it on a branch with
`gh workflow run diagnostics.yml --ref <branch> -f quic-retirement-gate=true -f quic-retirement-gate-repetitions=10`,
or locally with the same filter and environment variables after installing the
pinned native library.

## Owner review of retry boundaries, retention and classification

Reviewed at engine head 15d8a19 plus this change:

- Bounds. The retry applies only to `SocketError.AddressAlreadyInUse` for an
  endpoint this process recorded within one second; recency is re-evaluated after
  every one-millisecond sleep, so a genuine conflict surfaces with the last
  socket error at most about one second after the recorded release. Other
  errors and unrecorded endpoints propagate on the first attempt. Both the
  System.Net.Quic binding and the native provider binding are covered; the native
  path maps MsQuic's address-in-use status (`0x80072740`, 48 or 98) to the same
  socket error before the policy sees it.
- Recording. `ShutdownAsync` records each endpoint immediately after that
  listener's `DisposeAsync` returns. The synchronous `Dispose` path and the
  failed multi-endpoint startup rollback both run through `ShutdownAsync`, so a
  rolled-back endpoint is recorded as well.
- Retention. One timestamp per distinct endpoint; `Record` prunes entries older
  than the window, so retention is bounded by the number of distinct endpoints
  released within one second, and the entries from a process's final release
  persist only until the next record. `Bind` allocates nothing beyond the
  exception the runtime already throws.
- Concurrency. The dictionary is guarded by a lock and the new counter is
  interlocked. Two sessions in one process racing for the same endpoint both
  retry until the window ends and the loser then receives the conflict; that
  one-second delay of a legitimate in-process conflict is the documented cost.
- Outcome. No correction to the policy was needed; the only production change is
  the `Retries` counter that makes the gate observable. Its unit coverage
  (`CountsOnlyTheBindsItRetried`) checks that rejected errors, foreign endpoints
  and expired windows are never counted.
- Classification. The quarantine classifier matches the exact raw test class and
  method, requires `stage=bind,` in the captured output and
  `SocketErrorCode: AddressAlreadyInUse` in the message, accepts exactly one TRX
  and prohibits DTDs. Owned restart methods, other stages, other socket errors,
  WebSocket timeouts, crashes and discovery loss remain failures, as checked
  earlier against 41 retained TRX files and four counterexamples. The ordinary
  regression step now checks a 4,743 floor before it may tolerate the raw
  failure.

## Retirement gate control on the stock dependency (local, 2026-10-11)

The gate was run on this change's working tree over engine head `15d8a19` on a
MacBook Pro with an Apple M5 Pro, macOS 27.0.1 (26A434) arm64, .NET SDK 10.0.401
and runtime 10.0.12, with the CI-pinned Homebrew libmsquic 2.6.2 bottle
(`c7d7fbda…`) relocated beside OpenSSL 3.6.5 and loaded through
`DYLD_FALLBACK_LIBRARY_PATH` (relocated `libmsquic.2.6.2.dylib` SHA-256
`662c2a12…43bcd`). Five gate processes used the CI filter and the floor of six:

| Process | Exit | Raw pair | Idle 64 | Served 32 | Drained 16 | Contended 64 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 2 | passed | 0 bridged | 0 bridged | 0 bridged | 1 bridged, failed |
| 2 | 2 | passed | 0 bridged | 0 bridged | 0 bridged | 2 bridged, failed |
| 3 | 2 | passed | 0 bridged | 0 bridged | 0 bridged | 1 bridged, failed |
| 4 | 0 | passed | 0 bridged | 0 bridged | 0 bridged | 0 bridged |
| 5 | 2 | passed | 0 bridged | 0 bridged | 0 bridged | 1 bridged, failed |

Every failure is `ContendedRestartsCompleteWithoutRetry` reporting that the
production retry bridged one or two `AddressAlreadyInUse` binds during 64
contended cycles, each case finishing in under 200 ms. The single-lane cases and
both raw runtime cases passed in all five processes on this fast host, which
shows why the ordinary suite cannot be the retirement evidence: with the retry in
place it passes, and without contention this host rarely loses the race. The
stock dependency still needs the workaround. The ordinary-mode check reported the
four gate cases skipped and counted alongside the six retry unit cases (10 total,
zero failures). Artifacts are retained under ignored
`TestResults/upstream-202/gate-local`: `environment.txt`, `repetitions.tsv`,
`gate-ledger.jsonl`, and each process's TRX and log.

## Exact head outcomes recorded

- PR #203 final head `580aa2b` (merged into the engine branch on 2026-10-09):
  [CI 38005558956](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38005558956)
  passed with 4,448 cases and zero failures in each Test job (macOS 4,417
  succeeded, Windows 4,445, Ubuntu 4,419; the remainder are platform skips),
  Security 38005558799, Malware 38005558829 and the fuzz run 38005558806 passed,
  and no quarantine warning was emitted; the quarantine was not exercised.
- Engine head `15d8a19`, the base of this change (PR #182 on 2026-10-11):
  [CI 38158991953](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38158991953)
  succeeded with 4,720 cases and zero failures in each Test job (macOS 4,708
  succeeded and 12 skipped, Windows 4,713 and 7, Ubuntu 4,710 and 10); no
  quarantine warning was emitted. Every check on that head was terminal success
  or an intentional skip.

## Retirement gate control in CI (dispatch 38169343155 on `6caee02`, 2026-10-11)

The first CI dispatch of the gate ran ten gate processes per platform on this
change's first head `6caee02` with the pinned native dependencies of the
regression job, no quarantine and no tolerated exit code
([run 38169343155](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38169343155)).
All three used .NET SDK 10.0.401 and runtime 10.0.12.

| Platform | Native library | Processes failed | Raw failures | Bridged binds: idle 64, served 32, drained 16, contended 64 |
| --- | --- | --- | --- | --- |
| macos-15 (macOS 15.7.9, arm64) | libmsquic 2.6.2 bottle, relocated dylib `6f045def…` | 9 of 10 | 2 (`connected=False`, `stage=bind`, cycles 1 and 11) | 1, 1, 3, 18 |
| ubuntu-24.04 (24.04.5) | libmsquic 2.6.2 `.deb` | 0 of 10 | 0 | 0, 0, 0, 0 |
| windows-2025 (10.0.26100) | runtime-bundled `msquic.dll` 2.5.10.154561281 | 0 of 10 | 0 | 0, 0, 0, 0 |

The macOS job failed as expected for the stock dependency, and that dispatch's
`ci-passed` fails with it; the Windows and Linux jobs passed. On macOS every
owned case and the raw case detected the defect: the retry bridged 23 binds
across the 40 owned cases of ten processes, and two raw processes failed before
completing their 32 cycles. The Linux and Windows passes match the version
table (`SO_REUSEPORT` on multi-partition Linux, synchronous `closesocket` on
Windows), but ten processes are 640 contended and 320 served restarts per
platform, far below the 7,040-restart campaign that recorded four Windows
failures, so they do not establish that behavior B is absent; the Windows
retirement requirement above stands. The job's `environment.txt` names every
installed runtime and the loaded library on each platform, which is the
evidence a candidate run must be compared against. Copies of the three
`quic-retirement-gate-<os>` artifacts are retained under ignored
`TestResults/upstream-202/gate-ci-6caee02`.
