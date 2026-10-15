# EmbedIO-Neo

[![NuGet version](https://img.shields.io/nuget/v/EmbedIO-Neo)](https://www.nuget.org/packages/EmbedIO-Neo)
[![Downloads](https://img.shields.io/nuget/dt/EmbedIO-Neo)](https://www.nuget.org/packages/EmbedIO-Neo)
[![CI](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/ci.yml)
[![Security](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/security.yml)
[![Malware scan](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/malware-scan.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/malware-scan.yml)
[![OpenSSF Scorecard](https://img.shields.io/ossf-scorecard/github.com/WilliamSmithEdward/embedio-neo?label=openssf%20score)](https://scorecard.dev/viewer/?uri=github.com/WilliamSmithEdward/embedio-neo)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

![HTTP/1.1: supported](https://img.shields.io/badge/HTTP%2F1.1-supported-brightgreen)
![HTTP/2: supported](https://img.shields.io/badge/HTTP%2F2-supported-brightgreen)
![HTTP/3: supported](https://img.shields.io/badge/HTTP%2F3-supported-brightgreen)

![EmbedIO-Neo](https://raw.githubusercontent.com/WilliamSmithEdward/embedio-neo/dcd0e9f3c27d0a618cf4aade10c7fea269db6d57/images/embedio_neo.png)

A small, modular web server for .NET. EmbedIO-Neo lets you add JSON APIs, static
files and WebSockets to desktop apps, local tools and services.

Upgrading? Coming from the original EmbedIO 3.x, start with the
[upgrade checklist](docs/compatibility/upgrading-from-embedio-3.md). Already on
EmbedIO-Neo v1, read the [v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md).

## Overview

V2 uses an independently authored HTTP engine with HTTP/1.1, HTTP/2 and HTTP/3
protocol implementations. The Microsoft HttpListener and Mono-derived listener
backends have been removed. Applications retain the EmbedIO namespaces and
modular request pipeline.

- JSON callbacks and attribute-based Web API controllers.
- Static, embedded and ZIP-backed files, including conditional requests and ranges.
- Managed WebSockets, sessions, CORS and server-sent events.
- HTTPS with an application-supplied certificate.
- Informational responses, trailers and optional capsule/tunnel APIs.
- Async I/O and graceful drain, with documented protocol and platform limits.

## Protocols and platforms

| Capability | Support |
| --- | --- |
| HTTP/1.1 | Default TCP listener; HTTP and HTTPS. |
| HTTP/2 | Cleartext prior knowledge and HTTPS with ALPN. TLS negotiation requires the .NET 10 asset. |
| HTTP/3 | HTTPS over QUIC/UDP. |
| Combined hosting | HTTP/1.1 and HTTP/2 over TCP alongside HTTP/3 over UDP on the same HTTPS endpoint. |
| WebSockets | HTTP/1.1 upgrade and negotiated extended CONNECT on modern transports. |

The library targets **.NET 10** and **.NET Standard 2.0**. Windows, Linux and macOS
have desktop regression coverage. The retained .NET Standard asset has separate
compatibility evidence; it does not provide HTTP/3 or establish support for every
older runtime. Mobile and sandboxed applications need their platform-specific
network permissions and deployment setup.

See the [HTTP/3 guide](docs/guides/http3.md), [HTTPS guide](docs/guides/https.md)
and [platform documentation](docs/README.md#platforms) for configuration.

## Installation

The package ID is **EmbedIO-Neo**; the assembly and C# namespaces remain
**EmbedIO**.

```sh
dotnet add package EmbedIO-Neo
```

To build from source, use the SDK selected by `global.json` and reference
`src/EmbedIO/EmbedIO.csproj` from your application.

## Usage

This complete console program exposes a JSON endpoint at
http://localhost:9696/api/status. Press Ctrl+C to stop.

```csharp
using System;
using System.Threading;
using EmbedIO;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .OnGet("/api/status", context => context.SendDataAsync(new { status = "ok" }));

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
```

`OnGet` matches a base path, including requests beneath it. Use
[controllers](docs/guides/getting-started/controllers.md) for exact routes and
parameters. To serve files, add `WithStaticFolder("/", directory, true)` after
your API modules; [mount order](docs/guides/multiple-static-folders.md) determines
which module handles a request.

## Guides and tools

| Task | Documentation |
| --- | --- |
| Read request bodies and define routes | [Requests and controllers](docs/guides/getting-started/requests.md) |
| Serve HTML and files | [Static files](docs/guides/getting-started/files.md) |
| Configure HTTPS | [Certificates and client trust](docs/guides/https.md) |
| Use WebSockets | [Startup and readiness](docs/user-reports/websocket-startup-messages.md) |
| Stream progress to a browser | [Server-sent events](docs/user-reports/progress-events.md) |
| Send interim responses or trailers | [Response field sections](docs/guides/response-field-sections.md) |
| Accept a capsule tunnel | [Tunnel ownership and authorization](docs/guides/capsule-tunnels.md) |
| Run a command-line server | [CLI and plugins](docs/guides/cli.md) |
| Serve a JSON file as REST collections | [JsonServer integration](CONTRIBUTING.md#extras-integration) |
| Build an app with an AI coding agent | [Agent primer](AGENT_PRIMER.md) |

Browse the [documentation index](docs/README.md) for additional usage,
platform and compatibility guides.

## Upgrading

### From the original EmbedIO 3.x

EmbedIO-Neo carries the archived [unosquare/embedio](https://github.com/unosquare/embedio)
forward. Namespaces, assembly names and the fluent `WebServer` pipeline are
unchanged, so most applications move by swapping packages, replacing SWAN and
rebuilding. Work through the
[upgrade checklist](docs/compatibility/upgrading-from-embedio-3.md): it lists
each change in order, with links into the detailed guides.

### From EmbedIO-Neo v1 to v2

Read the [Neo v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md).
It covers listener removal, approved API changes, stricter request and response
framing, response statuses, connection reuse and deployment changes. Rebuild
dependent assemblies and test your application's routes and clients before
upgrading.

## Contributing and license

Report issues and submit pull requests at
[WilliamSmithEdward/embedio-neo](https://github.com/WilliamSmithEdward/embedio-neo).
See [CONTRIBUTING.md](CONTRIBUTING.md) for pinned tooling and validation commands.

Maintained by William Smith. EmbedIO-Neo is an independent continuation of
[EmbedIO](https://github.com/unosquare/embedio), licensed under [MIT](LICENSE).
Original copyrights and third-party notices are preserved. The
[engine development record](docs/project/http-engine.md) contains conformance,
performance evidence and remaining acceptance work.
