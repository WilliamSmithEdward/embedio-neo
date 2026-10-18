# Opt-in idempotency for finite writes

Available in **EmbedIO-Neo 2.11.0 and later**.

Idempotency coordinates retries of an explicitly protected POST, PUT, PATCH or
DELETE operation. The client chooses one `Idempotency-Key` for one logical
operation and sends the same transferred body and representation headers on each
retry. A completed result is recorded **before** writing it to the network. A
retry can therefore recover that result if its first response was lost.

This is not an exactly-once transaction system. The default store is bounded and
process-local. Restarts, expiry, replacing a store, or choosing a different key
remove its protection. A durable implementation can preserve claims/results,
but application side effects still need transactional integration. A timeout or
connection failure does not establish that a business operation was rolled back.

## Controller integration

Configure policies before registering controllers. The following wiring assumes
that authentication has already established one trusted identity, using the
[bearer authentication module](bearer-authentication.md), for example. Select a
stable subject and trusted issuer/tenant namespace, not the bearer token, user
display name, client-supplied caller header, IP address or idempotency key.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using EmbedIO;
using EmbedIO.Idempotency;
using EmbedIO.WebApi;

var idempotency = new IdempotencyCoordinator();
var api = new WebApiModule("/api");
api.ConfigureIdempotency(idempotency, new Dictionary<string, IdempotencyPolicy>
{
    ["commands"] = new IdempotencyPolicy("my-app:commands:v1", CallerScope,
        TimeSpan.FromHours(1))
});
// Register application controllers after configuration. Install authentication
// before this module; decorate protected actions with [Authorize] as appropriate.

static string CallerScope(IHttpContext context)
{
    if (context.User is not ClaimsPrincipal principal)
        throw HttpException.Unauthorized();
    var identities = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
    if (identities.Length != 1) throw HttpException.Unauthorized();
    var subject = identities[0].FindFirst("sub");
    if (subject == null || string.IsNullOrEmpty(subject.Value))
        throw HttpException.Unauthorized();
    // The authentication configuration must bind this claim's issuer to its
    // validated authority. Include a trusted tenant identifier where applicable.
    return FormattableString.Invariant($"{subject.Issuer.Length}:{subject.Issuer}{subject.Value.Length}:{subject.Value}");
}
```

Apply `[Idempotency("commands")]` to a controller or a finite write method.
A method declaration overrides its class; `[Idempotency(null)]` explicitly opts a
method out. `ConfigureIdempotency` snapshots the policy dictionary. Unknown
policies, read routes, streaming result types and combined output-cache policies
fail registration rather than silently bypassing protection. Keep partitions and
caller scopes stable across deployments throughout the retry/retention window.
Changing a partition, caller-scope scheme, authority or raw target starts a
different lookup namespace and removes the old record's protection for that
request. If changed business semantics require a new namespace, reconcile
outstanding operations and route their retries to the old namespace deliberately;
do not automatically increment partitions with each application version. Hosts
serving the same operation space must compute the same keys and fingerprints.

Both reflection registration and generated bindings support these attributes.
AOT consumers use the usual `[GeneratedControllerBinding]` declarations. Existing
generated-binding constructors remain available. No new runtime dependency or
supported-target change is required.

Authorization and browser request protection run **on every request**, before
lookup/replay. Controller activation, before-handler hooks, body binding,
validation, action execution, serialization and controller release run only for
the owning execution. A replay skips that entire invocation. If a condition must
be reevaluated on every retry, implement it as an authorization policy or preceding
middleware, rather than a controller hook. Request-aware controller registration
retains its awaited release behavior; legacy controller disposal timing remains
unchanged. The record is published only after the entire registered handler task
has completed, including its existing cleanup.

Routes without an idempotency policy use their original invokers. Their body
binding, serialization, hooks, cancellation and default client no-store behavior
are unchanged. Opted-out actions do not require an idempotency key.

## Fluent handlers and client requests

```csharp
var protectedHandler = idempotency.Protect(policy, async (context, route) =>
{
    // Await the entire business operation and write one finite result here.
    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
});
```

Place authentication, authorization and browser protection ahead of this wrapper;
the wrapper does not authenticate clients. `ExecuteAsync(context, policy, handler)`
is the lower-level equivalent. Always write to the supplied staged context, never
a captured original context, and never retain it or launch unawaited work.

A client submits, for example:

```http
POST /api/jobs HTTP/1.1
Authorization: Bearer <credential>
Idempotency-Key: 8b46ca9b-e0d9-4e6f-9c5c-e3b2b83f7db8
Content-Type: application/json

