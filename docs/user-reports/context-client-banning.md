# Context-based and persistent client banning

[Upstream #438](https://github.com/unosquare/embedio/issues/438) proposed request-context
banning and permanent address policies. Thanks to rdeago for the proposal and
follow-up favoring dependency-free range matching. Neo implements the approved
additive design in `ClientBanningModule`; the existing `IPBanningModule`, its
criterion interface, static/instance controls and defaults remain available.

## Availability and runnable demonstration

This API is unreleased. Use a checkout containing this change; published 1.0.3
does not contain this module. From the repository root:

```sh
dotnet new console --framework net10.0 -n ClientBansDemo -o TestResults/ClientBansDemo
dotnet add TestResults/ClientBansDemo/ClientBansDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/ClientBansDemo/Program.cs` with this complete program:

```csharp
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Security;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var banning = new ClientBanningModule("/api", context => ClientKey(context.RemoteEndPoint.Address))
    .WithDeniedNetworks("192.0.2.0/24", "2001:db8::/32")
    .WithPermanentBanStore(Path.Combine(AppContext.BaseDirectory, "permanent-bans.json"))
    .WithCriterion(context => Task.FromResult(context.Request.Headers["User-Agent"] == "block-example"));

using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:9697/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithBasicAuthentication("/admin", m => m.WithAccount("demo", "local-demo-password"))
    .WithModule(banning)
    .OnGet("/api", c => c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding))
    .OnPost("/admin/temporary", c => Reply(c, banning.TryBanClient("127.0.0.1", TimeSpan.FromMinutes(5))))
    .OnPost("/admin/permanent", c => Reply(c, banning.TryBanClientPermanently("127.0.0.1")))
    .OnPost("/admin/unban-temporary", c => Reply(c, banning.TryUnbanClient("127.0.0.1")))
    .OnPost("/admin/unban-permanent", c => Reply(c, banning.TryUnbanClientPermanently("127.0.0.1")));

Console.WriteLine("API: http://127.0.0.1:9697/api; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);

static string ClientKey(IPAddress address) =>
    (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
static Task Reply(IHttpContext context, bool recorded) => context.SendStringAsync(
    recorded ? "confirmed" : "unchanged", "text/plain", WebServer.Utf8NoBomEncoding);
```

Run `dotnet run --project TestResults/ClientBansDemo`. In another terminal:

```sh
curl -i http://127.0.0.1:9697/api
curl -i -u demo:local-demo-password -X POST http://127.0.0.1:9697/admin/permanent
curl -i http://127.0.0.1:9697/api
```

Expect 200 with `ok`, 200 with `confirmed`, then 403. Stop and rerun the server:
`/api` still returns 403. The permanent key was restored from the same file.
Remove it live:

```sh
curl -i -u demo:local-demo-password -X POST http://127.0.0.1:9697/admin/unban-permanent
curl -i http://127.0.0.1:9697/api
```

Expect `confirmed`, then 200 with `ok`. Another restart preserves the removal.
The `/admin/temporary` and `/admin/unban-temporary` commands exercise live temporary
bans; those expire and are not saved across restarts. A request with
`User-Agent: block-example` triggers a temporary ban through the context criterion.

These administrative routes affect only the fixed local-demo IP. Their Basic
credentials and HTTP transport are only for a controlled loopback demonstration.
Use trusted HTTPS and proper authentication/authorization for real administration;
never expose a store path or arbitrary ban controls to unauthenticated callers.
The simple account dictionary is not a production identity/password store.
The admin route is outside `/api`, so the example can unban its own client.
Actions use base-path matching; these example routes also match descendant paths.

## Policy rules and live operations

Evaluation order is explicit:

1. A matching allow network permits the request without running criteria or the key selector.
2. A matching permanent deny network returns 403.
3. An active permanent or temporary ban of the selected client key returns 403.
4. At the configured client capacity, requests outside allow networks return 403 until capacity is freed.
5. Criteria run in registration order. The first `true` records a temporary ban and returns 403.
6. State is checked again after callbacks, so a concurrent ban or disposal cannot allow the pending request.

Empty network lists impose no network restriction. Allow rules override **all**
bans, including permanent client keys; add them deliberately. Deny networks never
expire and never appear in client-key snapshots. They and criterion callbacks are
configured before startup. Existing IP-module whitelist/expiration behavior is
not changed; see [IP-banning isolation](ip-banning-isolation.md).

`TryBanClient`, `TryUnbanClient`, `TryBanClientPermanently` and
`TryUnbanClientPermanently` work while serving requests. They require no reload.
`BannedClients` contains detached immutable temporary-ban records with UTC
expiration; `PermanentBannedClients` contains a detached snapshot of permanent keys.
Temporary unban never removes a permanent ban. Promoting a temporary key to a
permanent ban removes its temporary record; requesting a temporary ban for an
already permanent key returns false. Repeating permanent ban is idempotently true;
removing an absent/expired key returns false.

Keys use ordinal, case-sensitive comparison and must be nonblank, well-formed
Unicode strings of at most 1024 characters. No trimming, case folding or identity
inference is applied. Existing later temporary expiration is never shortened.
Expiration is enforced when checking/updating/reading bans without a periodic
worker. Pending callbacks can make a new ban decision after an unban; unban does
not reset application-owned criterion state or cancel callbacks already running.

The default automatic duration is thirty minutes; the default capacity is 4096
unique temporary/permanent keys per module. Both are explicit constructor options.
At capacity, manual insertion of a new key returns false and requests fail closed
within the module's route. Active bans are never silently evicted. Size capacity
for the application, provide an appropriately trusted allow/admin path, and check
control results. Permanent and temporary state are independent across modules.

## Address matching and trusted identity

Network configuration accepts IP literals and IPv4/IPv6 CIDRs, without DNS.
IPv4 literals use canonical dotted decimal. Host bits are normalized to the subnet;
`127.0.0.42/8` therefore denotes `127.0.0.0/8`. Matching compares immutable prefix
bytes rather than enumerating every address, including `/0`, `/32` and `/128`.
IPv4-mapped IPv6 addresses normalize to IPv4 for matching. Mapped CIDRs require
at least 96 prefix bits and normalize to the corresponding IPv4 prefix.
An IPv6 `/0` does not cover IPv4; configure `0.0.0.0/0` separately if needed.
Configured zone/scope identifiers are rejected; source IPv6 scope identifiers do
not change network membership. Invalid input fails configuration, and a failed
batch adds none of its networks.

The key selector and criteria receive the actual `IHttpContext`. Choose a trusted
application identity, IP address or another explicit application key. Register the
module **after** authentication that establishes the identity and **before** the
handlers it protects. Do not assume the Basic-authentication module populates
`context.User`; it verifies credentials but does not establish an application
principal. Existing [route authorization guidance](route-authorization.md) shows
application-owned authentication state and authorization boundaries.

No forwarded header, username query parameter or client-supplied identity header
is trusted automatically. User agents are client-controlled metadata, useful for
policy but not proof of identity. An IP key groups clients behind the same NAT or
proxy. Trusted proxy resolution must be an application policy established before
this module; ordinary network lists use the transport remote address. Groups use
relative base routes, with the same configuration/ordering rules.

Callbacks are borrowed, not disposed, and can run concurrently. They must be
thread-safe, honor `context.CancellationToken` and not retain a request context.
Errors propagate through existing module error handling; failed callbacks do not
silently allow handlers. Cancellation is checked before and after callbacks and
before state confirmation. Disposal clears memory/releases the optional writer
lease; it does not wait for an uncooperative application callback or delete the store.

## Persistence contract

`WithPermanentBanStore(path)` is optional and configured before startup. Without
it, permanent bans last for the module lifetime but are not durable. With it, the
application-selected JSON file is loaded during configuration. Client keys are stored
in plaintext; the application owns file permissions and backups. Missing files
start empty; malformed, duplicate, oversized, unsupported-version or unavailable
stores throw rather than discarding bans. Do not catch these errors and start a
security policy that the application intended to restore.

Preconfigured permanent keys merge with restored keys; matching temporary keys
are promoted. Restore must fit the same combined capacity. Live permanent changes
save a complete versioned snapshot through a unique same-directory temporary file,
flush it, and replace/move it before confirming the in-memory change. A failed save
throws and does not confirm the ban/unban. Temporary bans, network lists, selectors
and callbacks are never serialized. Reapply network/callback configuration when
constructing the next server.

A separate `.lock` file holds an exclusive cooperating .NET writer lease for the
module lifetime. Dispose the module before reopening the same store. The lock file
may remain on disk after disposal; the open lease determines ownership. Do not edit
the file externally or share it with noncooperating writers. Same-directory atomic
replacement requires filesystem support; no fallback deletes the old file first.
This is a single-host application store, not distributed state or a transaction
across processes. It does not promise recovery from every power-loss/filesystem
failure. Existing abandoned temporary files are not treated as committed bans.

## Validation

The implementation is being validated before merge. Coverage includes live
ban/unban on both listeners; real same-port restart recovery; removal surviving
another restart; corrupt-file rejection; write-failure memory/disk consistency;
Unicode round trips; writer leases; concurrent updates; permanent/temporary
separation; bounded capacity; module/group isolation; cancellation/disposal races;
CIDR boundaries and 6000 framework-oracle membership comparisons. Existing IP
banning and parser regression coverage remains. Exact legacy runtime execution
and production power-loss recovery are not claimed by modern-host tests.
