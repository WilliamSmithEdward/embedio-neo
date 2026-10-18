# Security-header policy adversarial audit

Status: unreleased feature branch. This review covers the complete **new feature
integration surface**, not an independent fresh audit of every unrelated subsystem.
No library can certify the security of application-selected CSP text or HTML.

## Threat model and reviewed surface

An untrusted client can supply paths, headers, methods, conditional/range requests,
concurrent requests and cancellation. It cannot configure named profiles or run
application code. Trusted handlers can clear or overwrite response headers and use
cache/idempotency wrappers; these interactions must not silently reuse a nonce,
weaken a selected policy at final serialization, or infer TLS from client text.

| Surface | Review and executable evidence |
|---|---|
| Configuration/injection | Dedicated fixed header names; bounded visible-ASCII values; copied ordinal registries; immutable profiles. Tests reject controls, CRLF, DEL, Unicode, invalid framing/referrer/HSTS settings, fixed/malformed nonces and invalid generator names. CSP/Permissions directive semantics remain application-owned. |
| Controller pipeline | Reflection and actual generated bindings, inherited class/virtual-method attributes, method precedence, case-sensitive/insensitive routes, copied registries and atomic unknown-name failure. Authorization-denied tests verify selected route headers and zero controller activation. Existing binding constructors remain available. |
| Final HTTP serialization | TCP HTTP/1.0, HTTP/1.1 and HTTP/2, each cleartext/TLS, plus real QUIC HTTP/3. Wire checks bind body nonce to CSP, verify fresh requests, late-header restoration, actual-TLS HSTS and error/empty response behavior. Both owned serializers reapply after a failed preparation until actual commitment. |
| Streaming/upgrades | Direct stream writes/flush, policy changes after commitment, actual HTTP/1 WebSocket handshake and working binary echo with static/nonce profiles. HTTP/2â€“3 extended CONNECT/WebSocket uses the same reviewed multiplexed final-header serializer; no new feature-specific browser extended-CONNECT test is claimed. |
| Static files/HEAD | Real temporary file responses under static and nonce profiles; static policies retain conditional 304, byte range 206, bodies and HEAD semantics. Nonce profiles strip validators and use no-store. Wire cases reject nonce 206/304 and Content-Range, then produce replacement errors without stale representation fields. |
| Output caching | Policy selected before lookup bypasses existing entries; selection during an initial capture prohibits publication. Static profiles retain cache hits and current policy fields. Application-selected policy inside an already skipped cached handler cannot execute; configure outside the wrapper. |
| Idempotency | Runtime rejection before operation, rejection inside staged operation, and rejection through a captured original context. A reference-counted recording scope fences nonce creation for the full operation/replay; known controller nonce/idempotency combinations fail registration. |
| TLS/forwarding | HSTS depends on actual request transport state; spoofed forwarded scheme and simulated HTTPS cannot enable it. HSTS flags/lifetime are explicit, and preload requirements validated. No proxy/certificate/firewall behavior changes. |
| Custom/testing adapters | TestMessageHandler prepares after handler completion and cleanup, immediately before response conversion. External adapters have an explicit PrepareResponse contract and must accurately report TLS. No interface-breaking members added. |
| Concurrency/fuzzing | Three client requests enter a gate before release for each batch. Independent expected actors/nonces, enforced/report-only selection, shared-cache clearing, no-store, validator removal and control rejection. Deliberate body corruption must fail the specific nonce-binding invariant. |
| Unconfigured compatibility | No policy/state/nonce/Items allocation is introduced on unconfigured owned contexts; an optional response field and null checks are added. Existing manually written headers, defaults, target frameworks and runtime dependency groups are preserved. Allocation/compatibility gates and full regression suite are required. |

## Corrections made during review

1. Policy metadata must survive response-header clearing. Owned final-header serializers
   and the filtered WebSocket handshake reapply selected fields before transmission.
2. A nonce cannot share exact response bytes with cache/idempotency replay. Cache
   eligibility checks state before lookup and publication. Recording also marks the
   original context, preventing a captured-context escape around staged wrappers.
