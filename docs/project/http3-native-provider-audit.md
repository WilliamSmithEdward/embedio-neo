# Native HTTP/3 provider ownership audit

This audit covers the internal MsQuic provider foundation on
`codex/http3-native-datagrams` (draft PR 231, program issue 181) at
`0591b27d7da9f649477985b176eac4b53724e6a3`. That branch's base is
`codex/managed-http-engine` at `57151c5898998fe09b2345079be89a3fc028099f`.
The audit reviewed native ownership, callback rooting, allocation failures,
concurrent disposal, cancellation, shutdown and queued-connection cleanup in
`MsQuicApi.cs`, `MsQuicNativeListener.cs` and `MsQuicNativeConnection.cs`.

The reference is the official MsQuic v2.6.2 documentation:
[API overview](https://github.com/microsoft/msquic/blob/v2.6.2/docs/API.md),
[ConnectionClose](https://github.com/microsoft/msquic/blob/v2.6.2/docs/api/ConnectionClose.md),
[ListenerClose](https://github.com/microsoft/msquic/blob/v2.6.2/docs/api/ListenerClose.md),
[StreamClose](https://github.com/microsoft/msquic/blob/v2.6.2/docs/api/StreamClose.md),
[QUIC_CONNECTION_EVENT](https://github.com/microsoft/msquic/blob/v2.6.2/docs/api/QUIC_CONNECTION_EVENT.md),
[Settings](https://github.com/microsoft/msquic/blob/v2.6.2/docs/Settings.md)
and the [published header](https://github.com/microsoft/msquic/blob/v2.6.2/src/inc/msquic.h).
No MsQuic implementation code was consulted or copied. This audit changes no
production code.

## Reproduced defects

`MsQuicNativeOwnershipAuditTest` contains two failing regression tests. They
fail identically in three consecutive Windows runs and in the pinned
network-isolated Linux image.

### Accept-queue overflow drops the peer silently

When the 256-entry accept queue is full, the listener callback has already
installed the connection callback. It then closes the handle and returns
success. `ConnectionClose` without a prior shutdown is documented as an abortive,
silent shutdown, so the peer receives nothing. MsQuic clients retransmit and
wait for their handshake timeout.

RFC 9000 section 5.2.2 says a server refusing a new connection SHOULD send
CONNECTION_CLOSE with CONNECTION_REFUSED. The same listener already does this
when acceptance is disabled or stopping, because it returns a failure status from
`NEW_CONNECTION` without taking ownership.

| Path | Client outcome | Time |
| --- | --- | --- |
| Acceptance not enabled, failure returned | `ConnectionRefused` | 13 ms |
| 257th connection, queue full | `ConnectionTimeout` | until the client timeout |

The development record describes the full-queue path as "required to avoid
double-free rejection". The ConnectionClose documentation limits the double-free
to closing the handle and also returning failure. Returning failure without
closing is the documented rejection.

Test: `OverflowedAcceptQueueRefusesTheConnection`.

Proposed fix: reserve a queue slot with an atomic counter before calling
`AcceptConnection`. Return the existing failure status when no slot is free.
Release the slot when `AcceptAsync` dequeues a connection, and on any failure
after reservation. The rejected handshake then never acquires managed ownership.
This fix was not run, because production code is outside this audit's scope.

### Stopping an unstarted listener leaves accepts pending

`StopAsync` on a listener with acceptance enabled but never started returns a
completed task. It does not complete the accept channel. A pending or later
`AcceptAsync` then waits until the listener is disposed. The started case
already completes pending accepts with `ChannelClosedException`, which the
existing `NativeStopReleasesAPendingConnectionAccept` test asserts.

Test: `UnstartedStopReleasesAPendingConnectionAccept`.

Proposed fix: complete the accept channel writer in the unstarted branch of
`StopAsync`. Leave `_stopping` unchanged there, because a later `StopAsync`
would otherwise wait on a stop event that never arrives.

## Findings from inspection

These are not reproduced by a test. Each is a direct reading of the source
against the documentation above.

- **Peer streams break documented obligations.** `PEER_STREAM_STARTED` calls
  `StreamClose` without `SetCallbackHandler` and without a prior abortive
  shutdown. The API overview requires the handler to be set immediately. The
  StreamClose remarks call close-before-shutdown undefined behavior.
- **The stream handle offset assumes 64-bit.** The handler reads the stream at
  fixed offset 8. The header places `Stream` first in a pointer-aligned union after
  a 4-byte enum, so the offset is the pointer size. A 32-bit process would read
  `Flags` and close an invalid handle. The listener path already uses
  `IntPtr.Size`.
- **The stream path is unreachable today.** The configuration passes no settings,
  and both peer stream counts default to 0. An experiment showed a System.Net.Quic
  client blocked in `OpenOutboundStreamAsync` until its 20-second deadline. The
  statement that peer streams are closed is therefore not yet exercised.
- **Non-recoverable exceptions escape native callbacks.** Both callbacks catch
  only exceptions that `ExceptionPolicy.IsRecoverable` accepts.
  `OutOfMemoryException` from allocation in the accept path propagates into
  MsQuic and terminates the process. Before ownership transfers, the accept path
  could catch every exception and return the failure status.
- **One accept failure faults listener stop.** A recoverable exception in the
  accept path calls `Stopped.TrySetException`. A single failed connection would
  make every later `StopAsync` throw, even after a clean stop. No
  deterministic trigger exists without fault injection.

## Hypotheses refuted by experiment

- **Disposing the registration owner first does not break acceptance.** A
  disposed `SafeHandle` whose reference count is still positive accepts
  `DangerousAddRef`. Connections are accepted and stop completes normally.
- **Queued connections drained at listener disposal do not hang their peers.**
  The listener's UDP socket closes, and the client fails in about one second
  with an unreachable-host socket error. The server still sends no QUIC refusal.
- **An accepted connection keeps its registration and API alive.** After
  listener, configuration, registration and API disposal, the registration and
  API stay unreleased until the connection closes.
- **A canceled `AcceptAsync` does not consume a later connection.**

The last three are now passing coverage tests:
`AcceptedConnectionRetainsEveryParentUntilItCloses`,
`CanceledAcceptDoesNotConsumeTheNextConnection` and
`ConnectionBeforeAcceptanceIsRefusedPromptly`.

## Validation

| Environment | Native and codec cases | Passed | Failed |
| --- | --- | --- | --- |
| Windows 11 10.0.26300, .NET 10.0.12 | 60 | 58 | 2 |
| Pinned Linux image, .NET 10.0.12, libmsquic 2.6.2, no network | 27 | 25 | 2 |

The failures are the two reproductions above. All 22 existing native lifetime
cases and all 33 datagram codec cases pass. The Linux run used the
Windows-built managed binaries, as the earlier native validation did. It was run
in image
`sha256:cad57be0903a303f62b6492f695d658256f0c728af9a9c5454c839093fb29df3`.

The five new cases raise discovery from 4724 to 4729. The CI and CONTRIBUTING
floors are unchanged here and still pass. The integrating branch should set the
floor from actual discovery.

## Limitations

- macOS was not run.
- No 32-bit process was run. The offset finding comes from the header.
- The proposed fixes were not executed.
- Finalizer-thread closure with live callbacks was reviewed but not exercised.
- Fault injection for allocation failure was not attempted.
