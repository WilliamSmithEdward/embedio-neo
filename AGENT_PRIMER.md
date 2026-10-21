# EmbedIO-Neo primer for AI agents

This is a primer for an AI agent that builds an application on EmbedIO-Neo v2.
It states the rules and APIs an agent needs to write correct code the first
time, and links to the guides that hold the detail. It is not a contributor
guide; see [CONTRIBUTING.md](CONTRIBUTING.md) for changing the library itself.

Library development must run the pinned source-quality checks described in
CONTRIBUTING after the final edit. They reject prettyCSharp formatting issues,
informational diagnostics and higher severities, and incomplete scans; hidden
hints are report-only. These are contributor checks, not application runtime requirements.

Every API named here exists in the v2 source. When this primer and a guide
disagree, the guide and the source win.

Request-abort cancellation requires 2.9.0+. Opt in with
`context.GetRequestAbortedToken()` or a `[RequestAborted] CancellationToken`
controller parameter; existing context tokens and bare parameter binding are
unchanged. HTTP/1 FIN remains a valid half-close by default. Explicit
`CancelOnHttp1InputEnd = true` is appropriate only for clients that will not
half-close before reading. Pass the token to streaming, upload and application
work; retain deadlines because disconnect detection is best effort. Generated
bindings support AOT and omit the token from OpenAPI. The in-process testing
context observes its caller token. See [request-abort cancellation](docs/guides/request-abort.md)
for completion ownership, HTTP/2 and HTTP/3 resets, TLS/proxy limits and examples.
Canceled request-abort tokens keep their cancellation source until the token can
be collected, so retained-token registrations and signaled wait handles remain
usable. Parent registrations still detach at completion.
The full 5,000-case nightly shared-state campaign has a three-hour job allowance
for physical connection waits; ordinary jobs remain 45 minutes. Individual
15-second case deadlines and five-second cleanup grace remain unchanged.
Request-abort fuzz artifacts retain the outstanding wait and token state on a
case deadline; retained traces may depend on runtime scheduling and are not
necessarily deterministic reproductions.

### Typed minimal endpoints (2.16.0+)

Use `EmbedIO.Endpoints` for opt-in exact MapGet/MapPost/MapPut/MapPatch/MapDelete/
MapHead/MapOptions routes, MapMethods and nested MapGroup prefixes. Existing OnGet
prefix callbacks and controllers retain their behavior. GeneratedEndpointBinding
on a static partial class creates MethodEndpoint descriptors for its public static
handlers; pass those to MapGet for reflection-free/AOT registration. Delegate/method
group overloads compile bindings through reflection at registration and require
an untrimmed runtime. Keep helper methods private and handler names unique.

Reuse typed request binding, Validate, HttpResults, explicit JSON/multipart and
streaming contracts. Framework context/request/response/route/principal and bare
CancellationToken parameters are request-owned; the token is context.CancellationToken,
not implicit disconnect detection. FromServices uses the optional DI package's
request scope. JSON body context and response serializer metadata are configured
separately. Captured delegate targets are application-owned, shared and never disposed.

Configure authentication, CORS, rate limits and DI before the first server.Map* call,
which installs a root endpoint module at that position. Unknown paths pass through.
Policies and hooks freeze with Compile, AddEndpoints, Links or startup; configure
all children first. Group authorization requirements combine with AND; AllowAnonymous
is explicit, and RequireAuthorization restores accumulated requirements. Closest
explicit cache/idempotency/browser/security-header selection wins. Use the existing
module policy registries and restrictions. Literal routes precede parameter routes;
avoid ambiguous parameterized templates. GET does not imply HEAD. WithName supplies
safe named links and operation IDs. AddEndpoints reads the effective OpenAPI contract.
Before/after callbacks are fluent endpoint hooks, not controller BeforeHandler attributes.
See [minimal endpoints](docs/guides/minimal-endpoints.md) for complete generated/AOT
code, ownership, override order, fuzzing, startup failures and latency experiments.

### Typed request binding (2.15.0+)

Use `[QueryObject]` / `[FormObject]` for flat mutable DTOs, `[RequestField]` for
aliases/required presence/duplicate policy, and `[HeaderField]` / `[CookieField]`
for explicit parameters. `[RouteField]` opts custom TryParse IDs into invariant
route parsing; implicit route conversion is unchanged. Missing DTO fields retain
initializers; scalar duplicates reject by default, arrays retain repeated keys,
and empty nullable values become null. Validation remains opt-in `[Validate]`.
Query/form names use ordinal case-insensitive matching; cookies are exact-case
and never URL-decoded.

Limits: 128 fields, 1024 values, 4096 UTF-16 code units/value, 64 KiB decoded UTF-8
(names charged per pair), and 256 KiB encoded form bytes after decompression.
Strict percent/Unicode errors are 400; wrong form media is 415. New FormObject
cannot share an action with legacy form/JSON/multipart body attributes. The parsed
source is read-only and request-local; DTO instances are new. Generated bindings
support Native AOT without DTO JSON metadata and flatten OpenAPI fields. Raw
header/cookie arrays fail OpenAPI generation unless explicitly excluded/documented.
Auth still precedes activation/binding; header/cookie input never establishes
identity or automatically extends cache keys/idempotency fingerprints. Existing
binding APIs/defaults are preserved. See [typed request binding](docs/guides/typed-request-binding.md)
for supported shapes, ownership, errors, examples and concurrent fuzzing.

### Typed HTTP results (2.14.0+)

Main adds root-namespace `HttpResult<T>`, concrete result types and
`HttpResults` factories. These APIs require version 2.14.0+. Only explicit declared
result types opt in; ordinary objects and existing disposal timing are preserved.
Use `HttpResults.Ok(book)` / `NotFound<Book>()` for the common typed base, or
`HttpResult<OkResult<Book>, NotFoundResult<Book>>` (two or three distinct alternatives)
for precise generated OpenAPI metadata. Broad/dynamic results have a default
response fallback; no success status is invented. Payloads honor the module
serializer; supply source-generated options/JsonTypeInfo for AOT. Task and ValueTask
results await execution once and retain controllers until completion.

`NamedRoutes.FromController(baseRoute, binding)` names routes using OperationId
or ControllerName.MethodName; duplicate aliases need explicit names/templates.
`CreatedAt` generates escaped root-relative Locations with invariant values and
never trusts request Host/forwarding headers. File paths are trusted application
input. Stream results transfer ownership unless leaveOpen, execute once and must
be disposed if abandoned, including the SelectedResult of a typed alternative. Files/streams reuse range/conditional handling, bypass
cache publication and reject idempotent recording; finite results support both.
Security policies and nonce no-store/replay restrictions remain authoritative.
See [typed results](docs/guides/typed-results.md) for helpers, limits, lifecycle,
request-abort/producer cancellation, OpenAPI and testing contracts.

### Typed errors and validation (2.14.0+)

Version 2.14.0 adds BadRequest(problem), NotFound(problem), Conflict(problem) with
fixed 400/404/409 statuses; problem.Status must match. ValidationProblem accepts
copied string-array field maps or DataAnnotations ValidationResult sequences and
returns a 400 errors object. Limits are 1024 fields/messages/results, 1024 path
characters, 4096 message characters and 64 KiB UTF-8 field/message text. Empty
sets, null/empty arrays, blank messages, invalid UTF-16 and excessive inputs fail.
Success annotations are ignored; unnamed errors use an empty path. It does not run
validators or change automatic [Validate]. These additions require version 2.14.0+.
Generated OpenAPI describes concrete problem/validation schemas without JSON
metadata for error classes; explicit response annotations win. Shared general/
validation statuses use a general schema. Existing dynamic Problem and empty
factories remain unchanged. Explicit problems bypass exception-handler customization
and HTML negotiation as before. GetProblem and GetValidationErrors in Testing
unwrap alternatives; snapshots cannot mutate future executions. Security policies,
HEAD, idempotency and normal cache rules remain unchanged. See
[typed errors](docs/guides/typed-error-results.md) for contracts and examples.

### Response security headers (2.13.0+)

Version 2.13.0 adds opt-in `EmbedIO.Security.SecurityHeadersPolicy`,
`SecurityHeadersModule` and `[SecurityHeaders("name")]`. These APIs require
NuGet 2.13.0+. Configure server `options.SecurityHeaders` or configure copied named
controller profiles before registration. Method attributes override inherited class
attributes and module defaults. A selected profile replaces all seven managed fields,
including omissions; keep host-wide HSTS consistent across profiles. Static values and rendered-size bounds are
precomputed, including nonce expansion. Existing defaults/manual headers are unchanged until explicitly selected.

Use exact CSP source `'nonce-{nonce}'` and `context.GetSecurityHeadersNonce()` for
trusted, attribute-encoded script/style markup only. Each response has fresh 256-bit
randomness. Nonce profiles force no-store, remove validators, bypass existing output
cache entries and reject partial/304 representations and idempotent recording.
Select policies before cache wrappers; selecting inside a skipped cached handler
cannot affect a hit. External caches must honor no-store. HSTS requires explicit
lifetime and actual TLS; forwarded text/simulated test URLs do not establish TLS.
First-party listeners and TestWebServer prepare final headers automatically; external
adapters must call `SecurityHeadersPolicy.PrepareResponse` immediately before copying
headers to their transport. See [security headers](docs/guides/security-headers.md)
and the [scoped adversarial audit](docs/project/security-headers-audit.md).

