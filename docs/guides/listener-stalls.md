# Investigating an unresponsive listener

[Upstream #595](https://github.com/unosquare/embedio/issues/595) reports two servers
in a MAUI app: one for frontend files, another for APIs. The API server stops
responding after an unspecified number of requests on device builds. The report
does not supply its code, runtime, listener mode, device, request count, or logs.
The attributed production comments also describe missing shutdown notifications
and rotation/resume failures.

## Confirmed shutdown defects

Two internal-listener shutdown problems were reproduced independently of the
original application:

- `Stop` or `Dispose` could leave `GetContextAsync` waiting indefinitely.
  Disposing its semaphore did not complete pending waits, so the server's
  `RunAsync` could remain pending and never emit `Stopped`. Disposing the listener
  also left `IsListening` true.
- Shutdown relied on closing each queued context's connection to remove the
  context. A connection already closed before a late registration could leave
  the context in the queue, making disposal spin indefinitely. Stop could leave
  such a context for the next listener start.

The listener now cancels pending accepts on shutdown, sets `IsListening` false,
and removes queued contexts explicitly before closing their connections.
Semaphore resources are disposed only after the last active accept finishes,
following the [.NET disposal contract](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim.dispose?view=net-10.0).
Registration, dequeue, and lifecycle transitions are coordinated. A canceled
accept returns a consumed queue signal rather than stranding another waiter's
request; dequeue continues past keys removed by another operation.

Caller cancellation retains the caller's token. Explicit listener termination
can fault an outstanding accept/`RunAsync` with `HttpListenerException` error 995;
observe that task instead of discarding it. Canceling the token passed to
`RunAsync` remains the usual graceful stop: await completion, then dispose.
`StateChanged` reaches `Stopped` when the accept loop ends, including after fatal
listener cleanup. Stop/start of the underlying listener remains supported;
create a fresh `WebServer` for a host restart, as its run lifecycle is one-shot.

These changes repair demonstrated shutdown failure paths. They do not establish
why the reporter's original device server stopped or hung in the first place.

## Distinguish stopped from blocked

Record both listener identity and server state, and observe the task returned
by `RunAsync`. A state event alone cannot tell you whether one route is blocked
inside application code or whether the accept loop failed. Log request entry,
completion, duration, and exceptions with a request ID; retain full exception
stacks, native socket error codes, and device logs.

Probe a small route that avoids the database, outbound HTTP, and long-running
application work. Also probe a frontend file. If the frontend responds and an
API health route responds, investigate the failing API's awaited work and
dependencies. If only one listener fails, inspect that listener's task and logs.
If both disappear, inspect application lifecycle and process termination.
Record the PID so a stopped listener can be distinguished from an app restart.

Dispose client responses and streams promptly. Avoid synchronous blocking of
async work, unbounded detached tasks, and unbounded requests to slow dependencies.
Give application work explicit timeouts and cancellation, and handle its errors
where it runs. The accept loop's completion does not promise that every
application worker has drained. Request queues or automatic restarts may mask a
failure; do not treat them as a confirmed root-cause fix.

## Two servers and MAUI lifetime

Two listeners can operate on separate ports or distinct prefixes on a shared
endpoint. Give each host clear ownership, a retained run task, and a deliberate
shutdown policy. Stopping one must not dispose the other. A single server with
API modules before a catch-all static module is also an existing supported
configuration, but consolidating servers is not required by this fix.

See [MAUI Android hosting](../platforms/maui-android.md) for application-owned
hosts, rotation/activity recreation, observed workers, and orderly replacement.
The device OS was not identified in #595. Android emulator coverage verifies the
tested Android configuration; it is not evidence for an unspecified iOS device,
OEM battery policy, prolonged suspension, or an OS-killed process.

## Regression evidence and remaining diagnosis

The [listener tests](../../test/EmbedIO.Tests/Issues/Issue595_ListenerTermination.cs)
cover idle shutdown, multiple waiting accepts, caller cancellation, stop/start,
concurrent queue draining, shutdown/cancellation races, and a controlled late
registration after disconnection. The last case reproduces the internal race
ordering through reflection; it does not claim that the original app used that
ordering.

The [two-server tests](../../test/EmbedIO.Tests/Issues/Issue595_TwoServers.cs)
exercise static files and API responses with 320 request pairs per topology
(separate ports and shared endpoint), independent backend disposal, incomplete
headers, disconnect after dispatch, in-flight shutdown, close-callback errors,
and fatal listener cleanup.

The test-only Android fixture additionally hosts a real static frontend and API
listener, sends 320 request pairs with eight concurrent clients, checks both
listeners through rotation, activity recreation and brief Home/resume, replaces
them, then disposes only the backend while verifying the frontend still serves
files and the backend run task completes. CI retains JSON observations and logs
in `android-smoke`. The Android job in [run 37271010975](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37271010975/job/111637802828)
passed on API 29 with runtime 10.0.12: all request pairs were served, the process
ID stayed unchanged, and backend disposal completed its run task while the
frontend stayed listening and served files.

If a stall persists, please supply a minimal runnable app and exact library,
MAUI/.NET, OS and device versions; listener modes/prefixes; lifecycle start/stop
code; controllers; client response disposal; repeatable request sequence/count;
health-probe results; state/task/PID observations; and complete exception/device
logs. Remove secrets and private request data. A reproducible remaining defect
can be investigated without guessing at the original cause.
