# WebSocket parity

Eight Windows message cases per implementation cover text/binary and whole/fragmented
messages on both listeners. Unicode text and binary bytes 0, 1, 127, 128 and 255
round-trip with the correct message type. Each output is checked against the
sent bytes in addition to comparing implementations.

The Linux (`unix`) profile compares the four managed message cases. It does not claim a
full old native WebSocket comparison, because the upstream native DTO workload
already terminates its listener; see [Lifecycle](lifecycle.md).

The macOS profile skips these four cases for every build. On macOS the original
EmbedIO's managed WebSocket can leave the echo unanswered, and its process aborted
when the harness's receive deadline fired (run 38449281895). Skipping the cases on
both sides keeps the case lists aligned. EmbedIO.Tests covers Neo's WebSocket
behavior on macOS; see the [audit method](README.md).

The client first receives an application-owned `ready` greeting. This deliberately
avoids upstream's separately confirmed early-message-loss behavior; it does not
establish readiness-free message parity. The client aborts after receiving the
echo, avoiding the old Windows native full-handshake close deadlock. These
constraints are explicit fixture scope, not suppressed failed close tests.

[Startup-message guidance](../user-reports/websocket-startup-messages.md) and
[Windows native shutdown guidance](../user-reports/windows-native-websocket-shutdown.md)
document the focused fixes and their broader Neo regressions. Ping/pong,
simultaneous close, backpressure, concurrent writes, application callback failures,
large messages and native runtime/device differences are not compared here.
Evidence: `websocket/` cases and bounded run logs. See the [audit method](README.md).
