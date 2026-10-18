# Opt-in output caching

Output caching requires **EmbedIO-Neo 2.10.0+**.

`EmbedIO.Caching.OutputCache` reuses the completed bytes of explicitly public,
finite GET and HEAD responses. A hit skips action argument binding, action
execution and serialization. Controller activation, authorization, browser
protection and before-handler hooks still execute for every request. Routes
without a policy retain their existing behavior. No new runtime dependency is
required; both library targets support the cache.

## Controller policies

Keep the cache instance so mutation handlers can invalidate it. Configure named
policies before registering controllers:

```csharp
using System;
using System.Collections.Generic;
using EmbedIO;
using EmbedIO.Caching;
using EmbedIO.Routing;
using EmbedIO.WebApi;

var cache = new OutputCache();
var api = new WebApiModule("/api");
api.ConfigureOutputCaching(cache, new Dictionary<string, OutputCachePolicy>
{
    ["catalog"] = new OutputCachePolicy(TimeSpan.FromSeconds(10),
        varyByHeaders: new[] { "X-Catalog-Version" }, tags: new[] { "catalog" })
});
api.RegisterController<CatalogController>();

public sealed class CatalogController : WebApiController
{
    [Route(HttpVerbs.Get, "/catalog"), OutputCache("catalog")]
    public object Catalog() => new { name = "Example", available = true };
}
```

Add `api` to the server before starting it. Generated controller bindings honor
the same attribute and policies without reflection or runtime code generation.
A class-level attribute supplies a default; a method attribute replaces it.
`[OutputCache(null)]` disables that default for one action. Unknown policy names
fail controller registration. Policies are immutable; the configuration copies
the supplied dictionary.

The base controller's automatic client `no-store` headers are omitted only for
an eligible public request to an action with an output-cache policy. Sensitive
or otherwise ineligible controller requests retain the base client no-store
behavior. Explicit `Response.DisableCaching()` in
an override, hook or action still prevents storage. No client cache lifetime is
added automatically. Do not use these policies for personalized responses, live
streams, actions with side effects, custom request binders or validators that
must execute on every hit. Put security decisions in authorization or hooks,
where they always run. Controller session access bypasses caching.

An opted-in finite action owns its controller until the action and serializer
finish, including cancellation and failure. Disposal/release is awaited after
that work and before publication, and also occurs on a hit. A release failure
publishes nothing, and a failed release is never invoked a second time. This is the new cache path's lifetime;
uncached legacy controller disposal timing remains unchanged.

## Fluent handlers

Use `ExecuteAsync` for an action module, or `Cache(policy, routeHandler)` to wrap
a `RouteHandlerCallback` registered with a routing module:

```csharp
var policy = new OutputCachePolicy(TimeSpan.FromSeconds(10), tags: new[] { "catalog" });
var routes = new RoutingModule("/");
routes.Add(HttpVerbs.Get, RouteMatcher.Parse("/catalog", false),
    cache.Cache(policy, (context, _) => ResponseSerializer.Json(context,
        new { name = "Example", available = true })));
```

Place authentication, authorization, CORS and request-specific middleware before
this wrapper. Only use the context passed to the wrapped handler for response
writes. Do not retain it or start unawaited writes. The context delegates normal
HTTP features, including request cancellation and response sections; a protocol
upgrade or tunnel is not a cacheable endpoint.

## Keys and exclusions

Keys include the policy identity, HTTP method/version, scheme/authority (including port),
exact raw path/query, and all values of Accept, Accept-Encoding, Accept-Language
and Origin. Query order and path casing are preserved. HEAD has a separate key;
it cannot reuse or populate a GET entry. Additional `varyByHeaders` names must
include **every** header your action uses to select data. Do not rely on a Vary
response field to add new key dimensions: unsupported Vary fields and `Vary: *`
prevent storage. Keys preserve missing fields and individual value boundaries.

An HTTP/3 request without an explicit zero Content-Length is conservatively
reported as potentially having a body by the current engine, even for a GET. It
bypasses caching; a client that sends Content-Length: 0 can use the cache. The
cache never consumes an unknown request body to discover whether it is empty.

Requests with a body, credentials, cookies, ranges, conditions, Cache-Control,
Pragma or an upgrade bypass lookup and storage. Only 200 responses without
cookies, Set-Cookie, Cache-Control, Pragma, ranges or trailers are stored.
Explicit SSE/JSON streaming controller results bypass the cache. Application
flushes, response sections, session access, completion callbacks, early close,
oversized output, failures and cancellation prevent publication. Ordinary
finite string/JSON serialization, including its completion flush, is supported.

Only representation fields (content type/coding/language/disposition, ETag,
Last-Modified, Vary and Server) are replayed. The transport supplies fresh Date
and framing. Upstream and hook headers are recomputed for each request, so CORS
and request identifiers are not taken from an earlier request. Current supported
Vary fields are merged with the stored representation's requirements. An action that
adds other fields is conservatively excluded rather than losing those fields
on a hit. Do not use action headers for per-request security or diagnostics.

## Freshness, invalidation and concurrency

Cached data is a **snapshot**, not a live view. The lifetime is measured with a
monotonic clock from fill start; a fill that outlasts it is not stored. Lifetime
must be positive and at most one day. After a successful data commit, call
`cache.InvalidateTag("catalog")` before reporting the mutation complete, or
`cache.Clear()` for all data. Tags are case-sensitive. Invalidation also fences
all earlier fills, including a matching fill that has not published yet, so an
old result cannot repopulate the cache after the call. The original request may
still receive the snapshot it was already producing. An already admitted hit
may also finish concurrently with invalidation; this is not a transactional
read-your-writes system. Revalidate or bypass caching when strict consistency
is required. Separate cache instances/processes require application-managed
invalidation in each process. Do not cache secrets.

Requests for the same missing key wait for one producer. Canceling a waiter
does not cancel that producer or other waiters. A failed/canceled producer
publishes nothing; another waiter may try again. Waits use the current context
cancellation token; configure a request deadline for bounded application work.

Defaults are 256 resident entries, 16 MiB accounted body/key/header bytes,
256 KiB per body, 16 simultaneous captured distinct keys, and 8,192 key
characters. Entry count bounds index/object overhead separately; the byte count
is not total process memory. Capture buffers are bounded per active fill, with
an additional bounded copy when publishing. Overflow keys/fills and oversized
responses bypass caching without truncating their response. Least-recently-used
entries are evicted; expired entries are removed lazily, with no timer or worker.
`Count` may include expired resident entries; `SizeBytes` reports accounted bytes.

## Validation

`OutputCacheTest` covers cache and uncached paths, controller parity, security
exclusions, invalidation during fills, canceled producers/waiters, bounds and
real transport framing/compression. The `output-cache` state-machine campaign
uses an independent three-owner snapshot ledger and an injected-corruption
negative oracle. See [multi-connection fuzzing](multi-connection-fuzzing.md).

Run `dotnet run --project test/EmbedIO.Performance -c Release --
--output-cache-latency 1000` on a quiet host for real HTTP/1.1 first-miss and
warm p50/p95/p99 measurements against uncached and policy-bypass controls.
The first miss is measured after a control request; it is not process startup.
The workload creates and serializes 128 product rows. Report that workload and
environment with results; no universal latency improvement is implied.
