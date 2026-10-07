# Report progress with server-sent events

[Upstream #587](https://github.com/unosquare/embedio/issues/587), reported by
Dennis (`dos-ise`), asks how to stream progress from an EmbedIO controller.
[Mark Crossley's follow-up](https://github.com/unosquare/embedio/issues/587#issuecomment-2551030733)
asks whether a working approach was found.

The report writes ten `1` characters with `text/event-stream` and a fixed
ten-byte content length. Flushing those characters does not make them SSE
events: an event needs a `data:` field and a blank line. Use UTF-8, stream without
a precomputed content length, and flush after each complete event. A browser
`EventSource` then dispatches events as they arrive rather than waiting for the
controller to return. These rules follow the
[HTML SSE specification](https://html.spec.whatwg.org/multipage/server-sent-events.html).

## Simpler streaming with Neo's event writer

The additive `OpenEventStream()` helper prepares the SSE headers. Its writer
frames UTF-8 data, handles multiline values, and flushes each event:

```csharp
// Inside an async controller action; configure strict transport writes before server startup.
var events = HttpContext.OpenEventStream();
for (var completed = 1; completed <= 10; completed++)
{
    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken);
    await events.WriteAsync((completed * 10).ToString(), eventName: "progress");
}
await events.WriteAsync("done", eventName: "complete");
```

This API is included starting with EmbedIO-Neo 1.0.2. Use that package version
or later, or reference `src/EmbedIO/EmbedIO.csproj` from a source checkout.
The complete published-package example below is equivalent using existing APIs.

Call `OpenEventStream()` once before writing the response. Await writes in order;
overlapping writes fail rather than interleaving frames. The writer does not
need disposal and does not close the response stream. It propagates transport
errors and always observes server cancellation; an optional `cancellationToken`
adds per-write cancellation. Pass a linked lifetime token to both delays and
writes when you need an application timeout. Handle cancellation and transport
failures as in the complete example below.

Use `WriteCommentAsync()` for a flushed heartbeat. Optional `id:` fields are
available through `WriteAsync(data, id: "42")`; they do not provide storage or
replay. Multiline data is split into safe `data:` lines. CR, LF and NUL in event
names or IDs are rejected before any bytes are written. The helper introduces
no dependency and changes no listener default or existing API behavior.

## Run a complete progress demo on published 1.0.1

This finite demo simulates ten units of work. It does not import or persist
anything. Create a .NET 10 application using the verified published package:

```sh
dotnet new console -n ProgressDemo -f net10.0
cd ProgressDemo
dotnet add package EmbedIO-Neo --version 1.0.1
mkdir wwwroot
```

Replace `Program.cs` with:

```csharp
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    stopping.Cancel();
};

using var server = new WebServer(options => options
    .WithUrlPrefix("http://localhost:9696/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithWebApi("/api", module => module.WithController<ProgressController>())
    .WithStaticFolder("/", Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), false);
server.Listener.IgnoreWriteExceptions = false; // Set before starting the listener.
try { await server.RunAsync(stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }

public sealed class ProgressController : WebApiController
{
    [Route(HttpVerbs.Get, "/progress")]
    public async Task ReadProgress()
    {
        Response.ContentType = "text/event-stream";
        Response.SendChunked = true;
        Response.Headers["Cache-Control"] = "no-cache";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30)); // Demo's maximum request lifetime.
        var token = lifetime.Token;
        try
        {
            for (var completed = 1; completed <= 10; completed++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token); // Simulated work.
                var frame = Encoding.UTF8.GetBytes($"event: progress\ndata: {completed * 10}\n\n");
                await Response.OutputStream.WriteAsync(frame, 0, frame.Length, token);
                await Response.OutputStream.FlushAsync(token);
            }

            var done = Encoding.UTF8.GetBytes("event: complete\ndata: done\n\n");
            await Response.OutputStream.WriteAsync(done, 0, done.Length, token);
            await Response.OutputStream.FlushAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { } // Transport failed; end this subscriber's stream.
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
}
```

The API is registered before the static catch-all. Its base path is `/api`, so
the exact controller route `/progress` is reached at `/api/progress`. The action
returns `Task`: it writes its own response and must not return a second JSON
result. `SendChunked` replaces the report's `ContentLength64 = 10`; do not set
both. The response stream belongs to EmbedIO, so the action does not dispose it.

Create `wwwroot/index.html` with this complete browser client:

```html
<!doctype html>
<html lang="en">
<meta charset="utf-8">
<title>Progress demo</title>
<h1>Progress demo</h1>
<button id="start">Start demo</button>
<button id="stop" disabled>Stop watching</button>
<progress id="progress" max="100" value="0"></progress>
<output id="status" aria-live="polite">Ready</output>
<script>
const start = document.getElementById("start");
const stop = document.getElementById("stop");
const progress = document.getElementById("progress");
const status = document.getElementById("status");
let source;

function stopWatching(message) {
    source?.close();
    source = undefined;
    start.disabled = false;
    stop.disabled = true;
    status.textContent = message;
}

start.onclick = () => {
    progress.value = 0;
    start.disabled = true;
    stop.disabled = false;
    status.textContent = "Waiting for progress";
    source = new EventSource("/api/progress");
    source.addEventListener("progress", event => {
        progress.value = Number(event.data);
        status.textContent = `${event.data}%`;
    });
    source.addEventListener("complete", () => stopWatching("Complete"));
    source.onerror = () => stopWatching("Connection ended before completion; start again to retry");
};
stop.onclick = () => stopWatching("Stopped watching");
window.addEventListener("pagehide", () => source?.close());
</script>
</html>
```

Run `dotnet run` **from the ProgressDemo directory**, then open
`http://localhost:9696/`. Click **Start demo**. The bar advances through 10%,
20%, and so on at approximately one-second intervals; it reaches 100% and
the status becomes **Complete**. The client closes its `EventSource` after the
completion event, preventing its normal automatic reconnection from restarting
this demonstration. Named events require `addEventListener("progress", ...)`;
`onmessage` receives only events without a different `event:` name.

Alternatively, run `curl -N http://localhost:9696/api/progress` (PowerShell users
can use `curl.exe`). Expect ten frames followed by a completion frame:

```text
event: progress
data: 10

event: progress
data: 20

```

The last two frames are `event: progress` / `data: 100` and `event: complete` /
`data: done`, each ending with a blank line. Ctrl+C stops the server gracefully.

## Connect it to a real operation

Do not start a real, mutating import from a reconnectable GET subscription.
Start it with a POST, return an operation ID, and use a separate GET stream to
observe that existing operation. Define authentication, ownership, retention,
replay or resume behavior, and failure/cancellation events for your application.
The example deliberately does not implement an operation manager or promise
that retries resume work. Stopping observation is distinct from canceling work;
an application should expose explicit operation cancellation if it needs it.

Serialize structured progress data as JSON on the `data:` line rather than
inserting untrusted multiline text into event fields. Keep the stream handler
asynchronous and await writes to respect a slow subscriber; do not create an
unbounded queue of progress messages. For long quiet intervals, send an SSE
comment heartbeat such as `: keep-alive\n\n` and flush it. Proxy buffering and
timeouts can delay delivery even when the server flushes correctly.

The server's cancellation token is not a browser-disconnect token. A failed
write can end the stream when strict write errors are enabled, but detection
is not instantaneous. See [streaming response closure](streaming-response-close.md)
for resource ownership, `OnClose`, cancellation, heartbeat and disconnect limits,
including the cleanup correction delivered in 1.0.2. This demo uses APIs already in
1.0.1; it does not require that correction for normal completion.

The original application's complete setup and browser code were not provided.
This is a verified support pattern, not a claim that its exact application was
reproduced. The new helper simplifies the same existing capabilities. If it still fails, provide a
minimal server and browser client, runtime/OS, listener mode and proxy details.