### Output caching (2.10.0+)

`OutputCache` in `EmbedIO.Caching` is explicitly opt-in through a fluent wrapper
or `[OutputCache("policy")]` plus `ConfigureOutputCaching`. Generated bindings
support the same policies. Authorization and hooks run on every hit; argument
binding, action execution and serialization do not. Uncached defaults and legacy
controller disposal timing remain unchanged. Opted-in actions use awaited
controller release after serialization and before cache publication; release
faults publish nothing and cleanup runs once. Sensitive bypasses retain the base
client no-store behavior, and hits preserve current supported Vary fields. Explicit no-store, credentials, cookies,
streams, conditions, ranges, failures and canceled/oversize fills bypass storage.
Always declare all representation-selecting request headers. Data remains a
snapshot until TTL or explicit tag invalidation after a successful data commit;
invalidation fences older in-flight fills but is not transactional consistency.
Storage and capture concurrency are bounded. HTTP/3 GETs with unknown body
length bypass caching; an explicit zero Content-Length permits reuse. See the
[output-caching guide](docs/guides/output-caching.md) for exclusions, ownership,
key dimensions, limits, fuzzing and real-loopback latency measurement.

### Resumable uploads (2.12.0+)

`EmbedIO.Tus.TusUploadModule` and application-owned `DiskTusUploadStore` add opt-in
tus 1.0 creation/HEAD/PATCH/DELETE endpoints in 2.12.0+. NuGet 2.11.0 lacks these APIs.
Install authentication/security middleware before the module and require a stable
trusted tenant/issuer/subject owner selector on every request. Its second argument
is the effective method, including `X-HTTP-Method-Override`; authorize that method.
Controller authorization attributes do not protect this separate module.

Use a dedicated private local directory outside static mounts, with host-only OS
permissions and protected parent directories. One store exclusively owns it;
modules may borrow that instance. Dispose after all hosts stop. Fixed-length
uploads reserve their full length at creation; configure global/per-owner byte
and session caps, per-PATCH bytes and active participants. Set client chunk sizes
within the patch cap. Defaults: 1 GiB/upload, 4 GiB total, 128 sessions,
16 sessions/1 GiB per owner (bounded by global caps), 16 MiB/PATCH, four active
operations per store/participants per module and 24-hour fixed expiry. Completed
files expire too. Periodically call `RemoveExpired()`; creation/reopen also clean
expired idle sessions. No background maintenance is installed.

HEAD returns the durable committed offset; use it after any ambiguous PATCH
response. Recovery truncates uncommitted tails and fails closed for damaged
committed records or incompatible lower budgets. Failed/checksum-mismatched
patches roll back. Checksum support is sha1 for tus interoperability and sha256;
these are not authentication. Creation-with-upload, deferred lengths,
concatenation and checksum trailers are not advertised.

`OnCompleted` is awaited after the first durable completion transition, including
empty creation. It is not durable/exactly-once job delivery: crashes can lose a
notification, and callback failure leaves completed bytes. Reconcile
`GetUploads(owner)` using application-maintained owner scopes and idempotent
publication keyed by upload ID. Copy completed bytes with `CopyCompletedToAsync`,
validate privately and publish using application rules. Client metadata never
names a store path. Both targets retain existing APIs/defaults/dependencies;
unconfigured requests do not enter this feature. See the
[resumable upload guide](docs/guides/resumable-uploads.md) for protocol, security,
client limits, recovery and fuzz replay. In 2.12.0+, browser protection also checks
method overrides before exempting safe verbs or WebSocket handshakes from CSRF
tokens; a safe override cannot exempt a physical unsafe method. See the
[adversarial audit](docs/project/tus-security-audit.md). This correction requires
2.12.0+.

### Write idempotency (2.11.0+)

`EmbedIO.Idempotency.IdempotencyCoordinator` and immutable `IdempotencyPolicy` protect
explicit finite write routes through `Protect`, `ExecuteAsync`, or named
`[Idempotency("policy")]` / `ConfigureIdempotency` controller declarations.
Method attributes override classes; null disables inheritance. Both reflection
and generated bindings work. Configure before registering controllers.

Always resolve a stable trusted caller/tenant/issuer scope after authentication;
never trust a client-supplied identity header, display name or idempotency key.
Keep partitions and caller-scope rules stable through the retry window; changing
the namespace resets lookup protection and requires deliberate reconciliation.
Every protected request requires one bounded `Idempotency-Key`. Retry the same
logical operation with identical transferred bytes and declared representation
headers. A mismatched fingerprint is 409. Authorization/browser guards execute
on every retry; activation, hooks, binding, action and cleanup execute only for
the owner. Put per-request checks in guards, not owner-only hooks.

The finite response is staged and recorded before network delivery. Uncertain
handler/storage outcomes stay blocked instead of automatically executing again.
Capacity exhaustion is 503, not early eviction. Active claims never expire;
terminal records expire lazily after retention. Defaults bound participants,
request/response bytes, metadata and store reservations. Streaming, sessions,
cookie changes and completion callbacks are unsupported inside staged work.
Await all cleanup. Unconfigured routes retain their existing behavior.

The default `MemoryIdempotencyStore` is process-local; expiry, restart or store
replacement removes protection. Optional `IIdempotencyStore` implementations must
provide atomic acquisition and owner-token-fenced completion/failure. Durable
claims alone do not transact business effects. Reconcile uncertain operations;
do not blindly choose another key or wait for expiry. See the
[idempotency guide](docs/guides/idempotency.md) for the precise store contract,
limits, failure matrix, concurrency fuzzing and cross-target support.

## 1. What you are building on

| Item | Value |
| --- | --- |
| NuGet packages | Core: `EmbedIO-Neo`; optional: `EmbedIO-Neo.DependencyInjection`, `EmbedIO-Neo.Jwt`, `EmbedIO-Neo.JsonServer`, `EmbedIO-Neo.Cli`, `EmbedIO-Neo.Testing` |
| Assembly and namespaces | `EmbedIO`, `EmbedIO.WebApi`, `EmbedIO.Routing`, `EmbedIO.WebSockets`, `EmbedIO.Files`, `EmbedIO.Security`, `EmbedIO.Net` and others |
| Targets | `net10.0` and `netstandard2.0` |
| Current source version | 2.16.0, the v2 engine. Typed minimal endpoints require 2.16.0+. Typed request binding requires 2.15.0+. Typed results and validation/error helpers require 2.14.0+. Response security headers require 2.13.0+. Resumable tus uploads and method-override browser protection require 2.12.0+. Write idempotency requires 2.11.0+. Output caching requires 2.10.0+. Request validation requires 2.4.0+. Route hooks and enum status overloads require 2.3.0+. Streaming multipart requires 2.1.0+; bearer authentication and controller authorization require 2.2.0+. 1.0.x is the previous major version. |
| Install | `dotnet add package EmbedIO-Neo` |

Check which version the project references before you write code. A 1.0.x
package lacks the v2 engine, HTTP/2, HTTP/3 and every API the guides mark as
new in Neo v2.

The `net10.0` asset is the full product. The `netstandard2.0` asset has no
HTTP/3, no HTTP/2 over TLS (no ALPN), no telemetry, no Brotli and no TLS
certificate selector. Target `net10.0` unless you must support an old runtime.

## 2. A complete working server

This program runs as written. It serves a JSON API through a controller, a
WebSocket echo endpoint and a static folder, with explicit request limits.

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
Directory.CreateDirectory(webRoot);

using var server = new WebServer(options => options
        .WithUrlPrefix("http://localhost:9696/")
        .WithMaximumRequestBodyBytes(1_048_576)
        .WithRequestTimeout(TimeSpan.FromSeconds(30))
        .WithMaximumConcurrentRequests(256))
    .WithWebApi("/api", api => api.WithController<ItemsController>())
    .WithModule(new EchoSocket("/ws"))
    .WithStaticFolder("/", webRoot, true);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

public sealed class ItemsController : WebApiController
{
    [Route(HttpVerbs.Get, "/items/{id}")]
    public object GetItem(int id) => new { id };

    [Route(HttpVerbs.Post, "/items")]
    public object CreateItem([JsonData] CreateItemInput input)
    {
        if (string.IsNullOrWhiteSpace(input?.Name))
            throw HttpException.BadRequest("Name is required.");

        HttpContext.Response.StatusCode = 201;
        return new { name = input.Name };
    }
}

public sealed class CreateItemInput
{
    public string? Name { get; set; }
}

public sealed class EchoSocket : WebSocketMessageModule
{
    public EchoSocket(string path)
        : base(path)
    {
        MaxMessageSize = 65_536;
    }

