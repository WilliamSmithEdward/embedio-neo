# Changelog

## [Unreleased]

- Begin the owner-approved modern HTTP engine replacement: managed chunked request
  decoding, asynchronous body reads and draining, strict framing validation,
  pipeline buffer adoption and reduced response-header allocations. Malformed or
  ambiguous requests previously tolerated are rejected; see the
  [migration notes](docs/compatibility/migration.md#managed-http-framing-unreleased)
  and [protocol roadmap](docs/project/http-engine.md). The development branch now
  includes HTTP/2, HPACK and RFC 8441 WebSockets, plus internal HTTP/3/QPACK/QUIC
  connection dispatch; public HTTP/3 listener integration remains in development.
  See the roadmap for target restrictions and outstanding conformance work.

- Reject malformed managed WebSocket frame metadata before consuming payloads,
  enforce minimal wire-length encoding, and reject lengths that cannot fit the
  current payload representation before integer conversion. Validate incoming
  close status codes and UTF-8 reasons before processing or echoing them (#190). See the
  [compatibility notes](docs/compatibility/migration.md#managed-websocket-framing-unreleased).

- Enforce `WebSocketModule.MaxMessageSize` on the managed listener before payloads
  are buffered, complete the close handshake after rejecting oversized or invalid
  text messages, deliver messages received just before the peer's close, and stop
  failed close writes from escaping as unobserved task exceptions. Buffered frame
  reads, 64 KiB send frames and fewer copies raise managed echo throughput by
  about 8x for 64 KiB messages and 11x for 1 MiB messages in the documented
  benchmark. A bounded stateful fuzzer now runs in the Fuzz workflow (#190). See the
  [migration notes](docs/compatibility/migration.md#managed-websocket-limits-delivery-and-send-framing-unreleased)
  and [benchmark](test/EmbedIO.Performance/README.md#managed-websocket-echo).

- Keep the managed WebSocket receiver active during local closing so valid peer acknowledgements complete promptly. Use asynchronous receive-completion signaling while preserving close payloads, cancellation and shutdown limits (issue #184).

- Remove compiler/analyzer suppressions and null-forgiving operators, correct nullable
  contracts and resource ownership, and enforce warnings as errors in ordinary and
  platform builds. CI checks formatting and rejects new suppressions. The owner-approved
  API migration uses `RawTarget`, `Validate.RoutePath`, and URI-valued URL APIs while
  retaining compatible string inputs where possible; see the
  [migration guide](docs/compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved).
  Recovery boundaries propagate process/resource corruption exceptions rather than
  treating them as ordinary request failures.

- Add the owner-approved opt-in ClientBanningModule with context criteria, ordinal client keys, permanent IPv4/IPv6 CIDR policies, live temporary/permanent controls and optional atomic file-backed permanent-ban persistence. Existing IP-banning APIs/defaults/targets/dependencies remain unchanged (upstream #438).

- Add the owner-approved WithBasicAuthentication fluent helper with explicit scope, configure-before-registration and optional realm, preserving existing authentication behavior and manual registration (upstream #439).

- Document existing streaming write-error controls, with reverse-proxy disconnect, compression and buffering compatibility coverage (upstream #457).

- Retain URL-prefix configuration and document endpoint binding, path routing and certificate ownership, with real listener compatibility coverage (upstream #464).

- Add owner-approved opt-in ILoggerFactory diagnostics forwarding in the optional DI package, with registration disposal and failure/recursion counters. Core APIs, defaults and dependency groups remain unchanged (upstream #475).

- Add opt-in SameSite controls for application and local session cookies, with existing defaults retained. Preserve independent raw cookie attributes and WebSocket response cookies, including mixed native typed/raw headers and long-lived expiration (upstream #479).
- Correct inherited suffix byte ranges: select final bytes, clamp oversized suffixes, reject zero suffixes, and ignore positive suffixes for empty files. Retain Int64 skip offsets for non-seekable resources beyond 2 GiB. Existing explicit-range, HEAD and validator policies remain; see suffix-range migration guidance (issue #170).

- Owner-approved compatibility change: malformed nonempty values rejected by supported controller route converters now return HTTP 400 instead of 500, before handler invocation. Preserve valid conversions, controller/configuration errors, optional/missing routes, query/body binding and transport policies; see the migration guide (issue #163, upstream #505 follow-up).

- Avoid ZIP archive reader thread-pool starvation on modern runtimes by using task-based synchronous gate waits; preserve archive serialization, cancellation, stream ownership and defaults (upstream #491 follow-up).

- Documented default JSON migration contracts and audited DTO fidelity, derived
  members, member exposure, timestamps, references, numeric tokens, formatting
  and depth. Added fourteen default-contract regressions including real HTTP
  preservation and failure recovery. Current serializer behavior, public APIs
  and dependencies are unchanged. Increased the full-suite time budget to five
  minutes after two coverage runs ended near three minutes with no failing
  assertions but incomplete case counts; the 1717-case minimum remains required.

- Serialize shared ZIP archive lookup/open and entry reads without whole-entry buffering. Preserve stream ownership/capabilities and cancellation, and wake pending archive operations during disposal (upstream #491).

- Preserve managed-listener bytes for pipelined requests, including successors after drained Content-Length bodies. Add isolated JSON/plaintext benchmark endpoints and protocol regressions (upstream #495).

- Restore reasonable upstream EmbedIO JSON behavior transparently: preserve raw string controls, accept trailing commas and lossless legacy number forms, bind quoted booleans/enum names/culture-valid dates/public properties with private setters, return legacy defaults for empty input, and serialize non-finite floating values as named strings. Keep explicit custom options, accurate Unicode/UTC handling and rejection of failed conversions/trailing garbage. See [JSON compatibility](docs/user-reports/json-migration-compatibility.md) for boundaries after SWAN removal; no production dependency or API/target change.

- Stop native queued/new WebSocket data and application callbacks after a valid close request, preserve invalid-parameter and secondary-close cancellation behavior, and observe caller cancellation during the managed acknowledgement wait. Keep the public state enum, listener defaults and existing shutdown policies unchanged (upstream #502).

- Serialize managed WebSocket messages/control-frame writes and native data/Windows close-output sends, honor send cancellation with safe partial-write cleanup, and validate IPv6/default-port prefixes and colon-bearing paths correctly after a selective upstream #534 audit. Preserve public APIs, listener defaults, targets and dependencies.

- Read managed response keep-alive defaults from the parsed request instead of caching them during context construction. Honor connection-close requests and HTTP/1.0 close-delimited responses, preserve explicit application overrides and response-helper ownership, and document one-writer chunked streaming (upstream #510).

## [1.0.3] - 2026-10-07

Patch release with native Basic-authentication and WebSocket lifecycle fixes,
plus the approved opt-in routing, MIME policy, pre-request and WebSocket-message
APIs. Existing defaults, package identities, legacy targets and production
dependencies are retained. See the user-report guides for each API and runtime
limitation. No breaking change is introduced.

- Add optional application-owned FileModule.MimeTypeProvider and awaited non-final PreRequestModule callbacks. Preserve local MIME overrides, fallback/compression negotiation, cache ownership, existing request data and defaults (upstream #521 follow-up).

- Add per-WebApiModule CaseInsensitiveRoutes for invariant-culture controller-route literals, preserving sensitive defaults/mounts and request data. Isolate matchers, reject conflicting case-equivalent declarations and deduplicate same-handler aliases (upstream #521).

- Set Basic authentication challenges through the native response API so Microsoft listeners on .NET Framework do not return 500 for valid credentials. Retain challenge/replacement and credential behavior; add real modern/legacy listener regressions (upstream #524).

- Document host-to-Android-emulator forwarding and extend API 29 CI with distinct host/device ports, remove/recreate checks and owned mapping cleanup; retain production APIs and defaults (upstream #536).

- Document and verify anonymous reads/protected writes in one controller using existing pre-handler hooks, separate authentication/permission checks and untouched JSON binding. Provide explicit demo-token and production-provider boundaries without adding authorization attributes or changing defaults/dependencies (upstream #538).

- Document and verify serving application-owned OpenAPI documents and optional local Swagger UI through existing APIs, including published 1.0.2 binding/validation/asset checks and browser GET/POST execution. Automatic generation is not implemented; production APIs and dependencies remain unchanged (upstream #539).

- Isolate IP-banning configuration, criteria, duration, bans and disposal by module instance. Add scoped TryBanClient/TryUnbanClient/BannedIPs controls while preserving route-wide static methods; keep surviving modules registered for purging and avoid retaining abandoned modules (upstream #545).

- Add opt-in WebSocketMessageModule with separate complete text/binary callbacks, strict UTF-8, per-connection ordering and documented rejection/error handling. Preserve legacy subclasses/frame callbacks and correct native close mappings for unsupported data (1003) and going away (1001). Correct a concurrent test-address allocation race exposed during validation (upstream #547).

- Verify existing SWAN removal and document an application-owned bridge to ILogger providers, with explicit filtering, exception-text and ownership limits and unchanged production dependencies (upstream #548).

- Document the modern SDK/legacy library target policy, published-package asset selection and consumer validation without removing legacy compatibility (upstream #549).

- Evaluate standalone utility packaging, document the current public/dependency inventory and utilities-only reuse, and retain existing assembly/package identities without production changes (upstream #550).

- Evaluate the fluent argument-validator proposal, document built-in .NET guards and legacy-compatible checks, and protect existing validation contracts without adding an API or dependency (upstream #551).

- Document custom trace destinations, bounded UI handoff and managed private-key HTTPS diagnostics for the Unity report, with real diagnostics/TLS regression coverage and unchanged production behavior (upstream #553).

- Document inbound versus outbound UWP/AppContainer loopback access, real HTML/API/session diagnostics and a scoped Windows isolation CI probe without changing production defaults (upstream #554).

- Avoid the Windows native HttpListener WebSocket full-close lock inversion by coordinating close-frame output and a single receiver through public .NET APIs. Preserve completed handshakes, close status/reason and cancellation; recheck against released .NET 11 before considering removal (#105).

- Retain WebSocket messages received before connection initialization completes, synchronize managed dispatch wake-ups, clean up failed initialization and emit one disconnection notification. Guard asynchronous managed callback errors without changing callback APIs or defaults (upstream #556).

- Document and verify early responses with unread request bodies, including the legacy HttpClient sequence and fixed-length connection reuse, without changing production defaults (upstream #558).

## [1.0.2] - 2026-10-05

Patch release with HTTP response framing, HEAD metadata, cookie, static-file and
listener lifetime fixes, plus the merged opt-in SSE and response-preparation APIs.
The five package identities, target frameworks and runtime dependencies are retained.

- Suppress HEAD response bodies and chunk bytes on both listeners while preserving stream validation and asynchronous completion. Synchronize native HEAD length metadata and last property/header assignment; preserve normal GET and connection-close behavior (upstream #564).

- Reduce chunk-prefix and charset-extraction allocations while preserving exact
  wire bytes and legacy attribute selection/errors. Add 42 compatibility cases,
  allocation budgets and chunked HTTP/HTTPS response verification.

- Omit `Content-Encoding: identity` when response negotiation selects no transformation, clearing stale coding metadata while retaining `Vary: Accept-Encoding`, compressed headers and existing negotiation results (upstream #566). Add real-listener media, range, cache and text/JSON regressions.

- Honor configured managed-response encodings and explicit charset parameters. Add the opt-in `FileModule.OnPrepareResponse` callback for per-resource headers before transmission, including cached, conditional and range responses (upstream #567). Preserve unconfigured defaults and file bytes.
- Preserve the selected range length when a static-file request populates a cold content cache; cached full-file bytes no longer overwrite the partial response length.

- Reduce managed listener body-drain and header serialization allocations while
  preserving request/response bytes, framing, cancellation and connection policies.
  Add POST benchmarks, allocation budgets and 26 focused compatibility cases.

- Make managed response close/dispose atomic and idempotent so stale disposal cannot close a newer keep-alive request. Add 15 response-ownership/concurrency cases and document JSON/binary response ownership and existing controller lifetime options.

- Drain synchronous managed socket accepts iteratively; preserve rearming and the
  macOS IPv6 worker. Add 42 framing, fragmentation, burst/reset and queued-accept
  cases. Document confirmed header/framing gaps without changing parsing policy.

- Release managed listener timers, transport wrappers and connection-owned buffers
  on terminal closure; preserve keep-alive and prevent reader restart during forced
  shutdown. Add 22 HTTP/HTTPS and WS/WSS lifetime cases and a dependency-free
  end-to-end benchmark with bounded cleanup verification in desktop CI.

- Open immutable ZIP archives with shared read-only access and release owned handles when archive validation fails. Add 13 regression cases covering multiple hosts/readers, read-only files, disposal and HTTP parity.
- Reduce managed listener queue snapshots and duplicated endpoint bookkeeping.
  Preserve replacement endpoint ownership during alias shutdown; isolate request
  bodies to Content-Length and keep connection disposal from disposing its listener.
  Preserve APIs, defaults and supported request/routing behavior; add measured queue
  allocation budgets and focused compatibility coverage.

- Use true asynchronous managed response writes with caller cancellation; preserve write-error policy. Repair premature chunk termination on empty writes and large-header body-prefix calculations. Add 42 regression cases for 1â€“6 MB files, compression/cache variants, action/JSON responses and backpressure.
- Avoid compiling unused parameterless base-route regexes during configuration.
  Signal synchronous startup readiness without polling; stop failed listeners,
  honor startup cancellation and enforce the documented single RunAsync call.
  Add 31 cold-start and port-reuse regression cases and fresh-process allocation budgets.

- Add MimeType.Xml and MimeType.TextXml constants and document opt-in controller XML responses using existing serializers; preserve default JSON behavior.
- Reduce content-negotiation, header/token parsing, WebSocket metadata and frame-read
  allocations. Avoid rate-history snapshots and preserve request records during
  concurrent purges. Keep existing thresholds, shared history ownership, parser
  behavior, APIs, defaults and dependencies; add 49 regression cases and CI budgets.

- Preserve pre-existing endpoint prefixes when a live alias registration fails; roll back only the prefixes actually added by that attempt.

- Register managed localhost prefixes on both enabled loopback families, with rollback and ownership-safe endpoint cleanup. Keep failed live additions retryable and reject duplicate ownership or incompatible HTTP/HTTPS endpoint registrations.
- Repair static-file cache eviction, replacement accounting and cleaner ownership.
  Reduce HTTP, routing, diagnostics and WebSocket allocations; remove dormant
  internal WebSocket compression paths while preserving compressed-frame rejection.
  Add 50 regression cases and reproducible allocation budgets in desktop CI.
  Public APIs, target frameworks, defaults and dependencies are unchanged.

- Emit configured response cookies during managed WebSocket upgrades (#64),
  preserving their HTTP attributes and separate header lines. Explicit response
  cookies take precedence over legacy request-cookie echoes of the same name;
  unrelated request cookies retain their existing behavior. Add 13 regression cases.

- Preserve separate `Set-Cookie` fields in managed WebSocket upgrade responses
  instead of comma-folding multiple cookies (upstream #583). Legacy request-cookie
  echo and other handshake-header behavior are unchanged.
- Added an opt-in SSE event writer that prepares headers, safely frames multiline
  UTF-8 events and heartbeat comments, flushes each frame and observes cancellation.
  Added a runnable progress server/browser guide for upstream #587. Existing APIs,
  listener defaults, target frameworks and dependencies are unchanged.
- Preserve configured HttpOnly and Secure cookie attributes with the Microsoft
  listener (#59), including late cookie configuration, empty responses and
  successful WebSocket upgrades. Add 22 real-listener regression cases.

- Ensure request-completion callbacks run after final response flush or close
  failures, including canceled streaming handlers (upstream #588). Document SSE
  framing, server cancellation and transport-error handling without changing
  listener defaults or treating client disconnects as server cancellation. Reuse
  the native response stream and close idempotently so Unix response disposal
  does not escalate into fatal listener cleanup.
- Fixed query-data parsing overwriting cached form data within a request (#57).
- Fixed WebSocket negotiation when clients offer multiple subprotocols in one
  header value (#58).
- Fixed static-file HEAD response framing: known representation lengths are
  preserved, and unknown compressed lengths are omitted (#60).
- Fixed native Unix WebSocket completion reacquiring a disposed HTTP response
  stream and shutting down the listener (#62).
- Added 106 regression and boundary cases for listener adapters, WebSocket
  messages, HTTP modules, request binding/bodies, sessions, files and utilities.

## [1.0.1] - 2026-10-05

Patch release with managed-listener shutdown, HTTPS and macOS accept fixes.
The four existing packages retain their identities and target frameworks; the
optional `EmbedIO-Neo.DependencyInjection` package joins the release family.

- Added required native MAUI HTTPS fixtures for Windows, iOS, Mac Catalyst and
  Android, with disposable CA provisioning, normal client/WebView trust checks,
  external HTTPS verification and a reusable physical-device probe for feature #26.
  Simulator/emulator results do not establish legacy Xamarin or physical-device support.

- Mitigated macOS IPv6 accept-completion process crashes after immediate client
  resets (#38). A dedicated background accept worker catches invalid peer-address
  errors and continues accepting, while request/TLS I/O remains asynchronous.
  macOS IPv6 endpoints use one additional thread each; other endpoints retain
  asynchronous accept. Added reset stress and worker shutdown regressions.

- Fixed managed-listener HTTPS requests reporting HTTP URLs (upstream #593).
  Request URL reconstruction now preserves absolute-target path/query case and
  bracketed IPv6 hosts without a port; added real HTTP/TLS regressions and
  platform HTTPS URL-scheme validation.

- Added optional `EmbedIO-Neo.DependencyInjection` integration with per-request
  scopes, constructor and explicit handler-argument injection, awaited resource
  cleanup, and Generic Host lifecycle support. Existing registration APIs and
  core dependency requirements are unchanged. See
  `docs/architecture/dependency-injection.md` for setup, ownership, and limits.

- Fixed internal listener stop/dispose leaving pending accepts and missing Stopped
  notifications; shutdown now drains stale queued contexts without spinning.
  Added cancellation, queue concurrency, disconnect, and two-server regressions
  plus Android two-listener load/lifecycle coverage for upstream #595. The original
  device stall cause is not confirmed.

- Moved managed-listener TLS authentication out of the socket accept callback
  and under the first-request timeout, with pending handshakes tracked for shutdown.
  Added six desktop HTTPS regression cases, Android and Mac Catalyst HTTPS smoke
  coverage, and certificate/trust guidance for feature #26. Platform execution
  evidence and remaining MAUI app-model work are tracked in that feature.

- Added MAUI Android lifecycle and supervised-background-work guidance for upstream
  #597, four real-listener regressions, and a required Android 10 emulator smoke.
  Production behavior is unchanged; the original application crash has no supplied
  reproduction or stack trace.

- Grouped documentation into guides, platforms, compatibility, and project
  folders, with updated navigation and redirects for existing links.

- Organized documentation with a central index and CLI/migration guides under
  docs; retained redirects for previous entry points and removed unused legacy
  branding and IDE settings.

- Added asynchronous outbound HTTP guidance and six real-listener integration
  cases for upstream #598. The original empty-response diagnosis still requires
  a complete reproduction; production behavior is unchanged.

- Documented multiple static-folder mounts and explicit missing-file pass-through
  for upstream #599, with eight regression cases. Routing defaults are unchanged.

- Added upstream #601 regression coverage and MAUI Mac Catalyst startup guidance.
  The reported SWAN Console.WindowHeight failure path was already removed in
  1.0.0. The sandboxed MAUI Mac Catalyst HTTP/WebView smoke passed in CI;
  confirmation on the reporter's original environment remains outstanding.
- Documented opt-in .NET JSON cycle handling for upstream #600 and added eight
  response-serialization regression cases. Existing default behavior is unchanged.

## [1.0.0] - 2026-10-04

First independent Neo release, continuing Unosquare's EmbedIO, Extras JsonServer,
and CLI. Thank you to Unosquare, Mario A. Di Vece, Giovanni Perez, and the original
contributors for the foundation we can maintain and build upon. Original MIT
copyright and embedded third-party notices remain in the source and packages.

The package family is `EmbedIO-Neo`, `EmbedIO-Neo.JsonServer`,
`EmbedIO-Neo.Testing`, and `EmbedIO-Neo.Cli`. Existing assembly names, namespaces,
and the `embedio-cli` command are retained. Neo's version sequence starts at
1.0.0 independently of upstream's 3.x versions.

- Consolidated EmbedIO, JsonServer and the CLI into one repository.
- Added .NET 10 while retaining .NET Standard 2.0 library targets.
- Removed SWAN with approval; see MIGRATION.md for source and binary changes.
- Ported the CLI with approved plugin migration; see CLI.md.
- Added locked builds and the repository-standard CI, security, malware scan,
  update and release-preview workflows.
- Escaped carriage returns and newlines in trace output to prevent request data
  from forging additional log records; diagnostic observers retain the original data.
- Added daily URL path/query fuzzing with a committed corpus and signed package
  provenance through GitHub attestations in the release workflow.

### Approved migration changes

SWAN removal changes configuration, JSON options, logging and exception APIs.
Rebuild downstream applications and follow [MIGRATION.md](docs/compatibility/migration.md), including
validation of JSON client contracts. Archived EmbedIO 2.x CLI plugins must be
rebuilt and adapted as described in [CLI.md](docs/guides/cli.md#provenance-and-migration).
These migrations were approved before implementation; this release adds no
further runtime behavior changes.

### Validation and limits

The 367-case regression suite passes on Windows, Linux and macOS with .NET 10.
Locked package builds, security and malware scans, and the release preview are
required before publication. .NET Standard 2.0 is retained; older runtimes and
.NET 8 have not received equivalent runtime validation. Inherited analyzer
warnings remain. Fuzzing covers URL paths and query data, not listener or
WebSocket-frame parsing. JsonServer's persistence limitations are documented in
CONTRIBUTING.md. No measured performance improvement is claimed.

The detailed initial baseline is recorded in docs/embed_io_neo.md.
