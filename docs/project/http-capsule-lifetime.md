# Capsule and tunnel lifetime validation

This records independent lifetime and resource testing of the unreleased
`IHttpTunnelContext`, `HttpTunnel` and `HttpCapsuleChannel` APIs described in the
[capsule and tunnel guide](../guides/capsule-tunnels.md). It belongs to
[program #181](http-engine.md) and depends on the capsule/tunnel draft (PR #224).
It adds tests and documentation only; no production behavior changed.

## What the tests exercise

Every case runs the managed listener on a real socket and drives it with a peer
that is independent of EmbedIO's own codecs:

- HTTP/1: raw `TcpClient`, plain and TLS 1.3, ordinary CONNECT and capsule Upgrade
  (`HttpTunnelLifetimeTest`).
- HTTP/2 cleartext: the .NET `HttpClient` sending extended CONNECT with a custom
  `:protocol` (`HttpTunnelLifetimeHttp2Test`), and a hand-written frame and HPACK
  literal peer where the exact closing frame matters (`HttpTunnelPeerAbortTest`).
- HTTP/3: a raw `QuicConnection` peer with literal QPACK fields (the `Tunnel*`
  cases added to `Http3ListenerTest`). Hosts without QUIC report these as ignored.

Each case observes both sides: the outcome of the application's pending operation,
the number of context close callbacks, what the peer receives, and a sibling
request on the same listener. On HTTP/2 and HTTP/3 the sibling shares the tunnel's
connection, so a passing sibling shows the failure stayed on one stream.

| Concern | HTTP/1 | HTTP/2 | HTTP/3 |
| --- | --- | --- | --- |
| Peer abort during a pending read | Reset: `IOException` | Explicit RST_STREAM: `OperationCanceledException` (stream), `IOException` (capsules) | RESET_STREAM: `QuicException` |
| Peer graceful end vs abort | FIN reads as end; TCP reset raises `IOException` | END_STREAM reads as end; RST_STREAM never does | FIN reads as end; RESET_STREAM never does |
| Peer abort during a backpressured write | Write fails, output bounded | Write fails; stall at 48 KiB (stream window) | Write fails; stall at about 496 KiB |
| Application cancels a pending read | Output completion and close still work | Same | Not separately tested |
| Handler throws with a read in flight | Read released, connection closed | Read released, peer sees RST_STREAM INTERNAL_ERROR (2) | Not separately tested |
| Incomplete outgoing capsule | Connection ends after the partial capsule | Peer sees RST_STREAM PROTOCOL_ERROR (1) | Peer sees H3_MESSAGE_ERROR (0x10e) |
| Concurrent CompleteOutput/Close/Dispose | One shared task each; stream closed once | Not separately tested | Not separately tested |
| Server stop with a pending operation | Backpressured write released; RunAsync finishes | Pending read released | Pending read released (raw and capsule) |
| 1 MiB peer input around server send completion | SHA-256 match, plain and TLS, raw and capsule | SHA-256 match, raw and capsule | SHA-256 match, raw and capsule |
| Retained objects after repeated tunnels | 64 tunnels; tunnel and stream collectable | 64 tunnels on one connection; collectable | Not separately tested |
| Sibling isolation while a writer is parked | Not applicable | Four 256 KiB sibling responses complete | Not separately tested |

In every case the close callback ran exactly once, and a later request succeeded.
The capsule input cases interleave empty and unknown-type capsules, which the
application skips, so the hash covers only the bytes the application actually read.

## Findings for coordination

These were reproduced but are not changed here. They are reported to the owner of
the production code.

1. **No forced abort while a write is parked.** If one task's write waits for peer
   flow-control credit, `CloseAsync`, `Dispose` and `DisposeAsync` all stay pending.
   The context close callback does not run and the stream's resources stay held.
   Only a peer read, a peer reset or a server stop releases them. After that,
   everything settles once. This was the same on HTTP/1 and HTTP/2; HTTP/3 was not
   probed. The guide already says to await pending writes before completing output,
   and the supported sequence (cancel the writer's token, then close) settles in
   under a second. The gap is that an application that loses track of a write has no
   way to tear the tunnel down until the peer acts.
2. **HTTP/1 close outcome after transport failure.** With a parked write, a peer
   reset made the write fail with `IOException` while `CloseAsync` reported success.
   On server stop, `CloseAsync` failed with `ObjectDisposedException` rather than a
   cancellation. Neither leaks anything; both make the close task a weak signal of
   whether output was delivered.
3. **Client library disposal is not a reset.** Disposing an `HttpClient` extended
   CONNECT response stream surfaced to the application as a clean end of input,
   while an explicit RST_STREAM from the raw peer surfaced as an error. The listener
   behaves correctly. The `HttpClient` cases are therefore named for disposal, and the
   reset guarantee is pinned by the raw-frame peer.

## Limits

The runs used Windows 11 (build 26300) with .NET 10 runtimes 10.0.11 and 10.0.12
installed; HTTP/3 used the Windows MsQuic stack. Linux and macOS, the netstandard2.0
asset and HTTP/2 over TLS were not run for these cases.

The HTTP/1 TLS peer negotiates the platform default, as the existing context tests
do: TLS 1.3 on Windows and Linux, TLS 1.2 against the macOS server. TLS 1.2
close_notify ends both directions, so under TLS 1.2 the bulk-input case sends all
peer input before the server completes output. That branch was exercised on Windows
by temporarily forcing a TLS 1.2 client; all six TLS cases passed. It has not run on
macOS itself.

Retention checks use weak references after forced collections. They show that
closed tunnel and stream objects are not kept reachable. They are not a measure of
socket, native QUIC or pooled buffer usage.

HTTP/1 cannot signal a stream-level error. An incomplete capsule or application
failure ends the whole connection, and the peer's own framing must detect the
truncation.
