# Health checks and application readiness

These APIs require EmbedIO-Neo 2.8.0 or later.
It works with both core targets and adds no runtime dependencies. It is opt-in:
constructing a service installs no endpoints and changes no ordinary request path.

## Desktop and embedded startup

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Health;

using var stop = new CancellationTokenSource();
using var server = new WebServer(HttpListenerMode.EmbedIO, "http://127.0.0.1:9696/");
var applicationReady = 0;
using var health = new HealthCheckService(server, new[]
{
    new HealthCheckRegistration("application", token =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new HealthCheckResult(
            Volatile.Read(ref applicationReady) == 1
                ? HealthStatus.Healthy : HealthStatus.Unhealthy));
    }),
});

// Optional: omit this module when only the containing application needs readiness.
server.WithModule(new HealthCheckModule(health));
var running = server.RunAsync(stop.Token);
try
{
    // Finish application initialization here. Register real routes before RunAsync.
    Volatile.Write(ref applicationReady, 1);
    await health.WaitForReadyAsync(TimeSpan.FromSeconds(10), stop.Token);
    // It is now appropriate for the desktop host to navigate its WebView.
    // Keep the application running until its normal lifetime ends.
    Console.ReadLine();
}
finally
{
    stop.Cancel();
    await running;
}
```

`WaitForReadyAsync` does not start the server or send an HTTP request. It polls
dependency checks every 100 ms by default, with a configurable interval. The
transition to Listening wakes startup waits immediately, without waiting for that
poll interval. It throws
`TimeoutException` on its finite deadline, `OperationCanceledException` for caller
cancellation, or `InvalidOperationException` after drain, stop or health-service
disposal. Starting the wait before `RunAsync` is supported. Keep and observe the
`RunAsync` task: it carries startup failures. A readiness result is an observation,
not a guarantee that a dependency will remain available for a later request.

This wait never exercises controller routes. Application checks can themselves
warm databases or other code: report that separately when measuring cold starts.
Use a non-HTTP readiness signal for first-request latency measurements.

## Liveness, readiness and shutdown

`CheckLiveness()` checks whether the server is listening and the health service
is undisposed; it never invokes application checks. A failed dependency does not
fail liveness. Drain alone does not fail liveness while the server remains in its
Listening state.

`CheckReadinessAsync()` requires Listening, no drain and a Healthy result from
every configured check. An empty check set means listener readiness only. Created,
Loading, Stopped, drain and service disposal are Unhealthy. For the concrete
`WebServer`, the existing `DrainStarted` token invalidates readiness immediately;
custom `IWebServer` implementations must report Stopped, or dispose the service,
to signal retirement. An unavailable lifecycle can return an empty check map.

`HealthStatus.Degraded` is distinct in reports but does not admit readiness. Both
Degraded and Unhealthy map to HTTP 503; Healthy maps to 200. Health checks do not
gate ordinary requests, restart the process, or install a background scheduler.
Your host decides what to do with their results.

## Check limits, ownership and cancellation

The service copies at most 32 registrations, with unique ordinal names of 1 to
64 characters. A registration has a finite timeout (5 seconds by default,
1 millisecond to 1 minute). Descriptions are at most 512 characters. Registrations
are immutable; their callback closures still belong to the application. Make
shared mutable state thread-safe. Readiness waits allow 1 ms to 1 day, with a
poll interval of 1 ms to 1 minute.

Different registrations execute concurrently on the thread pool. Concurrent
probes on one service share an evaluation; completed results are not cached.
Cancellation of an individual HTTP client or in-process waiter cancels its wait,
not the shared evaluation. Each check's own timeout, drain, stop and disposal request cancellation
of the checks. Readiness responses finish at the deadline even if a check blocks
synchronously or ignores cancellation. A timed-out callback stays owned by its
slot: later probes report Unhealthy without starting another copy until the old
callback actually finishes. No late result makes a stopped service ready.

.NET cannot forcibly terminate application code. A permanently hung callback can
retain one task (and its application resources) per registration, per service.
Cancellation callbacks must also be short and cooperative. They are dispatched
off the server lifecycle thread, and recoverable exceptions from checks or their
cancellation callbacks do not escape into server shutdown. Check exceptions produce
a generic Unhealthy description; exception messages and stack traces are not
included in reports. Dispose the service when its containing host ends. The
module does not own the service, and the service does not own the server.

## Optional HTTP endpoints and access control

The module defaults to `/health/live` and `/health/ready`. Only exact paths match;
other paths pass through to later modules. GET and HEAD are supported; other
methods receive 405 with `Allow: GET, HEAD`. HEAD has the same status and content
length as GET, without a body. Responses use `Cache-Control: no-store` and
`Pragma: no-cache`. Minimal responses contain only Healthy, Degraded or Unhealthy.
No session is created and no request body is read.

Place the module after required authentication/access-control modules and before
catch-all file or action modules. Controller authorization attributes do not
protect module routes. An optional `authorize` callback protects both endpoints;
false or a recoverable exception returns 403 without running dependency checks.
Use an existing authenticated identity and policy for this decision:

```csharp
server.WithModule(new HealthCheckModule(health,
    baseRoute: "/internal/health/",
    includeDetails: true,
    authorize: context => Task.FromResult(
        context.User?.Identity?.IsAuthenticated == true
        && context.User.IsInRole("Operations"))));
```

Detailed output is JSON with `status` and `checks` (per-check status and optional
description), written without reflection for Native AOT. Enabling details requires
an explicit authorization callback. The callback is application code: returning
true unconditionally makes those details public. Never put credentials, connection
strings or personal data in descriptions. Minimal endpoints still reveal
availability; protect or restrict their network exposure as appropriate. Apply
rate limits if untrusted callers can trigger expensive dependency checks.

## Validation and fuzzing

Focused tests cover lifecycle, readiness recovery without HTTP warmup, aggregation,
configuration limits, shared waiters, cancellation isolation, uncooperative checks,
throwing/blocking cancellation callbacks, authorization, redaction and real
HTTP/1, HTTP/2 and HTTP/3 GET/HEAD behavior. The `health-readiness` state-machine
campaign uses three hosts and an independent dependency/identity ledger to cover
concurrent probes, cancellation, recovery, replacement and cross-host isolation.
A checked trace and injected-corruption negative oracle run in the fuzz workflow.
The Native AOT smoke checks startup waiting and protected JSON without dynamic
code. The existing net472 smoke checks the .NET Standard asset on the installed
Windows Framework; a net472 compile target is not a claim of testing an exact
4.7.2 runtime.
See [stateful surface fuzzing](stateful-surface-fuzzing.md).