{"name":"import-2026-10"}
```

After a lost response, reuse that key, URL, method, body and representation
headers. Generate a new key only for a new logical operation. Keys must be one
nonempty visible-ASCII value of at most 128 characters; commas, whitespace and
multiple field values are rejected. A missing key on an opted-in route is 400.

## Identity, fingerprints and fresh headers

The opaque store key hashes the stable policy partition, trusted caller scope,
URL authority, exact raw target (including query), method and client key, with
length-delimited fields. The fingerprint hashes the transferred body and declared
header values. Reusing a key with a different fingerprint returns 409 without
executing. Keys and fingerprints do not contain raw credentials or request bodies.

Content-Type, Content-Encoding, Accept, Accept-Encoding, Accept-Language and
Origin are included by default. Add all other representation-selecting request
headers through `varyByHeaders`; up to 32 total are supported. Header names are
case-insensitive, while field values and their boundaries are compared exactly.
An undeclared `Vary` requirement, including `*`, fails closed. Current supported
Vary requirements merge with recorded ones on replay. Unchanged request-specific
headers from preceding guards stay fresh; they are not copied from an earlier
request. Cookies set by preceding authentication middleware remain per-request.

Compression happens during finite staging and the resulting bytes are recorded
with representation metadata. Delivery regenerates content length and transport
framing for the current request. Date/Server, hop-by-hop fields, cookies,
authentication headers and protocol handoffs are not replayable result headers.

## Bounds and outcome handling

Defaults are deliberately bounded:

| Setting | Default |
| --- | --- |
| Coordinator participants (including body readers/waiters) | 32 |
| Transferred request body | 256 KiB |
| Staged response body | 64 KiB |
| Additional response metadata | 16 KiB / 64 fields |
| Duplicate wait | 5 seconds, configurable up to 30 seconds |
| Terminal retention | Required policy value, at most one day |
| Store claims | 256 maximum, subject to byte reservations |
| Store result reservation | 80 KiB per claim within a 16 MiB pool |

`MemoryIdempotencyStore` reserves each claim's maximum entry bytes up front.
Consequently the default 16 MiB pool admits 204 reservations, even though its
separate entry-count limit is 256. Resident/index overhead and bounded keys are
separate from the reported reserved bytes. The coordinator bounds simultaneous
body buffers and staged responses. No timers or background workers are required.
Use the existing [request limits/deadlines](request-limits.md) as well.

| Condition | Result |
| --- | --- |
| Existing completed request | Recorded status, finite bytes and representation headers |
| Different fingerprint | 409; no action |
| Another execution still running | Bounded cancellation-aware wait, then replay or 409 |
| Prior execution outcome uncertain | 409; no automatic reexecution during retention |
| An already-waiting retry loses its original record through expiry/store loss | 409 and uncertainty; no fresh execution by that waiter |
| Claim/participant capacity exhausted | 503; no new execution and no early eviction |
| Request body exceeds policy cap | 413 before claiming or invoking |
| Missing/malformed key or unsupported request shape | 400 before claiming |
| Handler/serialization/cleanup fault or unrecordable response | Original error; retained uncertain claim |
| Result commit acknowledgment lost | Original error; retry uses the persisted result if present, otherwise stays blocked |
| Network write fails after result recording | Result remains available to retries |

A normally returned finite error response is recorded too; throwing after a
possible side effect makes the outcome uncertain. Do not blindly switch to a new
key or wait for expiry to work around an uncertain operation: reconcile it against
application state. Terminal retention begins when completion/failure is recorded.
Active claims **never** expire or evict automatically, even if their execution
outlives the configured retention. Completed/uncertain records expire lazily.

Staging supports finite responses only. Session mutation, response cookies,
completion/close callbacks, trailers, informational responses, SSE, protocol
handoffs and direct transport closure are unsupported. Await cleanup inside the
operation or its request-aware controller release. All output is buffered up to
the configured cap; flushes cannot provide streaming progress. An oversized or
unsupported response after a side effect retains uncertainty rather than allowing
another execution.

The caller's context token governs body reading, handler work and waiting. A
waiter's cancellation never cancels an owner. A known completed result is recorded
before delivery even if cancellation races that completion. Storage completion
and failure marking use a noncancelable token to avoid reopening an uncertain
operation; custom stores must enforce their own bounded I/O deadlines. The optional
[request-abort feature](request-abort.md) remains available on the staged context.

## Durable storage contract

Supply an `IIdempotencyStore` to share state beyond one process. Its operations
must be atomic and linearizable across every supported process/host:

- `AcquireAsync` creates exactly one absent-key owner or returns existing state;
  compare fingerprints before exposing a result and never disclose an existing
  owner's token. Capacity failures must not silently drop protected records.
- `CompleteAsync` commits an immutable result only for the exact still-active
  owner token. Never overwrite a terminal result or a newer owner.
- `FailAsync` marks the exact active owner uncertain; never remove its claim or
  replace a completed result. Storage errors fail closed.

A database adapter generally needs an atomic unique-key insert, conditional
owner-token updates, retained terminal rows and explicit recovery of abandoned
active rows. Recovery must reconcile business state first; time passage alone is
not evidence that a write did not happen. Keep records and business mutations in
the same transaction where that guarantee is needed. No SQL/Redis adapter,
automatic retry, cross-resource transaction or exactly-once guarantee is supplied
by this change. The default implementation is intentionally in-memory.

## Validation and replay

The 61 regression cases exercise caller/route/payload boundaries, simultaneous writes,
uncertain outcomes, lost store acknowledgments, ownership fencing, capacity,
expiry, fresh headers, controller/security parity and owned HTTP/1.0, HTTP/1.1,
HTTP/2 and HTTP/3 transports. Shared smoke code exercises Native AOT generated
bindings and the .NET Framework reflection path, including unconfigured writes.

The `idempotency` state machine uses three in-process hosts and coordinators
sharing one atomic store. Its independent business ledger checks at-most-one
execution during retention, exact results, fingerprint conflicts, caller isolation,
waiter cancellation, uncertain outcomes and capacity without early eviction.
This model does not claim wire-fault injection; wire protocols have separate tests.

```powershell
dotnet run --project test/EmbedIO.Fuzz -c Release -- --idempotency-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/idempotency.json
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines 1729 500 idempotency
```

The negative oracle injects a foreign result and must reject it. Campaign failures
retain traces and runtime/source provenance for replay; concurrent schedules are
not guaranteed deterministic. See [state-machine fuzzing](multi-connection-fuzzing.md).
