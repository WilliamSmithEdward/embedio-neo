# Contributing to EmbedIO-Neo

EmbedIO-Neo is maintained by William Smith. Use the issues and pull requests at
https://github.com/WilliamSmithEdward/embedio-neo for this fork.
Please follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Compatibility policy

Enhancements, simplifications, bug fixes, and measured performance improvements
should preserve existing consumers' behavior. Keep public APIs, namespaces,
assembly and package identities, supported target frameworks, configuration,
and HTTP/WebSocket behavior compatible.

Any breaking change requires discussion with William and his explicit approval
before implementation. Document the approved change, its rationale, affected
consumers, and migration steps in the release notes. A major version bump alone
does not substitute for approval.

CI uses the SDK's C# parser in `tools/EmbedIO.AnalyzerGuard` to reject every
null-forgiving expression, including interpolation and inactive preprocessor branches.
Logical negation and exclamation marks in literal text are permitted. The separate
Python guard rejects compiler/analyzer directives and build configuration opt-outs.

## Development

The core library, test helpers, and JsonServer target .NET Standard 2.0 and .NET 10.
The tests target .NET 10.

The required `MAUI HTTPS` CI jobs also run a test-only native app on Windows,
iOS, Mac Catalyst and Android. They exercise platform client trust, HTTPS WebView
rendering and a separate strict HTTPS client. The app and certificate generator
remain outside the solution and shipped packages. See
[MAUI HTTPS validation](docs/platforms/maui-https-validation.md) for pinned
toolchains, per-platform dependency locks, artifacts and physical-device steps.

Install the .NET 10 SDK selected by `global.json` (10.0.400 is the floor;
`latestFeature` allows later feature bands), then run:

```sh
dotnet restore EmbedIO.sln --locked-mode
dotnet build EmbedIO.sln --configuration Release --no-restore
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj --configuration Release --no-build --report-trx --results-directory TestResults --timeout 5m --minimum-expected-tests 2946
```

Keep dependency versions explicit in `Directory.Packages.props`. Pin CI actions
to verified commit SHAs when changing workflows. SDK, test-runtime, and CI
modernization should be reviewed separately from library behavior changes.
Commit every project's `packages.lock.json`. When deliberately updating a package,
run `dotnet restore EmbedIO.sln --force-evaluate`, inspect the lock-file changes
for every target, and verify a subsequent locked restore succeeds.

Tests use NUnit 5, NUnit3TestAdapter's Microsoft.Testing.Platform integration,
and NUnit.Analyzers. `global.json` selects native MTP mode. Use `--project` to
select a project, `--report-trx` instead of VSTest's `--logger trx`, and `--timeout`
for a whole-run timeout (not the old per-test hang timeout). CI checks for at
least 2946 executed/reported test cases to catch accidental discovery loss; update
that baseline deliberately when adding or removing tests. The five-minute suite
budget allows the full coverage-enabled Windows run to finish; the discovery
minimum remains enforced. NUnit 5 async assertions
must be awaited.

CI also runs test-only MAUI Mac Catalyst and Android apps outside the ordinary
solution and shipped packages. The Android fixture uses the pinned .NET 10 SDK,
MAUI workload, locked packages, and an Android 10/API 29 emulator; the job is
included in `CI passed`. Run it independently with
`gh workflow run android-smoke.yml --ref <branch>` and inspect its `android-smoke`
artifact. See [Android hosting guidance](docs/platforms/maui-android.md) for the
tested lifecycle phases and limits. A desktop test pass is not Android evidence.

For coverage, append these options to the test command:

```sh
--coverlet
```

Coverlet's MIT-licensed MTP integration replaces `coverlet.msbuild`. Reports are
written under the results directory and uploaded with TRX reports in CI. Coverage
is collected for the core, JsonServer, and CLI; test utilities are excluded. There is
no coverage percentage gate until a meaningful baseline is agreed.
The explicit filters in `test/EmbedIO.Tests/testconfig.json` avoid Coverlet's
automatic exclusions, which otherwise exclude `EmbedIO.Cli` from this test suite.

