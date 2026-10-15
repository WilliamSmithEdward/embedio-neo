# Behind a reverse proxy

Behind a reverse proxy, every request arrives from the proxy: the connection's address is the
proxy's, and when the proxy terminates TLS the connection is plain HTTP. Proxies pass the
original values in headers, `Forwarded` (RFC 7239) or `X-Forwarded-For`, `X-Forwarded-Proto` and
`X-Forwarded-Host`. Any client can send those headers too, so they mean something only when the
connection comes from a proxy you trust.

`ForwardedHeadersModule` reads them from trusted proxies only, and reports the result as a
`ForwardedRequest` next to the connection's own values, which it does not change.

This is new in Neo v2; the published 1.0.x packages do not include it.

## A complete program

```csharp
using System;
using System.Threading;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Forwarding;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

// The proxy runs on this machine and sets X-Forwarded-For and X-Forwarded-Proto,
// replacing any values the client sent.
var forwarding = new ForwardedHeadersModule("/", ForwardedHeaderSources.XForwardedFor | ForwardedHeaderSources.XForwardedProto)
    .WithTrustedProxies("127.0.0.1", "::1");

using var server = new WebServer("http://localhost:9696/")
    .WithModule(forwarding)
    .WithModule(new ActionModule(context =>
    {
        var forwarded = context.GetForwardedRequest();
        var text = $"client {forwarded.ClientAddress} over {forwarded.Scheme} ({forwarded.Outcome}); "
            + $"connection from {forwarded.PhysicalRemoteEndPoint.Address}";
        return context.SendStringAsync(text, "text/plain", WebServer.DefaultEncoding);
    }));

Console.WriteLine(new Uri("http://localhost:9696/"));
await server.RunAsync(cancellation.Token);
```

A request through the proxy, here simulated with curl from the same machine:

```sh
curl -H "X-Forwarded-For: 203.0.113.9" -H "X-Forwarded-Proto: https" http://localhost:9696/
```

answers `client 203.0.113.9 over https (Applied); connection from 127.0.0.1`, or `::1` when curl
connects over IPv6. The same headers from any other address are ignored.

## The result

`context.GetForwardedRequest()` returns:

| Property | Meaning |
| --- | --- |
| `ClientAddress` | The original client's address when hops were applied, otherwise the connection's. |
| `Scheme`, `IsSecure` | The scheme the client used, `http` or `https`. |
| `Host` | The host the client requested, only when a trusted proxy forwarded one in the allowed hosts; otherwise `null`. |
| `Outcome` | `Applied`, `UntrustedPeer`, `NoHeaders`, `Malformed`, `Conflict`, or `NotProcessed` when no module handled the request. |
| `HopsApplied`, `HostRejected` | How many proxy hops were read, and whether a forwarded host was refused. |
| `PhysicalRemoteEndPoint`, `PhysicalIsSecure` | The connection itself: the nearest proxy, and whether it used TLS. |

`context.Request.RemoteEndPoint`, `IsSecureConnection` and `Url` keep the connection's values.
Code that should see the original client reads `GetForwardedRequest()` instead:

- **Logging and auditing:** log `ClientAddress`, and `PhysicalRemoteEndPoint` for the proxy.
- **Rate limiting:** key on the original client with
  `RateLimitingModule.ClientKey(context.GetForwardedRequest().ClientAddress)`; see
  [Rate limiting](rate-limiting.md). Put the forwarded-headers module first.
- **Redirects and absolute URLs:** build them from `Scheme` and `Host`, and fall back to a
  configured public URL when `Host` is `null`. Never build them from the request's own `Host`
  header, which the client chose.
- **Cookies:** `LocalSessionManager.CookieSecure` and similar settings are configuration, not
  per request; set them for the scheme your proxy serves.

## Trust

- `WithTrustedProxies` takes IPv4 or IPv6 addresses or CIDR networks, such as `10.0.0.0/8`. Host
  names are not accepted. IPv4-mapped IPv6 connections match their IPv4 entries. At least one is
  required to start.
- Headers from any other connection are ignored, and the outcome is `UntrustedPeer`.
- Select only the headers your proxy always writes or replaces. A proxy that appends to a header
  the client sent leaves the client's entries in front of its own; the module reads from the
  nearest proxy outward and stops at the first sender that is not a trusted proxy, so those
  entries are never reached.

## Hops

`ForwardLimit`, 1 by default, is the most hops to read. Set it to the number of trusted proxies
in front of the server. Each hop names who sent the request to the proxy that recorded it, and
how; the module reads the nearest proxy's record first, and moves to the next only while the
sender it names is itself a trusted proxy and the limit allows.

With `ForwardLimit = 2`, trusted `127.0.0.1` and `10.0.0.0/8`, and `X-Forwarded-For: 1.2.3.4,
203.0.113.9, 10.0.0.5`, the client is `203.0.113.9`: the nearest proxy says `10.0.0.5` sent the
request, a trusted proxy, which says `203.0.113.9` sent it. The limit stops there, so `1.2.3.4`,
which anyone could have written, is not used.

A hop that names no address, `unknown` or an obfuscated identifier such as `_hidden`, still
applies its scheme and host, but the walk stops there and the client address stays the last
known one.

## Hosts

A forwarded host is used only if it matches `WithAllowedHosts`, such as `example.com` or
`example.com:8443`; an entry without a port allows any port. A host that is not allowed is
ignored and `HostRejected` is set. Reading `X-Forwarded-Host` requires allowed hosts, so a
module configured to read it without any fails to start. The `host` parameter of `Forwarded`
follows the same rule.

## What is rejected

Nothing is applied, and the connection's values stand, when:

- `Forwarded` and `X-Forwarded-*` headers arrive together and both are selected (`Conflict`): the
  two may describe the chain differently;
- a value does not parse (`Malformed`): an address that is not a canonical IPv4 address or an IPv6
  address, an IPv6 address with a zone, an unquoted IPv6 `for` in `Forwarded`, a port outside
  1 to 65535, a scheme other than `http` or `https`, a host with anything other than a name or
  address and a port, a repeated parameter, or an unterminated quote;
- the headers list more than 32 hops, or exceed 8192 characters together (`Malformed`).

The module never rejects a request itself. Check `Outcome` where a malformed or conflicting chain
should be an error.

## Formats

- `Forwarded: for=192.0.2.60;proto=https;host=example.com, for="[2001:db8::7]:4711"`, with IPv6
  addresses and ports quoted as RFC 7239 requires. `by` and extension parameters are ignored.
- `X-Forwarded-For: 203.0.113.9, 10.0.0.5`, with optional ports and IPv6 addresses bare or in
  brackets. `X-Forwarded-Proto` and `X-Forwarded-Host` list one value per hop and are matched to
  `X-Forwarded-For` from the nearest proxy.
- Several header lines with the same name are read in order, as one list.
