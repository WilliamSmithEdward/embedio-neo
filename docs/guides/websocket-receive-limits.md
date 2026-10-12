# Outstanding WebSocket deliveries (Neo v2, unreleased)

`WebSocketModule` subclasses can configure `ReceiveLimits` in their constructor,
before the module starts. The default is `null`, preserving unlimited outstanding
deliveries. This optional policy complements `MaxMessageSize`, which limits each
individual message.

For example, a module constructor can assign:

```csharp
MaxMessageSize = 64 * 1024;
ReceiveLimits = new WebSocketReceiveLimits(
    maximumOutstandingMessagesPerConnection: 8,
    maximumOutstandingPayloadBytesPerConnection: 512 * 1024,
    maximumOutstandingMessagesPerModule: 128,
    maximumOutstandingPayloadBytesPerModule: 8 * 1024 * 1024);
```

Each nullable limit is independent. Counts must be positive and byte limits must
be nonnegative; zero bytes allows empty messages only. Empty messages consume a
message count. A policy whose four limits are null has the same admission behavior
as a null policy. Configuration is locked once the module starts.

An outstanding delivery includes a partially assembled message, a complete message
waiting for initialization or dispatch, and an unfinished message callback Task.
Module limits aggregate reservations across its connections. Fragmented messages
retain their reservation while fragments arrive. A callback retains its reservation
until its returned Task finishes, even if the peer disconnects first. Return a Task
that represents all the work whose payload lifetime you want the policy to cover;
detached background work and application-retained copies are outside that lifetime.

Byte limits count logical payload reservations. They are not a total heap limit:
frame metadata, buffers, application objects and copies can use additional memory.
Releasing a reservation does not promise immediate garbage collection or pool
trimming.

When a new data message or fragment would exceed a configured outstanding limit,
the connection rejects it with WebSocket close status **1008** before storing its
payload. This is admission rejection, not TCP receive throttling. Control frames do
not consume message reservations. The existing individual-message limit is checked
first and retains close status **1009** for oversized messages. Applications should
choose limits suitable for their workload and reconnect/retry policy.

The policy preserves callback overlap and the sequential behavior of
`WebSocketMessageModule`. It adds no initialization deadline or graceful-drain
deadline. Awaiting callbacks indefinitely can keep their reservations indefinitely;
the application must provide its own appropriate cancellation and completion.

See [WebSocket initialization](../user-reports/websocket-startup-messages.md),
[callback behavior](../user-reports/websocket-message-callbacks.md), and the
[Neo v1-to-v2 migration guide](../compatibility/neo-v1-to-v2.md).
