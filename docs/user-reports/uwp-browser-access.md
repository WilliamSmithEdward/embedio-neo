# Desktop browser access to a UWP-hosted server

[Upstream #554](https://github.com/unosquare/embedio/issues/554) describes an
EmbedIO server inside Xamarin's UWP target. Its in-app UI works, but desktop
Firefox on the same PC times out. Another participant could connect from a
second PC while local Postman failed. This guide addresses that traffic direction;
it does not claim the original, unavailable application was repaired.

## Identify the client and server first

`localhost` names the computer or device running the client. A second PC must
use the server PC's reachable address. `*` and `+` are listener-prefix wildcards;
they are not browser destination addresses. Explicit prefixes also match their
HTTP hostname: a `localhost` prefix and a `127.0.0.1` request are not interchangeable
merely because both can use an IPv4 loopback socket.

Windows applies network isolation to UWP/AppContainer apps independently of
whether EmbedIO reports `Listening`. Microsoft's
[firewall troubleshooting guide](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/troubleshooting-uwp-firewall)
distinguishes these two controls:

| Connection direction | Diagnostic control for the app |
| --- | --- |
| UWP/AppContainer client to an unpackaged localhost server | Outbound allowance: `LoopbackExempt -a` |
| Unpackaged desktop browser to a UWP/AppContainer listener on the same PC | Inbound allowance: `LoopbackExempt -is` |
| Another PC to the listener's LAN address | Check binding, intended network capabilities and that connection's firewall policy |
| An in-app WebView to an in-app server | Check its actual process/app model and policy separately; this is not the external-browser probe |

The upstream Fiddler/Nightingale advice concerned an outbound allowance. Its
presence does not establish inbound browser access. The original timeout is
consistent with inbound isolation, but confirming a particular app requires its
manifest, versions, actual URLs and network observations.

## Check the installed UWP application

For the intended networks, inspect the existing manifest's
`internetClientServer` and `privateNetworkClientServer` declarations. Microsoft's
[networking basics](https://learn.microsoft.com/en-us/windows/apps/develop/networking/networking-basics)
explains which capabilities apply. A declared capability is not proof that a
loopback connection is allowed. Check the installed Release package as well as
the development build; reinstall after changing capabilities rather than assuming
an existing installation changed.

Find the exact installed package family name; this is not its display name:

```powershell
Get-AppxPackage -Name 'YourExactInstalledPackageName' |
    Select-Object Name, PackageFamilyName
```

For a sideload/debugging investigation, run the following in an elevated terminal,
substituting that exact family name:

```powershell
CheckNetIsolation.exe LoopbackExempt -is -n="YourActualPackageFamilyName"
```

Keep this terminal running while the packaged server listens. Test from a second
terminal on the same PC, replacing `8585` with the app's port:

```powershell
curl.exe --noproxy "*" --max-time 5 http://127.0.0.1:8585/
curl.exe --noproxy "*" --max-time 5 http://127.0.0.1:8585/api/probe
```

Use the app's real API route instead of `/api/probe` if different. A wildcard
prefix accepts these hostnames; for an explicit hostname prefix, use its matching
URL. Stop the inbound tool with Ctrl+C when the diagnostic session ends.
Microsoft's [IPC guidance](https://learn.microsoft.com/en-us/windows/apps/develop/communication/interprocess-communication#loopback)
requires this tool to remain running and limits this approach to sideload/debug
scenarios with local administrative access. It is not an automatic production
permission fix and EmbedIO does not run it for applications.

## Establish a desktop baseline

This complete program verifies the HTML/API/session composition outside UWP.
It uses the already published EmbedIO-Neo 1.0.2 APIs. It does not reproduce
Xamarin, UWP, a WebView, or Angular itself.

```powershell
dotnet new console --name AccessProbe --framework net10.0
cd AccessProbe
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace `Program.cs` with:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Diagnostics;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.WebApi;

var folder = Path.GetFullPath("wwwroot");
Directory.CreateDirectory(folder);
File.WriteAllText(Path.Combine(folder, "index.html"),
    "<!doctype html><title>EmbedIO probe</title><p>HTML is reachable.</p>");

using var trace = new TextWriterTraceListener("embedio-probe.log");
Log.Source.Listeners.Add(trace);
Log.Source.Switch.Level = SourceLevels.Verbose;
Trace.AutoFlush = true;
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager()
    .WithWebApi("/api", m => m.WithController<ProbeController>())
    .WithStaticFolder("/", folder, true, m => m.WithContentCaching(true));
server.StateChanged += (_, e) => Console.WriteLine($"State: {e.NewState}");
Console.WriteLine("Open http://127.0.0.1:8877/; press Ctrl+C to stop.");
try { await server.RunAsync(stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
finally { Log.Source.Listeners.Remove(trace); }

public sealed class ProbeController : WebApiController
{
    [Route(HttpVerbs.Get, "/probe")]
    public object Probe()
    {
        HttpContext.Session["visits"] = HttpContext.Session.GetValue<int>("visits") + 1;
        return new
        {
            visits = HttpContext.Session.GetValue<int>("visits"),
            localEndpoint = HttpContext.Request.LocalEndPoint.ToString(),
            remoteEndpoint = HttpContext.Request.RemoteEndPoint.ToString(),
        };
    }
}
```

Run `dotnet run`, then use another terminal:

```powershell
curl.exe --noproxy "*" http://127.0.0.1:8877/
curl.exe --noproxy "*" --cookie-jar cookies.txt http://127.0.0.1:8877/api/probe
curl.exe --noproxy "*" --cookie cookies.txt http://127.0.0.1:8877/api/probe
```

The HTML says `HTML is reachable.` The API reports `visits: 1`, then `visits: 2`
for that cookie jar, with the loopback endpoints and an ephemeral client port.
A fresh browser/cookie jar has its own counter. This is temporary demonstration
state, not persisted application data. API registration precedes the catch-all
static folder. The `wwwroot` path and log file are relative to the process's
working directory; generated `index.html` is overwritten by this probe on startup.

## Collect useful diagnostics

Inspect `embedio-probe.log` and the `StateChanged` output. Configured prefix logs
show configuration; they do not enumerate every bound socket. The controller
above reports actual local/remote endpoints only after a request arrives. An
IPv4 loopback connection on a dual-stack socket can appear as
`::ffff:127.0.0.1`; normalize it for comparison rather than treating it as a
remote client.

A connection timeout before any request reaches EmbedIO differs from an HTTP
400/404/500 response. Record the failing URL and whether the listener logs a
request. For reproducible isolation drops, Microsoft's firewall troubleshooting
procedure supports a bounded `netsh wfp capture start keywords=19` / `netsh wfp
capture stop` trace. Correlate the destination address, port, package SID and
blocking filter. Avoid guessing that every timeout is a controller failure.

## Validation and limits

[Eight desktop regressions](../../test/EmbedIO.Tests/Issues/Issue554_UwpHostAccess.cs)
exercise wildcard/explicit prefixes, cached/uncached files, API-before-static
routing, actual endpoints, independent cookie jars and shutdown. They preserve
existing behavior rather than adding a UWP-specific library switch.

The test-only [AppContainer fixture](../../test/EmbedIO.AppContainerSmoke) and
[pinned Windows job](../../.github/workflows/appcontainer.yml) check the child
process's restricted token and package SID, blocked incoming loopback before and
after an outbound allowance, real HTML/API/session responses under an inbound
session, restoration after that session ends, and scoped profile/ACL/exemption
cleanup. Its operations are guarded for disposable GitHub runners. It is outside
the solution and shipped packages. A configured job is not passing evidence until
its result artifact succeeds.

The modern fixture is a .NET 10.0.12 Win32 process in a real AppContainer, not a
Xamarin/UWP/.NET Native application or a WebView. Full-trust MAUI Windows passes
also do not establish classic UWP behavior. No original application, manifest,
Angular assets or exact Xamarin/Windows versions were supplied. For a remaining
failure, provide those details plus the installed package family name, client and
server locations, exact prefixes/URLs, Release versus debug results, capabilities,
diagnostic allowance direction and a minimal runnable reproduction. The tracking
issue is [#107](https://github.com/WilliamSmithEdward/embedio-neo/issues/107).
