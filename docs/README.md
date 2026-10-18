# Documentation

EmbedIO-Neo keeps the existing `EmbedIO` namespaces and offers a small modular
HTTP/WebSocket server, integrated CLI, JsonServer, and testing helpers.
Start with [Your first JSON endpoint](guides/getting-started/README.md)
for a complete app you can run, then choose files or controllers as needed.

## Getting started

- [Finite write idempotency (2.11.0+)](guides/idempotency.md)
- [Opt-in output caching](guides/output-caching.md)

- [Your first JSON endpoint](guides/getting-started/README.md): install, run, and get JSON.
- [Serve HTML and files](guides/getting-started/files.md): serve a folder and combine it with an API.
- [Use a controller](guides/getting-started/controllers.md): group endpoints with explicit routes.
- [Routes, verbs, and parameters](guides/getting-started/requests.md): GET, POST, PUT, DELETE, query strings, and JSON bodies.
- [Agent primer](../AGENT_PRIMER.md): rules, APIs and pitfalls for an AI agent building an application on v2.

## Usage guides

- [Stateful application-surface fuzzing](guides/stateful-surface-fuzzing.md): controller parity, persistence rollback, lifecycle races, file mutation, request codings, multipart ownership and forwarding/CORS/conditional policies.

- [Response-pipeline fuzzing](guides/response-pipeline-fuzzing.md): real HTTP/1-3 ranges, validators, compression, HEAD, cookies/trailers and cancellation with independent oracles.
- [Route hooks and enum statuses](guides/route-hooks.md): controller/action preprocessing choices in 2.3.0+, async ordering, cache policy and compatible status overloads.

- [Browser request protection](guides/browser-request-protection.md): opt-in host/origin protection (2.7.0+) with allow-lists, session-bound anti-forgery tokens, controller exemptions and WebSocket origin checks.
- [Bearer authentication and authorization](guides/bearer-authentication.md): asynchronous validation, controller roles and policies, optional JWT validation and identity-isolation campaigns.
- [Multi-connection state-machine fuzzing](guides/multi-connection-fuzzing.md): deterministic actor traces, replay, invariant-preserving shrinking and bounded cleanup.
- [Streaming multipart uploads](guides/streaming-multipart.md): bounded streaming, controller binding, complete-request validation and staged file cleanup.
- [WebSocket handshake status](guides/websocket-handshake-status.md): exact endpoints, module order, and HTTP/1 versus extended CONNECT.
- [Outstanding WebSocket deliveries](guides/websocket-receive-limits.md): opt-in connection and module limits covering assembly, queues and unfinished callbacks.
- [WebSockets during a graceful drain](guides/graceful-drain-websockets.md): new in Neo v2, an opt-in 1001 close on drain and the `DrainStarted` signal, over HTTP/1.1, HTTP/2 and HTTP/3.

- [Response-time diagnostics](guides/response-timing.md): server handling duration, preserved context age and the difference from client latency.
- [Request trailers](guides/request-trailers.md): optional completion-aware immutable snapshots separate from initial headers.

- [Capsule and tunnel carriers](guides/capsule-tunnels.md): optional Neo v2 APIs, still under validation, authorization, streamed values, native datagram packet ownership and carrier lifetime.

