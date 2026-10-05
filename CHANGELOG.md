# Changelog

## [Unreleased]

- Ensure request-completion callbacks run after final response flush or close
  failures, including canceled streaming handlers (upstream #588). Document SSE
  framing, server cancellation and transport-error handling without changing
  listener defaults or treating client disconnects as server cancellation.

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
