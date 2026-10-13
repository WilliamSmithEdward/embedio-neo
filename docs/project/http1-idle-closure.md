# HTTP/1 keep-alive idle closure investigation

[Issue #281](https://github.com/WilliamSmithEdward/embedio-neo/issues/281)
reviews the timeout reset reported by the historical #276 campaign. This work
is based on PR #182 head `b3e55b5`. It adds observations and a proposed design;
it changes no production closure path, timeout, public API or runtime dependency.
Those historical observations did not change closure behavior. William has since approved the scoped policy; its implementation and remaining validation are described below.

## Behavior observed before the approved change

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

## Historical proposal before approval

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

The original decision was whether changing reset to orderly EOF for that idle
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

## Approved orderly-idle candidate

William approved the scoped policy in issue #281 on 2026-10-12. The candidate
starts from accepted PR #182 base `6b943c77`; it is unreleased. The preceding
sections retain historical observations and the design discussion, rather than
claiming current-source or cross-platform acceptance.

Completed and fully consumed HTTP/1 exchanges waiting for a successor can
attempt orderly send shutdown when the existing timeout wins admission
arbitration. Ready/decrypted, buffered or partial successor input conservatively
retains abort behavior. Head processing and stopping its deadline share the
retirement gate. A request that loses that arbitration is not dispatched or
retried invisibly. Socket readiness is checked too; bytes can still arrive after
that decision, so an orderly wire outcome is not guaranteed for a racing peer.

Plain TCP uses send shutdown before disposal. TLS calls public SslStream shutdown;
TCP send shutdown follows only actual successful TLS shutdown completion. If the
TLS write is pending, the socket and stream are disposed immediately, without
adding a grace period. That write remains lifetime-owned and registered for
listener drain until its actual terminal state is observed. Expected interrupted
write errors are diagnosed as fallback aborts; resource-release and unexpected
operation failures remain observable through lifetime completion. Cancellation
alone is not completion, and the server does not wait for a peer closure alert.

The .NET Standard asset uses the existing public-method capability detection.
Older runtimes without that method retain their existing TLS closure behavior.
There is no private runtime shim or new dependency. The fifteen-second default,
configured deadlines, public APIs and partial-request/response aborts remain
unchanged. Clients should handle terminal idle closure and open a new connection
as needed, rather than depending specifically on a reset exception. See the
[Neo v1-to-v2 migration guide](../compatibility/neo-v1-to-v2.md#http1-idle-closure).

The first candidate Windows/.NET 10 run passed 29 focused cases, including both
actual fifteen-second idle cases, four deterministic admission/expiry cases and
four pending-write ownership cases. Both TLS 1.2 timeout and explicit shutdown
controls emitted a complete 31-byte alert record; its encrypted description was
not decoded. This is not packet-level FIN/ACK proof or older-runtime acceptance.
Additional actual blocked public TLS-write interruption controls, an unchanged
current-source baseline comparison and full platform gates remain required.
### Qualified Windows asset controls

A forced, warning-free rebuild on accepted base `685f6a05` passed all 33 focused
cases for each core asset on Windows 10.0.26300 / .NET 10.0.12. Actual loaded
framework attributes and core hashes matched the compiled and staged files;
the modern runner was restored and hash-verified afterward. The same actual
public SslStream blocked-write controls passed for IOException and cancellation,
with terminal write observation preceding listener detach. The controlled
critical exception-object case retained exact fault identity, physical socket
closure and a faulted lifetime completion; it creates no real memory pressure.

The accepted production baseline `02283f13` reproduced the three targeted
idle EOF/TLS-alert assertion failures, with its other eleven controls passing.
An intermediate run labelled candidate reused that baseline DLL after
source restoration preserved timestamps. Its real 13 failures are retained
as invalid candidate qualification, not accepted or labelled a production
regression. Forced rebuilding, compiled/staged identity guards and actual
runtime identity checks correct that validation procedure. The earlier failed
TLS fixture's pending-task terminal state was not observed; its corrected
fixture resolves prerequisites before starting a write and observes the task
in finally, including on assertion failure.

[Compact source-qualified evidence](evidence/http1-idle-closure-281.json)
retains source hashes, loaded identities, actual case outcomes and unsuccessful
qualification. Full desktop/MAUI/security gates and older-runtime TLS fallback
validation remain pending. These modern-host runs are not older-runtime proof.