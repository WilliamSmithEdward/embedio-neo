# Stop, cancel, drain and dispose a server

[Neo #43](https://github.com/WilliamSmithEdward/embedio-neo/issues/43) evaluates
the roadmap item from [upstream #546](https://github.com/unosquare/embedio/issues/546):
a general-purpose async service base class with explicit stop and disposal, with
`WebServerBase` derived from it. This guide records the lifetime contract the
Neo v2 engine enforces, the gaps found while measuring it, and the decision not
to replace the inheritance contract.

## The four ways a server ends

| Operation | Accept loop | In-flight requests | `RunAsync` result | Caller's token |
| --- | --- | --- | --- | --- |
| Cancel the token passed to `RunAsync` | Stops at once | Context tokens cancel; connections close | Completes normally | Canceled by the caller |
| `DrainAsync(timeout)` | Refuses new connections | Complete, or are terminated at the deadline | Completes normally after the drain | Untouched |
| `Dispose()` | Stops at once | Connections abort; context tokens cancel | Completes normally | Untouched |
| `Listener.Stop()` | Stops at once | Connections close; context tokens cancel when `RunAsync` completes | Completes normally | Untouched |

`RunAsync` does not wait for in-flight handlers in the cancel, dispose and stop
cases. Use `DrainAsync` when responses must finish before the process exits.
Startup failures, such as a port already in use, and fatal listener cleanup still
fault `RunAsync` with the underlying error; the state is `Stopped` either way.

## The server-owned lifetime token

The token a module receives in `OnStart`, the token a session manager receives in
`Start`, and `IHttpContext.CancellationToken` are one server-owned token. It is
linked to the token passed to `RunAsync` and is also canceled when the server is
disposed and when `RunAsync` completes for any reason. The caller's token is
never canceled by the server.

Previously those tokens were the caller's token itself. Disposing a server
without canceling that token left the session purge loop running, left modules
that started background work unaware the server had ended, and left an HTTP/1.1
handler awaiting its context token running after its connection was aborted.
Multiplexed HTTP/2 and HTTP/3 contexts already observed their stream ending.
This correction lives on the Neo v2 engine development branch and is unreleased;
published 1.0.3 retains the earlier behavior.

Applications that run work past the end of a request should still link their own
token when that work must outlive the server, for example a final flush to a
database. A handler that only awaits `context.CancellationToken` now stops when
the server stops.

## Repeat calls and restart

- `RunAsync` twice on a running server throws `InvalidOperationException`:
  the server has already been started.
- `RunAsync` after the server stopped throws `InvalidOperationException` saying
  the server cannot be restarted. A stopped server is final: its listener, modules
  and lifetime token are spent. Create a new instance; the port is released.
- `RunAsync` on a disposed server throws `ObjectDisposedException` before
  binding anything.
- `RunAsync` with an already canceled token completes with state `Stopped` and
  never binds the port.
- `Dispose` is idempotent. Repeated, concurrent or late disposal is a no-op after
  the first call, and disposal racing with cancellation, drain and `Listener.Stop`
  still ends `RunAsync` normally with state `Stopped`.
- `Listener.Stop()` while running ends `RunAsync` normally. Previously the run
  task faulted with `HttpListenerException` 995 even though the stop was
  requested by the application.

## Resource ownership

| Resource | Owner | Released when |
| --- | --- | --- |
| `Listener` | The server | `Dispose`, fatal cleanup, or the end of `RunAsync` |
| Modules | The server | `Dispose` |
| Session manager | The application | Its purge loop ends with the lifetime token; the manager itself is not disposed |
| In-flight handlers | The application | Canceled through the context token; awaited only by `DrainAsync` |
| The token passed to `RunAsync` | The caller | Never canceled by the server |

State changes are raised once each in order: `Created` to `Loading`, `Loading` to
`Listening`, `Listening` to `Stopped`. A startup failure raises `Loading` to
`Stopped`.

## Measured scenarios

`test/EmbedIO.Tests/Issues/Issue43_ServerLifetime.cs` covers the acceptance
criteria of #43 against the real TCP engine. Four cases fail on the unchanged
engine: disposal did not cancel an in-flight HTTP/1.1 handler, disposal did not
cancel the module and session tokens, `Listener.Stop()` faulted `RunAsync`, and
restart reported the wrong reason.

| Scenario | Result |
| --- | --- |
| State changes for cancel, dispose and drain | Three transitions, once each, in order |
| Repeated `RunAsync`, restart after stop, replacement instance on the same port | Both calls rejected with a specific message; replacement serves |
| Dispose twice while cancel, drain and `Listener.Stop` race | No exception, `RunAsync` completes, state `Stopped` |
| Dispose before `RunAsync` | `ObjectDisposedException`, port never bound |
| Pre-canceled token | State `Stopped`, port never bound |
| Module and session tokens after dispose | Both canceled; the caller's token is not |
| In-flight handler after dispose (HTTP/1.1 and HTTP/2) | Handler observes cancellation; client request fails |
| In-flight handler after token cancellation | Handler observes cancellation before `RunAsync` completes |
| In-flight handler after `Listener.Stop()` | `RunAsync` completes normally; the handler is canceled afterwards |
| Drain with one in-flight request | New connection refused, in-flight response delivered, drain and run complete |
| `TestWebServer` started and disposed 100 times | Disposal racing run completion never throws; the lifetime source is released only after both have canceled it |

Independent servers and shutdown during concurrent traffic remain covered by the
existing Neo #595 fixtures. MAUI platform validation is paused in CI, so no app
model beyond the desktop runtimes is exercised here.

## Decision

No `StopAsync`, `DisposeAsync` or service base class is added. The existing
`RunAsync` task, the caller's token, `DrainAsync` and `Dispose` compose into the
four shutdown shapes above, and the server-owned lifetime token closes the
ownership gaps that were found. Deriving `WebServerBase` from a new service class
would change the public inheritance contract for every existing subclass; an
additive `DisposeAsync` that awaits the run task is a possible follow-up and
needs its own approval and release notes.
