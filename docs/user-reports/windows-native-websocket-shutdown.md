# Windows native WebSocket shutdown

This correction is included starting with EmbedIO-Neo 1.0.3. It affects
`HttpListenerMode.Microsoft` on Windows. The default `HttpListenerMode.EmbedIO`
uses a different WebSocket implementation.

## What failed

Closing both ends at approximately the same time can deadlock inside Windows'
native `HttpListener` WebSocket implementation. Local .NET 10.0.11 stacks showed
`CloseOutputAsyncCore/TakeLocks`, receive/abort and keep-alive threads blocked.
The evidence matches [dotnet/runtime #115559](https://github.com/dotnet/runtime/issues/115559).
Microsoft's [fix #132314](https://github.com/dotnet/runtime/pull/132314) corrects
lock ordering inside native `CloseAsync` and targets .NET 11.

## How EmbedIO avoids that path

The Windows native adapter sends the closing frame through public
`CloseOutputAsync`, then completes the handshake through a coordinated receiver.
Only one receive operation can be active. An already pending module receive can
consume the peer's closing frame; if there is none, the closing caller receives
it. This also supports closing from connection and message callbacks, where
waiting for the module to resume receiving would otherwise stall.

`IWebSocket.CloseAsync` still waits for the handshake, preserves the requested
status and reason, and honors cancellation. Concurrent close calls wait for the
same connection to finish. A canceled caller waiting behind another close does
not abort that existing operation. Cancellation of an active close aborts the
transport to release a pending receiver. No arbitrary close deadline, new public
API, dependency, private-field access or listener-default change is introduced.
Coordination gates are disposed only after their in-flight callers finish.
Other platforms retain their native full-close implementation.

A silent peer can still leave an uncanceled graceful close pending. Supply an
application-appropriate cancellation token when directly calling
`context.WebSocket.CloseAsync`; module disposal retains its existing lifetime
and cancellation policy. This change does not promise all application shutdown
will finish despite blocked callbacks or uncooperative peers.

## Validation and .NET 11 recheck

The tracking issue is [#105](https://github.com/WilliamSmithEdward/embedio-neo/issues/105).
Regressions exercise handshake completion, single-receiver ownership, concurrent
close callers, UTF-8 reason validation, cancellation, callback-originated close,
client resets, module disposal and healthy subsequent connections. The native
simultaneous-close fixture sweeps timing across 100 connections; managed-mode
cases retain compatibility coverage. Passing stress tests reduce the risk of a
race regression but do not prove every possible schedule has been exercised.

**Recheck once .NET 11 is released:** verify the final Windows runtime contains
Microsoft's lock-order fix and run the same shutdown and compatibility regressions
against that released runtime. Check maintained .NET 10 servicing builds for any
verified backport as well. Only then consider removing or narrowing the workaround
through a reviewed PR. The current implementation does not disable itself merely
because it detects version 11 or a prerelease.
