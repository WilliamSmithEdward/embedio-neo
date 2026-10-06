# Send immediately after a WebSocket connection

[Upstream #556](https://github.com/unosquare/embedio/issues/556), reported by nd1012 and confirmed by [WindBlowAssCold](https://github.com/unosquare/embedio/issues/556#issuecomment-2703409559), described first messages disappearing when ClientWebSocket sent immediately after ConnectAsync. The managed transport started receiving before WebSocketModule subscribed to messages, and discarded queued messages while the subscriber was absent.

The correction retains early messages in the existing transport queue and wakes dispatch when the module subscribes after OnClientConnectedAsync completes. A synchronized dispatcher also prevents competing consumers and missed idle-queue wake-ups. Transport control frames continue working during initialization; the fix does not postpone reception or require a server readiness message.

Connection initialization still precedes application-message callbacks. Initialization failures and cancellation now pass through the existing context cleanup block, and a context's disconnection notification is emitted once. Asynchronous managed-message callback exceptions are caught at the event boundary and logged. Existing callback APIs, listener modes, defaults and dependencies are unchanged.

These corrections are **unreleased** and unavailable in NuGet 1.0.2. Use a source checkout containing the fix for the example below.

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
Console.WriteLine("WebSocket endpoint: ws://127.0.0.1:8877/socket; press Ctrl+C to stop.");
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

Concurrent client/server shutdown with Windows' native HttpListener WebSockets also stalled during validation, matching [dotnet/runtime #115559](https://github.com/dotnet/runtime/issues/115559). A separate unreleased correction now avoids that native full-close path using public .NET APIs while retaining a complete closing handshake. See [Windows native WebSocket shutdown](windows-native-websocket-shutdown.md) for scope, validation and the .NET 11 recheck. NuGet 1.0.2 does not include that correction.

## Validation

Two controlled managed ClientWebSocket cases lost early text/binary bursts before correction, while the equivalent native cases passed. A separate deterministic transport test showed messages being discarded before subscription. Four initialization-failure/cancellation cases also failed to clean up accepted contexts before the cleanup fix.

Twenty-five focused cases cover late subscription, concurrent enqueue/dispatch handoff, real text/binary/fragmented/empty startup bursts, initialization-before-message ordering, ping/pong while initialization is pending, failed/cancelled initialization, client close/server stop, independent clients and asynchronous managed-message callback faults. The reported package version and exact original application were not supplied; this reproduces the mechanism rather than claiming that exact environment. Required Windows/Linux/macOS and security checks apply before merge.
