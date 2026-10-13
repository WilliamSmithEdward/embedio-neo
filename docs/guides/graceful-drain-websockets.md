# WebSockets during a graceful drain

`WebServer.DrainAsync(timeout)` stops accepting new work and lets accepted work
finish within a deadline. An open WebSocket is accepted work that only ends when
one side closes it, so by default the drain waits for it until the deadline.
Only then is the connection ended, usually by cutting it, so the client sees an
abnormal closure rather than a clean close.

Two opt-in settings let the drain end sooner:

- `WebSocketModule.CloseOnDrain = true` makes the drain send that module's open
  WebSockets a close with status 1001 (Going Away). The drain then finishes once
  clients complete the closing handshake, still bounded by its deadline.
- `WebServer.DrainStarted` is a cancellation token canceled when the drain starts.
  Use it to end other long-lived work, such as event streams.

Without them nothing changes. `RunAsync` cancellation and disposal stop the server
immediately, as before; they are not drains.

This is unreleased and available only from source.

## A complete program

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.WebSockets;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:8686/")
        .WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new ChatModule("/chat"));

// Long-lived work of your own can end itself here.
server.DrainStarted.Register(() => Console.WriteLine("Draining..."));

var running = server.RunAsync();
Console.WriteLine(new Uri("ws://localhost:8686/chat"));

var stopping = Task.Delay(Timeout.Infinite, shutdown.Token);
if (await Task.WhenAny(running, stopping) == running)
{
    await running; // Surface a startup or listener failure promptly.
    return;
}

try { await stopping; }
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }

// Clients get 1001 Going Away; the drain ends once they have closed, or after 10 seconds.
await server.DrainAsync(TimeSpan.FromSeconds(10));
await running;

internal sealed class ChatModule : WebSocketModule
{
    public ChatModule(string path)
        : base(path, true)
        => CloseOnDrain = true;

    protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        => BroadcastAsync(buffer, other => other != context);
}
```

`RunAsync` returns normally once the drain has finished.

## What happens to each WebSocket

With `CloseOnDrain`, as the drain starts:

- Each open WebSocket of the module is sent a close with status 1001 and the
  reason `Server is draining`. A message the server is already sending is
  finished first, so it is not cut short.
- A WebSocket the module accepts after the drain started, for example one whose
  upgrade request was already in progress, is closed the same way.
- `OnClientDisconnectedAsync` runs once per connection, as for any close. This
  holds when the application or the client closes the socket at the same time.
- A client that never answers the close is bounded by the existing WebSocket
  close policy and the drain's deadline. If the drain's cancellation token is
  canceled, remaining connections are aborted at once.

The setting applies over HTTP/1.1 upgrades and over HTTP/2 and HTTP/3 extended
CONNECT. On HTTP/2, closing a WebSocket stream does not affect other requests on
the same connection; they finish as usual. HTTP/3 drain also waits for each client
to close its QUIC connection after the server's GOAWAY, which cooperative clients
do once their streams are done.

Modules inside a `ModuleGroup` are included. Set `CloseOnDrain` before the server
starts; the module's configuration is locked from then on.

## `DrainStarted`

The token is canceled once, on the first call to `DrainAsync`, after modules with
`CloseOnDrain` have initiated their closes and before the listener stops admitting
work. Callbacks registered on it run synchronously within that call, so keep them
short. An exception a callback throws is logged and does not stop the drain. Disposal
and `RunAsync` cancellation do not cancel it.

## Validation

Real clients test the behavior over HTTP/1.1 and HTTP/2 (`ClientWebSocket`) and over
HTTP/3 (an RFC 9220 extended CONNECT stream). The tests cover:

- the default wait to the deadline and the 1001 close with early completion;
- a 4 MiB message in flight, a callback-originated close and a simultaneous
  client close, each with exactly one disconnection;
- an unresponsive client bounded by the deadline, and drain cancellation;
- HTTP/2 sibling requests, an upgrade accepted after the drain started, nested
  modules, and `DrainStarted` with a throwing callback.

Graceful drain under endurance load is recorded in the
[endurance campaign](../project/http-engine-endurance.md).
