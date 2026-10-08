# Managed WebSocket close acknowledgements

Neo issue #184 investigates intermittent macOS ARM64 .NET 10.0.12 client disposal failures during server-initiated WebSocket close handshakes. The observed client stack enters `HttpContentStream.Dispose`, `ManagedWebSocket.DisposeCore` and `WaitForServerToCloseConnectionAsync`. A passing rerun alone does not establish a repair.

## Demonstrated server defect

The managed receiver previously read frames only while its state was `Open`. Local closing changes that state to `CloseSent`, so the receive loop could exit after an unrelated frame, or miss the close acknowledgement that follows it. The closing operation then waited for its full timeout rather than observing the peer's valid acknowledgement. Some receive-loop exits also failed to signal completion.

The receiver now remains active in `CloseSent`, reads the close acknowledgement without dispatching new application data, and signals completion on every exit. An asynchronous completion task replaces the disposable receive wait handle. The close operation retains its existing timeout and caller cancellation, preserves the outgoing status/reason and releases transport resources on completion. Silent peers and blocked application callbacks still require the existing cancellation/lifetime policy. Public APIs, default close limits, listener selection and dependencies are unchanged.

## Evidence and limits

Six real raw-TCP cases cover closes initiated by connection callbacks, message callbacks and external callers, both with and without intervening text/binary frames. Current-main baseline failed all six because transport EOF did not arrive within 500 milliseconds of acknowledgement; the corrected implementation passes. Each case repeats ten times and checks the exact close payload, a single disconnect, no new application message delivery during closing and a healthy subsequent HTTP request. Two additional real `ClientWebSocket` cases run 64 sequential connection- and message-initiated closes each, preserving the original receive/acknowledge/dispose sequence and rejecting any client exception. Existing cancellation, simultaneous-close and disposal regressions remain required.

This proves the server acknowledgement defect and its correction. It does not independently prove the exact internal .NET client disposal race or guarantee a runtime defect cannot recur. Hosted Windows/Linux/macOS validation is required before merge; the original failure artifacts remain retained. No runtime private fields are patched, no null reference is swallowed and no test assertion or runtime timeout is weakened.

The Windows native-listener workaround and its released-.NET-11 recheck are separate; see [Windows native WebSocket shutdown](windows-native-websocket-shutdown.md).

## Independent runtime probe

`test/EmbedIO.RuntimeCloseProbe` contains a self-contained .NET console reproduction with no project or package reference to EmbedIO. Its peer implements only a loopback TCP upgrade and the exact close/acknowledgement frames. Each client awaits connect, receive and close output in sequence, then disposes; application calls never overlap. This follows the [documented ClientWebSocket concurrency contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-10.0#remarks).

The control closes TCP immediately after acknowledgement. The second scenario delays TCP closure by 980–1020ms, around the [runtime's one-second server-close wait](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.WebSockets/src/System/Net/WebSockets/ManagedWebSocket.cs). Reports retain client operation stages, peer delays and actual wait durations, complete exception stacks, runtime, architecture and OS. Every recorded error makes the process fail; capturing a null reference does not accept or hide it. Process/resource corruption exceptions propagate.

With SDK 10.0.401 and runtime 10.0.12 installed, run from the repository root:

```sh
dotnet restore test/EmbedIO.RuntimeCloseProbe/EmbedIO.RuntimeCloseProbe.csproj --locked-mode
dotnet build test/EmbedIO.RuntimeCloseProbe/EmbedIO.RuntimeCloseProbe.csproj -c Release --no-restore
dotnet test/EmbedIO.RuntimeCloseProbe/bin/Release/net10.0/EmbedIO.RuntimeCloseProbe.dll 1024 16 TestResults/runtime-close-probe/result.json
```

The arguments select connections per scenario, concurrent independent clients and the report path. Both scenarios run, yielding 2,048 close cycles. Each scenario has a three-minute cancellation budget. The test-only project is not packable and pins runtime 10.0.12 without roll-forward. It builds with the solution so SDK analyzers and CodeQL include its source. CI runs it on Windows, Linux and macOS with two logical processors; every job is required by `CI passed`.

A successful probe establishes only that this bounded experiment did not reproduce the internal client failure. It does not prove the runtime's disposal implementation is free of races or that every peer/application sequence is equivalent.
