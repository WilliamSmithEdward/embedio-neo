# Custom diagnostics and HTTPS in a Unity host

[Upstream #553](https://github.com/unosquare/embedio/issues/553) was reported by
`AgrYpn1a` using Unity 2020, Mono and .NET Standard 2.0. Their SWAN logger combined
several enum values; former maintainer `rdeago` explained that SWAN expected a
single threshold despite its Flags annotation. The reporter confirmed that
selecting Trace restored logging, then reported an HTTPS error and an exception
with automatic certificate registration. The exact Unity version, platform,
certificate and runnable project were not supplied.

Neo has removed SWAN. Configure `EmbedIO.Diagnostics.Log.Source`, a .NET
`TraceSource`, using existing .NET APIs. Register the destination **before**
constructing/starting the server so startup failures can be observed. This is a
verified support answer for Neo, not a claim that the original Unity TLS failure
has been reproduced or repaired. See the [migration guide](../compatibility/migration.md)
for the already approved SWAN API changes.

## Capture whole events and hand them to the UI

Server diagnostics may arrive on background threads. A custom listener should
format an entire `TraceEvent`, respect its `Filter`, and avoid calling a UI
logger directly from that callback. Enqueue records, then consume them from the
application's main-thread update. Keep the callback quick and nonthrowing; avoid
logging recursively into the same source. A throwing listener can disrupt the
caller. Disposal must release resources without a placeholder
`NotImplementedException`.

Save this complete application-owned helper as `QueuedTraceListener.cs`. Its
bounded queue keeps an inactive UI from accumulating unlimited diagnostic
records; it drops new records when full and exposes the cumulative count.
`Write`/`WriteLine` enqueue their supplied text, while the `TraceEvent` overrides
preserve each formatted EmbedIO event as one record.

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

// Application code, not a new EmbedIO API. Compatible with C# 7.3 syntax.
public sealed class QueuedTraceListener : TraceListener
{
    private readonly object gate = new object();
    private readonly Queue<string> pending = new Queue<string>();
    private const int Capacity = 256;
    private int dropped;

    public override bool IsThreadSafe { get { return true; } }
    public int Dropped { get { lock (gate) { return dropped; } } }

    public bool TryRead(out string message)
    {
        lock (gate)
        {
            if (pending.Count == 0) { message = null; return false; }
            message = pending.Dequeue();
            return true;
        }
    }

    public override void Write(string message) { Enqueue(message); }
    public override void WriteLine(string message) { Enqueue(message); }

    public override void TraceEvent(TraceEventCache cache, string source,
        TraceEventType type, int id, string message)
    {
        if (Filter == null || Filter.ShouldTrace(cache, source, type, id,
            message, null, null, null))
            Enqueue(type + ": " + message);
    }

    public override void TraceEvent(TraceEventCache cache, string source,
        TraceEventType type, int id, string format, params object[] args)
    {
        if (Filter == null || Filter.ShouldTrace(cache, source, type, id,
            format, args, null, null))
            Enqueue(type + ": " + (args == null ? format
                : string.Format(CultureInfo.InvariantCulture, format, args)));
    }

    private void Enqueue(string message)
    {
        lock (gate)
        {
            if (pending.Count == Capacity) { dropped++; return; }
            pending.Enqueue(message ?? string.Empty);
        }
    }
}
```

## Run a desktop baseline first

This complete .NET 10 program separates ordinary tracing/listener behavior from
Unity's runtime and UI integration. It uses the published package:

```sh
dotnet new console --framework net10.0 --name CustomDiagnostics
cd CustomDiagnostics
dotnet add package EmbedIO-Neo --version 1.0.2
```

Add `QueuedTraceListener.cs` above and replace `Program.cs` with:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Diagnostics;

using var listener = new QueuedTraceListener();
Log.Source.Listeners.Add(listener); // Preserve other destinations.
Log.Source.Switch.Level = SourceLevels.Verbose;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    Log.Source.TraceEvent(TraceEventType.Warning, 0, "Custom destination is attached.");
    using var server = new WebServer(o => o
        .WithUrlPrefix("http://127.0.0.1:8877/")
        .WithMode(HttpListenerMode.EmbedIO))
        .WithModule(new ActionModule("/hello", HttpVerbs.Get,
            c => c.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)));
    var running = server.RunAsync(stop.Token);
    try
    {
        while (!running.IsCompleted)
        {
            Drain();
            await Task.Delay(50); // Console harness; Unity uses Update instead.
        }
        await running;
    }
    finally
    {
        stop.Cancel();
        await running;
        server.Dispose(); // Keep diagnostics attached through disposal.
        Drain();
    }
}
finally
{
    Log.Source.Listeners.Remove(listener);
}

void Drain()
{
    for (var i = 0; i < 64 && listener.TryRead(out var message); i++)
        Console.WriteLine(message);
    if (listener.Dropped != 0)
        Console.WriteLine("Diagnostic queue overflow; dropped records: " + listener.Dropped);
}
```

Run `dotnet run`. In another terminal:

```sh
curl -i http://127.0.0.1:8877/hello
```

Expect `200` with `hello`, a warning confirming attachment, listener startup,
and a complete request summary. Ctrl+C stops the server; shutdown/disposal
messages are consumed before removal. The console harness consumes on its own
application loop, not a Unity main-thread guarantee.

`Log.Source` is **process-wide**, including its switch and listeners. Configure
verbosity once in the host, preserve other destinations, and remove only the
listener you own. Detaching this listener does not restore global verbosity or
change another listener's filter. Use `SourceLevels.Information` for normal
information/warnings/errors, `Warning` for warnings/errors, `Verbose` for the
additional debug/trace events, and `Off` for none. .NET's SourceLevels values
already describe cumulative sets; do not carry over SWAN's custom enum expression.
A listener `EventTypeFilter` provides an additional restriction for that
particular destination. A permissive listener cannot bypass the source switch.

`TraceSource.TraceEvent` calls are conditional on the `TRACE` compilation symbol.
Neo's ordinary SDK Debug/Release builds enable it. If an application-side probe
above is missing, check that application's build defines TRACE; adding it to
Unity cannot restore calls removed from a separately rebuilt library. Also check
the source switch, listener filter, destination and whether the UI is consuming
the queue. A rebuilt Mono/IL2CPP player needs its own runtime/stripping validation.
See [Microsoft's TraceEvent documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.tracesource.traceevent).

## Integrate with Unity's existing host

Import the .NET Standard 2.0 assembly and its required dependencies using your
project's established package/import process. A desktop NuGet install command is
not a Unity package-manager command. The retained target is not proof of
compatibility with every Unity Mono/IL2CPP version; WebGL hosting is unverified.

The following is an **integration excerpt**, not a complete server component.
Add the helper above to your project, attach it before the existing server
startup, and consume it from that component's `Update` method. Keep existing
server ownership and graceful shutdown; remove/dispose the listener only after
that server has stopped and been disposed. Install one bridge per host rather
than one per request or per frame.

```csharp
private QueuedTraceListener diagnostics;

// Call on the main thread before the existing server startup.
private void AttachDiagnostics()
{
    diagnostics = new QueuedTraceListener();
    EmbedIO.Diagnostics.Log.Source.Listeners.Add(diagnostics);
    EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
}

private void Update()
{
    string message;
    for (int i = 0; i < 64 && diagnostics != null && diagnostics.TryRead(out message); i++)
        UnityEngine.Debug.Log(message); // Or your ImGui logger, here on the main thread.
}

// Call after awaited server shutdown/disposal, on the main thread.
private void DetachDiagnostics()
{
    if (diagnostics == null) return;
    Update();
    if (diagnostics.Dropped != 0)
        UnityEngine.Debug.LogWarning("EmbedIO diagnostic records dropped: " + diagnostics.Dropped);
    EmbedIO.Diagnostics.Log.Source.Listeners.Remove(diagnostics);
    diagnostics.Dispose();
    diagnostics = null;
}
```

Drain periodically and watch the dropped count while diagnosing; this helper is
an illustrative UI bridge, not durable/audit logging. Its per-update work is
bounded, so a backlog may remain at shutdown. For complete file capture, attach a
`TextWriterTraceListener` separately and flush/remove/dispose it after shutdown.
Do not publish credentials, request bodies, PFX passwords or private keys in logs.

## Diagnose HTTPS independently of the logger

For `HttpListenerMode.EmbedIO`, provide a certificate **with its private key**
and leave `AutoLoadCertificate`/`AutoRegisterCertificate` disabled. Those inherited
Windows helpers operate on certificate stores and HTTP.sys; they are not required
for portable managed TLS. Registering a public certificate in a store does not
prove the application loaded a usable private key or that a client trusts it.

This is a **replacement setup excerpt** for an existing Unity desktop host:

```csharp
string path = System.IO.Path.Combine(UnityEngine.Application.streamingAssetsPath, "cert.pfx");
// Provision the password securely; do not hardcode or print it.
var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(path, pfxPassword);
if (!certificate.HasPrivateKey)
{
    certificate.Dispose();
    throw new System.InvalidOperationException("The HTTPS certificate needs its private key.");
}
var server = new EmbedIO.WebServer(o => o
    .WithUrlPrefix("https://127.0.0.1:6060/")
    .WithMode(EmbedIO.HttpListenerMode.EmbedIO)
    .WithCertificate(certificate));
// Retain both objects. Await shutdown and dispose server before certificate.
// Add the existing application modules and run using its owned cancellation token.
```

Use a filesystem path rather than prepending `File://` to a file-loader argument.
Unity [StreamingAssets paths vary by platform](https://docs.unity3d.com/2020.3/Documentation/ScriptReference/Application-streamingAssetsPath.html):
Android/WebGL paths may be URLs and need a platform-specific asset-loading path;
the desktop excerpt must not be copied unchanged there. Do not ship a shared
production private key in StreamingAssets. Key-import flags and TLS providers
also vary by runtime; verify them on the deployment platform. On modern .NET,
prefer `X509CertificateLoader.LoadPkcs12FromFile`; the constructor above is for
the retained .NET Standard 2.0 consumer surface.

Check [HasPrivateKey](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate2.hasprivatekey),
validity dates, server-authentication use and Subject Alternative Name for the
actual URL. Clients need issuer trust independently of the server key. With an
appropriately provisioned test CA and IP SAN for 127.0.0.1, probe:

```sh
curl --cacert test-root.pem -i https://127.0.0.1:6060/your-existing-route
```

TLS authentication happens before an HTTP route is dispatched. A failed handshake
may yield no request summary, and the managed transport does not promise a log
for every rejected TLS handshake. A Listening state or visible startup log is
not proof of a successful handshake. Preserve the full client TLS error and
server startup exception; do not disable certificate validation to make a probe
pass. See [HTTPS guidance](../guides/https.md) for provisioning and platform limits.

## Validation and remaining limits

Eight regression cases exercise real startup/error/shutdown diagnostics, source
thresholds, independent listener filtering/removal, concurrent complete events,
private-key PFX import, HTTPS success without Windows registration, and client
rejection before HTTP dispatch. They run in desktop Windows/Linux/macOS CI.
The helper and desktop program are verified separately against the published
1.0.2 package; the helper is also compiled for .NET Standard 2.0. No production
API, dependency, default or framework-target change is needed.

Unity 2020 Mono, IL2CPP, ImGui integration and the reporter's certificate remain
unverified. For a remaining failure, provide a minimal project with the exact
Unity version/backend/OS, Neo version, TRACE/stripping settings, listener mode,
certificate public metadata (`HasPrivateKey`, SAN, expiry) and full exception or
client TLS error. Do not attach a PFX, password or private key. This support answer
can be reopened with that reproduction; it does not claim to repair an
unconfirmed Unity-specific TLS defect.