StyleCop and its suppression files are removed. Existing explicit CA/IDE rule
severities were migrated from the ruleset to `.editorconfig`; SDK analyzers and
`EnforceCodeStyleInBuild` provide build checks. UTF-8 BOM, indentation, trailing
whitespace, namespace style, and `var` preferences remain configured. StyleCop's
Hungarian-prefix, member-layout, and XML-comment wording checks are intentionally
not reproduced. This is not a claim of identical analyzer coverage.

Format only the files you change, for example:

```sh
dotnet format whitespace EmbedIO.sln --no-restore --include path/to/Changed.cs
dotnet format whitespace EmbedIO.sln --no-restore --verify-no-changes --include path/to/Changed.cs
```

Ordinary fixes should format only changed files; repository-wide formatting belongs
to an explicit cleanup task. All testing and analyzer packages remain development-only.
Library targets are unchanged.

Builds treat compiler and analyzer warnings as errors. CI also verifies formatting
and runs `python scripts/check_analyzer_suppressions.py`, which rejects diagnostic
pragmas, suppression attributes, disabled rule severities, nullable-disable directives,
warning exemptions and analyzer-off build settings. Fix the diagnostic rather than
adding an exemption. SDK analyzers and NUnit.Analyzers provide the lint checks;
no additional runtime dependency is needed. Owner-approved API changes for this
cleanup are documented in [the migration guide](docs/compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved).

## Pull requests

- Keep changes focused and explain the problem and resulting behavior.
- Add regression coverage for bug fixes and tests for new behavior.
- Report the checks run and any limitations. Include reproducible before/after
  measurements for performance claims.
- Update usage documentation when relevant. William manages release versions;
  routine pull requests should not bump them independently.
- Preserve the MIT license, original copyright notices, and third-party notices.
- Obtain William's review before merging. Any accepted breaking change must
  include the approval reference and migration documentation described above.

The approved NuGet IDs are `EmbedIO-Neo`, `EmbedIO-Neo.JsonServer`,
`EmbedIO-Neo.Testing`, and `EmbedIO-Neo.Cli`. Assembly names and namespaces remain
unchanged; downstream users change their package references. Neo's independent
release series starts at 1.0.0. Do not treat upstream releases as Neo releases.



## Dependency policy and remaining work

Prefer built-in .NET APIs. A local implementation may replace a package when
parity tests cover its supported behavior, errors, and edge cases. Keep build/test
tools separate from shipped runtime dependencies. Do not replace dependencies
by copying their public types into a different assembly and claiming binary
compatibility.

SWAN has been fully removed with William's explicit approval. Built-in .NET APIs and local helpers replace its services. The .NET 10 core has no runtime package dependencies; .NET Standard 2.0 requires Microsoft System.Text.Json and its support packages. See [MIGRATION.md](docs/compatibility/migration.md) for the approved breaking changes.

The unused Nullable polyfill and legacy FxCop package have been removed. SDK
analyzers and EditorConfig-based style checks replace StyleCop. Builds treat warnings
as errors, and CI rejects diagnostic suppressions. The console sample and its Tubular, Dynamic LINQ, and frontend dependencies have been removed. Its two grid-only tests were removed with it; core HTTP, routing, WebSocket, and JsonServer coverage remains. The legacy Xamarin sample has also been removed, including its platform projects and WebView dependencies. Both library target frameworks are retained.

## Extras integration

JsonServer was imported from `unosquare/embedio-extras` commit
`62234841c1b261494d0acee7174cfee5dcac60cf`. Its MIT notice is preserved in
`licenses/embedio-extras-LICENSE` and included in its package. The project keeps
the `EmbedIO.JsonServer` assembly/namespace and references this repository's core.
No new runtime package is required. Typed collection operations replace dynamic
binding internally. Tests cover inherited read/CRUD behavior and error statuses,
concurrent writes, and persistence errors.