- [Request-abort cancellation](guides/request-abort.md): opt-in request-owned tokens, explicit HTTP/1 input-end policy, controller binding and streaming integration.
- [Request limits, admission and deadlines](guides/request-limits.md): opt-in header and body byte limits, body read and connection deadlines, connection count, bounded concurrent requests and queue, cooperative request deadlines, deferred `100 Continue` and per-route policies.
- [Request-body byte limits](guides/request-body-limits.md): encoded versus decoded budgets, immutable routed ownership, configured error callbacks, unread-input validation and borrowed stream ownership.
- [Rate limiting](guides/rate-limiting.md): new in Neo v2, opt-in request admission through System.Threading.RateLimiting, with client and identity keys, 429 and Retry-After, queues, cancellation, memory bounds and limits.
- [Behind a reverse proxy](guides/forwarded-headers.md): new in Neo v2, trusted-proxy handling of Forwarded and X-Forwarded-* headers, with proxy allow-lists, hop limits, an allowed-host policy and what is rejected.
- [Request validation](guides/request-validation.md): opt-in DataAnnotations validation in 2.4.0+, bounded nested models, field errors, generated/AOT bindings and OpenAPI constraints.
- [Bounded SSE event channels](guides/sse-event-hub.md): opt-in broadcast (2.6.0+), bounded replay, reconnect gaps, subscriber limits, controller integration and fuzzing.
- [Streaming JSON responses](guides/streaming-json.md): opt-in JSON arrays and NDJSON in 2.5.0+, bounded records, client consumption, cancellation and failure handling.
- [Problem details for API errors](guides/problem-details.md): new in Neo v2, opt-in RFC 9457 `application/problem+json` error responses, with safe defaults, request IDs, content negotiation and migration notes.
- [Health checks and readiness](guides/health-checks.md): pending next release, opt-in dependency checks, desktop startup waits, protected health endpoints and shutdown readiness.
- [Metrics and tracing](guides/telemetry.md): opt-in HTTP server metrics and spans with W3C trace context, route templates and server identity.
- [Trimming and Native AOT](guides/trimming-and-native-aot.md): AOT-safe JSON with source-generated metadata, Web API controllers through generated bindings and their build-time diagnostics, annotated reflection APIs and what a Native AOT host supports.
- [OpenAPI documents](guides/openapi.md): new in Neo v2, OpenAPI 3.1 documents written from generated controller bindings, with operation attributes, security requirements from authentication modules, declared routes for other modules, routes the document cannot describe and its limits.
- [HTTP/3 listener](guides/http3.md): new in Neo v2, QUIC mode, certificates, usage and current limits.
- [HTTPS](guides/https.md): certificates, client trust, and desktop/MAUI validation limits.
- [Rotating the server certificate](guides/certificate-rotation.md): new in Neo v2, `ServerCertificateSource`, replacing certificates while TCP and QUIC listeners run.
- [TLS policy](guides/tls-policy.md): new in Neo v2, client certificates, protocol versions and SNI certificate selection, with per-listener support.
- [Command-line server](guides/cli.md): options, static serving, plugins, and CLI provenance.
- [Multiple static folders](guides/multiple-static-folders.md): mount order and explicit fallback.
- [Asynchronous outbound requests](guides/async-outbound-requests.md): controller return types,
  completed JSON results, and troubleshooting.
- [Circular JSON references](guides/json-circular-references.md): opt-in .NET serialization settings.

- [Listener stalls](guides/listener-stalls.md): two-server diagnostics, shutdown fixes, and regression scope.

## User reports

- [Suffix byte ranges](user-reports/suffix-range-responses.md): corrected tail selection, boundaries and migration behavior.

- [Upstream parity audit](parity-audit/README.md): findings by functional area from the pinned upstream/Neo differential suite.

- [Default JSON preservation audit](user-reports/default-json-preservation-audit.md): DTO fidelity and verified upstream compatibility differences.

- [Concurrent ZIP resources](user-reports/zip-provider-concurrency.md): bounded shared-archive streaming, ownership and cancellation.

- [TechEmpower workloads](user-reports/techempower-benchmarks.md): isolated JSON/plaintext host, managed pipelining and measurement limits.

- [Former Xamarin sample](user-reports/xamarin-sample-location.md): pinned historical source and maintained desktop/MAUI examples.

- [WebSocket overlap and close](user-reports/websocket-overlap-and-close.md): original stress workload, close admission and caller-owned cancellation.

- [Binary controller responses](user-reports/binary-controller-responses.md): passthrough serialization, media metadata and a complete image endpoint.

- [Selective upstream backport audit](user-reports/upstream-backport-audit.md): patch decisions, WebSocket send concurrency and prefix validation.

- [Common hosting concepts](user-reports/common-hosting-concepts.md): task-focused entry points, lifetime ownership and preserved extension contracts.

- [WebSocket message callbacks](user-reports/websocket-message-callbacks.md): opt-in text/binary callbacks, strict UTF-8, ordering and transport limits.

- [Logging provider integration](user-reports/logging-provider-integration.md): opt-in ILoggerFactory forwarding, application-owned bridges, Serilog, filtering and lifetime ownership.

