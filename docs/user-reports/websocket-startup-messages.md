# Send immediately after a WebSocket connection

[Upstream #556](https://github.com/unosquare/embedio/issues/556), reported by nd1012 and confirmed by [WindBlowAssCold](https://github.com/unosquare/embedio/issues/556#issuecomment-2703409559), described first messages disappearing when ClientWebSocket sent immediately after ConnectAsync. The managed transport started receiving before WebSocketModule subscribed to messages, and discarded queued messages while the subscriber was absent.

The correction retains early messages in the existing transport queue and wakes dispatch when the module subscribes after OnClientConnectedAsync completes. A synchronized dispatcher also prevents competing consumers and missed idle-queue wake-ups. Transport control frames continue working during initialization; the fix does not postpone reception or require a server readiness message.

Connection initialization still precedes application-message callbacks. Initialization failures and cancellation now pass through the existing context cleanup block, and a context's disconnection notification is emitted once. Asynchronous managed-message callback exceptions are caught at the event boundary and logged. Existing callback APIs, listener modes, defaults and dependencies are unchanged.

These corrections are included starting with EmbedIO-Neo 1.0.3. The example below uses a source checkout; a package reference to 1.0.3 or later also includes them.

## Complete echo server

From that checkout:

```sh
dotnet new console --framework net10.0 --name SocketServer --output TestResults/SocketServer
dotnet add TestResults/SocketServer/SocketServer.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/SocketServer/Program.cs` completely:

```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.WebSockets;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new EchoModule());
Console.WriteLine(new Uri("ws://127.0.0.1:8877/socket"));
await server.RunAsync(stop.Token);

public sealed class EchoModule : WebSocketModule
{
    public EchoModule() : base("/socket", false) { }

    protected override Task OnMessageReceivedAsync(
        IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        => SendAsync(context, Encoding.UTF8.GetString(buffer));
}
```

Run `dotnet run --project TestResults/SocketServer`. This example exchanges text messages; it does not define a binary application protocol. The module endpoint matches `/socket` exactly, rather than every path below it.

## Complete client without an artificial delay

In another terminal:

```sh
dotnet new console --framework net10.0 --name SocketClient --output TestResults/SocketClient
```

Replace `TestResults/SocketClient/Program.cs` completely. ClientWebSocket is built into .NET; the client needs no EmbedIO package reference.

```csharp
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var socket = new ClientWebSocket();
await socket.ConnectAsync(new Uri("ws://127.0.0.1:8877/socket"), timeout.Token);
await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hello")),
    WebSocketMessageType.Text, true, timeout.Token);

using var message = new MemoryStream();
var buffer = new byte[1024];
WebSocketReceiveResult result;
do
{
    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
    if (result.MessageType != WebSocketMessageType.Text)
        throw new InvalidOperationException("Expected a text echo.");
    message.Write(buffer, 0, result.Count);
} while (!result.EndOfMessage);
Console.WriteLine(Encoding.UTF8.GetString(message.ToArray()));
await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
```

Run `dotnet run --project TestResults/SocketClient`. Expect `hello`, then a clean client exit. Send occurs immediately after successful ConnectAsync, with no Thread.Sleep or empty-message readiness exchange. Press Ctrl+C in the server terminal to stop gracefully.

## Connection initialization and application protocols

[RFC 6455 sections 4.1 and 6.1](https://www.rfc-editor.org/rfc/rfc6455.html#section-6.1) permit sending when the WebSocket connection is open. The protocol does not require the server to send the first application message. The reporter's application-level readiness workaround therefore should not be needed to compensate for this dispatch bug.

Keep OnClientConnectedAsync focused on initialization and let it complete. Do not wait there for an OnMessageReceivedAsync callback: application-message dispatch starts afterward on both listeners. An application can still define a deliberate readiness/authentication exchange when its own protocol needs one.

The change preserves existing asynchronous callback behavior. On the managed listener, callback completion can overlap; retaining receive-order invocation does not introduce a new guarantee of serialized asynchronous completion. It also does not impose a new startup queue cap, message-size default or initialization deadline. Queued messages are not a promise to replay application data after the connection has closed.

### Windows native shutdown limitation

Concurrent client/server shutdown with Windows' native HttpListener WebSockets also stalled during validation, matching [dotnet/runtime #115559](https://github.com/dotnet/runtime/issues/115559). A separate correction included starting with EmbedIO-Neo 1.0.3 avoids that native full-close path using public .NET APIs while retaining a complete closing handshake. See [Windows native WebSocket shutdown](windows-native-websocket-shutdown.md) for scope, validation and the .NET 11 recheck. NuGet 1.0.2 does not include that correction.

## Validation

Two controlled managed ClientWebSocket cases lost early text/binary bursts before correction, while the equivalent native cases passed. A separate deterministic transport test showed messages being discarded before subscription. Four initialization-failure/cancellation cases also failed to clean up accepted contexts before the cleanup fix.

Twenty-five focused cases cover late subscription, concurrent enqueue/dispatch handoff, real text/binary/fragmented/empty startup bursts, initialization-before-message ordering, ping/pong while initialization is pending, failed/cancelled initialization, client close/server stop, independent clients and asynchronous managed-message callback faults. The reported package version and exact original application were not supplied; this reproduces the mechanism rather than claiming that exact environment. Required Windows/Linux/macOS and security checks apply before merge.

## Historical startup race and current-source verification

[Upstream #401](https://github.com/unosquare/embedio/issues/401), reported by
DennisKuypers, identified the accept-to-subscription interval in
[revision 96f0fcc](https://github.com/unosquare/embedio/blob/96f0fcc72db73e45f61240e0134de9bf1348a212/src/EmbedIO/WebSockets/WebSocketModule.cs#L192).
The initial application handshake was sent from Chrome 78 on Windows 10 to a
.NET Framework 4.8 server. DennisKuypers reported a higher failure frequency under
debug/load and suggested delaying server processing to expose the race. geoperez
and rdeago investigated; a subsequently deleted account also pointed out that
reception began before event subscription.

The same managed startup mechanism was corrected through
[Neo #103 / PR #104](https://github.com/WilliamSmithEdward/embedio-neo/pull/104).
For this older report, 24 additional controls hold connection initialization open,
observe that the transport has actually queued the complete application handshake
and burst, and only then release initialization. They verify exactly-once,
in-order callbacks and independent client transcripts for text/binary and fragmented
messages over HTTP/1 and HTTP/2, with both plain TCP and TLS. All pass on unchanged
current integration source; no new production fix, API or callback guarantee is
introduced. The initialization gate is a test control, not a recommended client delay.

The actual published **EmbedIO-Neo 1.0.3** package also passes all 12 HTTP/1 controls
with each of its .NET 10 and .NET Standard 2.0 assemblies on a .NET 10 host. This
verifies the package's fix for the controlled timing window; it does not reproduce
the unavailable original application or Chrome 78/.NET Framework 4.8 environment.
HTTP/2 startup controls apply to the Neo v2 engine, not that v1
package. [Fork tracking issue #324](https://github.com/WilliamSmithEdward/embedio-neo/issues/324)
records the attributed report, discussion, reactions and validation.

### Current v2 listener selection

The original discussion suggested `HttpListenerMode.Microsoft` as a comparison.
That backend remains a historical v1 detail. William approved its removal from
Neo v2: current integration applications use the replacement engine, and cannot
select Microsoft mode. See the
[v2 listener migration](../compatibility/neo-v1-to-v2.md#listener-selection-and-deployment).
Do not restore the removed backend or add a client sleep to address startup loss.
The complete server/client examples above use `HttpListenerMode.EmbedIO` and send
immediately after connection. Application-level authentication or readiness can
still be an intentional protocol choice, but is not required to repair this race.
