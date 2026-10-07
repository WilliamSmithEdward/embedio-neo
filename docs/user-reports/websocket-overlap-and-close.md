# Overlapping WebSocket traffic and close requests

[Upstream #502](https://github.com/unosquare/embedio/issues/502) reported browsers
closing a busy connection with “Received start of new message but previous
message is unfinished.” bdurrer's [sample](https://github.com/bdurrer/embedio-websocket-example)
sends JSON every 20 ms and returns large tables after simulated work. The
maintainer discussion correctly identified two surfaces: complete data messages
must not interleave, and closing a connection can also write a control frame.

The related [selective backport audit](upstream-backport-audit.md) introduced
whole-message/transport-write synchronization in the portable listener and data
send/Windows close-output synchronization in the native adapter. Those fixes are
on main, not in published EmbedIO-Neo 1.0.3. This investigation exercises the
original workload shape and addresses the remaining close-request interval.

## What happens when closing starts

A valid native close request can wait behind an active send before its closing
frame is written. Previously, already queued sends could still transmit during
that interval, and incoming data could invoke application callbacks. A private
close-request marker now rejects new and queued data sends and suppresses new
application data callbacks while continuing to receive the closing handshake.
An active transport write is allowed to finish; callbacks already in progress
are not forcibly terminated.

The public `System.Net.WebSockets.WebSocketState` values and the adapter's
underlying-state reporting are retained. There is no added `CloseRequested`
enum value: `State` may still be `Open` while the close frame waits, so a state
check alone cannot guarantee that a subsequent send will be accepted. Await the
send and handle its failure as part of the application's connection lifetime.

Invalid close parameters are validated before closing admission changes. A
failed or cancelled initial close is terminal; cancellation of a secondary
waiter does not introduce an adapter abort of the close already in progress.
The native runtime still owns its transport behavior. Existing Windows native
shutdown coordination and the released-.NET-11 recheck remain as described in
[the shutdown guide](windows-native-websocket-shutdown.md).

## Bound close through the existing cancellation API

This partial snippet belongs in an application-owned WebSocket callback or
connection-lifetime method. `context` is its `IWebSocketContext`; add
`System`, `System.Threading` and `EmbedIO.WebSockets` imports:

```csharp
using var closing = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
closing.CancelAfter(TimeSpan.FromSeconds(5));
try
{
    await context.WebSocket.CloseAsync(CloseStatusCode.Normal, "done", closing.Token);
}
catch (OperationCanceledException) when (closing.IsCancellationRequested)
{
    // Record the application timeout/shutdown if useful; the connection is terminal.
}
```

The five-second policy is selected by the application, not a new library default.
The portable listener retains its existing acknowledgement-wait duration but
now observes the supplied cancellation token during that wait. Previously, a
silent peer could make close finish its wait without observing cancellation.
Native close also continues to use the caller's cancellation/lifetime policy.
Dispose token sources after the awaited operation completes.

Avoid starting new work for a connection after requesting close. Stop periodic
broadcast producers as part of shutdown and await the operations you own. Do
not fire-and-forget close or rely on a successful earlier `State` check. For
complete-message text/binary callbacks and UTF-8 handling, see
[message callbacks](websocket-message-callbacks.md).

## Evidence and limits

The adapted regression uses the sample's JSON subprotocol, 20 ms inputs,
deterministic delays spanning 50–150 ms, and 1,000 rows of ten 20-character
columns. Sequence IDs and deterministic column contents make loss, duplication
and corruption observable. It tests one/two clients on both listener modes,
overlapping targeted replies with two large broadcasts, parses every complete
JSON message and validates every row/column. All four source cases pass. The
same workload against the exact published 1.0.3 assembly reproduced an aborted
native single-client connection in one of four cases; the other three passed.
This is an adapted .NET-client reproduction, not execution of the exact original
EmbedIO 3.4.3/Windows 10/Chrome/Firefox installation.

Controlled transports separately reproduce the remaining close-admission and
callback gaps and the ignored managed acknowledgement-wait cancellation. The
corrected cases preserve invalid-parameter behavior, cancelled secondary-close
compatibility, data/framing synchronization, lifetime cleanup and the existing
native shutdown regressions. Full desktop/platform/security gates are required
before merge. No public API, listener default, framework target or dependency is
changed, and no default timeout or new state enum is introduced.

The sample's public-domain dedication is preserved in the investigation evidence;
credit for the report and workload remains with bdurrer. Thanks also to
radioegor146 and rdeago for identifying why data-only locking was insufficient,
and to the participants who kept the discussion active.

If a concern remains, provide a minimal server/client, listener mode, runtime/OS,
message sizes and the observed close code/error. Avoid sharing application
secrets or private payloads; generated representative data is enough to test
framing and lifecycle behavior.
