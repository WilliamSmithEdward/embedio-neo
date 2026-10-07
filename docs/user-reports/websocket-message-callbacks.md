# Text and binary WebSocket callbacks

[Upstream #547](https://github.com/unosquare/embedio/issues/547), proposed by
original maintainer `rdeago`, identified a useful goal: receive complete text or
binary messages without repeatedly interpreting a byte buffer and receive-result
object. Its v4 design also removed frame callbacks and changed existing subclass
contracts. Neo preserves those contracts and adds an opt-in alternative instead.

## Availability

`EmbedIO.WebSockets.WebSocketMessageModule` is a new source API. It is not in
published EmbedIO-Neo 1.0.2. Until a release includes it, use a checkout containing
this change and a project reference. No package/assembly/namespace identity,
dependency or target-framework change is required. Existing subclasses of
`WebSocketModule` continue to use their original callbacks.

## Run a complete echo endpoint

Create a .NET 10 console application outside the repository. Replace
`/path/to/embedio-neo` with the absolute path of your checkout:

```sh
dotnet new console --framework net10.0 --name MessageEcho
cd MessageEcho
dotnet add reference /path/to/embedio-neo/src/EmbedIO/EmbedIO.csproj
```

Replace `Program.cs` with:

```csharp
using System;
using System.Threading;
using EmbedIO;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new EchoEndpoint());
Console.WriteLine("Connect to ws://127.0.0.1:8877/socket ; press Ctrl+C to stop.");
await server.RunAsync(stopping.Token);
```

Save this complete class as `EchoEndpoint.cs` in the same application:

```csharp
using System.Threading.Tasks;
using EmbedIO.WebSockets;

public sealed class EchoEndpoint : WebSocketMessageModule
{
    public EchoEndpoint() : base("/socket", enableConnectionWatchdog: false)
    {
        MaxMessageSize = 64 * 1024;
    }

    protected override Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
        => SendAsync(context, "echo: " + text);

    protected override Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data)
        => SendAsync(context, data);
}
```

Run `dotnet run`. From a browser developer console, connect and send text:

```javascript
const socket = new WebSocket("ws://127.0.0.1:8877/socket");
socket.onmessage = event => console.log(event.data);
socket.onopen = () => socket.send("hello € 😀");
```

Expected text response: `echo: hello € 😀`. A binary message is echoed as the
same bytes. The endpoint is exactly `/socket`; adding a child path does not
select it. Press Ctrl+C in the server console to cancel and dispose the server.
There is no static-folder, session or DI setup to configure for this example.

## Callback behavior

Override either callback or both:

- `OnTextMessageReceivedAsync(context, text)` receives a complete decoded string,
  including an empty string. Fragment boundaries may split a UTF-8 sequence;
  decoding happens after message assembly, using strict UTF-8.
- `OnBinaryMessageReceivedAsync(context, data)` receives the complete byte array,
  including an empty array. It is not text-decoded.

Messages are dispatched in order per connection and the returned callback task
is awaited before the next callback starts. Different connections progress
independently. Use `context.CancellationToken` in asynchronous application work
and return promptly; do not wait inside a callback for a later callback on the
same connection. The module cannot forcibly terminate application code that
ignores cancellation.

Keep sends made by a callback awaited. Work started independently of these
callbacks must coordinate its own sends to a connection; this module does not
add a universal send lock to the existing `IWebSocket` API.

Inherited configuration, connection/disconnection hooks and send helpers remain
available. `Encoding` controls inherited text sends; reception always validates
UTF-8 regardless of that property. The sample sets `MaxMessageSize` to 64 KiB;
the inherited default remains zero (unlimited). That limit is measured in bytes,
not decoded characters.

## Rejections and failures

| Condition handled by the module | Close status |
| --- | --- |
| Text or binary callback not overridden | 1003, unsupported data |
| Text delivered to the module is not valid UTF-8 | 1007, invalid payload |
| Complete message exceeds configured MaxMessageSize | 1009, message too big |
| Unexpected message callback exception | 1011, server error |

An endpoint overriding only the text callback therefore rejects binary, and a
binary-only endpoint rejects text. Control/close frames are handled by the
transport, not delivered as text/binary application messages. After a connection
closes or cancellation occurs, pending messages do not enter another callback.

Callback failures are diagnosed and closed using a generic reason rather than
exposing the exception to the peer. A `DecoderFallbackException` thrown by
application code is a callback failure (1011), not evidence of invalid incoming
text. Cancellation requested through the context token follows normal shutdown.
To request an application policy close, use the existing API and return:

```csharp
// Partial replacement inside a callback; do not continue processing afterward.
await context.WebSocket.CloseAsync(
    CloseStatusCode.PolicyViolation, "Request rejected.", context.CancellationToken);
return;
```

The original proposal's public exception-driven close API and replacement of
connection callbacks are not adopted here. Existing WebSocketException
constructors and legacy connection-hook behavior are unchanged.

## Transport and compatibility boundaries

The Windows native HTTP.sys WebSocket backend can reject malformed text before
it reaches the module. The local malformed-text probe produced native HRESULT
`0x83760002` in `WebSocketProtocolComponent.WebSocketGetAction`, followed by a
client connection reset rather than an observable 1007 handshake. No application
message callback ran, and a fresh connection remained usable. This is an
existing native transport boundary, not a new strict decoder or a claimed
runtime repair. Other native runtime versions/platforms may report their own
protocol failure. The module promises 1007 when it receives the invalid bytes;
it cannot override a transport's earlier rejection.

The native adapter's close-code mapping is corrected for `UnsupportedData`
(1003, previously incorrectly mapped to 1007) and `Away` (1001, previously
rejected). These are protocol corrections to the existing documented enum values.
No default listener backend is changed.

The managed backend assembles a message before this module's size check; the
native module loop also checks size during assembly. A per-message size limit
does not impose a hard memory/backlog bound on all transport buffering or slow
application callbacks. There is no receive-loop rewrite or allocation-improvement
claim in this change.

Legacy `OnMessageReceivedAsync` and `OnFrameReceivedAsync` remain available with
their existing behavior. Native frame callbacks represent receive-buffer chunks,
not guaranteed wire-frame boundaries; the managed legacy path does not invoke
that callback. Retaining it does not change those historical differences. The
new class has a sealed complete-message dispatcher, so its subclasses implement
the text/binary callbacks instead of the legacy combined callback.

## Validation

Forty-three focused cases exercise both listener modes: empty and large payloads,
fragmented Unicode/binary data, ordered bursts, asynchronous callback ordering,
independent connections, default rejection, native close-code mapping, strict
malformed UTF-8 variants, callback errors, cancellation, healthy reconnects and
existing subclass/frame-callback behavior. The Windows native early rejection
is recorded explicitly rather than mislabeled as a graceful 1007 response.

Both core targets and the new example classes compile. An old-style subclass
also compiles against published 1.0.2 for .NET Standard 2.0 and .NET 10, while
the live legacy regression uses the current core. Compilation is not a blanket
binary/platform compatibility certification. Cross-platform repository CI remains
required. Framing and status semantics follow [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455.html),
especially complete UTF-8 messages and sections 7.4/8.1.
