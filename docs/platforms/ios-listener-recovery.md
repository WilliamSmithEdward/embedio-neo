# Listener replacement and iOS suspension

[Upstream #402](https://github.com/unosquare/embedio/issues/402) reported an iOS
application that could not start a replacement server after suspension made its
old listener unusable. JRosanowski proposed that removal reconstructed a prefix
whose reference did not match a dictionary key. The original application,
runtime/version and a minimal suspension reproduction were not supplied.

## What current source verifies

The PR #182 integration branch uses an owner-scoped prefix-lease ledger. Shutdown
retires the registered leases themselves, detaches the final endpoint binding and
closes its listening socket. A subsequent listener gets a fresh endpoint. Retiring
one owner of a shared endpoint preserves the other owner's routes and socket.
Removal uses the addresses registered at startup even if the global address-family
setting changes later.

Twenty-two HTTP/TLS controls verify owner inventories, closed listening sockets,
port release, distinct replacement endpoints and independent client responses.
They cover Stop, Dispose, graceful drain, cancellation awaited at the WebServer
level, equivalent newly allocated prefix strings, localhost aliases, shared
owners and a deliberately closed physical listening socket. All pass on unchanged
integration source; no new production fix or API is introduced for this report.
A forced socket failure is a controlled desktop test, not iOS suspension.

## Await the previous generation before replacement

A stopped WebServer is final; create a new instance for the next generation.
Cancel the prior generation, await its RunAsync task, and dispose it before starting
a replacement on the same prefix. Keep lifecycle transitions serialized in your
application rather than starting overlapping generations.

This complete console example verifies two generations on one endpoint:

```csharp
using System.Net.Http;
using System.Text;
using EmbedIO;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var client = new HttpClient();
client.DefaultRequestHeaders.ConnectionClose = true;
const string url = "http://127.0.0.1:9696/";

async Task RunGeneration(string marker)
{
    using var stop = new CancellationTokenSource();
    using var server = new WebServer(o => o.WithUrlPrefix(url)
        .WithMode(HttpListenerMode.EmbedIO))
        .OnGet("/", context => context.SendStringAsync(marker, "text/plain", Encoding.UTF8));
    var running = server.RunAsync(stop.Token);
    try
    {
        var body = await client.GetStringAsync(url, deadline.Token);
        if (body != marker) throw new InvalidOperationException("Wrong server generation.");
        Console.WriteLine(body);
    }
    finally
    {
        stop.Cancel();
        await running.WaitAsync(deadline.Token);
    }
}

await RunGeneration("first");
await RunGeneration("replacement");
```

The example is for the unreleased integration source. It demonstrates orderly
replacement, not permission to serve indefinitely while an iOS app is suspended.
For HTTPS, retain strict client certificate trust; see [HTTPS guidance](../guides/https.md).

## iOS lifecycle boundary and remaining validation

Apple documents that foreground apps generally transition briefly through the
background before suspension, and recommends stopping tasks and releasing shared
resources during that transition. Use the applicable application or scene lifecycle
notifications in your host app; scene-based apps can have more than one scene.
See Apple's [background preparation](https://developer.apple.com/documentation/uikit/preparing-your-ui-to-run-in-the-background)
and [background execution sequence](https://developer.apple.com/documentation/uikit/about-the-background-execution-sequence).
A library endpoint cleanup correction cannot grant background execution.

The remaining validation is a real iOS app lifecycle sequence: serve a foreground
request, enter the background and allow suspension, retire/await the prior server
generation, return to the foreground, and verify a new HTTP/TLS server on the same
prefix. Record the device/iOS version, .NET/MAUI runtime, lifecycle timestamps,
exceptions, server state and actual client responses. Test local-network and
certificate configuration separately from endpoint retirement; preserve platform
policies and certificate validation.

The reporter's exact historical environment has not been tested. MAUI CI is
[temporarily paused at the owner's direction](maui-https-validation.md#temporary-owner-requested-pause),
and existing HTTPS smoke checks do not exercise background suspension. The fork
[tracking issue #320](https://github.com/WilliamSmithEdward/embedio-neo/issues/320)
remains open for this specific iOS validation gap; the desktop endpoint-lifetime
claim is answered by the controls above. Credit to JRosanowski for the report and
to geoperez and rdeago for the original investigation and platform discussion.
