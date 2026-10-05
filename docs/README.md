# Documentation

EmbedIO-Neo keeps the existing `EmbedIO` namespaces and offers a small modular
HTTP/WebSocket server, integrated CLI, JsonServer, and testing helpers.
Start with the [project README](../README.md) for installation and a working server.

## Usage guides

- [Command-line server](cli.md): options, static serving, plugins, and CLI provenance.
- [Multiple static folders](multiple-static-folders.md): mount order and explicit fallback.
- [Asynchronous outbound requests](async-outbound-requests.md): controller return types,
  completed JSON results, and troubleshooting.
- [Circular JSON references](json-circular-references.md): opt-in .NET serialization settings.

## Compatibility and platforms

- [Migration](migration.md): approved SWAN/JSON changes and consumer migration.
- [MAUI Mac Catalyst](maui-mac-catalyst.md): listener startup and sandbox entitlements.

## Project and contribution

- [Neo baseline and direction](embed_io_neo.md): initial fork decisions and acknowledgments.
- [Changelog](../CHANGELOG.md): released and unreleased changes.
- [Contributing](../CONTRIBUTING.md): build, test, and compatibility policy.
- [Security](../SECURITY.md): reporting vulnerabilities.
- [Code of Conduct](../CODE_OF_CONDUCT.md).
- [License](../LICENSE) and [third-party notices](../licenses/README.md).

DocFX configuration remains at the repository root in `docfx.json`. Its content
includes these guides and generated API metadata; documentation deployment is
not configured. The old root `CLI.md` and `MIGRATION.md` remain as short entry
points for existing links.