    protected override Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
        => SendAsync(context, text);
}
```

Try it: `GET http://localhost:9696/api/items/42` returns `{"id":42}`.

## 3. Server construction and lifetime

- Build with `new WebServer(options => options.WithUrlPrefix(...)...)` or
  `new WebServer("http://localhost:9696/")`. The listener is created in the
  constructor, so listener and TLS configuration errors throw there.
- Configure everything before starting. Options and modules lock when the
  server starts; setting them later throws `InvalidOperationException`.
- `RunAsync(token)` starts the server and completes when it stops. Call it once.
  A stopped server cannot be restarted; create a new instance.
- Stop by cancelling the `RunAsync` token or disposing the server. Open
  WebSockets get a 1001 close and the server waits at most one second for them.
  There is no `StopAsync`.
- `DrainAsync(timeout)` stops accepting work and lets admitted requests finish.
  `DrainStarted` is a token cancelled when a drain begins.
- `server.Start()` runs `RunAsync` in the background and returns once listening.
  Startup failures then surface only through the background task, so prefer
  awaiting `RunAsync`.
- `server.StateChanged` and `server.State` report Created, Loading, Listening
  and Stopped.

## 4. Choosing the protocol

Select with `options.WithMode(HttpListenerMode.X)`:

| Mode | Serves | Requirements |
| --- | --- | --- |
| `EmbedIO` (default) | HTTP/1.1 and HTTP/2 over TCP | HTTP/2 needs TLS with ALPN (`net10.0` asset) or cleartext prior knowledge |
| `EmbedIOHttp3` | HTTP/3 over QUIC/UDP only | `net10.0`, HTTPS prefixes, a certificate with its private key, native QUIC (MsQuic), TLS 1.3 |
| `EmbedIOCombined` | Both of the above on the same HTTPS prefixes | Everything HTTP/3 needs; both bindings must succeed |

- HTTP/2 needs no opt-in in the default mode.
- `HttpListenerMode` value 1 (the old Microsoft listener) is removed and throws.
- Unsupported HTTP/3 platforms throw `PlatformNotSupportedException` at startup.
- There is no automatic Alt-Svc advertisement; clients must be told HTTP/3 is
  available. See the [HTTP/3 guide](docs/guides/http3.md).

WebTransport is not supported by the public listener. The internal session core
and native validation hosts do not make it available to applications. The independent
EmbedIO.Quic generic transport core is under `src/EmbedIO.Quic`. Its separate
`EmbedIO.Quic.Development` host is under `test/EmbedIO.Quic`, references the actual
library HTTP/3 protocol and uses a legacy development OpenSSL loader, the private
OpenSSL 3.5.9 boundary on Unix, or the Windows SDK recordless Schannel
interface for TLS. In this unreleased branch, the .NET 10 library asset compiles the
same internal engine sources; the standalone `EmbedIO.Quic` project remains an
independent build boundary. The host no longer compiles copies of the HTTP/3 codecs. The Windows development profile uses owned non-exportable CNG RSA/ECDSA
credentials and negotiates AES-128-GCM/SHA-256 or AES-256-GCM/SHA-384. The packet core
also implements ChaCha20-Poly1305/SHA-256, exercised through the OpenSSL peer profile;
Windows Schannel does not advertise that profile. Its Schannel implementation compiles in the
.NET 10 library under `Net/Internal/Http3/Tls`, with certificate-source and TLS-policy
integration exercised by the actual WebServer fixture. Windows client-certificate
policies now have direct controller, chain, callback and revocation checks. All three
packet cipher profiles retain their hash and header key across key updates, with
connection-wide authentication-failure limits. Resumption and supported-platform
TLS/certificate parity remain pending. The standalone
core and development host remain nonpackable. Existing package dependency groups
and .NET Standard compilation are unchanged; the public listener still selects
its existing providers. The fuzz harness exercises the library's engine source. It includes
loss recovery, NewReno congestion control and an H3/WebTransport echo adapter with
current reliable-reset framing, reusable receive windows and explicit connection
closing/draining. Receive packet history advances with bounded ACK ranges and a
permanent duplicate-rejection floor. Retired send records and acknowledgement
history also compact within fixed bounds. Per-key encryption budgets (conservatively capped at the AES-GCM limit for
all profiles) and ACK-confirmed local/peer key generations replace the cumulative send-packet quota.
Completed stream records retire after receive/application completion and any required
send acknowledgement; cumulative stream credit replenishes within resident bounds.
The stream core accepts an owner-bound application adapter with explicit byte
consumption and reliable-prefix ownership. The raw multiplexed echo profile
exercises it without HTTP/3 control-stream or WebTransport session assumptions;
its async test-host API adds owned reads/writes, outbound bidi/uni streams,
consumption-driven buffering, cancellation with explicit read-abort codes, FIN-ACK
completion and bounded datagram/open/accept queues. Application commands wake the
UDP owner directly. The Windows TLS factory captures the fallback certificate when
constructing each handshake, then selects and clones the certificate after a complete
ClientHello. It preserves bounded ASCII SNI spelling, calls the configured selector
once and isolates selector failures. Later publication applies to new handshakes;
existing connections keep their original credential. Ordinary library TLS omits
development diagnostic strings. Its Windows path honors `ClientCertificateMode.None`,
`Allow` and `Require`, builds the client-authentication chain using presented intermediates,
and applies the existing validator and revocation option before TLS completion. Supplied
roots are not trusted automatically. Accepted requests use the existing
`Request.GetClientCertificate()` API; certificate copies retain their existing lifetime
independently of native TLS cleanup. Full platform/configuration parity remains pending.
A bounded execution pool now serializes each connection's factory, packet,
timer, application and cleanup work. Its mailbox counts both queued and executing
input toward packet/byte limits; shutdown waits for active native calls. Direct
lifecycle, packet-mutation and native first-flight checks exercise this scheduling
boundary. The shared UDP development host now uses the scheduled dispatcher:
handshake deadlines include queue time, routing is established before sending,
and output limits include queued and in-flight sends. Shutdown waits for native
work and sends, releases sibling owners and reports cleanup failures. The library now
also has an internal socket binding whose acceptance lifetime is separate from UDP
ownership, preserving active traffic during HTTP/3 drain. Ordinary binding or
cancellation-callback failures are reported after session teardown;
connection workers and sibling bindings still receive cleanup. A failed drain clears
the previous session so the listener can restart. Per-instance listener
construction allows the real WebServer pipeline to validate that binding with
development credentials; public provider selection remains unchanged.
The replacement stream/datagram adapters now drive the library's HTTP/3 protocol
from the development UDP host. Ordinary body-read cancellation preserves the
existing distinction between local timeout (`H3_NO_ERROR`) and request cancellation;
CONNECT streams retain their transport cancellation behavior. The new independent
`test/EmbedIO.Quic/verify_http3.py` checks actual HTTP/3 uploads, complete timeout
responses and healthy sibling requests. Its request-lifetime regressions run with
`python -m unittest discover -s test/EmbedIO.Quic -p test_http_peer.py`
in the pinned peer environment. The helper reports an authenticated peer close to
HTTP waiters when draining begins, while leaving the reference engine's drain timer
and connection cleanup intact. `test/EmbedIO.Quic/verify_listener.py` additionally
exercises actual WebServer controllers, bearer authentication and policy authorization,
request-abort cancellation, endpoint metadata isolation, active-response drain and UDP
rebind through the new binding. OpenSSL and Windows non-exportable RSA/ECDSA profiles
passed this development-host validation. It rejects TLS configurations outside its
fixture profile. `test/EmbedIO.Quic/verify_tls_listener.py` additionally verifies
SNI selection, authenticated certificate publication, strict trust/hostname rejection,
and healthy existing connections through the library's Windows TLS implementation.
`scripts/run-quic-development.py --schannel` includes Windows listener/policy checks
and native RSA/ECDSA mutations. It also runs the client-certificate wire matrix and seeded
full-handshake certificate/key mutations through `verify_client_certificate.py` and
`fuzz_client_certificate.py`. These include a continuing healthy connection and retained
input/fixture replay. Public WebTransport and complete credential/configuration parity
remain pending before changing the public listener default.
A provider-neutral TLS boundary caches handshake state and
releases packet keys with its connection. Bounded ClientHello framing and transport
parameter extraction have property, deterministic and coverage-guided fuzz coverage. Production
configuration and full TLS/certificate parity remain pending.
The source core owns packet processing and destination-ID routing through
`QuicConnection` and `QuicConnectionDispatcher`, with injected TLS/application factories
and validated internal stream-count/idle configuration. Scheduled factories receive
the connection owner and copies of the local/remote endpoint metadata; the async stream
application uses its coalescing wake callback
for application commands, so outgoing work need not wait for a peer packet or periodic timer.
The shared development host also runs owned async stream handlers on that path.
All development protocols use the same stream owner. The synchronous registry is retained only in the
development test host as a deterministic reference. Registry shutdown releases all sibling owners before
reporting ordinary cleanup failures. Direct managed fuzzing exercises both owners
without loading native TLS; independent peers validate the native path. The bounded
development socket listener supplies admission limits, handshake deadlines and
cancellation across multiple connections. Async accept/owner bounds follow the validated
transport stream capacities, including the production 128/16 profile. Receive windows
rent pages as data arrives and clear/return them after consumption, read abort,
reliable-reset suffix discard, retirement or connection shutdown; empty streams do
not reserve full payload windows. Discard retains offset/final-size validation and
keeps application-delivered bytes under explicit consumption ownership. It has
no public application API; the intended fully validated
replacement will remove MsQuic from the production transport, but the current
HTTP/3 provider remains in place. The [development and testing record](docs/project/quic-development-and-testing.md)
tracks all fuzz engines, extensive local runs and remaining production acceptance. The Windows
validation helper checks native lifetime and a bounded first-flight matrix against
read-only host TLS-group policy. P-256 must succeed; X25519 must succeed when enabled
and reject without keys when disabled. Missing policy evidence or unexpected outcomes
fail the gate; validation never changes machine policy. Shared HTTP lifecycle fuzzing distinguishes
expected stopped-client disconnects from response failures and cleanup deadlines.
Once the replacement is complete, remove MsQuic-specific quarantines/workarounds
and review every open Neo issue; close issues only after validating their resolution.
See [its scope and reproduction steps](docs/project/quic-engine-feasibility.md)
and [WebTransport readiness](docs/project/webtransport-upstream-readiness.md).