- [Modern .NET and legacy compatibility](user-reports/modern-dotnet-legacy-compatibility.md): current targets, NuGet asset selection and SDK versus runtime requirements.

- [Listener chunk framing and charset allocations](user-reports/listener-wire-performance.md): exact wire/extraction parity, measured allocations and rejected routing candidate.

- [Listener body and header allocations](user-reports/listener-body-performance.md): measured drain/serialization savings, POST benchmarks and async-read compatibility limits.

- [HTTP listener boundaries](user-reports/listener-boundaries.md): iterative accepts, framing coverage and confirmed parsing-policy gaps.

- [Listener connection lifetimes](user-reports/listener-connection-lifetimes.md): terminal cleanup, retained resources and end-to-end HTTP/HTTPS benchmarks.

- [HTTP listener audit](user-reports/http-listener-audit.md): compatible queue/endpoint simplifications, body isolation and measured queue allocations.

- [Cold-start audit](user-reports/cold-start-audit.md): fresh-process measurements, startup failure cleanup and remaining startup dependencies.

- [XML responses](user-reports/xml-responses.md): explicit serializers, MIME types and encoding.

- [Dual-stack localhost](user-reports/dual-stack-localhost.md): loopback registration, routing and cleanup.

- [Additional performance audit](user-reports/performance-audit-second-pass.md): negotiation, header parsing, WebSocket metadata and request-history improvements.
- [Performance and code audit](user-reports/performance-audit.md): measured allocation reductions, cache eviction fixes and compatibility checks.
- [Server header](user-reports/server-header.md): customize HTTP responses and understand backend/upgrade limits.

- [Request logging](user-reports/request-logging.md): hide completion summaries while preserving other diagnostics.

- [WebSocket cookies](user-reports/websocket-cookies.md): separate handshake cookie fields and legacy listener behavior.
- [Progress events](user-reports/progress-events.md): a complete SSE progress server and browser client, plus the additive event writer.
- [Streaming response closure](user-reports/streaming-response-close.md): SSE, completion callbacks, server cancellation and disconnect detection.

- [macOS accept reset crashes](user-reports/mac-accept-reset.md): mitigation, thread cost, and stress coverage.

- [Request URL scheme and HTTPS](user-reports/request-url-scheme.md): transport security and URL reconstruction fixes.

- [Medium-sized response stalls](user-reports/medium-response-stalls.md): async transport writes, framing regressions and diagnostics.

- [Read-only ZIP archives](user-reports/read-only-zip.md): shared-reader hosting, handle ownership and a published-package workaround.

- [Concurrent controller responses](user-reports/concurrent-controller-responses.md): response ownership, binary output, request-aware cleanup and keep-alive isolation.

- [Static-file charset](user-reports/static-file-charset.md): opt-in response metadata, text/binary encoding and listener differences.

- [Uncompressed Content-Encoding](user-reports/identity-content-encoding.md): omit identity metadata while preserving media bytes, ranges and compression.

- [HEAD response metadata](user-reports/head-response-metadata.md): correct byte lengths, body suppression and safe GET/HEAD controller patterns.

- [Unread request bodies](user-reports/unread-request-bodies.md): early POST responses, connection reuse and legacy HttpClient validation.

- [WebSocket startup messages](user-reports/websocket-startup-messages.md): send immediately after connection, initialization ordering, cleanup and historical subscription-race validation.
- [Managed WebSocket close acknowledgements](user-reports/managed-websocket-close-acknowledgements.md): issue #184 reproduction, receive completion and validation limits.
- [Windows native WebSocket shutdown](user-reports/windows-native-websocket-shutdown.md): concurrent close, cancellation and the released .NET 11 recheck.
- [UWP browser access](user-reports/uwp-browser-access.md): inbound/outbound isolation, scoped diagnostics and HTML/API/session verification.
- [Unity custom diagnostics](user-reports/unity-custom-diagnostics.md): trace destinations, UI handoff and private-key HTTPS diagnostics.
- [Argument validation](user-reports/argument-validation.md): built-in guards, legacy contracts and the fluent-validator proposal decision.
- [Utility package extraction](user-reports/utility-package-extraction.md): utility inventory, reuse, assembly identities and the standalone-package decision.