3. Header preparation is not necessarily commitment. Owned responses consult actual
   commitment, so an uncommitted encoding failure does not prevent safe error retries.
4. Only CSP fields interpret the nonce template. Literal `{nonce}` text in an unrelated
   Permissions-Policy field must not be silently removed or create nonce state. A
   focused regression preserves it literally; semantic validity remains the host's job.
5. A newly generated nonce cannot describe a cached partial/304 representation.
   These statuses and Content-Range fail closed. The existing error replacement
   removes stale representation fields before retrying serialization.

6. Repeated nonce placeholders must be bounded by their final 44-character Base64
   expansion, not only by input-template length. A new configuration assertion failed
   before correction. Construction now rejects expanded fields over 4,096 characters;
   wire cases exercise a near-limit CSP on HTTP/1–3, and fuzzing independently
   models valid/invalid expansion counts, including mandatory boundary counts 77/78.

These are feature-development findings, not claims of newly discovered vulnerabilities
in published 2.12.0. Fixture compilation/setup corrections are not security findings.

## Validation record

The expanded Windows run passed 49 focused cases with no skips, including HTTP/3.
The expanded gated campaign passed 1,000 traces (three actors, 48 generated steps),
and the negative oracle rejected its injected mismatch. The rebuilt Windows suite passed 7,316 cases with 21 expected skips (7,337 total).
All 50 new regression identities passed on Windows, Linux and macOS, with no
previous-release cases missing. macOS retained one existing raw MsQuic
AddressAlreadyInUse failure under the unchanged, owner-approved runtime quarantine;
that runtime defect is not claimed fixed. Normal and quarantine paths now share
the same 7,337-case discovery floor. Final formatting,
analyzer, compatibility, AOT and CI results are recorded in the PR; no pending or
configured check should be described as passed. Local logs stay under ignored
`TestResults/security-headers`.

## Deployment assumptions and limits

- Policy configuration is trusted. Safe ASCII does not imply a restrictive or valid
  CSP. Report-only does not enforce. Browser policy evaluation and real application
  XSS resistance need application/browser tests; no cross-browser penetration test
  is claimed here.
- Nonces authorize trusted script/style elements. Never attach them to untrusted
  markup or reuse rendered HTML outside the framework cache. Proxies/CDNs and
  service workers must honor no-store. Already persisted application data is not erased.
- Policy overrides replace every managed field. HSTS is host-wide, persists in user
  agents, and requires coordinated settings across profiles. TLS termination belongs
  to the proxy; inbound forwarded text alone cannot establish TLS.
- Middleware scope/order matters. Pre-context transport errors and an earlier
  independently rejecting middleware cannot use a later controller policy. Use a
  server baseline for application-wide coverage.
- Custom adapters must prepare immediately before final transmission. Callers must
  obey existing context lifetime/single-writer rules; arbitrary concurrent response
  mutation, custom serialization and malicious application code are outside the contract.
- Security headers do not replace authentication, authorization, CSRF, input limits,
  output encoding, secure cookies or TLS. No unrelated endpoint exposure/defaults
  are changed. No release is authorized by this PR.

The informational IDE0330 suggestion is narrowly suppressed on the existing
OutputCache gate, multiplexed-response lifecycle gate and new SecurityHeadersState
gate: `System.Threading.Lock` is absent from .NET Standard 2.0. Each exception is
matched to its exact file/member/diagnostic/reason by the suppression guard. The
shared object-monitor implementation is retained for both targets without compiler
branches; errors, warnings and unrelated messages remain unsuppressed.

The handshake constructor also retains Substring under narrowly guarded IDE0057:
.NET Standard 2.0 has no System.Range/System.Index. No target-only compiler branch
or additional runtime dependency is introduced for these style suggestions.

Additional member-scoped compatibility messages retained after reviewing all severities:

| File/member | Diagnostic | Missing API |
|---|---|---|
| `src/EmbedIO/Caching/OutputCache.cs` / `internal async Task ReplayAsync(IHttpContext context)` | CA1835 | memory-based Stream async overloads. |
| `src/EmbedIO/Caching/OutputCache.cs` / `public OutputCache( int maxEntries = 256, long maxBytes = 16 * 1024 * 1024, int maxResponseBytes = 256 * 1024, int maxConcurrentFills = 16, int maxKeyCharacters = 8192 )` | CA1512 | ArgumentOutOfRangeException throw helpers. |
| ``src/EmbedIO/Hosting/WebServerBase`1.cs`` / `public async Task RunAsync(CancellationToken cancellationToken = default)` | CA1513 | ObjectDisposedException.ThrowIf. |
| `src/EmbedIO/Hosting/WebServerOptionsBase.cs` / `public int RequestQueueLimit` | CA1512 | ArgumentOutOfRangeException throw helpers. |
| `src/EmbedIO/Idempotency/IdempotencyCoordinator.cs` / `private static byte[] HashBytes(byte[] value)` | CA1850 | SHA256.HashData. |
| `src/EmbedIO/Idempotency/IdempotencyCoordinator.cs` / `private static async Task<byte[]> ReadBodyAsync(IHttpContext context, int limit)` | CA1835 | memory-based Stream async overloads. |
| `src/EmbedIO/Idempotency/IdempotencyCoordinator.cs` / `public IdempotencyCoordinator(IIdempotencyStore? store = null, int maxConcurrentRequests = 32)` | CA1512 | ArgumentOutOfRangeException throw helpers. |
| `src/EmbedIO/Net/Internal/Http1/HttpListenerResponse.cs` / `private void RequireMutableHead()` | CA1513 | ObjectDisposedException.ThrowIf. |
| `src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs` / `private sealed class Output` | CA1844 | memory-based Stream.WriteAsync override. |
| `src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs` / `private async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)` | CA1513 | ObjectDisposedException.ThrowIf. |
| `src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs` / `private void EnterSectionConfiguration()` | CA1513 | ObjectDisposedException.ThrowIf. |
| `src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs` / `private void EnsureHeaders()` | CA1513 | ObjectDisposedException.ThrowIf. |
| `src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs` / `public long ContentLength64` | CA1512 | ArgumentOutOfRangeException throw helpers. |
| `src/EmbedIO/Security/SecurityHeadersPolicy.cs` / `private static void ValidateCsp(string? value)` | CA2249 | string.Contains overload with StringComparison. |

## Local latency and allocation evidence

On Windows 10.0.26300 / .NET 10.0.13, three fresh server processes per phase
compared the pinned published 2.12.0 consumer with the feature source. All complete
bodies and selected security fields were checked. Medians below are milliseconds:

| Configuration | First server response | Process launch to first complete response | Warm p50 | Warm p95 |
|---|---:|---:|---:|---:|
| Published 2.12.0 | 34.1684 | 117.8312 | 0.0487 | 0.1151 |
| Feature, disabled | 34.7097 | 120.3241 | 0.0464 | 0.1575 |
| Feature, static profile | 35.2743 | 124.9985 | 0.0508 | 0.1070 |
| Feature, nonce profile | 35.4519 | 126.3830 | 0.0616 | 0.1509 |

The disabled warm p50 remains close in this small sample; this
is not evidence of a statistically significant change. Opted-in policies have a
measurable setup/cryptographic cost. These are loopback client/server timings,
not production throughput, cross-platform guarantees or browser timings. Cold
means a fresh process, not cleared OS/file caches. The baseline ran before the
feature group; see [all samples, phases and binary hashes](evidence/security-headers-latency.json).
Existing hot-path, audit, listener-queue and cold-start allocation budgets passed
without changing thresholds. No latency gate was loosened.

The feature measurements above include the final constructor expansion bound.
The raw report also retains the earlier feature samples. No earlier sample was
removed to improve the reported result. The hosts differ in their setup of optional
security profiles, so these are consumer scenarios rather than an isolated estimate
of the core null-check cost.

Final-head Linux run 39167323318 exposed a pre-existing SSE fixture race: HTTP/2
EOF arrived before the handler disposed its subscription. The completion test now
awaits an explicit handler-finally signal with a five-second bound before asserting
zero subscribers. The original failed TRX is retained; production SSE behavior and
the zero-slot assertion are unchanged. Replacement exact-source gates are required.