## 5. HTTPS and TLS

```text
options.WithUrlPrefix("https://*:8443/").WithCertificate(certificate)
```

- An `https` prefix without a certificate or certificate source throws before
  the server starts. The certificate needs its private key.
- TLS 1.2 is the minimum; TLS 1.0 and 1.1 clients cannot connect.
- Rotate certificates with `EmbedIO.Net.ServerCertificateSource`: pass it to
  `WithCertificateSource(source)` and call `source.Publish(newCertificate)`.
  Configure a source or a fixed certificate, not both.
- Client certificates, protocol versions and SNI selection go in
  `EmbedIO.Net.ServerTlsPolicy` through `WithTlsPolicy(policy)`.
- Two servers sharing one TCP endpoint must present the same certificate and TLS
  policy, or startup throws. HTTP and HTTPS cannot share an endpoint.
- The store helpers (`WithCertificateThumbprint`, `WithAutoLoadCertificate`) are
  Windows-only.

Guides: [HTTPS](docs/guides/https.md), [TLS policy](docs/guides/tls-policy.md),
[certificate rotation](docs/guides/certificate-rotation.md).

## 6. Modules and request flow

A server is a pipeline of modules. Order is the single most common source of
bugs.

- Modules run in registration order. Each is skipped unless the request path is
  under its base route. Dispatch stops at the first module that handles the
  request. If none does, the response is 404.
- Final handlers end dispatch: `WithWebApi`, `WithStaticFolder` (and the other
  file modules) and WebSocket modules.
- Middleware does not end dispatch unless it rejects the request:
  `WithCors`, `WithBasicAuthentication`, `WithIPBanning`, bearer authentication,
  forwarded headers and rate limiting.
- Register middleware first, then APIs and WebSockets, then specific file
  mounts, then any catch-all static folder at `/` last.
- `OnGet("/x", handler)` and the other `On*` shortcuts match a base path,
  including everything beneath it. Use a controller for exact routes.
- Module registration: `WithModule(module)`, `WithWebApi(baseRoute, configure)`,
  `WithStaticFolder(baseRoute, path, isImmutable)`, `WithEmbeddedResources`,
  `WithZipFile`, `WithCors`, `WithBasicAuthentication`, `WithIPBanning`,
  `WithLocalSessionManager`.
- Modules with no `With*` helper (bearer authentication, rate limiting, forwarded
  headers, client banning) are added with `WithModule(new ...Module(...))`.

## 7. Routing and Web API controllers

- Derive from `WebApiController`. Mark methods with
  `[Route(HttpVerbs.Get, "/items/{id}")]`. The module's base route is prefixed:
  `WithWebApi("/api", ...)` plus `/items/{id}` serves `/api/items/{id}`.
- `{id}` requires a non-empty segment; `{id?}` allows an empty one. Query strings
  are not part of route matching.
- `[BaseRoute(verb, "/files/")]` matches a prefix; the remainder is in
  `Route.SubPath`. A base route must end with `/`; a normal route must not.
- Parameter binding: a binding attribute, else the route parameter with the same
  name (converted to the parameter type), else the parameter default.
  - `[QueryField]`, `[QueryField(true)]` (required, 400 if missing)
  - `[FormField]`, `[FormData]`, `[QueryData]`
  - `[JsonData]` deserializes the body; invalid JSON is a 400
  - `[FromServices]` (DI package)
  - `[MultipartForm] MultipartReader` (2.1.0+, streaming; see below)
- Return an object to send JSON with status 200. Return `Task<T>` for async work.
  Return `void` or `Task` for no body. `ValueTask` is not awaited; do not use it.
- Set `HttpContext.Response.StatusCode` before returning to change the status.
- Throw `HttpException.BadRequest(message)`, `NotFound()`, `Unauthorized()`,
  `Forbidden()` or `MethodNotAllowed()` for HTTP errors.
- In 2.2.0+, use `[Authorize]`, `[Authorize("policy", Roles = "editor,admin")]`
  and `[AllowAnonymous]` from `EmbedIO.Authorization`. Configure named policies
  before controller registration and register authentication before the API.
  Authorization runs before controller activation, preprocessing and body binding.
  `OnBeforeHandler()` remains available for application-specific checks.
- In 2.3.0+: `[BeforeHandler(nameof(Prepare))]` on a controller or
  action selects a public instance `void`/`Task` hook (zero parameters or one
  `CancellationToken`). `Append` runs `OnBeforeHandler()` then the named hook;
  `Replace` runs only the named hook; `[BeforeHandler(Behavior =
  BeforeHandlerBehavior.Skip)]` runs neither. The action attribute replaces the
  class selection, including inherited defaults. Hooks run after authorization
  and activation, before body/parameter binding. They cannot bypass authentication,
  authorization or earlier modules. Unannotated controllers behave as before.
  The default controller hook disables response caching; Replace/Skip omit that
  default, so set an appropriate cache policy explicitly. Hooks are awaited;
  use request-aware activation/release for dependencies needed across the action.
- In 2.3.0+: `HttpStatusCode` overloads cover response helpers,
  redirects, standard HTML, informational sections, problem details and OpenAPI
  responses. Use `Response.SetStatusCode(HttpStatusCode.Accepted)` to avoid a
  cast; `Response.StatusCode` remains an `int`. Existing integer APIs and their
  validation still apply, including custom numeric statuses where supported.
- Controllers need a public parameterless constructor for
  `WithController<T>()`, or use `WithController<T>(() => new T(...))`.
- `CaseInsensitiveRoutes` on the Web API module enables case-insensitive literal
  route text. Set it before registering controllers.

Guides: [controllers](docs/guides/getting-started/controllers.md),
[routes and parameters](docs/guides/getting-started/requests.md),
[base routes](docs/guides/controller-base-routes.md),
[route hooks and enum statuses](docs/guides/route-hooks.md) (2.3.0+).

Generated bindings carry multipart and authorization metadata as well as routes.
The OpenAPI builder describes `multipart/form-data` request bodies and can derive
bearer requirements with `AddBearerAuthentication(schemeName, module)`. Register
the real module before protected endpoints; an OpenAPI security declaration alone
does not enforce access. Streaming multipart metadata cannot infer part names or
file types. Guide: [OpenAPI documents](docs/guides/openapi.md).

### Request validation (2.4.0+)

- Add `[Validate]` to an action parameter to enable DataAnnotations rules on it
  and its declared model's public properties/typed collection elements. Existing
  unannotated actions keep their behavior. Authorization/hooks precede validation.
- Use `[Required]` on the parameter to reject null. Invalid input throws a 400
  ProblemDetailsException with CLR field paths; configure WithProblemDetails with
  HtmlHttpExceptionHandler = null for consistent JSON errors. Custom handlers remain
  in control. The action is skipped and existing controller cleanup still runs.
- Limits default to depth 16, 1024 nodes and 32 errors; exceeded traversal is a 400,
  not silently accepted input. Cycles stop and shared children retain each path.
  Parsing/body limits are separate. Validators must be synchronous/thread-safe.
- Custom attribute usage/multiplicity and overridden action-parameter validation
  annotations follow CLR inheritance in generated and reflection bindings.
- Generated/AOT binding emits direct accessors and cached rules; no per-request
  reflection. EMBEDIO015 reports unsupported settings/models/reflection-dependent
  rules. OpenAPI overlays request-only constraints; string minimums conservatively
  account for UTF-16 versus Unicode code-point length and retain exact extensions.
- .NET Standard 2.0 adds pinned System.ComponentModel.Annotations 5.0.0; .NET 10
  uses built-in annotations. Keep the guide, fuzz campaign and AOT smoke current.
  Guide: [request validation](docs/guides/request-validation.md).