- [Independent IP banning modules](user-reports/ip-banning-isolation.md): scoped controls, route-wide compatibility and lifecycle validation.
- [OpenAPI and Swagger UI](user-reports/openapi-and-swagger-ui.md): application-owned API documents, a runnable example and optional local UI assets.
- [Authorization by route and verb](user-reports/route-authorization.md): anonymous reads, protected writes and authorization before body binding.

- [Android emulator port forwarding](user-reports/android-emulator-port-forwarding.md): host/device ports, selected-serial commands and real emulator validation.

- [Basic authentication on the Microsoft listener](user-reports/basic-authentication-native-listener.md): legacy restricted-header fix and real authentication validation.

- [Controller route case matching](user-reports/controller-route-case.md): per-module opt-in literals, preserved data/defaults and configuration/authorization boundaries.
- [MIME policy and pre-request processing](user-reports/mime-policy-and-pre-request.md): injectable module policy and awaited callbacks with preserved defaults.
- [Chunked response streaming](user-reports/chunked-response-streaming.md): one writer, parsed protocol/keep-alive policy and verified wire framing.

- [JSON migration compatibility](user-reports/json-migration-compatibility.md): transparent legacy acceptance and documented boundaries.

- [SPA client routes](user-reports/spa-client-routes.md): direct navigation without redirects using existing provider injection.

- [Malformed typed route parameters](user-reports/typed-route-validation.md): approved 500-to-400 change and preserved error boundaries.

- [SameSite cookies](user-reports/samesite-cookies.md): opt-in response/session policy and preserved raw cookie fields.

- [Endpoint configuration](user-reports/endpoint-configuration.md): explicit binding, prefix paths, shared ports and certificate ownership.

- [Streaming write errors](user-reports/streaming-write-errors.md): error policy, reverse proxies, compression and buffering.
- [Deferred responses](user-reports/deferred-responses.md): the commitment contract, replaceable error responses and dispose-on-success buffering.
- [Server lifetime](user-reports/server-lifetime.md): cancel, drain, dispose and stop compared, the server-owned lifetime token, repeat calls and ownership.
- [Byte ranges from any module](user-reports/range-responses.md): the opt-in representation helper, every range criterion end to end, compression and what is not provided.
- [Configuration locking](user-reports/configuration-locking.md): when each surface locks, object initialization, startup validation, nested objects and read-only collection views.
- [Object initialization and fluent setup](user-reports/object-initialization.md): the same server both ways, collection initializers for modules and accounts, and which helpers stay methods.
- [Fluent Basic authentication](user-reports/fluent-basic-authentication.md): scoped registration, configuration and preserved authentication behavior.
- [Context and persistent client banning](user-reports/context-client-banning.md): live controls, CIDR policies and optional restart persistence.

## Architecture

