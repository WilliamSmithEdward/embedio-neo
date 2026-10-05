# Cold-start audit

This audit uses `940470d47e6aa8cc3a5a1447854366969066dc59` as its baseline.
It examines construction, configuration, readiness and startup failures after
[the earlier performance passes](performance-audit-second-pass.md).

## Changes

Parameterless base routes already match paths without their regex. They now avoid
compiling that unused regex during construction. Root routes, case sensitivity,
segment boundaries, subpaths, caching and normalization keep their existing
behavior. A lazy fallback preserves the legacy concrete parameter-name list's
behavior if a caller mutates it. Parameterized and terminal routes still compile
eagerly; their first request does not inherit deferred compilation work.

`Start()` previously scheduled `RunAsync()` and repeatedly allocated one-millisecond
delays while polling state. If a custom server returned or faulted before reporting
Listening/Stopped, the helper could wait forever. It now subscribes before dispatch,
wakes on readiness or run-task completion, observes faults, honors cancellation,
and removes its event subscription. It retains the existing behavior of returning
after startup failure; use and await `RunAsync()` when the startup exception matters.

A module, session manager or state callback can fail after the listener has bound
a port. Previously `RunAsync()` changed state to Stopped without closing that
listener, preventing a replacement server from taking the same endpoint until
the failed instance was disposed. Failures now use the existing fatal-stop hook
to release the listener. Cleanup errors are logged without replacing the original
failure. Consumers still dispose the server and cancel their owned token in finally;
this does not automatically dispose application-owned modules or session managers.

Cancellation is checked before preparation, after preparation, before each module,
and before reporting Listening. An already-canceled run avoids binding a socket.
Startup callbacks remain synchronous and must cooperate with cancellation.
An atomic guard enforces the already-documented single call to `RunAsync()`;
a second call faults without re-preparing or stopping the active run. To restart,
construct a new server. Raw listener Stop/Start support is unchanged.

Public/protected signatures, frameworks, dependency versions, default listener,
prefix normalization, certificate options and HTTP/route behavior are unchanged.

## Fresh-process measurements

Build the dependency-free, unpackaged performance runner as described in
[its README](../../test/EmbedIO.Performance/README.md), then run:

```sh
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --cold-start base-100
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --verify-cold-start
```

For before/after comparisons, use identical runner source against both core
revisions. Each sample runs in its own process; no workload is warmed first.
The local comparison alternated revision order across 21 samples per workload.
Results below are medians on Windows x64, SDK 10.0.400 / .NET 10.0.11.
Allocations are measured on the calling thread; timer/worker-thread allocations
are not included. Workload time includes first-use JIT/static initialization but
excludes process launch and result serialization.

| Workload | Before B | After B | Before ms | After ms | CI ceiling B |
| --- | ---: | ---: | ---: | ---: | ---: |
| One root base route | 49,920 | 43,096 | 7.60 | 7.78 | 80,000 |
| 100 parameterless base routes | 996,384 | 139,832 | 9.61 | 8.60 | 250,000 |
| 100 action modules | 1,020,752 | 164,200 | 9.92 | 8.71 | 300,000 |
| 100 parameterized terminal routes | 1,250,416 | 1,251,216 | 10.68 | 10.75 | 1,500,000 |
| Server construction, no listener start | 28,056 | 28,056 | 13.39 | 13.40 | 80,000 |
| Socket-free Start with 50 ms preparation delay | 12,680 | 8,736 | 83.26 | 68.20 | 30,000 |

The extra 800 bytes for 100 parameterized matchers is the additional private
fallback field. Root/server timings did not improve. Delayed-start timing includes
thread-pool and timer scheduling, so it is an artificial readiness comparison.
Allocation ceilings run with three fresh processes per workload on desktop CI;
elapsed times are informational. These numbers do not measure OS filesystem-cache
coldness, full process startup, TLS setup, first-response latency or real
application controller/plugin registration costs.

## Other startup dependencies inspected

- Controller registration reflects methods/attributes and compiles handlers before
  requests. Keep registration before readiness; lazy registration would move errors
  and cost into the first request. No controller cache or deferred activation was added.
- Named managed prefixes resolve DNS synchronously during registration, except the
  existing localhost/IP/wildcard paths. DNS failures keep their existing binding
  fallback. This audit does not change hostname scope or DNS timeout behavior.
- Windows certificate auto-loading/registration is opt-in and can run `netsh` or
  access a certificate store during construction. Its process waits have no timeout.
  Pre-provision certificates and use an explicit certificate where appropriate;
  these options/defaults were not silently changed.
- CLI plugin discovery scans DLLs and loads exported types; missing dependencies,
  invalid controller signatures and plugin construction failures can prevent startup.
  File providers can fail when their root is absent or inaccessible, or when watcher
  initialization fails. Such configuration errors still surface to the caller.
- Custom startup/state/log callbacks can block indefinitely or throw. Cancellation
  cannot interrupt a synchronous callback that ignores it. Await and supervise
  `RunAsync()`, retain diagnostics, and keep startup callbacks bounded.
- Occupied ports, HTTP.sys permissions, platform network entitlements and certificate
  trust remain deployment prerequisites. Existing [HTTPS](../guides/https.md),
  [Mac Catalyst](../platforms/maui-mac-catalyst.md) and
  [Android](../platforms/maui-android.md) guides cover the relevant setup.

## Validation

Thirty new cases cover early completion/failure, cancellation at preparation and
module boundaries, concurrent/repeated starts, cleanup failure, readiness event
lifetime, base-route matching/caching and the legacy mutable-list fallback.
Six real-listener cases check pre-canceled starts and immediate port reuse after
module/Listening-callback failure, keeping the failed server alive.

The baseline passed 15 of 24 socket-free cases and failed nine startup regressions.
All 143 targeted socket-free startup/routing/prior-performance cases passed after
the changes. Both library targets build; public/protected signatures match the
baseline and hot/cold allocation budgets pass. Local real-listener tests are avoided
because they trigger firewall prompts; full listener/native-platform validation
uses required CI. Logs, preserved binaries and sample data are under ignored
`TestResults/cold-start`.
