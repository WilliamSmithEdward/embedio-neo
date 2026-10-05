# MAUI Android: listener lifetime and background work

This guide answers [upstream #597](https://github.com/unosquare/embedio/issues/597),
which reports a .NET 8 MAUI Android app crashing during work started by a
controller, followed by a socket permission error when restarting. The report
contains no reproduction, exception stack, socket error code, or device details.
The guidance and tests below verify supported patterns; they do not establish
the cause of that application's crash.

## Choose the listener explicitly

Use the built-in EmbedIO listener when hosting on Android:

```csharp
var server = new WebServer(options => options
    .WithUrlPrefix("http://127.0.0.1:59697/")
    .WithMode(HttpListenerMode.EmbedIO));
```

`HttpListenerMode.Microsoft` selects `System.Net.HttpListener` when its support
check succeeds; it is not Kestrel. JoaquinC539's original comment recommends
trying EmbedIO mode. The Android fixture uses that mode and a nonprivileged
loopback port. This does not prove that changing modes cures every startup error.

Ensure the merged Android manifest declares
`android.permission.INTERNET`. A local HTTP client or WebView may also need a
cleartext policy appropriate to its target SDK and destination. The test app
allows cleartext for its loopback probe; do not copy that broad setting into a
production manifest without considering your application's network policy.
Binding to loopback restricts this fixture to the device itself; LAN serving
requires a deliberate address, access-control, and network-security decision.

## Keep the host separate from an activity or page

An Android activity can be recreated independently of its application process.
Starting the server from every page load or activity creation can create
duplicate listeners. Disposing an application-wide server on every window stop
or activity destruction can unexpectedly disconnect clients during navigation
or rotation.

Give one application-owned service responsibility for the server, its
cancellation token, and the task returned by `RunAsync`. Observe that task's
completion and failures. Serialize start/stop operations; keep them independent
of page creation unless a page-scoped listener is an intentional requirement.
For foreground-only operation, explicitly stop on the relevant lifecycle event
and create a fresh listener when resuming.

The [MAUI lifecycle documentation](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/app-lifecycle?view=net-maui-10.0)
describes window and Android callbacks. Application ownership does not guarantee
indefinite background execution. Android can kill an app process, and cached
processes can receive limited execution time; see
[Android process lifecycle](https://developer.android.com/guide/components/activities/process-lifecycle).
Long-lived background serving needs a separately designed Android service and
compliance with the platform's current restrictions. This guide does not add one.

## Observe work started by a controller

An exception from an awaited request handler is processed by EmbedIO's request
exception handling. A separate thread or `async void` callback is outside that
awaited request pipeline. A `try/catch` around server startup cannot catch an
exception thrown later on an unrelated thread.

If a request must return before work finishes, let an application-owned worker
accept a copy of the input and supervise the work. Catch failures inside that
worker, log them, and retain its task so shutdown can cancel and drain it:

```csharp
async Task RunObservedAsync(CancellationToken stopping)
{
    try
    {
        await DoLongOperationAsync(stopping);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        // Normal application shutdown.
    }
    catch (Exception error)
    {
        LogFailure(error);
    }
}
```

`DoLongOperationAsync` and `LogFailure` are application functions. The host must
track the returned task; this sketch is not a job queue or a durability guarantee.
Do not retain an HTTP context or write to its response after returning. Avoid
unbounded thread/task creation: define capacity, cancellation, and rejection
behavior appropriate to the workload. Return 200 only if it means the request
was accepted by your API; 202 may better express pending asynchronous work.

## Stop before restarting

Each `WebServer` instance is run once. To restart on the same port:

1. Prevent new work from entering your application-owned worker.
2. Cancel the host token and await the `RunAsync` task.
3. Cancel/drain tracked worker tasks with a bounded shutdown policy.
4. Dispose the old server and token source.
5. Create and start a new server instance.

Do not overlap old and new listener instances or reuse a disposed server.
If an earlier process is still running, an orderly restart within the new
process cannot release the earlier process's sockets. Permission denied and
address already in use are different errors: capture the actual
`SocketException.SocketErrorCode` and native error code instead of guessing.

## Validation and reporting a remaining failure

[Four desktop HTTP cases](../../test/EmbedIO.Tests/Issues/Issue597_BackgroundWorkAndLifecycle.cs)
cover awaited handler failure, an immediate response followed by a caught
background failure, worker cancellation, and repeated shutdown/rebind on the
same port.

The test-only [MAUI Android app](../../test/EmbedIO.AndroidSmoke) and
[emulator runner](../../scripts/run_android_smoke.py) exercise Android 10/API 29
with .NET 10, the EmbedIO listener, and real HTTP requests. They check caught
worker errors, rotation, explicit activity recreation, brief Home/resume, and
orderly listener replacement while recording process IDs and listener
generations. CI retains JSON observations, logcat, activity state, and emulator
logs in the `android-smoke` artifact. Android is included in the `CI passed` gate.
The [verified emulator run](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37267444705)
passed on API 29 with runtime 10.0.12: the process ID stayed unchanged across all
phases, and only the explicit listener replacement advanced its generation.
The fixture has its own locked test dependencies and is excluded from shipped
NuGet packages and the ordinary solution.

This does not reproduce the reporter's .NET 8 application, an unknown physical
phone/tablet, prolonged suspension, Doze, OEM battery management, or an OS-killed
process. A passing emulator run is evidence for the exercised patterns only.

For further diagnosis, provide a minimal runnable app, the exact EmbedIO/MAUI/.NET
versions, Android API/device, manifest, URL prefix and listener mode, controller
and worker code, lifecycle start/stop wiring, and complete exception/logcat
output. Include whether the PID changed, when the failure occurs, and the socket
error codes. Please omit secrets and private request data.