- [Modern HTTP engine program](project/http-engine.md): protocol roadmap, compatibility changes and performance acceptance.
- [QUIC lifetime and rebind](project/quic-lifetime.md): native disposal evidence, bounded retry, quarantine limits and retirement gates.
- [Upstream QUIC lifetime report](project/quic-upstream-report-draft.md): the MsQuic report posted as microsoft/msquic#6374, separating deferred datapath close from connection-held bindings, plus the unposted dotnet/runtime note.
- [Native QUIC abort disposal](project/native-quic-abort-disposal.md): preserve explicit read/write abort codes through cleanup, with real-peer regressions.
- [Linux test-host terminations](project/linux-test-host-terminations.md): native listener stop race diagnosis, unattributed glibc abort evidence and next diagnostics.
- [Native HTTP/3 provider ownership audit](project/http3-native-provider-audit.md): MsQuic acceptance, callback and ownership findings, reproductions and proposed fixes.
- [Native QUIC lifetime campaigns](project/http3-native-lifetime-campaigns.md): Apple Silicon lifetime and resource campaigns for the native MsQuic provider, confirmed native disposal defects and proposed fixes.
- [Cold-path JIT latency](project/cold-path-jit-latency.md): deferred experiment with Windows first-response measurements and mixed warm/specialized-endpoint tradeoffs.
- [First-endpoint latency](project/first-endpoint-latency.md): fresh-process HTTP measurements, literal-route cold-path improvements and remaining profiling targets.
- [Managed TCP write latency](project/http-tcp-latency.md): controlled socket-delay comparison with unchanged keep-alive and HTTP/2 reset regressions.
- [Managed engine load comparison](project/http-engine-load-comparison.md): separate-process HTTP/1.1, HTTP/2 and HTTP/3 measurements against main and Kestrel, profiled bottlenecks and prioritized improvements.
- [Direct request dispatch from connections](project/http1-direct-dispatch.md): the accept-queue hops removed from the HTTP/1.1, HTTP/2 and HTTP/3 request path, the lifecycle semantics kept, and the measurements against the base and Kestrel.
- [HTTP teardown exception attribution](project/http-teardown-exceptions.md): phase accounting, profiler correction and bounded protocol comparisons for issue #282.
- [Managed engine load baseline on a second host](project/http-engine-load-baseline-evo-x1.md): noise-controlled engine baseline against main and Kestrel on a Ryzen AI 9 HX 370 host, including request-inspection routes, failures and bottleneck evidence.
- [HTTPS 1 MiB uploads on the v2 engine](project/https-upload-read-path-436.md): the issue #436 re-measurement of HTTPS uploads against the v1.0.3 core at 2.0.0, transport read counts per request and the stack-sampled CPU attribution of the TLS read path.
- [Listener retirement test accounting](project/listener-retirement-test-accounting.md): owner-approved v2 backend removal, retained equivalent managed cases and remaining coverage migration.
- [Apple Silicon load and interoperability evidence](project/macos-performance.md): macOS harness portability, A/A noise, candidate versus main and Kestrel on one M5 Pro, and macOS-specific defects.
- [Managed engine endurance campaign](project/http-engine-endurance.md): long-running mixed-load, fault, drain/restart and WebSocket runs with resource-retention trends, open findings and coverage gaps.
- [HTTP/2 graceful output lifetime](project/http2-drain-output-lifetime.md): response-loss evidence, committed control-write reproduction and shutdown cancellation boundaries.
- [HTTP/2 output and dispatch performance](project/http2-performance.md): profiled small-response costs, output batching and response coalescing, before/after measurements and remaining bottlenecks.
- [HTTP/3 request path performance](project/http3-performance.md): profiled HTTP/3 request costs, request scopes, write coalescing and QPACK feedback, before/after measurements and remaining bottlenecks.
- [Multiplexed request-model allocation](project/http-request-model-allocations.md): profiled HTTP/2 and HTTP/3 request-model allocation, deferred query and referrer parsing, before/after load and in-process measurements and remaining contributors.
- [HTTP/3 handshake and latency on a less loaded host](project/http3-quiet-host-validation.md): paced QUIC churn with separate handshake and stream latency against Kestrel, a CPU-saturation control and the open handshake-refusal finding.
- [HTTP/3 transport abstraction cost](project/http3-abstraction-cost.md): cost of the provider-neutral transport boundary on the System.Net.Quic listener, guarded shared-machine procedure and how to repeat it for the native provider.

- [HTTP conformance audit](project/http-conformance.md): October 2026 standards baseline, black-box conformance and stateful campaigns, and prioritized findings.
- [HTTP standards applicability audit](project/http-standards-applicability.md): frozen October 2026 RFC, errata, registry and draft inventory, requirement-to-evidence matrix for extensions and application APIs, and reproduced gaps.
- [Linux glibc heap aborts](project/linux-glibc-heap-aborts.md): inconclusive double-free investigation, checked runs, CI scan, limits of attribution and configured crash capture.
- [Analyzer warning inventory](project/analyzer-warning-inventory.md): the gates in force, the zero-warning baseline across every repository project, classification and the reproducible script.

- [Dependency injection](architecture/dependency-injection.md): request scopes, controller ownership, and hosting.

- [Controller base routes](guides/controller-base-routes.md): scoped catch-all handlers, parameterized prefixes and fallback order.

## Compatibility

