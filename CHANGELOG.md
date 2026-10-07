# Changelog

## [Unreleased]

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
