# Configure listener endpoints without losing URL-prefix compatibility

[Upstream #464](https://github.com/unosquare/embedio/issues/464), proposed by
original maintainer rdeago, suggested replacing URL prefixes with simpler
port/address/certificate configuration. Users welcomed easier onboarding, but
AbeniMatteo described externally managed Windows HTTPS certificates and
fatcerberus required localhost-only access. bufferUnderrun described separate
application ports with module-level routes. The attributed discussion is
preserved in [Neo #48](https://github.com/WilliamSmithEdward/embedio-neo/issues/48).

## Decision

Neo retains URL prefixes and both listener modes. Removing them would change
public configuration, host/path selection and Windows HTTP.sys integration.
No new endpoint API, wildcard default or backend replacement is introduced.
Use the [Getting started programs](../guides/getting-started/README.md) for
complete JSON/file/controller applications; the endpoint choices below explain
only the configuration needed when moving beyond those examples.

## Start with one explicit local endpoint

This complete program uses published EmbedIO-Neo 1.0.3:

```sh
dotnet new console --framework net10.0 --name NeoEndpoint
cd NeoEndpoint
dotnet add package EmbedIO-Neo --version 1.0.3
```

Replace `Program.cs`:

```csharp
using System;
using System.Threading;
using EmbedIO;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .OnGet("/hello", c => c.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding));
Console.WriteLine("GET http://127.0.0.1:8877/hello ; press Ctrl+C to stop.");
try { await server.RunAsync(stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
```

Run `dotnet run`, then `curl http://127.0.0.1:8877/hello`. The response body is
`hello`. Press Ctrl+C for graceful shutdown. `.OnGet` matches a base path, so
`/hello/child` also returns `hello`. Choose an unused port if 8877 is occupied.

## Read the prefix as four choices

For `http://127.0.0.1:8877/scope/`, the scheme selects HTTP, the host selects an
address/host policy, the port is 8877, and the listener path is `/scope/`.
Prefixes require a trailing slash at the listener level; WebServer normalizes
its input and adds a missing trailing slash. Use explicit lowercase paths:
WebServer currently lowercases the registered prefix. This guide does not
change that inherited normalization.

| Choice | Managed EmbedIO listener | Microsoft listener |
| --- | --- | --- |
| `127.0.0.1` | IPv4 loopback socket and matching host | Windows HTTP.sys IP-based routing; Unix runtime behavior differs |
| `[::1]` | IPv6 loopback socket; bracket the literal in the URL | Native/runtime IPv6 prefix; requires IPv6 support |
| `localhost` | Both loopback families when IPv6 is enabled and available; host matching remains `localhost` | Hostname routing; on Windows this is not a socket-interface isolation guarantee |
| Explicit local interface IP | Binds that address, while preserving host matching | Windows HTTP.sys address/host routing; see the native configuration documentation |
| `*` or `+` | Broad socket binding and wildcard host routing | Native wildcard routing policies, which differ from managed precedence |
| Omitted port | HTTP 80 or HTTPS 443 | HTTP 80 or HTTPS 443; binding/permissions still apply |

The managed listener currently resolves other DNS hostnames to their first
resolved address; failed resolution falls back to a wildcard address. Do not use
an arbitrary or misspelled DNS name as an isolation mechanism. Prefer explicit
IP literals for a predictable managed binding. The global `EndPointManager.UseIpv6`
option affects wildcard binding and localhost IPv6 registration; it is not a
per-server switch. This work preserves those existing policies.

Windows HTTP.sys owns listening and URL routing for the Microsoft mode. Its
hostname prefixes, URL reservations, IP listen configuration and firewall policy
are separate concerns. Do not promise a localhost hostname prefix universally
prevents remote TCP access, or that a firewall prompt can never occur. The
historical reporter's prompt observation was application/platform-specific.
See Microsoft's [URL-prefix routing documentation](https://learn.microsoft.com/en-us/windows/win32/http/urlprefix-strings)
and [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=net-10.0).

## Keep listener selection separate from module routing

A listener prefix `/scope/` accepts requests under that path; it does **not**
remove `/scope` before module routing. Replace the `using var server` declaration and the following `Console.WriteLine`
in the program above with these lines; keep the shutdown code:

```csharp
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/scope/")
    .WithMode(HttpListenerMode.EmbedIO))
    .OnGet("/scope/hello", c => c.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding));
Console.WriteLine("GET http://127.0.0.1:8877/scope/hello ; press Ctrl+C to stop.");
```

Request `/scope/hello`, not `/hello`. An unmatched listener prefix can reject
with an HTTP error or close the connection before a response, depending on the
listener. Prefix paths are not an authorization boundary: configure
[authorization](route-authorization.md) separately.

Existing `WithUrlPrefixes(...)` supports several prefixes on one server,
including distinct ports. Distinct compatible listeners can share a port using
different host/path prefixes; the most specific matching path selects its
listener. Removing a nested listener leaves a broader registered prefix able to
handle that path. Duplicate ownership and incompatible HTTP/HTTPS endpoint
sharing are rejected. A port-only replacement would erase these choices.
For most applications, one root listener plus module routes is simpler than
sharing ports across several servers; see the getting-started examples.
Windows can also partition requests across processes with HTTP.sys; managed
Neo endpoint sharing is process-local. Do not infer identical wildcard
precedence or cross-process sharing from the two modes.

## Preserve certificate ownership

For cross-platform managed HTTPS, supply a private-key certificate using existing
`WithCertificate(...)`; see [HTTPS and listener modes](../guides/https.md)
for provisioning and trust requirements. The certificate stays separate
from the prefix's scheme/host/port. The managed listener does not turn on
Windows certificate auto-registration unless explicitly configured.

On Windows Microsoft mode, HTTPS uses HTTP.sys certificate bindings maintained
by Windows/the application administrator, including externally managed renewal.
Retain existing netsh/ACME workflows rather than replacing them with a managed
certificate default. A supplied managed certificate is not proof HTTP.sys has
been configured. Unix Microsoft's HttpListener does not support HTTPS; select
Neo's managed mode for that use case. This is an existing backend distinction,
not a newly changed default.

## Validation and limits

The compatibility tests exercise real HTTP with both listener modes: unchanged
module paths under a listener prefix, child routes, unmatched prefixes and
subsequent healthy requests, longest-prefix selection and nested-listener
shutdown, multiple independent ports, wildcard Host acceptance and explicit IPv6.
Local unelevated Windows cannot register native wildcard prefixes without a URL
reservation: only that exact permission failure is locally not applicable. CI
must execute these cases and does not allow that local skip. No local HTTP.sys
reservations or firewall policies are changed by this work. Existing prefix parsing, localhost
IPv4/IPv6, certificate and endpoint-ownership regressions cover default ports,
loopback binding, aliases and registration conflicts.

This evaluates the proposed removal as not planned and documents supported
configuration; it does not claim the historical original applications were
reproduced. These checks do not establish LAN reachability from another device,
HTTP.sys cross-process partitioning or an ACME renewal workflow. Dedicated
platform HTTPS jobs validate Neo's app models separately.
A hostname prefix is not a substitute for network isolation or authorization.
A future additive API should address a demonstrated gap with explicit binding
and compatibility rules; it needs separate review and approval.
