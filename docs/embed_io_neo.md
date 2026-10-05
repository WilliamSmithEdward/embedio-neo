# EmbedIO-Neo: baseline and direction

Prepared October 4, 2026, for the first EmbedIO-Neo commit.

EmbedIO-Neo is an independently maintained continuation of
[EmbedIO](https://github.com/unosquare/embedio), maintained by William Smith at
[WilliamSmithEdward/embedio-neo](https://github.com/WilliamSmithEdward/embedio-neo).
This document records the fork's initial source baseline. William authorized the
first public repository push on October 4, 2026. This is not a package release;
NuGet publication and release versioning remain separate decisions.

## Thanks to the original maintainers

Unosquare, Mario A. Di Vece, Geovanni Perez, and the original contributors deserve
our sincere thanks. They built a compact, approachable web server with a useful
modular design, an extensive set of HTTP and WebSocket features, and tests that
give this continuation a practical foundation. Their decision to share that work
under the MIT license makes it possible to maintain and extend it today.

We also appreciate the work behind EmbedIO Extras and the original CLI. Those
projects demonstrated useful ways to build on the server and now inform the
capabilities being brought together here. Neo builds on those contributions;
it does not claim original authorship of them or official affiliation with
Unosquare. Original copyright, permission, and applicable third-party notices
remain in [LICENSE](../LICENSE) and [licenses](../licenses).

## One repository

The core library, JsonServer, CLI, testing helpers, and test fixtures now live in
one repository and solution. They share build conventions, versioning, CI, and
documentation. Separate projects express their different purposes; they are not
separately maintained forks or external dependencies on the archived packages.

| Project | Purpose | Target |
| --- | --- | --- |
| `EmbedIO` | HTTP server, routing, files, sessions, Web API, and WebSockets | .NET Standard 2.0 and .NET 10 |
| `EmbedIO.JsonServer` | File-backed JSON collections and CRUD endpoints | .NET Standard 2.0 and .NET 10 |
| `EmbedIO.Cli` | `embedio-cli` development server and plugin host | .NET 10 |
| `EmbedIO.Testing` | Helpers for consumers testing server modules | .NET Standard 2.0 and .NET 10 |
| `EmbedIO.Tests` / `EmbedIO.Cli.TestPlugin` | Regression tests and a real CLI plugin fixture | .NET 10; not published |

## Changes prepared for the baseline

### Fork identity and repository preparation

- Updated ownership metadata, support links, README, contribution guidance, and
  branding for EmbedIO-Neo. William's supplied logo and icon are used by the README
  and documentation configuration.
- Preserved original licensing and added attribution for imported Extras and CLI
  work. Local agent instruction files are ignored by Git.
- Restored complete notices for retained MimeTypeMap, Mono networking, and
  websocket-sharp source. The root LICENSE carries those notices into packages;
  [source attribution references](../licenses/README.md) record the review scope.
  The inherited implementations remain in use, with no new runtime dependencies.
- Corrected unverified README platform claims, limited CodeQL to the remaining
  C# codebase, and removed Git-flagged trailing whitespace without changing logic.
- Configured the intended GitHub remote. Removed inherited publishing configuration
  that targeted the original project. The initial baseline had no publication
  workflow; the subsequent standards onboarding adds the release path below.
- The local folder is `F:\GitHub\embedio-neo`, and repository links and the Git
  remote use `WilliamSmithEdward/embedio-neo`.
- Retained inherited library assembly names, namespaces, and package identities
  where practical. The inherited `3.5.0` version is a development placeholder,
  not a claim that these changes are a compatible upstream 3.5 release.

### Runtime and dependency modernization

- Added a .NET 10 library target while retaining .NET Standard 2.0 for legacy
  consumers. Builds use the pinned .NET 10 SDK feature band and C# 14.
- Removed SWAN entirely. Configuration locking, diagnostics, background scheduling,
  conversion, and stream helpers now use local implementations and .NET APIs.
- Replaced SWAN JSON serialization with System.Text.Json. The default wrapper
  retains dictionary/list shapes and decimal numbers for untyped JSON. Focused
  tests cover selected SWAN parity cases; arbitrary serializers, attributes,
  formatting, and all edge cases are not claimed to be identical.
- Removed the unused Nullable polyfill and legacy FxCop tooling.
- Removed the console demo and Xamarin sample applications with William's approval.
  Their Tubular, Dynamic LINQ, frontend, Xamarin, and WebView dependencies are gone.
  Two sample-only grid tests were removed; library regression coverage was retained.

The .NET 10 production projects have no external runtime packages beyond .NET
and references to our own projects. The .NET Standard build retains Microsoft's
System.Text.Json 10.0.12 and its supporting packages. Inherited source, including
the MIME table and Mono-derived networking, remains part of the core and retains
its attribution. Dependency-free packaging does not mean all source was authored
by Neo's maintainer.

### Extras and CLI consolidation

JsonServer was imported from `unosquare/embedio-extras` commit
`62234731c1b261494d0acee7174cfee5dcac60cf`. Typed collection operations replace
internal dynamic binding. Request mutations and `UpdateDataStore` serialize
access within a module instance, complete persistence before reporting success,
and expose write failures. It remains a small JSON file store, with no transaction
rollback, multi-process coordination, or crash-safe atomic-write guarantee.

The CLI was ported from `unosquare/embedio-cli` commit
`a7541483bcbecafcbaaddfb0f5c73ea48774f00b`. It retains the `embedio-cli` command,
path/port/API options, root selection, browser launch, static serving, directory
listings, and live reload on the companion port. The port adds clean cancellation,
visible startup errors, recursive watching, and `--no-browser`; `--no-watch` now
disables script injection as well as watching. Current core file handling replaces
the archived hand-written static server. Plugin loading uses the shared core
assembly and supports current Web API controllers and WebSocket modules.

The tool packs locally as `EmbedIO-Neo.Cli`, using the shared version. This is a
prepared package identity, not an existing published release. See [CLI.md](../CLI.md)
for commands, plugin requirements, and local installation.

Other Extras modules were assessed rather than imported wholesale. Bearer-token
authentication needs a sound credential-validation design; Markdown needs a
tested parser and path handling; LiteLib/SQLite should remain optional; the old
ASP.NET Core adapter needs a modern port. Their status is documented in
[CONTRIBUTING.md](../CONTRIBUTING.md#extras-integration).

### Fixes and development tools

- Fixed wildcard listener-prefix parsing so `*` and `+` prefixes can start.
  Platform checks use built-in runtime information.
- Improved startup-error visibility and resource cleanup in affected tests.
  DNS expectations follow platform behavior rather than assuming identical
  hostname resolution on every OS.
- Migrated tests to NUnit 5.0.0, NUnit3TestAdapter 6.3.0, and native
  Microsoft.Testing.Platform. Async assertions are awaited and null-input test
  annotations now match their inputs.
- Added NUnit.Analyzers 4.15.0, Coverlet MTP 10.1.0, and TRX reporting. These are
  development dependencies, not shipped production dependencies.
- Replaced StyleCop with SDK analyzers and EditorConfig-based checks. Explicit
  inherited CA/IDE severities were migrated; StyleCop-only checks are not claimed
  to have exact replacements. Existing warnings remain visible.
- Configured pinned GitHub Actions for Windows, Linux, and macOS builds, tests,
  coverage, and result artifacts. CI checks a minimum test count to detect lost
  discovery. Cross-platform workflow execution awaits publication of the repo.

## Approved compatibility changes

Compatibility remains the default. William explicitly approved full SWAN removal
and the CLI's .NET 10/current-API port. Consumers using SWAN-exposed APIs must
adapt and rebuild. Archived EmbedIO 2.x CLI plugin binaries must be rebuilt and
ported to current controllers, routes, and WebSocket modules.

These changes are documented in [MIGRATION.md](../MIGRATION.md) and
[CLI.md](../CLI.md#provenance-and-migration). They require an appropriate release
version; retaining the old version in the working tree is not release approval.
Removing the sample applications does not remove the library's legacy target.

## Validation at this baseline

- Release solution build succeeds, including both library targets.
- Full suite on Windows/.NET 10: **366 cases, 364 passed, 2 platform-related skips**.
  This includes 19 CLI cases covering arguments, roots, files, API-only mode,
  plugin files/directories, WebSockets, nested file watching, cancellation, and
  listener-startup failure cleanup.
- Verified Cobertura coverage for the core, JsonServer, and CLI. Line coverage in
  this Windows run is 61.97%, 86.44%, and 78.12%, respectively. Explicit test
  configuration replaces Coverlet's automatic exclusions, which had excluded
  the CLI assembly. These measurements establish a baseline, not a coverage gate
  or evidence of performance improvement.
- The local CLI package was installed into an isolated test directory. Help,
  invalid-argument exit status, and HTTP serving from the installed tool passed.
  Package contents include our core assembly and the preserved license notices.
- After the source-notice cleanup, all four local packages were rebuilt and
  inspected. Each contains the complete root LICENSE; CLI and JsonServer also
  contain their respective upstream notices. Git's whitespace check passes,
  and the C# cleanup diff contains no changes when whitespace is ignored.
- Earlier validation also ran the library suite with .NET Standard assemblies on
  .NET 10. This does not substitute for tests on older runtimes.

Build warnings are not yet eliminated. .NET 8 runtime testing and legacy runtime
coverage remain follow-up work. The pre-push source-notice review confirmed the
known inherited components and restored their notices; its pinned references and
historical-provenance limits are recorded in [licenses/README.md](../licenses/README.md).

## Direction from here

1. Preserve useful legacy compatibility while making .NET 10 the preferred path.
   Keep .NET Standard 2.0; add actual .NET 8 runtime validation before promising
   tested support there.
2. Favor small, understandable implementations and built-in .NET APIs. Replace
   dependencies only with evidence of functional parity and acceptable maintenance
   cost. Keep third-party tools where they provide clear testing value.
3. Enhance and fix the existing server incrementally. Require reproducible
   measurements for performance claims and regression tests for behavior changes.
4. Expand the unified feature set selectively. Authentication, parsers, persistence,
   and hosting adapters need explicit designs and tests, not mechanical imports.
5. Reduce inherited warnings and verify networking edge cases in focused changes.
   Complete cross-platform CI and package/API review before release, and maintain
   source attribution as implementations change.
6. Discuss and document future breaking changes before implementation. William
   controls release identities, versions, publication, and when this prepared
   baseline is committed or pushed.

## Repository standards onboarding

The next maintenance step adopts William's `repo-standards` as the controlling
configuration. CI, Security and Malware scan provide aggregate merge gates;
SDK and NuGet dependencies are locked, actions use full SHAs, and scanner images
and YARA assets are pinned. SECURITY.md describes the scope and reporting policy.
The standard AGENTS.md block and CLAUDE.md import are tracked instead of ignored.

Publish builds GitHub package assets with security/malware reports and provenance
from approved version tags. Manual runs produce a release-preview artifact only.
The owner approved preparation of NuGet trusted publishing through the nuget
environment and WilliamSmithE profile, and registered the policy. Approved package
IDs are EmbedIO-Neo and its JsonServer, Testing and Cli companions, with existing
assembly names and namespaces retained. Tags and release titles use vX.Y.Z.
The inherited development version cannot be tagged as
a release. README examples were corrected and the
complete server and WebSocket snippets compiled against the current library.
Scheduled fuzzing mutates a committed corpus for URL paths and query data; the
first local seed passed 100,000 iterations. Listener and WebSocket frame fuzzing
are not claimed by that harness.

The onboarding security review also escaped CR/LF in trace output to prevent
request data from forging log records. Diagnostic observers still receive the
original message. The local suite now has 367 cases (365 passed, 2 platform skips).
The archive provider's ZIP-slip report is documented as a false positive because
it reads entries directly and never extracts them to filesystem paths.

The aim is a maintained, coherent EmbedIO continuation: easier to build, test,
understand, and extend, while treating existing users and the original authors'
work with care.
