# Stop a polling response when a transport write fails

[Upstream #457](https://github.com/unosquare/embedio/issues/457), reported by
ketodiet, describes ordinary HTTP polling through an ASP.NET reverse proxy.
Managed writes continued after the proxy connection closed, while native writes
reported failures. OffTimers supplied a continuous-write controller and proposed
exposing an internal stream; klasyc identified the existing public setting below.
The eight attributed comments are preserved in [Neo #176](https://github.com/WilliamSmithEdward/embedio-neo/issues/176).

## Configure the existing public write-error policy

Set this before starting the server:

```csharp
server.Listener.IgnoreWriteExceptions = false;
```

The managed listener defaults to `true` and snapshots the setting when a response
stream is created. With that suppression policy, successful `WriteAsync` calls
can continue after a transport failure. Set `false` before `RunAsync` to let the
handler observe failures; changing internal stream visibility or casting to an
internal type is unnecessary. This is an existing API, not a changed default.

The native listener delegates to .NET. Its stream may become disposed after a
disconnect even if write errors are ignored. Treat error suppression as a policy,
not a promise that the two backends produce identical exception types.

## Complete bounded polling example

The demonstration sends SSE comment heartbeats and tick events. The counter is
per connection and is not persistent application data. Use published 1.0.3:

```sh
dotnet new console --framework net10.0 --name NeoPolling
cd NeoPolling
dotnet add package EmbedIO-Neo --version 1.0.3
```

Replace `Program.cs`:

```csharp
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using EmbedIO;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .OnGet("/poll", async context =>
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers[HttpResponseHeader.CacheControl] = "no-cache";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var output = context.OpenResponseStream(buffered: false, preferCompression: false);
            for (var tick = 0; ; tick++)
            {
                var frame = Encoding.UTF8.GetBytes($": keep-alive\ndata: tick {tick}\n\n");
                await output.WriteAsync(frame, lifetime.Token);
                await output.FlushAsync(lifetime.Token);
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException || error is HttpListenerException || error is ObjectDisposedException)
        {
            Console.Error.WriteLine("Polling transport ended; this response is finished.");
        }
    })
    .OnGet("/health", c => c.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
server.Listener.IgnoreWriteExceptions = false;
Console.WriteLine("GET http://127.0.0.1:8877/poll ; press Ctrl+C to stop the server.");
try { await server.RunAsync(stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
```

Run `dotnet run`, then `curl -N http://127.0.0.1:8877/poll`. It emits
`: keep-alive`, `data: tick 0`, a blank line, and further events every second.
Stop curl to close that client connection. Request
`curl http://127.0.0.1:8877/health`; the body remains `healthy`.
Press Ctrl+C in the server terminal for graceful shutdown. Each poll also has an
application-owned two-minute limit; a browser EventSource can reconnect, so call
its `close()` method when the subscription should end.

`.OnGet` matches a base path, including children. A write/flush failure ends this
handler; it does not prove which peer or network component failed. The context
token belongs to server lifetime, not automatic client-disconnect cancellation.
The private linked token adds a response deadline without changing that contract.
Do not send a new JSON error response after streaming headers/body have started.

## Know where writes actually go

Keep `buffered: false` for live polling. With `buffered: true`, writes accumulate
in application memory and `FlushAsync` is not a transport probe: commitment
occurs when that buffering stream is disposed. Even with strict write errors,
those earlier memory writes cannot identify a vanished peer.

Compression also buffers output. Flush each logical event when negotiating gzip
or deflate; choose `preferCompression: false` for the simple example so buffering
is easy to reason about. The tests verify actual gzip negotiation and flushes,
not merely an Accept-Encoding request header. `WithSupportCompressedRequests`
controls incoming request decompression; it is not the response write-error setting.

Successful transport writes mean local acceptance, not remote application delivery.
Some writes can succeed before TCP reports a close/reset. There is no guarantee
that exactly the next write fails or that idle disconnection is detected instantly.
A half-closed TCP sending direction is not necessarily a peer unable to receive.

## Account for the reverse proxy

EmbedIO's transport peer is the proxy, not the final browser. If the proxy keeps
its upstream connection open, the backend may legitimately continue writing
while the downstream client is gone. Configure upstream cancellation, response
buffering and idle timeouts in the proxy; they cannot be inferred from a backend
write's success. Heartbeats provide write opportunities, while a deadline or
application subscription cancellation prevents an indefinite loop.

The regression fixture is a controlled two-hop TCP relay which closes the actual
backend connection after a first successful response, using both reset and orderly
shutdown. It verifies strict versus suppressed writes, plain/gzip output,
completion callbacks, buffered response commitment, unchanged server tokens and
healthy subsequent requests in both listener modes. It is not a reproduction of
the original ASP.NET/IIS application or every proxy's buffering behavior.

See [streaming lifetime and closure](streaming-response-close.md) for completion
callbacks and server cancellation, and [chunked response streaming](chunked-response-streaming.md)
for protocol framing. `OnClose` runs after request handling completes; it should
not be used to cancel the loop that prevents completion.

## Outcome

The reported continuing writes are reproduced under the managed suppression
policy, and existing `IgnoreWriteExceptions = false` surfaces the transport
failure in these scenarios. This work preserves all defaults, public APIs,
framework targets and dependencies. It documents a tested configuration answer;
it does not claim the original application was repaired or introduce a new
client-disconnect notification API.
