# Streaming responses, OnClose, and cancellation

[Upstream #588](https://github.com/unosquare/embedio/issues/588) reports an SSE
controller whose loop waits for an `OnClose` callback to cancel it. That creates
a lifetime cycle: the handler must finish before `OnClose` runs, but its loop
waits for that callback before finishing. Moving the same loop into a controller
does not change the lifetime rule.

`OnClose` means request processing has finished. It is useful for completion
bookkeeping; it is not a notification that a browser disconnected. Use `finally`
or `using` for resources owned by the handler, and let the handler return when
its work ends, a write fails, or its cancellation token is canceled.

`HttpContext.CancellationToken` is the token passed to `WebServer.RunAsync`.
Passing it to delays and writes makes server cancellation cooperative. A remote
client reset does not cancel it. Calling `Listener.Stop` or `Dispose` alone is
also not a substitute for canceling the application-owned token used by a
long-running handler.

## Runnable SSE controller

Create a .NET 10 console app and install the verified available package version
1.0.1 (the maintained library's namespace remains `EmbedIO`):

```sh
dotnet new console -n StreamingDemo -f net10.0
cd StreamingDemo
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace `Program.cs` with this complete program:

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
    .WithWebApi("/", module => module.WithController<EventsController>());
// Set before any response stream is created. The existing default is true.
server.Listener.IgnoreWriteExceptions = false;
try { await server.RunAsync(stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }

public sealed class EventsController : WebApiController
{
    [Route(HttpVerbs.Get, "/alarms")]
    public async Task ReadAlarms()
    {
        Response.ContentType = "text/event-stream";
        Response.SendChunked = true;
        Response.Headers["Cache-Control"] = "no-cache";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2)); // Application policy, not a library default.
        var token = lifetime.Token;
        HttpContext.OnClose(_ => Console.WriteLine("Alarm request processing finished."));
        try
        {
            for (var sequence = 0; ; sequence++)
            {
                var frame = Encoding.UTF8.GetBytes($"data: alarm-{sequence}\n\n");
                await Response.OutputStream.WriteAsync(frame, 0, frame.Length, token);
                await Response.OutputStream.FlushAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { } // The peer disconnected during transport I/O.
        catch (HttpListenerException) { } // Native listener transport failure.
        catch (ObjectDisposedException) { } // The response transport was closed.
        finally
        {
            // Unsubscribe any application event handlers or release per-request resources here.
        }
    }
}
```

Run `dotnet run`, then `curl -N http://localhost:9696/alarms` (use `curl.exe` on
Windows if PowerShell maps `curl` to another command). Expect `data: alarm-0`,
a blank line, then a new event every second. Stop curl to disconnect; stop the
server with Ctrl+C. The example also ends each stream after two minutes. A
browser's `EventSource` normally reconnects after the response ends; call its
`close()` method when the application intends to stop receiving events.
The framing and reconnect behavior follow the
[HTML SSE specification](https://html.spec.whatwg.org/multipage/server-sent-events.html).

The action returns `Task`, because it writes the response itself. A
`Task<string>` action that never returns is not a valid runnable example. SSE
events need UTF-8 text and a blank-line terminator; a lone dot is not an event.
Avoid buffering a live stream until the handler returns. Flush each event,
and account for buffering in any proxy between the server and browser.

## Disconnect detection limits

Configure `IgnoreWriteExceptions = false` before the first write if the streaming
loop should stop on failed transport writes. The managed listener captures this
setting when it creates the response stream. With its default `true`, failed
writes can be suppressed and the loop can continue after the client disappears.
The native Microsoft listener delegates the setting to .NET; its stream can
still become disposed after a disconnect, even when write errors are suppressed.

A successful write only means the local stack accepted it. It does not prove
that the client received the event, nor guarantee instant disconnect detection.
Regular SSE comment heartbeats (`: keep-alive\n\n`) provide write opportunities
when no events are available. Use an application-owned maximum lifetime or
explicit subscription cancellation as well. For bidirectional communications,
consider the existing WebSocket APIs.

## Verified cleanup correction

This investigation also reproduced a separate library defect: server cancellation
ended the handler, but a canceled final flush skipped context closure. In native
listener mode, response disposal could also throw before completion callbacks
ran. Cleanup now attempts asynchronous request completion and context closure
even after a flush failure, and invokes close callbacks even if response closure
fails. Callback ordering and exception isolation are preserved; public APIs,
listener defaults and disconnect-token semantics are unchanged.

Eight real-listener regressions exercise both listener modes: ordinary streaming
completion, server cancellation, remote reset with strict writes, and remote
reset with suppressed writes. They verify a real Web API route, an initial SSE
event, the token lifetime, exactly one close callback after the handler exits,
and healthy fresh connections after completion or reset.
A ninth in-process regression checks asynchronous cleanup followed by close
callbacks after a flush failure, including isolation of a failing callback.
The sample uses existing APIs available in 1.0.1; the cleanup correction is an
unreleased change and must not be assumed present in that published package.
The original application's listener mode and complete reproduction were not
supplied, so this does not establish the cause of its reported missing write
exception. Supply a minimal server, runtime/OS, listener mode and client close
steps if a problem remains.

The original explanation and token recommendation are credited to
[Riccardo De Agostini's reply](https://github.com/unosquare/embedio/issues/588#issuecomment-1790480365).
Neo's validation adds the distinction between server cancellation and remote
disconnect, and the cleanup correction described above.
