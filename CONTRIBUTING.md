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

CI requires the Mac Catalyst smoke job and the Windows, Mac Catalyst and Android
MAUI HTTPS platform jobs to succeed on every code change. The Android lifecycle
smoke runs on pushes to `main` and manual dispatches only. The iOS HTTPS job
and the Windows AppContainer smoke are manual only (owner decision,
2026-10-15): `gh workflow run maui-https.yml --ref <branch>` includes iOS by
default, and `gh workflow run appcontainer.yml --ref <branch>` runs the
AppContainer smoke. Their reusable workflows and standalone manual workflows
are enabled at William's request. Skipped jobs do not supply platform
validation evidence.

The HTTPS jobs run a test-only native app on Windows,
iOS, Mac Catalyst and Android. They exercise platform client trust, HTTPS WebView
rendering and a separate strict HTTPS client. The app and certificate generator
remain outside the solution and shipped packages. See
[MAUI HTTPS validation](docs/platforms/maui-https-validation.md) for pinned
toolchains, per-platform dependency locks, artifacts and physical-device steps.

Install the .NET SDK version that `global.json` pins exactly (`rollForward`
`disable`, so a newer SDK on the machine or runner is never picked up
silently). The pin is exact because the lock files record the SDK's implicit
packages, so every machine must restore with the same SDK. The trade-off is a
local one: you need that SDK installed, from the
[.NET downloads](https://dotnet.microsoft.com/download/dotnet/10.0) or with the
`dotnet-install` script and `--version <pin>`, and after the pin moves `dotnet`
refuses to work in the checkout until you install the new one.

SDK moves are deliberate and automated. Each Monday `update-dotnet-sdk.yml`
proposes the newest SDK patch in the pinned feature band as a pull request that
moves `global.json`, the workflows' `dotnet-version` pins, the test probes'
`RuntimeFrameworkVersion` pins and the lock files together. It waits out a
one-week cooldown unless Microsoft marks the release as a security release. In
separate pull requests it moves the MAUI workload `--version` to the newest
workload set for the pinned SDK patch (the set decides the mobile apps' Mono
runtime and their Apple and Android packs, which can need a newer Xcode), and
the conformance runner's SDK image once MCR publishes it. Workload-set pull
requests open as drafts labelled `needs owner decision` and wait for the owner.
It never changes feature band; that is left to a person.
`dotnet-sdk-canary.yml` builds and tests with the newest SDK each Monday,
without the pins, as an early warning; it gates nothing. A move by hand follows
the same rules. Then run:

```sh
dotnet restore EmbedIO.sln --locked-mode
dotnet build EmbedIO.sln --configuration Release --no-restore
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj --configuration Release --no-build --report-trx --results-directory TestResults --timeout 8m --minimum-expected-tests 6323
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
least 6323 executed/reported test cases to catch accidental discovery loss; update
that baseline deliberately when adding or removing tests. The full coverage
suite budget is eight minutes on Windows/Linux and twelve minutes for macOS CI
coverage. William approved the macOS increase on 2026-10-13 after CI
38421579986 stopped at eight minutes with 5,408 of 5,943 cases reported (zero
reported case failures), leaving incomplete validation. The new discovery floor
is 6,000. The original failure remains retained; all cases must finish and pass.
William approved the earlier Unix
allowance on 2026-10-10 and requested loosening the Windows allowance on
2026-10-11 after a coverage run reported all 4,976 cases with zero failures but
its host ended at the five-minute boundary. A longer budget does not classify
that host exit as resolved; the changed-source CI run must finish successfully.
Per-case deadlines, assertions, iterations, coverage and the discovery minimum
remain enforced; this is a whole-run allowance, not a failure exemption. NUnit 5
async assertions must be awaited.
The Test job allows 20 minutes on macOS and 15 elsewhere, so the macOS suite
budget plus setup fits: CI 38479185526 finished all 6,056 macOS cases one
second before a 15-minute job limit cancelled it (approved 2026-10-13).

CI also runs test-only MAUI Mac Catalyst and Android apps outside the ordinary
solution and shipped packages. The Android fixture uses the pinned .NET 10 SDK,
MAUI workload, locked packages, and an Android 10/API 29 emulator; the job runs
on pushes to `main` and manual dispatches, and `CI passed` expects it skipped on
pull requests. Run it independently with
`gh workflow run android-smoke.yml --ref <branch>` and inspect its `android-smoke`
artifact. See [Android hosting guidance](docs/platforms/maui-android.md) for the
tested lifecycle phases and limits. A desktop test pass is not Android evidence.

For coverage, append these options to the test command:

```sh
--coverlet
```

Coverlet's MIT-licensed MTP integration replaces `coverlet.msbuild`. Reports are
written under the results directory and uploaded with TRX reports in CI. CI passes
`--coverlet` only on pushes to `main`; pull requests run the same suite, floor
and timeouts without the instrumentation, since nothing consumes the reports
there. Coverage is collected for the core, JsonServer, and CLI; test utilities
are excluded. There is no coverage percentage gate until a meaningful baseline
is agreed.

A pull request that changes only documentation (`docs/**`, `images/**`, any
`*.md`, `docfx.json`, `toc.yml`; the `changes` job in `ci.yml` lists the exact
patterns) skips the test matrix, the compatibility jobs and the platform app
smokes. Static analysis, the package build (which still packs and checks
`README.md` and the icon) and `CI passed` run on every pull request. A code pull
request runs the compatibility jobs on Windows and Linux; pushes to `main` and
manual dispatches run them on macOS as well, plus the Android lifecycle smoke.

The opt-in QUIC and runtime diagnostics (`quic-cleanup-experiment`,
`quic-self-contained-experiment`, `quic-openssl-experiment`, `quic-rebind-probe`,
`runtime-close-probe`, `quic-retirement-gate`) are in `diagnostics.yml`, which
runs only by hand: `gh workflow run diagnostics.yml --ref <branch> -f <input>=true`.
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
to an explicit cleanup task. CI also checks the projects outside `EmbedIO.sln`, the MAUI
apps included, in folder mode, which loads no project and needs no restore or workload:

```sh
dotnet format whitespace test --folder --verify-no-changes
dotnet format whitespace tools --folder --verify-no-changes
```

All testing and analyzer packages remain development-only. Library targets are
unchanged.

Builds treat compiler and analyzer warnings as errors. CI also verifies formatting
and runs `python scripts/check_analyzer_suppressions.py`, which rejects diagnostic
pragmas, suppression attributes, disabled rule severities, nullable-disable directives,
warning exemptions and analyzer-off build settings. Fix the diagnostic rather than
adding an exemption. `python scripts/analyzer_inventory.py` builds the solution and
every project outside it with analyzers forced on and records per-project warning
counts under the ignored `TestResults/analyzer-inventory` folder; the recorded
baseline is in [the analyzer warning inventory](docs/project/analyzer-warning-inventory.md).
SDK analyzers and NUnit.Analyzers provide the lint checks;
no additional runtime dependency is needed. Owner-approved API changes for this
cleanup are documented in [the migration guide](docs/compatibility/migration.md#warning-free-api-cleanup-neo-v2-owner-approved).

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

SWAN has been fully removed with William's explicit approval. Built-in .NET APIs and local helpers replace its services. The core references the pinned System.Threading.RateLimiting package for the optional limiter integration. The .NET Standard 2.0 asset additionally requires System.Text.Json and its support packages; inspect each package dependency group when upgrading. See [MIGRATION.md](docs/compatibility/migration.md) for the approved breaking changes.

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

The QUIC lifetime retirement gate is opt-in. Its four owned restart cases are
skipped unless `EMBEDIO_QUIC_RETIREMENT_GATE=1`; select them with the raw
runtime rebind cases using
`--filter "TestCategory=QuicLifetimeRetirementGate|FullyQualifiedName~QuicRuntimeRebindTest"`,
or dispatch `diagnostics.yml` with the `quic-retirement-gate` input. The gate tolerates no
retry and applies no quarantine, so a runtime whose MsQuic still keeps a
disposed listener's port bound fails it by design. See
[QUIC lifetime and rebind](docs/project/quic-lifetime.md#retirement-gates).

Native QUIC module lifetime is also checked in three fresh processes on every
desktop CI runner, before coverage. The seven-case subset avoids initializing
System.Net.Quic and requires normal test-process exit; Linux additionally checks
stable mappings across 513 API lifecycles. See
[standalone module lifetime](docs/project/native-quic-module-lifetime.md).