Request handling and `UpdateDataStore` serialize access within one module
instance. Mutations finish writing before returning success; write errors are
reported instead of disappearing in background tasks. This is a small local
JSON store, not a transactional database: external mutation through `Data`,
multiple processes, rollback after write failure, and crash-safe atomic writes
are not supported by that synchronization.

Other Extras modules were assessed but not imported:

- BearerToken's default provider does not verify passwords and falls back to a
  fixed signing key when passed a null key. A replacement needs an explicit auth
  design, approved contract changes, and security tests. Do not hand-roll JWT
  cryptography merely to remove a dependency.
- Markdown needs a parser with tested CommonMark parity; .NET has no equivalent
  built-in parser. The archived implementation also needs a path-containment review.
- LiteLibWebApi depends on LiteLib/SQLite; a database adapter should stay optional.
- The ASP.NET Core adapter targets old hosting APIs and needs a separate port to
  the modern shared framework with contract tests.

## Initial fork changes

- Fork documentation, ownership metadata, support links, and sample branding now
  point to WilliamSmithEdward/embedio-neo. Original license notices remain intact.
- Libraries add a .NET 10 target while retaining .NET Standard 2.0; tests require .NET 10. CI actions are pinned to commit hashes.
- GitHub Actions replaces the inherited AppVeyor build/publishing configuration.
  Publish prepares GitHub assets and NuGet packages through trusted publishing
  on an approved version tag; manual runs are dry runs. Documentation deployment
  remains unconfigured. NuGet policy registration precedes the first release.
- Wildcard listener prefixes (`*` and `+`) are parsed separately from ordinary
  URI hostnames so wildcard servers can start. Existing namespaces, package IDs,
  and assembly names remain unchanged.
- JsonServer is maintained in this solution, with the persistence corrections
  described above. SWAN removal is an approved breaking change; consumers must rebuild and follow MIGRATION.md.

Before each release, obtain approval for its version, run cross-platform CI and
the publishing dry run, and review API changes against the previous Neo release.
The initial release includes the approved migrations in MIGRATION.md; a full
binary compatibility audit against upstream is not claimed. Older-runtime
validation remains follow-up work. No performance
improvement is claimed without a benchmark.

## CLI integration

The CLI is maintained in `src/EmbedIO.Cli` in this solution, targeting .NET 10
and referencing the local core without new runtime packages. Its command contract,
upstream provenance, local packaging, and approved plugin migration are documented
in [CLI.md](docs/guides/cli.md). `test/EmbedIO.Cli.TestPlugin` is an unpackaged fixture used
to test real assembly loading, controller routes, and WebSocket plugins.

The Windows CI test job also runs the test-only net472 Basic authentication
fixture against the netstandard2.0 core on its installed .NET Framework. It is
outside the ordinary solution and shipped packages; its pinned reference-assembly
package is development-only. See [the report guide](docs/user-reports/basic-authentication-native-listener.md).

The Windows job also runs `test/EmbedIO.LegacyRouteValidationSmoke` outside the ordinary solution and shipped packages. Its pinned `net472` compile target exercises the actual .NET Standard core on the installed Framework CLR, including the BCL numeric exception wrapper and preserved application/query/configuration errors; its 34-check JSON report is uploaded with the test artifacts.


### HTTP/3 transport tests

The modern .NET target uses System.Net.Quic. Local hosts without its native
prerequisites explicitly skip direct QUIC cases while still running the portable
framing/QPACK tests. Desktop CI installs version/hash-pinned MsQuic 2.6.2 packages
on Linux and macOS, and uses the runtime-bundled Windows library. It sets
`EMBEDIO_REQUIRE_QUIC=1` to reject absent capability rather than silently lose
coverage. After preparing the native library, use that environment variable with
`dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --filter FullyQualifiedName~Http3QuicTest`.
See [the engine validation record](docs/project/http-engine.md#required-native-quic-coverage-in-desktop-ci)
for package sources, platform limitations and actual test evidence.
