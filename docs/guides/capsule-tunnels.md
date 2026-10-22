# Capsule and tunnel carriers

These APIs are new in Neo v2 and remain development work for the managed
engine. They are not in the published 1.0.x packages. Current evidence and remaining platform gates are
in [capsule transport development](../project/http-capsule-transport.md).

Use the optional `IHttpTunnelContext` capability when implementing an HTTP
extension that defines a reliable capsule carrier, or a generic stream tunnel.
Existing context interfaces do not acquire new required members. The Microsoft
backend does not expose this capability. This API negotiates a carrier; it does
not connect to a destination, forward UDP/TCP, or implement WebTransport.

A handler chooses the offered extension and authorizes the request before calling
`AcceptTunnelAsync`. For HTTP/1, use an offered Upgrade protocol, or null for
ordinary authority-form CONNECT. For HTTP/2/3, select the requested extended
CONNECT protocol, or null for ordinary CONNECT. WebSockets continue to use their
existing acceptance API and module.

HTTP/2 and HTTP/3 extended CONNECT require an explicit handoff before successful
final headers. Ordinary 2xx output without AcceptTunnelAsync becomes an empty
501 response. Explicit errors and application dispatch are preserved. Direct
Request/Response custom extension implementations must migrate to HttpTunnel;
see [the v2 migration steps](../compatibility/neo-v1-to-v2.md#explicit-extended-connect-acceptance).

## Endpoint example

This complete .NET 10 program defines a local test extension named
`example-tunnel`: type-0 payloads up to 16 KiB are echoed, and unknown or larger
capsules are discarded incrementally. That limit is this example's policy, not
a library default. It is not an IANA registration or a general-purpose proxy.
The endpoint requires a peer implementing the same extension; ordinary browser
fetch requests do not establish it.

```csharp
using EmbedIO;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
using var server = new WebServer(HttpListenerMode.EmbedIO, "http://127.0.0.1:9696/")
    .WithAction("/carrier", HttpVerbs.Any, async context =>
    {
        if (context is not IHttpTunnelContext capability)
            throw new HttpException(System.Net.HttpStatusCode.NotImplemented, "Tunnel capability unavailable.");

        // Add application authorization here before negotiating the carrier.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var tunnel = await capability.AcceptTunnelAsync("example-tunnel", true, deadline.Token);
        var channel = tunnel.Capsules ?? throw new InvalidOperationException("Missing capsule channel.");
        var scratch = new byte[4096];
        while (await channel.ReadHeaderAsync(deadline.Token) is { } header)
        {
            if (header.Type != 0 || header.Length > 16384)
            {
                await channel.SkipPayloadAsync(deadline.Token);
                continue;
            }
            await channel.WriteHeaderAsync(0, header.Length, deadline.Token);
            var remaining = header.Length;
            while (remaining != 0)
            {
                var count = await channel.ReadPayloadAsync(scratch, 0, (int)Math.Min(remaining, scratch.Length), deadline.Token);
                await channel.WritePayloadAsync(scratch, 0, count, deadline.Token);
                remaining -= count;
            }
        }
        await tunnel.CompleteOutputAsync(deadline.Token);
        // The context joins stream closure when the handler returns.
    });
await server.RunAsync(stop.Token);
```

This example can accept HTTP/1 Upgrade and cleartext HTTP/2 extended CONNECT on
the managed listener. The same handler can run on an appropriately configured
[HTTP/3 listener](http3.md) with a certificate and a capable QUIC runtime. Current
independent carrier checks cover H2 cleartext/TLS and H3 in the pinned Linux
container; broader platform and lifecycle checks remain pending.

## Ownership and completion

`HttpCapsuleChannel` borrows an already negotiated stream. It owns neither that
stream nor its closure. One operation per direction may run at once; reading and
writing can run concurrently. Consume or skip the current payload before reading
another header, and finish a payload before writing another header. `CompleteOutput`
checks capsule framing and forbids later writes; it does not send transport FIN.

`HttpTunnel` owns its accepted duplex stream. `CompleteOutputAsync` finishes the
send direction subject to the negotiated transport closure semantics. Plain TCP, HTTP/2, HTTP/3 and TLS 1.3 allow remaining peer input. TLS 1.2 peers must finish sending before TLS close_notify. Keep the handler running while
reading that remaining input. Its first caller's cancellation token controls the
shared completion task; later calls observe the same success, cancellation or
failure. Await pending capsule writes before completing output.

Await `CloseAsync` on either target, or asynchronous disposal on .NET 10, to
observe completion and cleanup errors. Synchronous disposal starts the same
closure without blocking a worker. Do not close `Stream` independently while
operations still use it. An application may construct `HttpCapsuleChannel` over
another borrowed carrier or construct `HttpTunnel` for a custom backend, taking
responsibility for negotiation and the send-completion callback.

Declared capsule lengths do not allocate their value size. Unknown values are
skipped through bounded scratch storage so flow-control credit advances. Truncated
headers/values and unfinished output are failures. On the negotiated managed
carrier, malformed capsules abort the selected H2/H3 stream; an HTTP/1 failure
closes its connection. This does not make other client requests fail globally.

## Carrier and runtime limits

Capsule carriers cannot contain Content-Length, Content-Type or Transfer-Encoding.
Do not configure these response fields or use content serializers on an accepted
carrier. Capsule semantics come from the selected extension and explicit
`useCapsules` option; a header alone does not turn ordinary requests into capsules.
The response emits Capsule-Protocol with a true Boolean Item for intermediaries,
consistent with [RFC 9297](https://www.rfc-editor.org/rfc/rfc9297.html#section-3.4).

HTTP/1 handoff preserves bytes read with the request head and requires the application to consume any HTTP request body before switching. Inspect and authorize `Request.RawTarget` for
ordinary CONNECT destinations. .NET 10 uses public TLS send shutdown; the
netstandard asset binds the same public runtime API when available. A runtime
without it rejects TLS handoff before sending the success head. TLS 1.2 closure is defined by [RFC 5246 section 7.2.1](https://www.rfc-editor.org/rfc/rfc5246.html#section-7.2.1); TLS 1.3 supports independent closure under [RFC 8446 section 6.1](https://www.rfc-editor.org/rfc/rfc8446.html#section-6.1). macOS server-side SslStream currently negotiates TLS 1.2; the .NET 10 Network.framework TLS 1.3 option applies to clients only. See [the runtime explanation](https://devblogs.microsoft.com/dotnet/dotnet-10-networking-improvements/).

## HTTP datagrams

In unmerged PR #506, HTTP/3 runs on EmbedIO.Quic and an accepted tunnel exposes
`HttpTunnel.Datagrams` (RFC 9297) when both peers negotiate HTTP datagrams. The
released v2.16.0 provider returns null. Final platform acceptance remains
development work.

On a supporting backend, `HttpDatagramChannel.ReceiveAsync` returns an owned
`HttpDatagram`, or null when incoming delivery ends. An empty payload is a valid
packet. Dispose each received packet after using its `Payload`; do not retain
the memory after disposal. Canceling a receive does not discard unread packets.

`TrySend` copies the borrowed payload before returning. `Queued` means submitted,
not delivered or acknowledged: datagrams can be lost, reordered or duplicated.
Other results distinguish absent negotiation, an excessive payload, a full local
send queue and closed output. There is no automatic retry or reliable fallback.
The selected extension must define whether and how reliable DATAGRAM capsules
are used when QUIC datagrams are unavailable.

For an already accepted `HttpTunnel tunnel`, this echo loop uses the public
channel and returns each packet owner promptly. Its unreliable policy drops
packets that cannot currently be submitted; it does not retry or translate them
into reliable capsules:

```csharp
HttpDatagramChannel? channel = tunnel.Datagrams;
if (channel != null)
{
    while (await channel.ReceiveAsync(cancellationToken) is { } packet)
    {
        using (packet)
        {
            HttpDatagramSendStatus result = channel.TrySend(packet.Payload);
            if (result == HttpDatagramSendStatus.Closed
                || result == HttpDatagramSendStatus.Unavailable)
                break;
        }
    }
}
```

The caller still owns tunnel closure and protocol-specific authorization/control
messages. A null channel requires the extension's explicit unsupported/fallback
path; this loop alone does not implement that path.

`CanSend` reports outgoing capability. Incoming delivery can remain open after
`CompleteOutputAsync`, even when `CanSend` becomes false. `MaximumPayloadLength`
accounts for the HTTP stream identifier and can shrink with transport limits;
zero can permit an empty packet, so check `CanSend` separately. Full tunnel
closure stops new submissions and incoming delivery promptly, releases queued
packet owners and then awaits stream output completion. Already received packets
remain the application's responsibility.

Both HTTP peers and the QUIC transport must negotiate support. Per-request
queues hold at most 16 packets, with a connection-wide router bound of 128 packets
and 256 KiB in addition to the QUIC transport's own receive budget. Excess packets
are discarded without failing a healthy connection. These are bounds on this new
unreliable channel; existing WebSocket buffering defaults are unchanged.

Browser WebTransport sessions, native WebTransport streams and extension-specific
capsule value validation remain separate integration work.