## 8. Reading requests

- Helpers on the context: `GetRequestBodyAsStringAsync()`,
  `GetRequestBodyAsByteArrayAsync()`, `GetRequestDataAsync<T>()` (JSON),
  `GetRequestFormDataAsync()` and `GetRequestQueryData()`.
- **Read the body once and keep the result.** The helpers dispose the request
  stream. A second read fails or returns nothing, depending on protocol and
  version. Do not mix `[JsonData]` with a manual body read in the same request.
- A body can have unknown length: chunked uploads are decoded and
  `ContentLength64` can be -1. Read to the end instead of trusting the length.
- Compressed request bodies are decoded only when `WithSupportCompressedRequests(true)`
  is set. If you enable it, also set a finite decompressed-size limit.
- Read the body before the response completes, and never read the same request
  stream from two tasks at once.

### Streaming multipart uploads (2.1.0+)

- Use `using var reader = context.OpenMultipartReader(options)` or bind a
  controller parameter with `[MultipartForm] MultipartReader reader`.
- Await `ReadNextSectionAsync()` and read each section's `Body` sequentially.
  The reader streams bytes; your code owns destination streams and staging files.
- Set `MultipartOptions` or attribute limits for total/file/field bytes, parts,
  files, fields and section headers. Combine them with transport limits and
  admission control; per-request limits do not bound aggregate disk usage.
  Defaults include 128 MiB total, 64 MiB per file, 64 KiB per field, 128 parts,
  32 files, 128 fields and 16 KiB / 32 headers per part. Exceeded limits return
  413, malformed multipart returns 400 and other outer content types return 415.
- Only a null result from `ReadNextSectionAsync()` confirms complete validation.
  Stage uploads privately and publish them after that result; delete staged data
  on failure or cancellation. Do not use a supplied filename as a storage path.
- Disposing a section body or returning after the first file does not validate
  the remaining input. Do not mix multipart parsing with another body reader.

Guide: [streaming multipart uploads](docs/guides/streaming-multipart.md).

## 9. Writing responses

- `SendDataAsync(obj)` writes JSON. `SendStringAsync(text, contentType, encoding)`
  requires an explicit encoding; use `WebServer.Utf8NoBomEncoding`.
- `Redirect(location, statusCode)` accepts only 3xx statuses.
- Set status, headers and cookies before the first body write or flush. After
  headers are sent they cannot change.
- Response text is validated. `StatusDescription` with a control character throws
  `ArgumentException`; a header or cookie value with a control character throws
  `InvalidOperationException` when the response is written. Never copy request
  input into headers without validating it.
- `StatusCode` must be 200-599. Send 1xx through `SendInformationalAsync`; see
  [response field sections](docs/guides/response-field-sections.md).
- Cookies: `Response.SetCookie(cookie)` or `SetCookie(cookie, CookieSameSiteMode.Lax)`.
  `CookieSameSiteMode.None` requires a Secure cookie.
- Dynamic responses are compressed automatically when the client accepts it. On
  `net10.0`, Brotli wins ties with gzip.
- Server-sent events: `var events = context.OpenEventStream();` then
  `events.WriteAsync(data, eventName)`. See [progress events](docs/user-reports/progress-events.md).
- An unhandled exception becomes an empty 500. Use
  `server.WithProblemDetails()` (namespace `EmbedIO.Problems`) for RFC 9457
  JSON errors. See [problem details](docs/guides/problem-details.md).

### Browser request protection (2.7.0+)

These APIs require 2.7.0+. Own one
`EmbedIO.Security.BrowserRequestProtection` for a deliberate security scope,
constructed with immutable `BrowserRequestProtectionOptions`: explicit physical
server origins, optional trusted browser origins, and a token lifetime (20 minutes
by default, at most one day). Share it with token issuance and protected modules;
stop its users before disposal. No policy is enabled automatically.

Configure `WebApiModule.ConfigureBrowserProtection(protection)` before controllers.
It guards every route after authorization and before activation/hooks/body binding.
`[BrowserProtection]` requires unsafe-method tokens; a method overrides its inherited
class setting. `[BrowserProtection(BrowserProtectionMode.IgnoreToken)]` exempts only
tokens, retaining host/origin checks. Reserve exemptions for independently secured
non-browser routes. `AllowAnonymous`, bearer header presence and hook Skip do not
exempt token checks. Generated/reflection bindings preserve the same metadata.

Issue through a GET endpoint with `protection.GetToken(HttpContext)` after the
appropriate authentication/authorization. It uses the configured session manager,
returns a 119-character token and marks the response no-store. The browser sends
`X-CSRF-Token` for unsafe requests; URLs/bodies/cookies are not searched. GET, HEAD
and OPTIONS must remain non-mutating. Supplied origins must be singular and trusted;
WebSocket handshakes require Origin. Register `BrowserRequestProtectionModule`
before non-controller handlers/upgrades and before CORS short-circuiting as needed.

Tokens bind the current session and authenticated identity, including the type,
value, issuer and original issuer of the configured name and name-identifier claims.
Identical subjects from different issuers cannot reuse tokens in the same session.
Tokens expire independently of session activity and fail after policy replacement
or restart. Regenerate the session at login/logout, then fetch a new token. Regeneration resolves a cookie-backed
session even before other session access; coordinate already active requests
separately. This is not authentication, authorization caching, XSS protection or a
distributed token store. Forwarded origins are never trusted automatically. Session
store capacity remains application-owned. See the complete client/server example,
origin rules, ownership/bounds and limitations in
[browser request protection](docs/guides/browser-request-protection.md).

### Bounded SSE channels (2.6.0+)

`ServerSentEventHub` requires 2.6.0+. Own one hub per channel/security scope and return `hub.Stream()` (or
`Task<ServerSentEventStream>`) from a controller. Existing authorization runs
before subscription/replay, and owned controller disposal awaits the stream.
`Publish(data, eventName)` returns an opaque ID. Reconnects use `Last-Event-ID`;
missing history returns 409 and requires an application snapshot/resync. Replay
is bounded, in-memory and lost on hub replacement/restart, not acknowledged or
exactly-once delivery. Queues are bounded; overflow ends that subscriber.
`Complete()` drains; `Dispose()` discards. Direct `Subscribe()` users must dispose
their subscription. Ordinary SSE writers/default listeners remain unchanged.
Native EventSource cannot set bearer headers; use an authenticated fetch client
or the application's established cookie policy. Never put bearer tokens in URLs.
See [SSE event channels](docs/guides/sse-event-hub.md) for all default budgets,
heartbeat/disconnect limits, controller/browser examples and fuzzing.

## 10. JSON

- The serializer is `EmbedIO.Serialization.Json` over System.Text.Json, with
  compatibility settings: case-insensitive names, public fields included,
  quoted numbers, trailing commas, and properties with private or protected
  setters bound.
- **Over-posting.** Because public fields and non-public setters are bound,
  a request body can set any of them. Never deserialize request JSON straight
  into a domain or persistence type. Use a dedicated input type that contains
  only client-settable members, or mark the rest `[JsonIgnore]`.
- Customize: `WithWebApi("/api", ResponseSerializer.Json(options), ...)` for
  responses; `GetRequestDataAsync(RequestDeserializer.Json<T>(options))` for
  requests. `[JsonData]` always uses the library defaults.
- A fresh `new JsonSerializerOptions()` is strict standard behavior, not the
  compatibility behavior.

