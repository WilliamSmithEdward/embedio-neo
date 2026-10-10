# Capsule and tunnel carriers

These APIs are unreleased development work for the new managed engine. They are
not in the published package. Current evidence and remaining platform gates are
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
send direction while keeping peer input readable. Keep the handler running while
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
without it rejects TLS handoff before sending the success head. The current
half-close tests use TLS 1.3; older TLS/runtime behavior is not implied.

Native unreliable QUIC DATAGRAM delivery, browser WebTransport sessions and
extension-specific capsule value validation are separate capabilities. This API
provides reliable framing and carrier lifetime, not those protocol implementations.