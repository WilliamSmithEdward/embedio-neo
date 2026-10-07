# Localhost on IPv4 and IPv6

[Upstream #576](https://github.com/unosquare/embedio/issues/576), reported by
`KazWolfe`, describes empty replies when the managed listener is registered with
`localhost`, `127.0.0.1` and `[::1]` on the same port. Forcing IPv4 with curl or
using `Host: localhost` on the IPv4 connection failed; IPv6 worked.
The original environment was Windows 11 22H2, EmbedIO 3.5.2 and curl 7.83.1.

In the [follow-up investigation](https://github.com/unosquare/embedio/issues/576#issuecomment-1504565380),
KazWolfe identified registration of only the first DNS result and supplied an
[experimental multi-address patch](https://gist.github.com/KazWolfe/c110d101ecd00810cc89c13413495754).
This was the key diagnosis: an IPv4 endpoint could accept the TCP connection
without having the `localhost` prefix needed to route the HTTP request.

## Corrected managed-listener behavior

`http://localhost:<port>/` now registers on `127.0.0.1` and, when enabled and
supported, `::1`. It no longer depends on the order of localhost DNS results.
The prefix is available to requests carrying `Host: localhost` on either family.
This is loopback binding, not a wildcard binding or exposure on LAN interfaces.
Other hostname resolution, literal IP and wildcard binding rules are unchanged.
`EndPointManager.UseIpv6 = false` selects IPv4 only for localhost; configure this
process-wide setting before starting listeners rather than changing it live.

The fix is included starting with EmbedIO-Neo 1.0.2. To try it,
build the repository and reference `src/EmbedIO/EmbedIO.csproj` from a .NET 10
console application. A complete program using the corrected source is:

```csharp
using System;
using System.Threading;
using EmbedIO;
using EmbedIO.Actions;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithUrlPrefixes("http://localhost:45454/",
        "http://127.0.0.1:45454/", "http://[::1]:45454/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new ActionModule("/", HttpVerbs.Any,
        context => context.SendDataAsync(new { Message = "Hello, world!" })));
Console.WriteLine("Listening on loopback port 45454; press Ctrl+C to stop.");
await server.RunAsync(stop.Token);
```

After starting the program, both commands return `200` and the JSON message:

```sh
curl --noproxy '*' --ipv4 http://localhost:45454/
curl --noproxy '*' --ipv6 http://localhost:45454/
```

The explicit IP prefixes preserve access through those literal hostnames.
If clients only use `localhost`, a single `WithUrlPrefix` for that hostname is
enough after this fix. Explicit `[::1]` prefixes require IPv6 support regardless
of localhost configuration. Ctrl+C stops the server and releases its endpoints.

## Registration and cleanup

Registration is serialized to avoid competing socket factories for the same
endpoint. Each successful prefix records its actual endpoints; stopping removes
that registration without resolving DNS again or creating replacement sockets.
If a later bind fails, earlier binds in that prefix registration are rolled back.
Failed live prefix additions remain retryable. Rollback removes only prefixes
actually added by the attempt, preserving pre-existing registrations even when
the rejected request is an alias owned by the same listener.

Removal checks the full host, port, path, transport and owning listener, so two
listeners sharing an endpoint do not remove each other's routes. Duplicate
ownership and incompatible HTTP/HTTPS registrations on the same socket are
rejected instead of silently attaching an unusable configuration. A conflict
does not displace the existing listener. Free the conflicting endpoint and retry;
do not expect a partially started dual-stack listener to remain active.

## Validation and limits

Before the correction, two of four forced-address real HTTP cases failed on
current source. All twenty-three final regressions cover both families, explicit IP
prefixes and ordering, TLS, unregistered-host rejection, shared-host isolation,
partial-bind and multi-prefix rollback, registration conflicts, IPv4-only mode,
stop/restart, concurrent registration, live-prefix retries, and existing-route
preservation after rejected wildcard aliases or partial localhost alias expansion.

The tests control the TCP destination separately from the request hostname, so
DNS ordering and client fallback cannot hide a broken family. IPv6-specific
cases require platform IPv6 support. HTTPS checks trust only a disposable test
certificate and retain hostname validation. Native-listener defaults are
unchanged. The original 2023 Windows build and browser environment were not
recreated; the reported managed-listener failure was reproduced directly with
real HTTP connections. If a client still fails, provide its resolved address,
listener mode, prefixes, runtime and redacted Host/response headers.
