# Managed WebSocket close acknowledgements

Neo issue #184 investigates intermittent macOS ARM64 .NET 10.0.12 client disposal failures during server-initiated WebSocket close handshakes. The observed client stack enters `HttpContentStream.Dispose`, `ManagedWebSocket.DisposeCore` and `WaitForServerToCloseConnectionAsync`. A passing rerun alone does not establish a repair.

## Demonstrated server defect

The managed receiver previously read frames only while its state was `Open`. Local closing changes that state to `CloseSent`, so the receive loop could exit after an unrelated frame, or miss the close acknowledgement that follows it. The closing operation then waited for its full timeout rather than observing the peer's valid acknowledgement. Some receive-loop exits also failed to signal completion.

The receiver now remains active in `CloseSent`, reads the close acknowledgement without dispatching new application data, and signals completion on every exit. An asynchronous completion task replaces the disposable receive wait handle. The close operation retains its existing timeout and caller cancellation, preserves the outgoing status/reason and releases transport resources on completion. Silent peers and blocked application callbacks still require the existing cancellation/lifetime policy. Public APIs, default close limits, listener selection and dependencies are unchanged.

## Evidence and limits

Six real raw-TCP cases cover closes initiated by connection callbacks, message callbacks and external callers, both with and without intervening text/binary frames. Current-main baseline failed all six because transport EOF did not arrive within 500 milliseconds of acknowledgement; the corrected implementation passes. Each case repeats ten times and checks the exact close payload, a single disconnect, no new application message delivery during closing and a healthy subsequent HTTP request. Two additional real `ClientWebSocket` cases run 64 sequential connection- and message-initiated closes each, preserving the original receive/acknowledge/dispose sequence and rejecting any client exception. Existing cancellation, simultaneous-close and disposal regressions remain required.

This proves the server acknowledgement defect and its correction. It does not independently prove the exact internal .NET client disposal race or guarantee a runtime defect cannot recur. Hosted Windows/Linux/macOS validation is required before merge; the original failure artifacts remain retained. No runtime private fields are patched, no null reference is swallowed and no test assertion or runtime timeout is weakened.

The Windows native-listener workaround and its released-.NET-11 recheck are separate; see [Windows native WebSocket shutdown](windows-native-websocket-shutdown.md).
