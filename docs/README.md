# Documentation

EmbedIO-Neo keeps the existing `EmbedIO` namespaces and offers a small modular
HTTP/WebSocket server, integrated CLI, JsonServer, and testing helpers.
Start with [Your first JSON endpoint](guides/getting-started/README.md)
for a complete app you can run, then choose files or controllers as needed.

## Getting started

- [Your first JSON endpoint](guides/getting-started/README.md): install, run, and get JSON.
- [Serve HTML and files](guides/getting-started/files.md): serve a folder and combine it with an API.
- [Use a controller](guides/getting-started/controllers.md): group endpoints with explicit routes.
- [Routes, verbs, and parameters](guides/getting-started/requests.md): GET, POST, PUT, DELETE, query strings, and JSON bodies.

## Usage guides

- [HTTPS](guides/https.md): certificates, client trust, and desktop/MAUI validation limits.
- [Command-line server](guides/cli.md): options, static serving, plugins, and CLI provenance.
- [Multiple static folders](guides/multiple-static-folders.md): mount order and explicit fallback.
- [Asynchronous outbound requests](guides/async-outbound-requests.md): controller return types,
  completed JSON results, and troubleshooting.
- [Circular JSON references](guides/json-circular-references.md): opt-in .NET serialization settings.

- [Listener stalls](guides/listener-stalls.md): two-server diagnostics, shutdown fixes, and regression scope.

## User reports

- [Logging provider integration](user-reports/logging-provider-integration.md): an application-owned ILogger bridge, mapping, filtering and lifetime ownership after SWAN removal.

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

- [WebSocket startup messages](user-reports/websocket-startup-messages.md): send immediately after connection, initialization ordering and cleanup.
- [Windows native WebSocket shutdown](user-reports/windows-native-websocket-shutdown.md): concurrent close, cancellation and the released .NET 11 recheck.
- [UWP browser access](user-reports/uwp-browser-access.md): inbound/outbound isolation, scoped diagnostics and HTML/API/session verification.
- [Unity custom diagnostics](user-reports/unity-custom-diagnostics.md): trace destinations, UI handoff and private-key HTTPS diagnostics.
- [Argument validation](user-reports/argument-validation.md): built-in guards, legacy contracts and the fluent-validator proposal decision.
- [Utility package extraction](user-reports/utility-package-extraction.md): utility inventory, reuse, assembly identities and the standalone-package decision.

## Architecture

- [Dependency injection](architecture/dependency-injection.md): request scopes, controller ownership, and hosting.

## Compatibility

- [Migration](compatibility/migration.md): approved SWAN/JSON changes and consumer migration.

## Platforms

- [MAUI HTTPS validation](platforms/maui-https-validation.md): native client, WebView and external-client fixtures.
- [MAUI Android](platforms/maui-android.md): background work, listener ownership, and restart diagnostics.
- [MAUI Mac Catalyst](platforms/maui-mac-catalyst.md): listener startup and sandbox entitlements.

## Project and contribution

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
