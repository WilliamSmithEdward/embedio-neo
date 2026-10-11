# HTTP/1 keep-alive idle closure investigation

[Issue #281](https://github.com/WilliamSmithEdward/embedio-neo/issues/281)
reviews the timeout reset reported by the historical #276 campaign. This work
is based on PR #182 head `b3e55b5`. It adds observations and a proposed design;
it changes no production closure path, timeout, public API or runtime dependency.
The issue remains open for the client-visible policy decision and its implementation.

## Current behavior

After a reusable response completes and its request input is consumed, the
HTTP/1 actor resets the context and arms the existing fifteen-second timer.
That timer covers both waiting for the next request and reading a partial head.
It is stopped when a complete request is admitted. It is not an application
handler, request-body or response-output deadline.

`OnTimeout` calls `CloseSocket`, which calls `CloseTransport(false)`.
The connection detaches and disposes its socket without calling socket shutdown.
Transport wrappers, timer, parser buffer and listener ownership are then released.
Explicit abort uses `CloseTransport(true)` and attempts `Socket.Shutdown(Both)`.
Neither path calls `SslStream.ShutdownAsync` before disposing the TLS transport.
Thus an EOF seen through a TLS client alone does not prove authenticated TLS closure.

## Windows observations

The fourteen current-source cases pass on Windows 10.0.26300 / .NET 10.0.12.
The complete default idle cases actually wait fifteen seconds; only partial-head,
reuse-race and raw TLS comparison controls shorten the private timer in the fixture.
The independent public-API probe identifies the loaded core assembly by SHA-256
and runs unchanged against released 1.0.3 and an exact main snapshot.

Released 1.0.3 (modern core SHA-256
`772CED5FA1396C452BFBA627E04A5D420BF68F17404BABE71B92916856238D55`)
and main `1445c238afea7d5a237088e8f19c7a4e84eb43f1` both report
ConnectionReset on plain/TLS peers at approximately fifteen seconds. This
observation therefore predates the current replacement, rather than establishing
a new replacement regression.

Both current target assemblies also report the same plain/TLS reset under the
installed .NET 10 host; this does not prove older-runtime compatibility. The full
Windows suite passes 4,639 cases: 4,630 passed, nine expected skips, zero failures.
Both targets build without warnings, and source/format guards pass. The discovery
floor increases from 4,625 to 4,639 for the fourteen added cases.

| Observation | Plain TCP | TLS |
| --- | --- | --- |
| Complete 204 response, then default idle expiry | SocketError.ConnectionReset | IOException wrapping ConnectionReset |
| Partial successor head, then accelerated header expiry | ConnectionReset | ConnectionReset |
| Explicit abort with an incomplete/unread request body | EOF, no response bytes | EOF, no response bytes |
| Explicit abort after one of 100 declared response bytes | EOF, no synthesized completion | EOF, no synthesized completion |
| Peer reset after a completed response | Server cleanup completes | Server cleanup completes |
| Request write racing accelerated expiry | Complete successor or terminal closure permitted | Complete successor or terminal closure permitted |

These are observations on one runtime/OS, not promised cross-platform wire outcomes.
The assertions allow EOF or IOException as terminal input, but require bounded
cleanup, no manufactured successful response on abort, disposed timer/wrapper,
released parser buffer, and a healthy fresh client on the same listener. A racing
request that wins admission receives an explicit final 204; otherwise it loses
the connection. The fixture does not retry the racing request invisibly.

For TLS 1.2, a separate peer reads the underlying network stream after consuming
the complete response through its authenticated SslStream. Timeout yields zero
trailing record bytes and a reset on this host. The test-only comparison invokes
the existing tunnel send-shutdown primitive and observes a complete 31-byte TLS
record with content type 21 (alert), followed by EOF. The encrypted alert's
description is not decoded by this probe. TLS 1.3 hides the content type, so that
record comparison deliberately negotiates TLS 1.2; the ordinary TLS cases retain
runtime-default negotiation. This is not a packet capture or a TLS conformance claim.

The standalone probe avoids dependence on branch-only private cleanup fields.
It explicitly accesses the empty response writer before closing, because released
1.0.3 does not emit this empty head on context close alone. Its logs distinguish
complete response receipt from terminal idle-read failure. Earlier fixture-only
failures (timer-disposal assertion and incompatible whole-suite discovery during
assembly substitution) are retained under ignored TestResults.

## Proposed policy for discussion

[RFC 9112 sections 9.6 and 9.8](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.6)
describe staged TCP teardown to avoid losing the final response through a reset,
and TLS closure alerts before non-error shutdown.
The existing public [SslStream.ShutdownAsync API](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.shutdownasync?view=net-10.0)
can participate in TLS send shutdown, as the control demonstrates. A single
unconditional `Socket.Shutdown(Both)` in the timer is insufficient as a complete
TLS policy or as proof of safe reuse.

The proposed scope is orderly send shutdown only for a completed, fully consumed
HTTP/1 exchange waiting for a successor with no successor bytes admitted. A
partial head, unread body, interrupted response or peer failure remains an abort.
The actor must arbitrate admission and timeout under the same lifetime gate,
commit terminal ownership once and never dispatch a successor after closure wins.
Any TLS send-shutdown operation must finish or be aborted within the existing
cleanup budget. It must not wait indefinitely for a peer's closure alert, add a
new grace period, or leave a detached socket outside tracked cleanup.

Before implementation, agree whether changing reset to orderly EOF for that idle
state is desired, including the policy for the .NET Standard asset on runtimes
without a usable public TLS shutdown method. The current fifteen-second deadline
and advertised keep-alive timeout remain unchanged. Implementation then needs
deterministic tests for both admission/timeout winners, cancellation during TLS
shutdown, unread kernel input, repeated disposal and healthy subsequent peers.
The present scheduling race is an observation, not proof of both interleavings.

## Repeating the evidence

Build the solution with locked restore, then run:

```sh
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter FullyQualifiedName~Http1IdleClosureTest --minimum-expected-tests 14 --report-trx --results-directory TestResults/http1-closure --timeout 2m
dotnet restore test/EmbedIO.Http1ClosureProbe/EmbedIO.Http1ClosureProbe.csproj --locked-mode
dotnet run --project test/EmbedIO.Http1ClosureProbe/EmbedIO.Http1ClosureProbe.csproj -c Release --no-restore -p:CorePath=/absolute/path/to/EmbedIO.dll
```

The probe is outside the ordinary solution and all production packages. Its NUnit
dependency is test-only and locked. `http1-closure.yml` runs the fourteen cases and
the public probe on Windows 2025, Ubuntu 24.04 and macOS 15, comparing current
source (both target assemblies hosted by .NET 10), hash-pinned released 1.0.3 and the recorded main commit fetched during the
run. Each artifact records runtime identity, assembly hashes, peer observations
and TRX results. Configuring those jobs is not evidence that they passed.
Older-runtime/.NET Native application models and packet-level FIN/ACK ordering
remain outside this investigation's evidence.
