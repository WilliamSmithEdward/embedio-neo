# Fluent Basic authentication registration

[Upstream #439](https://github.com/unosquare/embedio/issues/439) proposed a fluent
registration helper for Basic authentication. Thanks to original maintainer rdeago
for identifying this consistency gap. Neo adds `WithBasicAuthentication` to the
existing `EmbedIO.WebModuleContainerExtensions` class; it uses the existing
`BasicAuthenticationModule` and its registration path.

## Availability

The helper is unreleased. Published EmbedIO-Neo 1.0.3 supports the equivalent
manual registration shown below. Until an authorized release includes this API,
try it against a checkout containing the change:

```sh
dotnet new console --framework net10.0 -n AuthDemo -o TestResults/AuthDemo
dotnet add TestResults/AuthDemo/AuthDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Run these commands from the repository root. Replace `TestResults/AuthDemo/Program.cs` with
this complete local demonstration, then run `dotnet run --project TestResults/AuthDemo`.
Keep temporary verification projects under ignored `TestResults` when contributing.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:9696/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithBasicAuthentication("/api", module =>
        module.WithAccount("demo", "local-demo-password"), realm: "Local demo")
    .OnGet("/api", context => context.SendStringAsync(
        "Authenticated", "text/plain", WebServer.Utf8NoBomEncoding))
    .OnGet("/public", context => context.SendStringAsync(
        "Public", "text/plain", WebServer.Utf8NoBomEncoding));

Console.WriteLine("Listening on http://127.0.0.1:9696/; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);
```

From another terminal:

```sh
curl -i http://127.0.0.1:9696/api
curl -i -u demo:local-demo-password http://127.0.0.1:9696/api
curl -i http://127.0.0.1:9696/public
```

Expect 401 and `WWW-Authenticate: Basic realm="Local demo" charset=UTF-8` for the
first request, 200 with `Authenticated` for the second, and 200 with `Public` for
the third. `/api` is a base path: `/api/child` is also protected. The public route
is outside that scope. Press Ctrl+C for graceful shutdown.

Basic credentials are encoded, not encrypted. These disposable credentials and
plain HTTP are only for a controlled loopback demonstration. Use trusted HTTPS
before sending real credentials, and an application-appropriate account policy.
This helper does not turn the existing in-memory dictionary into secure password
storage or an identity provider. See [native Basic authentication guidance](basic-authentication-native-listener.md)
and [HTTPS validation](../platforms/maui-https-validation.md).

## Registration contract

The base route and configuration callback are explicit; the optional realm defaults
to the module's normalized base route when null or empty. The callback runs before
registration. If it throws, the partially configured module is not inserted.
A null callback throws `ArgumentNullException` naming `configure`.

Register authentication **before** handlers it must protect. Registration remains
ordered; the helper cannot protect a request already completed by an earlier
module. Existing credential parsing, account comparison, challenge behavior,
listener choice and error policy are unchanged.

The helper returns the concrete container type. It works on `WebServer` and
`ModuleGroup`; in a group, the authentication base route is relative to the group:

```csharp
// Partial snippet: register this group before any outer catch-all handler.
var group = new ModuleGroup("/outer", isFinalHandler: false)
    .WithBasicAuthentication("/api", m => m.WithAccount("demo", "local-demo-password"))
    .OnGet("/api", c => c.SendStringAsync("Authenticated", "text/plain",
        WebServer.Utf8NoBomEncoding));
server.WithModule(group);
```

This protects `/outer/api` and its descendants. Without an explicit realm, this
module's realm is `/api/`, its relative normalized base route.

For published 1.0.3 or applications using named/custom authentication modules,
the existing registration API remains available. Replace only the authentication
registration line in the complete program with:

```csharp
.WithModule(new BasicAuthenticationModule("/api", "Local demo"),
    module => module.WithAccount("demo", "local-demo-password"))
```

This proposal adds no other authentication providers, account-storage policy,
dependencies, target changes or automatic registration.

## Validation scope

Ten new cases verify concrete return identity, explicit/null/empty realms,
configuration before insertion, null/failing callbacks and inherited route
validation. Real HTTP tests exercise server and nested-group registration on both
listener modes: missing, malformed and wrong credentials, case-insensitive Basic
scheme, colon-containing passwords, isolated protected handlers, unprotected
routes and subsequent denials. Existing authentication regression coverage remains.
CI must validate the complete change before merge; published availability is not
claimed by a source-only demonstration.
