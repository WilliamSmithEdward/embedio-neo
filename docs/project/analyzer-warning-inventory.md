# Analyzer warning inventory

[Neo #46](https://github.com/WilliamSmithEdward/embedio-neo/issues/46) asked for a
reproducible analyzer-warning baseline, a classification of findings and a record
of any justified suppressions, following the roadmap item in
[upstream #546](https://github.com/unosquare/embedio/issues/546). This document
is that baseline for the Neo v2 engine branch.

## Gates already in force

| Gate | Where | Effect |
| --- | --- | --- |
| `TreatWarningsAsErrors`, `AnalysisLevel` 10.0, `EnforceCodeStyleInBuild`, nullable enabled | `Directory.Build.props`, inherited by every project | Any compiler or analyzer warning fails the build. |
| No `none`, `silent` or `suggestion` severities | `.editorconfig` | No rule is turned down by configuration. |
| `scripts/check_analyzer_suppressions.py` | CI Static analysis | Rejects `#pragma warning`, `SuppressMessage`, `#nullable disable`, `NoWarn`, `WarningsNotAsErrors`, disabled analyzer settings and `WarningLevel` 0 in source and build files. |
| `tools/EmbedIO.AnalyzerGuard` | CI Static analysis | Parses C# and rejects the null-forgiving operator, including in inactive preprocessor branches. |
| `dotnet build EmbedIO.sln -p:RunAnalyzers=true -p:EnforceCodeStyleInBuild=true` | CI Static analysis | Builds the solution with analyzers forced on. Three projects outside the solution get the same build in their own jobs. |

StyleCop was removed in the Neo baseline and is not reinstalled. The API and
nullability changes made to reach a warning-free build are recorded in the
[migration guide](../compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved).

## Baseline

Recorded on 2026-10-11 at engine-branch head eda235ff with SDK 10.0.401 on
Windows, using `python scripts/analyzer_inventory.py`. The script restores from
the lock files, builds the solution and then every project outside it with
analyzers forced on, and counts distinct warning and error lines per build.

| Build | Restore | Result | Warnings | Errors |
| --- | --- | --- | --- | --- |
| EmbedIO.sln (10 projects) | locked | ok | 0 | 0 |
| test/EmbedIO.AppContainerSmoke | locked | ok | 0 | 0 |
| test/EmbedIO.Compatibility/Neo | locked | ok | 0 | 0 |
| test/EmbedIO.Compatibility/NeoStandard | locked | ok | 0 | 0 |
| test/EmbedIO.Compatibility/Upstream | locked | ok | 0 | 0 |
| test/EmbedIO.Conformance | locked | ok | 0 | 0 |
| test/EmbedIO.ContentCodingProbe | locked | ok | 0 | 0 |
| test/EmbedIO.Http1ClosureProbe | locked | ok | 0 | 0 |
| test/EmbedIO.HttpsCertificates | locked | ok | 0 | 0 |
| test/EmbedIO.JsonResponseBenchmark | locked | ok | 0 | 0 |
| test/EmbedIO.LegacyAuthenticationSmoke | locked | ok | 0 | 0 |
| test/EmbedIO.LegacyCookieSmoke | locked | ok | 0 | 0 |
| test/EmbedIO.LegacyHttpEngineSmoke | locked | ok | 0 | 0 |
| test/EmbedIO.LegacyRouteValidationSmoke | locked | ok | 0 | 0 |
| test/EmbedIO.LoadBenchmark | locked | ok | 0 | 0 |
| test/EmbedIO.NativeDependencyProbe/Package | locked | ok | 0 | 0 |
| test/EmbedIO.Performance | locked | ok | 0 | 0 |
| test/EmbedIO.QpackInterop | locked | ok | 0 | 0 |
| test/JsonMigrationProbe | locked | ok | 0 | 0 |

Nineteen builds, zero warnings, zero errors. The suppression checker and the
analyzer guard both pass on the same head.

Three projects are skipped by the script because their target frameworks need
platform workloads that a desktop SDK install does not carry:
`test/EmbedIO.AndroidSmoke` (net10.0-android), `test/EmbedIO.MacCatalystSmoke`
(net10.0-maccatalyst) and `test/EmbedIO.MauiHttpsSmoke` (Windows, iOS, Mac
Catalyst and Android targets). They inherit the same `Directory.Build.props`
gates and are built by their own CI workflows, which are paused by owner decision;
this inventory makes no claim about them.

## Classification

- Analyzer diagnostics classified as correctness, nullable-contract or resource-lifetime findings: none remained in the recorded build.
  The cleanup that reached this state is the owner-approved API work recorded in
  the migration guide; this inventory confirms that diagnostic result rather than repeating it. A warning-free build does not establish absence of semantic bugs.
- Justified suppressions: none exist. The repository policy is to fix the
  diagnostic, and the two guards fail CI on any suppression, so there is nothing
  to document as an accepted exemption.
- Severity downgrades: none exist in `.editorconfig`.

## Reproducing the baseline

```bash
python scripts/analyzer_inventory.py
```

Logs and `summary.tsv` are written under the ignored
`TestResults/analyzer-inventory` folder. The script exits non-zero when any build
fails or reports a warning, so it can serve as a local pre-push check. Run it
after changing analyzer configuration or adding a project outside the solution.

## Narrower gates

The issue asked that narrower gates be added only after their baseline is agreed.
This document is that baseline. The one gate not yet in CI is the sixteen
out-of-solution desktop projects that only their own jobs build: adding the
inventory script to the Static analysis job would make every project's zero
count a required check at the cost of about five extra minutes of restore and
build on one Ubuntu runner. That is a CI policy choice for William and is not made
here.