Guides: [JSON compatibility](docs/user-reports/json-migration-compatibility.md) and [over-posting](docs/user-reports/json-migration-compatibility.md#over-posting-and-the-binding-surface).

### Streaming JSON (2.5.0+)

Streaming JSON requires EmbedIO-Neo 2.5.0 or later. Return
`JsonStream.Array(source, itemTypeInfo)` as `JsonArrayStream<T>` or
`JsonStream.Ndjson(source, itemTypeInfo)` as `NdjsonStream<T>`; `Task` results
are also supported by generated/reflection bindings. Owned controllers stay
alive through streaming cleanup; non-streaming disposal timing and serializers
are unchanged. Use generated item metadata for AOT. Defaults are a 1 MiB
per-record limit, flush after each record and no compression preference.
The sequence is not collected; producer cancellation is cooperative and a
remote disconnect may only be detected by a later write. After commitment,
failures interrupt the response rather than appending an error or a successful
ending. Clients must handle incomplete arrays/streams. See
[streaming JSON](docs/guides/streaming-json.md) for a complete program,
client examples, NDJSON schema conventions and buffer/lifetime limits.

## 11. WebSockets

- Derive from `WebSocketMessageModule(path)` and override
  `OnTextMessageReceivedAsync` and/or `OnBinaryMessageReceivedAsync`. Messages are
  complete, delivered one at a time per connection, and text is strict UTF-8.
  Unhandled message types close with 1003, invalid text 1007, oversize 1009, a
  callback failure 1011.
- The lower-level `WebSocketModule` delivers raw frames to
  `OnMessageReceivedAsync`.
- The endpoint path matches exactly; deeper paths pass through.
- `SendAsync` and `BroadcastAsync` (protected) send to clients; failures are
  logged, not thrown.
- Set `MaxMessageSize` in the constructor. Check the
  [request-limits guide](docs/guides/request-limits.md) for its default.
- `KeepAliveInterval` (30 s) pings clients and closes those that do not answer.
  `TimeSpan.Zero` turns it off.
- `IWebSocket.CloseAsync` throws `ArgumentException` for close codes a server
  cannot send (below 1000, 1006, 1010, 1015-2999, 5000 and above) and for
  comments over 123 UTF-8 bytes.
- `IWebSocket.SendAsync` throws `WebSocketException` if the socket closes before
  the whole message is sent.
- Messages a client sends right after connecting are queued until
  `OnClientConnectedAsync` completes. For server-to-client sends right after
  connect, have the client send a readiness message first.
- WebSockets work over HTTP/1.1, and over HTTP/2 and HTTP/3 through extended
  CONNECT.

Guides: [startup messages](docs/user-reports/websocket-startup-messages.md),
[handshake status](docs/guides/websocket-handshake-status.md),
[receive limits](docs/guides/websocket-receive-limits.md).

## 12. Static files

- `WithStaticFolder("/", path, isImmutable)` serves a directory. Pass
  `isImmutable: true` when files do not change while the server runs; it allows
  caching. Other providers: `WithEmbeddedResources`, `WithZipFile`.
- Defaults: `index.html` as the default document, no directory listing (set
  `DirectoryLister = DirectoryLister.Html` to list), conditional requests and byte
  ranges supported.
- `%2F` and other encoded separators never act as path separators. A segment
  decoding to `/`, `\`, `.` or `..` is a 404. Containment in the served root is
  enforced.
- Precompressed `.br`/`.gz` siblings are served when
  `PrecompressedVariants` is set. See [precompressed files](docs/guides/precompressed-files.md).
- Several folders: register specific mounts first; for an overlay use
  `HandleMappingFailed(FileRequestHandler.PassThrough)`. See
  [multiple static folders](docs/guides/multiple-static-folders.md).

## 13. Security: what you must configure

Defaults protect against some abuse but not all of it. Read the
[request-limits guide](docs/guides/request-limits.md) for the current defaults.
For any server reachable from an untrusted network:

| Concern | Do this |
| --- | --- |
| Large bodies | `WithMaximumRequestBodyBytes(n)`, or per route with `WithRouteRequestLimits` |
| Slow or stalled clients | `MinimumRequestBodyDataRate` (240 B/s after 5 s, on by default) refuses a body that arrives too slowly while a handler reads it. `WithRequestTimeout(t)` caps total request time. `WithRequestBodyReadTimeout(t)` caps each body read. |
| Too many requests in flight | `WithMaximumConcurrentRequests(n, queueLimit)` |
| Too many connections | `WithConnectionLimits(maximumConnections, ...)` (not with `EmbedIOHttp3`) |
| Header abuse | `WithRequestHeaderLimits(bytes, count)`. Pass both arguments: omitting the count removes the count limit. |
| Compressed uploads | Only enable `WithSupportCompressedRequests(true)` with a finite decompressed limit. |
| Request rate | `RateLimitingModule` with a `PartitionedRateLimiter<IHttpContext>`. See [rate limiting](docs/guides/rate-limiting.md). |
| Behind a proxy | `ForwardedHeadersModule` trusts nothing until you call `WithTrustedProxies(...)`. Read results with `context.GetForwardedRequest()`; the request itself is not rewritten. See [forwarded headers](docs/guides/forwarded-headers.md). |
| Authentication | Basic authentication over HTTPS, or `BearerAuthenticationModule` before protected endpoints (2.2.0+). Use controller roles/policies for authorization; see below. |
| Banning abusers | `IPBanningModule` (instance APIs are scoped to that module) or `ClientBanningModule` (custom keys, CIDR allow/deny, optional persistent store). |
| Browser access | `WithCors(origins, headers, methods)` with explicit origins, not `*`, when credentials are involved. |
| Sessions | `WithLocalSessionManager(m => { m.CookieSecure = true; m.CookieSameSite = CookieSameSiteMode.Lax; })` on HTTPS. |
| Error detail | Keep the default empty 500 or problem details. Do not enable `ExceptionHandler.HtmlResponse` or `EmptyResponseWithHeaders` in production. |

### Persistent client bans

Configure `ClientBanningModule.WithPermanentBanStore(path)` before startup, using
an application-selected path outside static mounts. A module holds a cooperating
writer lease; dispose it before reopening the store. Changes save before the
in-memory ban/unban is confirmed, and persistence errors propagate. Do not edit
the active file externally or treat this local store as distributed state.

The 2.3.0 storage correction retries only Windows replacement error 1175,
which leaves both filenames intact, at most three times. Other failures and
exhausted retries still throw; there is no delete-first or permission-bypassing
fallback. Earlier releases, including 2.2.0, do not contain this correction. See
[persistent client banning](docs/user-reports/context-client-banning.md#persistence-contract).

### Bearer authentication and authorization (2.2.0+)

- Add `new BearerAuthenticationModule(baseRoute, validator)` before endpoints.
  The asynchronous validator accepts a token/cancellation token and returns an
  authenticated `ClaimsPrincipal`, or null for invalid credentials. Use HTTPS.
- Missing credentials are permitted by default so public and protected actions
  can coexist. Invalid supplied bearer credentials still fail. Set
  `RequireAuthentication = true` to require credentials for the whole module path.
- Configure `AuthorizationOptions.AddPolicy(...)` through
  `api.ConfigureAuthorization(...)` before registering controllers. Controller
  and method requirements combine with AND; roles within one attribute use OR.
  `[AllowAnonymous]` overrides action/controller attributes, not an earlier module.
  Applicable inherited attributes participate too. Unknown policy names fail
  controller registration; policy configuration is copied at registration.
- Missing/invalid credentials on a protected action produce 401; an authenticated
  principal that fails a role or policy gets 403. Malformed, duplicate or oversized
  bearer credentials produce 400. `MaximumTokenLength` defaults to 16,384 ASCII
  characters. Validators and policies must honor cancellation and support overlap.
- The optional `EmbedIO-Neo.Jwt` package provides `JwtBearerTokenValidator` and
  `JwtBearerOptions`. Supply issuer, audience, trusted verification keys and an
  algorithm allowlist. It validates signed tokens and lifetime; it does not issue
  tokens or discover keys remotely. Core authentication has no JWT dependency.
- In 2.7.0+: `JwtBearerOptions.AllowedTokenTypes.Add("at+jwt")`
  optionally enforces exact, case-sensitive signed header `typ` values. Empty lists
  preserve existing acceptance, including missing `typ`. Configured lists reject
  missing/wrong types; settings are copied when constructing the validator. Use
  the issuing authority's actual type; ID tokens must not become API credentials
  merely because they share signing keys. Keep audiences/policies distinct too.
- Authentication is per request, but JWT claims reflect issuance time. Short-lived
  access tokens bound that staleness; logout/session regeneration does not revoke
  a copied bearer token. For immediate revocation or account/role changes, check
  current application state in a validator wrapper or required named policy, using
  the verified `(issuer, subject)` identity and issuer-scoped `jti` when applicable.
  Reject missing required identifiers; fail closed on store failure. Cache lifetimes
  create a revocation-delay window and active requests are not retroactively undone.
- Verification keys are snapshotted, with no discovery or automatic rotation. Deploy
  a new validator for trust changes; do not mutate the original key/options expecting
  an existing validator to change. See the guide's revocation and rotation limits.

Guide: [bearer authentication and authorization](docs/guides/bearer-authentication.md).

## 14. Diagnostics and telemetry

- In 2.3.0+: the managed listener display name is
  `EmbedIO-Neo HTTP Listener`, including the startup log. The `EmbedIO`
  namespace, trace-source identity, and upstream license attribution are preserved.
- Logging goes to the `TraceSource` named `EmbedIO`
  (`EmbedIO.Diagnostics.Log.Source`). With the DI package,
  `loggerFactory.ForwardEmbedIODiagnostics()` forwards it to
  `Microsoft.Extensions.Logging`; dispose the returned registration after the
  server stops.
- `WithTelemetry()` (`net10.0` only) enables the `EmbedIO.Server` Meter and
  ActivitySource with standard `http.server.*` instruments.

Guide: [metrics and tracing](docs/guides/telemetry.md).

### Health and readiness (2.8.0+)

These APIs require 2.8.0+. `EmbedIO.Health.HealthCheckService`
monitors one server, with up to 32 immutable `HealthCheckRegistration` entries.
`CheckLiveness()` observes Listening without dependency checks;
`CheckReadinessAsync()` requires Listening, no drain and all Healthy results.
Degraded and Unhealthy both mean not ready. Results are not cached; overlapping
probes share an evaluation. Individual waiter cancellation does not cancel the
shared checks. Per-check deadlines default to 5 seconds; an uncooperative check
retains one execution slot until it finishes, preventing overlapping replacements.

Desktop hosts can await `WaitForReadyAsync(timeout, token)` before navigating a
WebView. It issues no HTTP request and does not warm controller routes, though
application checks can warm their dependencies. Observe `RunAsync` separately for
startup failures. Drain, stop or health-service disposal rejects readiness;
dispose the service with its host. Health checks do not gate ordinary requests.

Optional `HealthCheckModule` serves exact GET/HEAD `/health/live` and
`/health/ready` endpoints, 200 or 503 with no-store and minimal text. Place it after
required authentication/access-control modules, before catch-all routes.
Controller authorization attributes do not protect module endpoints. Detailed
JSON requires an explicit authorization callback; never put secrets in check
descriptions. No endpoints or background checks are installed by constructing a
service alone. Both core targets are supported with no added dependencies.
Guide: [health and readiness](docs/guides/health-checks.md).

## 15. Dependency injection and hosting

- Package `EmbedIO-Neo.DependencyInjection`.
- `server.WithDependencyInjection(provider)` creates one scope per request;
  register it before modules that use services.
- `api.WithControllerServices<T>()` activates a controller with constructor
  injection. `[FromServices]` binds a parameter from the request scope.
- `services.AddEmbedIO(configureOptions, configureServer)` runs the server as a
  Generic Host `IHostedService`.

## 16. Trimming and Native AOT

- Supported on the `net10.0` asset only.
- Reflection-based controller registration (`WithController<T>()`) does not work.
  Use the source generator, which ships inside the package:

```text
[GeneratedControllerBinding(typeof(ItemsController), JsonSerializerContext = typeof(AppJson))]
internal static partial class ApiControllers { }

api.WithController(ApiControllers.ItemsController)
```

- Use `SendDataWithMetadataAsync`, `GetRequestDataWithMetadataAsync` and
  `Json.SerializeWithMetadata` with source-generated `JsonTypeInfo`.
- Generator diagnostics EMBEDIO001-014 explain unsupported controllers and invalid
  authorization/hook attributes (014 requires 2.3.0+). `[MultipartForm]`,
  authorization and before-handler attributes work with generated bindings as well
  as reflection registration. Generated hooks use direct calls without runtime reflection.

Guide: [trimming and Native AOT](docs/guides/trimming-and-native-aot.md).

### Startup and first-endpoint latency

Prioritize warm steady-state and tail latency when a cold/warm tradeoff is
necessary; aim to improve both. A cold-start gain alone does not justify a
meaningful warm regression. Measure and report each separately, using Windows
desktop / embedded hosting as the lead environment.

Windows desktop / embedded hosting is the current primary performance target.
Measure your deployed application: listener readiness, first complete response,
first use of each endpoint and warm request latency are different costs.

- For a cold measurement, launch a fresh server process and report readiness
  through a non-HTTP signal. An HTTP health/readiness probe warms the request
  pipeline. Hitting another endpoint can also initialize shared controller,
  serializer, JIT and transport code. Keep that phase separate.
- Retain process launch to first complete response as well as endpoint latency.
  Moving initialization into startup can improve the latter while leaving the
  user's total wait unchanged. Fresh processes do not imply cold OS/file caches.
- Compare Release builds with the same runner, runtime, deployment settings and
  response validation, alternating baseline/candidate order. Separate TCP/TLS
  setup from requests on established connections. Record failures and sample
  spread; do not discard slow samples or treat short warm windows as p99 evidence.
- Generated controllers and JSON metadata are supported alternatives to reflection
  and are required for Native AOT. Measure them with your application and compare
  deployment modes; their availability alone is not a latency guarantee.
- Preserve request limits, authentication and data freshness while optimizing.
  Do not run mutating handlers as an implicit warmup or cache user responses to
  obtain a lower first-hit number. Keep application warmup explicit and safe.

The repository's fresh-process runner is documented in
[performance checks](test/EmbedIO.Performance/README.md#first-endpoint-latency).
The [first-endpoint report](docs/project/first-endpoint-latency.md) describes the
literal-route optimization included in 2.3.0 and its measured limits. It is absent
from 2.2.0 packages. Preserve existing public APIs and behavior
when changing the library; follow [CONTRIBUTING.md](CONTRIBUTING.md).

The [cold-path experiment](docs/project/cold-path-jit-latency.md) retains
measurements of HTTP/1 header/logging extraction. That optimization was deferred
after mixed warm results and is absent from the library source; the numbers
describe diagnostic binaries, not current production behavior. The requested
listener display name is `EmbedIO-Neo HTTP Listener`. The measurement runner,
wire regressions and evidence remain available for future investigations.

### Response-pipeline validation (2.3.0 source tree)

The test-only response state machines combine file ranges, conditional requests,
gzip/deflate, HEAD metadata, cookies, trailers and cancellation over real HTTP/1,
HTTP/2 and HTTP/3. They cover cached/streamed and empty/large representations,
actual multi-client overlap and a flushed-prefix cancellation boundary.
Expected bodies/ranges are independent of production helpers; negative oracle
self-tests must reject deliberately corrupted responses. See
[response-pipeline fuzzing](docs/guides/response-pipeline-fuzzing.md) for commands,
corpus and limits. These checks validate correctness and cleanup; they do not
establish latency or exhaustive coverage, and configured CI is not a passing run.

The additional [stateful application campaigns](docs/guides/stateful-surface-fuzzing.md)
cover reflection/generated controller parity, permanent-ban persistence and failed
saves, real-listener cancellation/disposal, mutable-file cache generations,
request content-coding chains/decoded limits, and multipart drain/cancel/ownership.
Their independent ledgers, six negative oracle checks and replay traces supplement
existing parser/transport fuzzing; they do not establish exhaustive coverage or
performance. Production APIs/defaults/dependencies are unchanged.

Three additional in-process policy campaigns exercise trusted-proxy forwarding,
CORS preflights and conditional/range precedence with three overlapping actors,
independent expectations, numeric extremes and three negative oracle checks.
They preserve existing trust, wildcard and method behavior. See the same
[stateful application guide](docs/guides/stateful-surface-fuzzing.md) for replay
commands and the difference between helper-output checks and wire validation.

## 17. Pitfalls checklist

- Registering a static folder at `/` before the API: the API never runs.
- Using `OnGet("/a", ...)` expecting an exact match: it also matches `/a/b`.
- Reading the request body twice, or with `[JsonData]` plus a manual read.
- Binding request JSON into a domain type (over-posting).
- Changing status or headers after writing the body.
- Putting unvalidated request text into a header, cookie or reason phrase.
- Using `ValueTask` as a controller return type.
- Configuring options or modules after the server started.
- Calling `RunAsync` twice, or reusing a stopped server.
- An `https` prefix with no certificate.
- Expecting HTTP/3 on `netstandard2.0` or without native QUIC.
- Relying on defaults for an internet-facing server instead of setting limits.
- Calling `WithRequestHeaderLimits(bytes)` without a count.
- Sending a WebSocket close code a server cannot use, such as 1006.
- Upgrading from v1 without reading the
  [v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md).

### Validation when changing the library

Follow [CONTRIBUTING.md](CONTRIBUTING.md) for the pinned SDK, locked restore,
warning-free builds, regression suites and required platform gates. Parser checks
combine FsCheck with instrumented AFL++/libFuzzer campaigns; multipart joins the
URL/query surface. Multi-connection state-machine campaigns cover shared routing,
sessions, rate limits, WebSocket budgets, tunnels, authentication/JWT and real
HTTP/1, HTTP/2 and HTTP/3 connections. They retain replayable traces, binary
provenance and bounded cleanup, and shrink matching invariant failures. These
trace campaigns are not coverage-guided fuzzers; QUIC needs a prepared host.
Fuzzing dependencies remain test-only. A configured workflow is not proof of a
passing run. See [property testing](docs/guides/property-testing.md) and
[multi-connection fuzzing](docs/guides/multi-connection-fuzzing.md).

## 18. Where to read more

| Topic | Guide |
| --- | --- |
| First app | [Getting started](docs/guides/getting-started/README.md) |
| Files | [Static files](docs/guides/getting-started/files.md), [streaming uploads](docs/guides/streaming-multipart.md), [resumable uploads](docs/guides/resumable-uploads.md) |
| Authentication | [Bearer authentication, policies and optional JWT](docs/guides/bearer-authentication.md) |
| Startup and latency | [First-endpoint measurements](docs/project/first-endpoint-latency.md), [Native AOT](docs/guides/trimming-and-native-aot.md) |
| Limits and deadlines | [Request limits](docs/guides/request-limits.md), [body limits](docs/guides/request-body-limits.md) |
| HTTP/3 | [HTTP/3 listener](docs/guides/http3.md) |
| OpenAPI | [OpenAPI documents](docs/guides/openapi.md) |
| Regression and fuzzing | [Property testing](docs/guides/property-testing.md), [multi-connection state machines](docs/guides/multi-connection-fuzzing.md) |
| Graceful shutdown | [Draining with WebSockets](docs/guides/graceful-drain-websockets.md) |
| Trailers and 1xx | [Response field sections](docs/guides/response-field-sections.md) |
| Everything else | [Documentation index](docs/README.md) |

SSE wire EOF can arrive before the server handler finishes disposing its subscription.
Lifetime tests must await handler completion before asserting released subscriber slots;
a successful server shutdown alone does not join every request continuation.

### Portable EmbedIO.Quic TLS development

The internal Linux/macOS TLS adapter uses an original private polling ABI over
pinned OpenSSL 3.5.9 built with `no-quic`; the managed engine owns the transport.
The common TLS factory preserves per-handshake certificate-source snapshots, SNI,
client-certificate modes and managed chain/revocation/callback policy. Packet keys
and peer parameters alone do not admit application traffic: TLS Finished and
client-certificate policy must succeed first. Native buffers and handles remain
bounded and owned by the serialized connection. Windows retains Schannel.

Public HTTP/3 provider selection and package dependency groups are unchanged.
A separate unpublished native runtime package and package-only consumers are in
development; oldest supported libc/musl and remaining RID validation are pending. Build instructions and the exact archive pin are in
[`native/EmbedIO.Quic.Tls`](native/EmbedIO.Quic.Tls/README.md). The portable CI job
runs independent wire, SNI, publication, mutual TLS and bounded mutation checks on
Linux and macOS; a configured job does not establish a successful run. Local Linux
validation passed 24 wire cases, six SNI cases, live publication, 24 certificate
policy cases and 5,000 native-auth mutations, plus six common-factory JIT/NativeAOT
wire cases and 3,072 wrapper-sanitizer ownership lifecycles. Those initial proofs
used the recorded development build; final restricted native-build validation is
recorded separately in the development guide. None of this work is released.

The portable boundary's private ABI is now 2. Server credential preparation keeps
runtime-discovered intermediates in order, preserves partial issuers and omits the
trust anchor; issuer installation transfers only public DER. Native ABI 1 is
rejected during preflight. Local Linux tests compare five chain profiles with a
BCL TCP control and check exact peer-visible issuer order and cleanup. The store
fixture requires an explicitly disposable Linux profile and stores public CA
certificates without adding a trusted root. Generated chain certificates include
subject/authority key identifiers, and the TCP control explicitly enables strict
X.509 validation on every peer Python version. The independent revocation fixture
serves signed CRLs and OCSP responses, including nonce handling and bounded HTTP
GET/POST. It preserves the existing policy assertions. Hosted macOS 26 ARM64
revalidation passed all 24 certificate-policy cases, including online good/revoked
checks, plus wire/SNI/publication, all three ciphers and 256 native-auth mutations
in CI run 39638190853. This does not establish other macOS RIDs or native packaging.

The owned connection refreshes its monotonic time after initial TLS processing,
including application certificate policy, before starting a closing interval.
Established packet processing does not take this extra clock reading. A delayed
revocation responder exercises rejection after validation exceeds a loopback PTO;
policy expectations, handshake limits and response deadlines remain enforced.

Portable builds now emit a binary audit with RID/architecture, dependency,
export and platform-version metadata. Requested glibc assets reject requirements
above 2.27; Linux x64 CI builds in a digest-pinned manylinux compiler environment
and the tested artifact requires only GLIBC_2.17. macOS builds explicitly target
15.0 and verify their load commands. Binary metadata does not establish all RID,
musl, signing, package-consumer or runtime acceptance. The native artifacts and
receipts are retained as package inputs. The unpublished `EmbedIO-Neo.Quic.Native`
project stages exactly six Linux/macOS x64/ARM64 RIDs by default and checks the
packed native bytes, license, manifest and source identity. It stays outside the
ordinary solution; the six existing packages and core dependency groups remain
unchanged until provider transition. This avoids placing native runtime assets in
the legacy .NET Standard package graph.

The portable workflow now builds on matching hardware, assembles one runtime
package and validates portable/RID/Native AOT consumers with isolated local feeds
and fresh caches. Generated controllers and source-generated JSON avoid reflection
requirements in the focused AOT consumer. Its wire checks cover all three ciphers,
bearer policy, bounded uploads/rejections, request-abort cancellation and cleanup;
the package-based full harness retains the broader protocol and certificate suites.
A local musl x64 preview passed the initial six JIT/AOT cipher profiles; exact
later repeats are recorded in the development guide. Single-RID preview switches
are local development aids, not distribution acceptance. All six RID runs, oldest
OS/libc, native signing/loading, final-payload scans, browser and endurance/latency
acceptance still apply. Public provider selection is unchanged and none of these
native package changes is released.


The development connection now honors authenticated peer `NEW_CONNECTION_ID`
retirement watermarks and retransmits `RETIRE_CONNECTION_ID` through the shared
recovery owner. It retains at most two active peer IDs and four unacknowledged
retirements, releases acknowledged records and uses the replacement ID before
sending a close. These bounds remain constant over long-lived rotation. This
handles peer destination IDs. Server-issued IDs and reset emission are described
below; migration and Retry remain separate acceptance work.

CI run 39653100127 built all six native assets and assembled the combined package.
Its complete package-consumer suites passed on Linux x64, Linux ARM64 and macOS
ARM64. Both musl consumers stopped at Git checkout ownership validation; the
owner-matched Docker correction subsequently passed the full local musl x64
consumer against the same six-RID feed. Intel macOS stopped before wire testing
because the pinned cryptography 50.0.2 peer dependency has no published x64 wheel.
The workflow now prepares a separate, unpublished source-built test wheel of
that same version against the reviewed static OpenSSL SDK, with pinned build
tools, Cargo lock, wheel/source/native-receipt checks and retained licenses.
Subsequent run 39671061660 at `61fcc167` passed all six native builds, the
combined package and all six complete consumers, including Intel macOS and both
musl architectures. Its Android emulator download failed before app startup;
the unchanged failed-job retry passed on attempt two. This wheel remains test
infrastructure, not a production dependency or an assertion of upstream Intel
macOS support. See the development record for evidence and the remaining
production, browser, endurance and latency work.


The unmerged engine now recognizes received stateless resets using tokens for
peer IDs it has actually used; unused and retired IDs do not qualify. Valid
packet authentication takes precedence. A recognized reset enters silent draining
and cancels active application work, including request-abort tokens. The dispatcher
uses endpoint-bound keyed token identities when a randomized header does not
identify its connection. Shared-endpoint candidates first preserve authentication
by the issued-ID owner; only failure permits forwarding to the token owner. This
adds no token hash to the usual single-connection packet path. Reset routes and endpoint
groups retire with connection ownership. Server-issued IDs and reset emission
are described below; path migration remains development work.
The new model, dispatcher, independent-peer and fuzz checks are recorded in the
development/testing guide. Public provider selection remains unchanged.


The unmerged engine issues fixed 16-byte random server IDs and maintains two
active IDs per connection. The initial reset token travels in authenticated
server transport parameters; a spare ID and replacements use reliable
NEW_CONNECTION_ID deliveries. RETIRE_CONNECTION_ID rejects unissued sequences
and retirement of the carrying packet's destination. Immutable routing metadata
adds and removes every active alias before related output is sent. A private
listener secret derives reset tokens; connection cleanup does not discard that
secret, and listener disposal clears it after workers stop. Secrets are not
persisted across listener restarts.

Unknown valid short-header packets can receive a smaller stateless reset. These
responses have a global 64-attempt-per-second allowance and a small bounded
share of the output queue. Active IDs use ordinary connection processing.
Model tests cover 10,000 retirements, loss, bounds, malformed frames and cleanup;
Windows reference peers passed 32 rotations and two retired-ID reset emissions
on each AES profile, and Linux passed the same checks on all three cipher profiles.
This remains unmerged development: public provider selection, runtime dependency
groups, and supported application configuration are unchanged. Local regression/fuzz results, the low AFL++ feedback-stability limitation and
final changed-source acceptance for this increment are recorded in the testing guide.


The development-only standards capture tool uses the hash-locked
`.github/requirements/standards-capture.txt` parser dependency. It permits only
primary-source HTTPS origins and redirects, limits individual responses/indexes
to 64 MiB and the RFC update graph to 512 documents, and rejects XML DTD/entity
markup. Offline controls run in CI's existing source-quality environment. See
[HTTP conformance](docs/project/http-conformance.md) for isolated capture commands;
new captures do not overwrite or redate the documented frozen inventory.


Same-path `PATH_CHALLENGE` responses in the unmerged engine echo the exact eight
bytes once and pad the UDP datagram to 1200 bytes, except where the existing
unvalidated-address amplification budget requires less. Recovery does not repeat
the response frame. Truncated challenge/response data yields frame-encoding error
7; an unmatched complete response retains protocol-violation error 10, permitted
by RFC 9000 section 19.18. The model, fuzz and independent-peer hooks are in the
[development/testing record](docs/project/quic-development-and-testing.md).
Server-initiated path validation and NAT rebinding remain separate integration
work; public provider selection is unchanged.
