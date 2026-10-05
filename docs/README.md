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

- [Request URL scheme and HTTPS](user-reports/request-url-scheme.md): transport security and URL reconstruction fixes.

## Compatibility

- [Migration](compatibility/migration.md): approved SWAN/JSON changes and consumer migration.

## Platforms

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
