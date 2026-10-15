# WebSocket handshake status

A WebSocket module accepts connections at its configured endpoint. For a module
mounted at `/register`, connect to `ws://127.0.0.1:9696/register` (or `wss://`
when HTTPS is configured), rather than a descendant such as `/register/extra`.
A trailing slash and a query string remain supported.

## A minimal server

Create a console application referencing EmbedIO-Neo. These examples use the
existing `EmbedIO` namespaces and APIs; the corrections below are new in
Neo v2 and are not in the published 1.0.x packages.

```csharp
using System.Text;
using EmbedIO;
using EmbedIO.WebSockets;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:9696/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager()
    .WithModule(new EchoModule("/register"));
await server.RunAsync(stop.Token);

sealed class EchoModule : WebSocketModule
{
    public EchoModule(string path) : base(path, false) { }

    protected override Task OnMessageReceivedAsync(
        IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        => result.MessageType == (int)System.Net.WebSockets.WebSocketMessageType.Text
            ? SendAsync(context, Encoding.UTF8.GetString(buffer))
            : SendAsync(context, buffer);
}
```

## A .NET client

Run this in a separate console application while the server is listening.
The sample sends one complete, short text message; a general client must assemble
fragmented messages and handle close frames.

```csharp
using System.Net.WebSockets;
using System.Text;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var socket = new ClientWebSocket();
await socket.ConnectAsync(new Uri("ws://127.0.0.1:9696/register"), timeout.Token);
await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hello")),
    WebSocketMessageType.Text, true, timeout.Token);
var buffer = new byte[256];
var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, result.Count));
await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
```

For HTTPS, configure the server certificate and client trust as described in
[HTTPS guidance](https.md). Do not disable certificate validation to work around
a handshake status error.

## Diagnose an unexpected HTTP 200

Modules execute in registration order. An earlier final HTTP handler can produce
an ordinary 200 before the WebSocket module sees the request. Register specific
WebSocket endpoints before a catch-all final handler. An explicitly configured
HTTP handler at a descendant remains an ordinary HTTP handler, even when the
client requests an upgrade.

The managed listener previously let a parent WebSocket module claim unsupported
descendants without performing a handshake, producing an empty 200. The correction
passes these requests to subsequent modules: a nested WebSocket endpoint or HTTP
route can handle them, and otherwise the server returns 404. Exact endpoint
matching and explicit module order remain unchanged. A normal GET without upgrade
headers at the socket endpoint is rejected as a bad handshake.

A successful **HTTP/1.1** WebSocket upgrade sends **101 Switching Protocols**.
The correction also records 101 in the managed response model, so application
observers and completion diagnostics agree with the wire. Previously the wire
could correctly contain 101 while response metadata still said 200.
**HTTP/2 and HTTP/3 extended CONNECT** use a successful **200**, not 101; do not
apply the HTTP/1 expectation to those protocols.

## Evidence and limitations

[Upstream #404](https://github.com/unosquare/embedio/issues/404) reported 200 instead
of 101 with a Windows 10 / .NET Framework 4.7.2 server and a client in a .NET
Standard 2.1 library. The exact client URI and custom `WebSocketCallbackModule`
implementation were not supplied. The discussion by georgeinva2004, geoperez and
rdeago is preserved in [the fork tracking issue](https://github.com/WilliamSmithEdward/embedio-neo/issues/317).
These two current defects were independently reproduced; this does not establish
the cause in the unavailable original application or reproduce its historical runtime.

Twenty-four regressions exercise independent .NET ClientWebSocket connections and
raw handshake responses over plain TCP and TLS, exact/trailing/query endpoints,
descendant fallback, nested sockets, ordinary HTTP handlers, module order,
rejection followed by healthy connections, sessions and HTTP/2 CONNECT metadata.
Fourteen fail on the unchanged integration baseline; all pass after correction.
Existing handshake, cookie and HTTP/2 WebSocket cases are retained.
