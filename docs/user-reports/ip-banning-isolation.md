# Independent IP banning modules

This guide addresses [upstream #545](https://github.com/unosquare/embedio/issues/545).
The original route-keyed registry reused one configuration for modules with the
same base route, even across servers or module groups. Starting one module locked
the others' configuration, and disposing one could clear another's bans and
remove it from periodic purging.

Neo now gives each module its own configuration. Whitelists, registered criteria,
default ban durations and automatic bans are independent. Disposing a module
removes only its registration and configuration. Surviving modules remain
registered for the existing one-minute purge schedule. The registry holds weak
references so abandoned modules are not kept alive by the registry; applications
must still dispose servers/modules to release their owned resources promptly.

## Select one module explicitly

The new instance controls are `TryBanClient`, `TryUnbanClient` and `BannedIPs`.
`TryBanClient` accepts minutes, a `TimeSpan` or an expiration `DateTime`, with an
optional `isExplicit` flag. `BannedIPs` returns a list snapshot using the existing
mutable `BanInfo` type. Controls throw `ObjectDisposedException` after disposal.

These additions are on main and are **not in published EmbedIO-Neo 1.0.2**.
Until a release includes them, use a checkout/project reference. In a separate
console project outside the checkout, replace `/path/to/embedio-neo` below with
your actual checkout directory:

```sh
dotnet new console --framework net10.0 -n BanningExample
cd BanningExample
dotnet add reference /path/to/embedio-neo/src/EmbedIO/EmbedIO.csproj
```

Replace `Program.cs` with this complete program:

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Security;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var restricted = new IPBanningModule();
using var independent = new IPBanningModule();
restricted.TryBanClient(IPAddress.Loopback, TimeSpan.FromMinutes(5));
restricted.TryBanClient(IPAddress.IPv6Loopback, TimeSpan.FromMinutes(5));

using var first = CreateServer("http://localhost:8877/", restricted);
using var second = CreateServer("http://localhost:8878/", independent);
Console.WriteLine("8877 returns 403; 8878 returns ok. Press Ctrl+C to stop.");
await Task.WhenAll(first.RunAsync(stop.Token), second.RunAsync(stop.Token));

static WebServer CreateServer(string url, IPBanningModule banning) =>
    new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
        .WithModule(banning)
        .WithModule(new ActionModule("/", HttpVerbs.Any,
            context => context.SendStringAsync("ok", "text/plain", Encoding.UTF8)));
```

Run `dotnet run`, then in another terminal:

```sh
curl -i http://localhost:8877/
curl -i http://localhost:8878/
```

The first returns HTTP 403; the second returns HTTP 200 with `ok`. Both ban
loopback address families because `localhost` resolution can select either.
Register the banning module before the response-producing module. For an
application administration endpoint, retain the desired module reference and
call its instance controls; require appropriate authentication/authorization
before exposing those controls to remote users.

## Existing static controls remain route-wide

Existing `IPBanningModule.TryBanIP`, `TryUnbanIP` and `GetBannedIPs` signatures
remain available. They operate on **all live modules registered under the exact
constructor route string**, across servers and relative module-group routes.
For example, `IPBanningModule.TryBanIP(address, 5, "/")` intentionally bans the
address in every live `/` module. Use instance controls for one server/group.

Static ban/unban returns true if any matching configuration records/removes the
ban. Unban clears the address's criterion data in every matching module, as it
did for the former shared configuration. `GetBannedIPs` returns a union with one
entry per address; when expirations differ it returns the latest expiration.
This union describes route-wide state, not the ban status of one selected
module. No matching registration still raises `ArgumentException` with
`baseRoute` as the parameter. A null route raises `ArgumentNullException`.
Single-module behavior and public signatures remain intact.

Automatic/configuration sharing is corrected; deliberately shared criterion
objects or application-global state are not made independent by this change.
Create separate criterion instances for separate modules. Custom purge/clear/
dispose callbacks should return promptly and must not wait for another thread
to perform controls on the same module: its controls and cleanup are serialized
to avoid disposal racing periodic purging. Other modules have independent locks;
user callbacks are not run under the registry's global lock. One configuration's purge exception is
logged and does not prevent the others from being purged.

Ban expiration retains the existing local-time and periodic-purge behavior;
this change does not promise immediate expiration or alter whitelist precedence,
listener defaults, supported targets or package dependencies.

## Validation

Twenty-one regression cases cover both listener modes with real HTTP, independent
servers and sibling module groups, whitelist/criterion/ban-duration isolation,
configuration locking, all instance/static overloads, union semantics, scoped
unban, disposal, survivor purging, faulty purge callbacks, weak registrations
and concurrent lifecycle operations, including gated purge/disposal and
independent controls during a slow callback. A separate pre-change probe reproduced
configuration identity, shared bans and disposal/registry interference.
The registry's purge action is invoked deterministically in tests rather than
waiting a minute; production scheduling remains unchanged.