- [Upgrading from EmbedIO 3.x](compatibility/upgrading-from-embedio-3.md): one-page checklist for applications moving from the archived upstream library, linking into both detailed guides.

- [Neo v1 to v2](compatibility/neo-v1-to-v2.md): migration guide for applications moving from Neo v1 to v2: listener, API, limit and behavior changes.

- [Migration](compatibility/migration.md): approved SWAN/JSON changes and consumer migration.

## Platforms

- [iOS listener recovery](platforms/ios-listener-recovery.md): endpoint retirement, awaited replacement, and the remaining suspension validation gap.

- [MAUI HTTPS validation](platforms/maui-https-validation.md): native client, WebView and external-client fixtures.
- [MAUI Android](platforms/maui-android.md): background work, listener ownership, and restart diagnostics.
- [MAUI Mac Catalyst](platforms/maui-mac-catalyst.md): listener startup and sandbox entitlements.
- [Native QUIC datagrams on macOS](platforms/quic-datagrams-macos-validation.md): local Apple Silicon evidence for the internal datagram owner, scoped to the exact tested commits.

- [Informational responses and response trailers](guides/response-field-sections.md): optional managed response sections and ownership rules.

## Project and contribution

- [Generated URL, query and multipart properties](guides/property-testing.md): bounded FsCheck generation, shrinking and reproducible campaigns.
- [Source layout](project/source-layout.md): responsibilities, partial-file naming and shared-source links.

- [Performance and cleanup audit](project/performance-cleanup-audit.md): allocation measurements, compatibility-preserving simplifications and TLS callback regression coverage.

- [Shared HTTP/1 listener shutdown](project/shared-http1-owner-shutdown.md): logical-owner retirement, idle transport handoff and final-endpoint cleanup.

- [HTTP/1 idle closure investigation](project/http1-idle-closure.md): TCP/TLS timeout observations, released/main controls and the approved orderly-idle policy with validation limits.

- [Cleanup after committed response completion](project/http-completed-response-cleanup.md): late cancellation, complete-wire state and preserved incomplete-response handling.

- [Capsule transport development](project/http-capsule-transport.md): streamed framing, negotiation constraints and remaining integration work.
- [Capsule and tunnel lifetime validation](project/http-capsule-lifetime.md): independent-peer cancellation, reset, drain and retention checks on HTTP/1, HTTP/2 and HTTP/3, with findings and limits.
- [WebTransport session core development](project/http-webtransport-core.md): draft-ietf-webtrans-http3-16 framing, session association, capsules, flow control and bounded buffering as an isolated internal core, with the transport integration contract and remaining gaps.

- [Neo baseline and direction](project/embed_io_neo.md): initial fork decisions and acknowledgments.
- [Changelog](../CHANGELOG.md): released and unreleased changes.
- [Contributing](../CONTRIBUTING.md): build, test, and compatibility policy.
- [Security](../SECURITY.md): reporting vulnerabilities.
- [Code of Conduct](../CODE_OF_CONDUCT.md).
- [License](../LICENSE) and [third-party notices](../licenses/README.md).

DocFX configuration remains at the repository root in `docfx.json`. Its content
includes these guides and generated API metadata; documentation deployment is
not configured. The old root `CLI.md` and `MIGRATION.md` remain as short entry
points for existing links.

- [Controller compression and response length](guides/controller-compression.md): streamed framing, buffered encoded lengths and the Xamarin report's validation limits.

- [Precompressed static files](guides/precompressed-files.md): opt-in .br and .gz variants, selection, validators, ranges, the 406 correction and measured compression levels.

- [Standalone native QUIC module lifetime](project/native-quic-module-lifetime.md): Linux unload crash reproduction, retained mapping, collectible contexts and fresh-process acceptance.

- [WebTransport upstream readiness](project/webtransport-upstream-readiness.md): decision to await a current-wire provider, test-only evidence and resumption gates.

- [Coverage-guided URL fuzzing](guides/coverage-guided-fuzzing.md): instrumented parser campaigns, seed replay and crash artifacts.
- [Production source maintenance audit](project/source-maintenance-audit.md): complete 434-file implementation review, reproduced defects and compatibility-preserving cleanup.
