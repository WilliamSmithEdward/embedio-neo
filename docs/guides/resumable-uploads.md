# Resumable uploads using tus

Pending the next release; unavailable in NuGet 2.11.0.

`EmbedIO.Tus.TusUploadModule` implements the [tus 1.0 protocol](https://tus.io/protocols/resumable-upload)
with creation, expiration, termination and checksum extensions. Clients create an
upload, query its committed byte offset and append from that offset. Interrupted
transfers can resume across connections and server process restarts. Existing
multipart readers, controllers and unconfigured requests retain their behavior.
The module is optional and supported by both library targets, without new runtime
packages. Native AOT does not require reflection or JSON metadata for this store.

## Host configuration and authentication

Use a dedicated private directory outside all static-file mounts. Provision OS
permissions so only the server account can read or write it, and protect its
parent directories too. The store does not grant permissions, change ACLs or
provide encryption at rest. Do not put it on a network filesystem or share its
files with another application. One store exclusively owns the directory;
multiple modules in that process can borrow the same store. Another process or
store instance is rejected while it is owned.

This factory accepts the application's existing asynchronous bearer validator.
Keep signature, issuer, audience, token-type and revocation checks in that
validator/policy layer; see [bearer authentication](bearer-authentication.md).
Serve external clients over HTTPS. Cookie-authenticated browser hosts must also
install [browser request protection](browser-request-protection.md).

```csharp
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Tus;

static WebServer CreateUploadServer(
    DiskTusUploadStore store,
    BearerTokenValidator validateToken,
    Func<IHttpContext, TusUpload, Task> publishValidatedUpload)
{
    var uploads = new TusUploadModule("/uploads/", store, SelectOwner)
    {
        OnCompleted = publishValidatedUpload
    };
    return new WebServer(options => options
            .WithUrlPrefix("http://127.0.0.1:9696/")
            .WithMaximumConcurrentRequests(32)
            .WithRequestTimeout(TimeSpan.FromSeconds(60)))
        .WithModule(new BearerAuthenticationModule("/uploads/", validateToken)
        {
            RequireAuthentication = true
        })
        .WithModule(uploads);
}

static string? SelectOwner(IHttpContext context, string effectiveMethod)
{
    // Apply the application's current upload permissions here on EVERY request.
    // effectiveMethod includes X-HTTP-Method-Override, so a POST carrying DELETE
    // must receive the application's DELETE permission check.
    if (effectiveMethod != "POST" && effectiveMethod != "PATCH"
        && effectiveMethod != "HEAD" && effectiveMethod != "DELETE"
        && effectiveMethod != "OPTIONS") return null;
    if (context.User is not ClaimsPrincipal principal) return null;
    var identities = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
    if (identities.Length != 1) return null;
    var subject = identities[0].FindFirst("sub");
    if (subject == null || string.IsNullOrEmpty(subject.Value)) return null;
    // The validator must bind the claim issuer to the validated authority.
    // Add a trusted tenant scope when a subject can exist in several tenants.
    return FormattableString.Invariant(
        $"{subject.Issuer.Length}:{subject.Issuer}{subject.Value.Length}:{subject.Value}");
}
```

The application creates and owns `DiskTusUploadStore`, supplies it to this
factory, awaits server shutdown and then disposes the store. For example:

```csharp
var options = new TusUploadOptions(privateUploadDirectory,
    maximumUploadBytes: 512L * 1024 * 1024,
    maximumStorageBytes: 2L * 1024 * 1024 * 1024,
    maximumUploads: 64,
    maximumPatchBytes: 4 * 1024 * 1024,
    lifetime: TimeSpan.FromHours(12),
    maximumConcurrentRequests: 4,
    maximumUploadsPerOwner: 8,
    maximumStorageBytesPerOwner: 512L * 1024 * 1024);
using var store = new DiskTusUploadStore(options);
// using var server = CreateUploadServer(store, validator, completionHandler);
// await server.RunAsync(shutdownToken);
```

The owner selector is mandatory, is called again on every operation and receives
the effective tus method. It returns null to deny access. The module also requires
an authenticated principal independently of that callback. Use a stable trusted
issuer/tenant/subject scope, never the bearer token, IP address, display name,
upload ID or a client-provided identity header. Keep this scope scheme stable
across restarts. Resource operations by a different owner return 404 and disclose
no offset. The store persists only a SHA-256 digest of the selected owner scope.

Place authentication, authorization, forwarding trust, rate limiting and browser
protection before the upload module. Controller `[Authorize]` attributes govern
controllers, not this module; enforce upload permission in the selector or earlier
middleware. Do not rely solely on the physical HTTP verb for authorization:
tus supports `X-HTTP-Method-Override`. The pending browser-protection correction
checks overrides too: safe physical verbs and WebSocket upgrade headers cannot
exempt an unsafe override from the token requirement. Mount controllers at separate paths; their
existing bindings, hooks, caching and idempotency behavior need no changes. Do not
wrap this streamed protocol in output caching or finite-write idempotency.

Browser clients need CORS handling before this module, permitting the required
methods and protocol request headers and exposing `Location`, `Upload-Offset`,
`Upload-Length`, `Upload-Expires`, `Upload-Metadata`, `Tus-Resumable`, `Tus-Version`, `Tus-Extension`,
`Tus-Max-Size` and `Tus-Checksum-Algorithm`. Preflight handling must run before the
module's authentication requirement. Restrict allowed origins deliberately.

## Client flow

Every non-OPTIONS request must contain `Tus-Resumable: 1.0.0`. Authenticate every
request, including OPTIONS discovery; a browser CORS preflight is handled by the
preceding CORS module. Wrong or missing protocol versions receive 412 with the
supported version. Use an existing tus client with its chunk size configured no
larger than `MaximumPatchBytes`; the default server patch cap is 16 MiB. Do not
enable creation-with-upload, deferred lengths or parallel concatenation.

1. `POST /uploads/` with `Upload-Length: 6` and an empty body returns 201 and an
   opaque `Location`. Persist that URL before sending file bytes.
2. `PATCH <Location>` with `Content-Type: application/offset+octet-stream`,
   `Upload-Offset: 0` and body `abc` returns 204 and `Upload-Offset: 3`.
3. After a connection interruption or restart, `HEAD <Location>` returns the
   committed offset, total length, original metadata and fixed expiry.
4. Seek the original local file to that offset and send its remaining bytes,
   using bounded PATCH requests. Each 204 acknowledges a durable committed offset.
5. `DELETE <Location>` terminates a complete or partial upload and frees its
   reservation. Once removed, it returns 404 on subsequent resource requests.

On an ambiguous/lost PATCH response, use HEAD before retrying. A stale offset gets
409 without modifying the upload. Concurrent mutation of the same upload also
gets 409; an excess active participant gets 503 immediately, without an unbounded
wait queue. A 404 after expiry requires a new upload. Ensure the local source file
has not changed before resuming: an offset alone cannot prove it is the same file.

Optional `Upload-Metadata` accepts at most 4 KiB of printable ASCII, 32 unique
keys of at most 64 characters and Base64-encoded binary values (or empty values).
The original valid header is returned by HEAD. Treat decoded values as untrusted;
filenames never determine store paths. IDs are generated by the server, and
unknown or malformed resource IDs are rejected.

The checksum extension supports `sha1` (required by tus) and `sha256`. Prefer
`sha256`; these are transfer checksums, not signatures or authentication. A PATCH
may send `Upload-Checksum: sha256 <Base64 digest of this PATCH body>`. Bad syntax
or an unsupported algorithm gets 400; a mismatch gets 460 and rolls back the
entire patch. Checksum trailers are not advertised or consumed.

## Budgets, expiration and cleanup

| Budget | Default |
| --- | --- |
| One declared upload | 1 GiB |
| Aggregate reserved bytes | 4 GiB |
| Resident sessions, including empty and completed uploads | 128 |
| One owner's sessions | Smaller of 16 and the global session cap |
| One owner's reserved bytes | Smaller of 1 GiB and the global byte cap |
| One PATCH | 16 MiB; configurable up to 64 MiB |
| Active store operations / participants per module | 4; configurable up to 256 |
| Fixed lifetime | 24 hours; configurable up to 30 days |
| Transfer buffer per active write/copy | 64 KiB |

The full declared length is reserved at creation, before accepting its bytes.
A session/owner/storage limit gets 507; a per-file or per-patch size limit gets
413. Reservations include completed files until deletion or expiry. Zero-length
uploads still consume a session slot and complete immediately at creation.
These are logical quotas, not filesystem preallocation or a promise of free disk
space. Metadata/file bookkeeping adds bounded overhead beyond reserved payload
bytes. Keep operational disk headroom and explicit server body-read/deadline
limits; application callbacks must honor cancellation and finish their work.

Expiry is fixed at creation and is not extended by HEAD, retries or progress.
**Completed files expire too.** Move validated application data into its own
durable storage before the deadline. Expired resources immediately return 404.
Creation and store reopening reclaim expired idle sessions. Call
`store.RemoveExpired()` periodically from an application-owned maintenance loop
to free abandoned files even when no new uploads arrive. No background thread or
timer is installed. Active operations are not deleted underneath their I/O; a
patch crossing its expiry rolls back, and the next cleanup can reclaim it.

## Recovery, completion and publication

PATCH writes directly to the private data file under exclusive per-upload
mutation ownership. It flushes file bytes before atomically replacing the small
committed-offset record. Only then does it acknowledge the new offset. A canceled,
short, oversized or checksum-failing PATCH rolls back to its previous offset.
The input and application destination streams remain owned by their callers.

Reopening the store validates bounded records and storage budgets, truncates
uncommitted data tails and removes uncommitted creation/record artifacts. A
committed record with missing/short data, invalid format, unexpected files or
reparse points fails closed. Lowering configured budgets below recovered usage
also fails closed; retain the previous settings to reconcile/delete uploads
before lowering them. A store that suffers a metadata I/O failure stops new
operations until reopened, so an uncertain commit is never overwritten using an
assumed offset. This covers process interruption on a supported local filesystem;
it does not promise survival of power loss, disk corruption, hostile local
writers or network filesystem semantics.

`OnCompleted` runs after the first durable transition to complete, including an
empty upload created by POST. It receives the current authenticated context and
an immutable `TusUpload` snapshot. It is awaited before a successful response.
A callback exception produces an error but leaves the upload complete; HEAD
reveals its committed state. Empty PATCH retries and startup do not automatically
repeat the notification. A process can stop between commit and notification, so
this callback is **not an exactly-once or durable job-delivery guarantee**.

Make publication idempotent using `upload.Id`. Maintain the application's trusted
owner scopes and reconcile `store.GetUploads(owner)` after restart or on the
owner's next visit. For a complete snapshot, use
`store.CopyCompletedToAsync(upload.Id, owner, destination, cancellationToken)` to
copy bytes into an application-owned private staging stream. Validate file type,
content and any application-wide digest there, then atomically publish the
validated result using application-specific rules. A callback must preserve the tus response status/headers and must not write an HTTP
response body. It must await its copy/validation and must not retain the request context or launch unawaited work.
Use `store.Delete(id, owner)` after successful durable publication to reclaim the
session. Requests replayed after deletion receive 404, so persist the application's
completion/result mapping separately where the client needs a durable receipt.

## Validation and fuzzing

The 89 regression cases and shared Framework/Native AOT smoke checks cover protocol negotiation, metadata/header validation,
owner/effective-method authorization, global and per-owner quotas, checksums,
cancellation/overlap, completion failures, fixed expiry, recovery faults and owned
HTTP/1.0, HTTP/1.1, HTTP/2 and HTTP/3 transfers.

The `tus-uploads` state machine runs three in-process HTTP hosts borrowing one
disk store. An independent owner/byte ledger checks concurrent append admission,
committed offsets, exact final file bytes, foreign-owner denial, stale offsets,
checksum rollback, quota accounting, deletion and reopen recovery with injected
uncommitted tails. It is an application-surface model; wire transports have
separate regressions. Its negative oracle must reject an injected wrong offset.
A separate owned child-process fixture is killed while the store is open and
must recover the acknowledged offset and exact resumed bytes.

```powershell
dotnet run --project test/EmbedIO.Fuzz -c Release -- --tus-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release -- --tus-recovery-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/tus-uploads.json
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines 1729 500 tus-uploads
```

Every tus model trace also probes a generated safe-verb/unsafe-override pair
through browser protection, asserting rejected requests preserve storage and a
valid session-bound token permits the intended operation. See the
[adversarial security audit](../project/tus-security-audit.md) for scope, findings
and deployment assumptions.

CI runs both self-tests, the retained replay and the shared-resource campaign on
Windows and Linux. Traces, transition counts and runtime/source provenance are
retained for replay. Ordinary requests have no added middleware, storage access
or module allocation unless the application installs the upload module.
