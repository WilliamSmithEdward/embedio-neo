# Security policy

## Reporting a vulnerability

Report a vulnerability privately, not in a public issue or pull request:
[open a private report](https://github.com/WilliamSmithEdward/embedio-neo/security/advisories/new).
Only the maintainer sees it. Include the commit or version, runtime, operating
system, listener configuration, and the smallest request or steps that show it,
with credentials and private data removed.

A confirmed vulnerability is fixed in a release on the GitHub releases page,
and the advisory is published with it, crediting you unless you ask otherwise.

## Supported versions

Neo's release series starts at 1.0.0. Only the latest Neo release receives security fixes;
archived upstream packages and older releases are not maintained separately.

## Scope

The library parses HTTP requests, headers, cookies, routes, URL-encoded data,
JSON and WebSocket frames. It serves local or embedded files, manages sessions,
and can use TLS certificates. JsonServer reads and writes a configured JSON file.
The CLI serves a directory, watches it, opens a browser, and loads local plugins.
Memory exhaustion, path escapes, protocol confusion, credential exposure, and
incorrect access controls in these implementations are in scope.

### Exposing files and plugins

Only serve directories intended for clients. Filesystem links can point outside
the selected directory. CLI plugins run with the process permissions; use trusted
plugins and a trusted working directory, including for automatic DLL discovery.
The development CLI is not a production security boundary.

### Authentication and persistence

Applications must configure authentication, authorization, TLS and request limits
for their deployment. JsonServer is a small local file store, without transactional
rollback, multi-process locking, or crash-safe atomic writes. Do not expose secrets
or use it where those persistence guarantees are required.

## How the code is checked

Three workflows check every pull request and every push to main, and their gates
decide whether a change can merge: **CI passed**, **Security passed** and
**Malware scan passed**. A gate passes only when every job before it did, and any
unexpected scanner finding fails it, whatever its severity. Security runs weekly;
Malware scan runs daily.

- **Code:** CodeQL security-extended queries for C# and Actions, plus Semgrep's
  default, C#, security-audit, secrets and GitHub Actions rules. SARIF goes to code
  scanning and run artifacts; the report checks findings and scan warnings.
- **Workflows:** zizmor audits the GitHub Actions workflows; a finding fails Security.
- **Dependencies:** NuGet audits direct and transitive locked dependencies; the
  JSON report is checked because the CLI command itself does not fail on findings.
- **Malware:** ClamAV, with signatures freshclam fetches and verifies on every
  run, and YARA-X with pinned YARA Forge rules scan every tracked file and the
  four built packages, packed and unpacked.
- **Fuzzing:** a dependency-free .NET mutation/property harness checks URL paths
  and URL-encoded query data against a committed seed corpus, on parser changes
  and daily. Failures print the seed and input for replay and regression tests.
  This is not a gate and does not cover listener or WebSocket frame parsing.
- **OpenSSF Scorecard** rates the repository's security practices on every change
  to main and weekly, and the README badge shows the result. It is not a merge gate.
  Request and WebSocket protocol parsing remain candidates for expanded fuzzing.

## Accepted findings

A finding is fixed, or accepted with a written reason in
`.github/security/accepted.toml` for security and
`.github/security/malware-accepted.toml` for malware. Security entries match tool,
rule, path and flagged source text; malware entries match tool, rule, path and
SHA-256. An entry that no longer matches fails the report. zizmor keeps its
exceptions in `.github/zizmor.yml` or inline beside the line they excuse, each
with its reason. The ZIP-slip report in `ZipFileProvider` is accepted because this
provider reads entries directly from an archive and never extracts them to filesystem
paths. The accepted entry documents the affected code and the review boundary.

## Pinning and updates

Actions use full commit SHAs, runners named OS releases, scanner images digests,
Python tools hash-locked files, and NuGet packages committed `packages.lock.json`
files. global.json pins the exact SDK with roll-forward disabled, matching the
workflows' SDK pins; the MAUI workload set is pinned for the same feature band.
The runtime has no separate pin in CI: actions/setup-dotnet first installs the
latest LTS runtime to get the newest `dotnet` host and offers no input to skip
it, so framework-dependent test hosts can run on a newer patch than the pinned
SDK carries until the next SDK move. Pinning it would mean replacing
setup-dotnet in every workflow. Test probes that need an exact runtime pin it
with `RuntimeFrameworkVersion` and roll-forward disabled.
YARA Forge rules and the YARA-X engine use releases and SHA-256 digests. ClamAV's
signatures change too often to pin, so freshclam fetches and verifies them.

Dependabot proposes actions, Python and NuGet updates with a seven-day
cooldown, except security updates and the owner's Python packages. The Update
YARA rules workflow proposes pin updates weekly, and the Update images workflow
new ClamAV, Semgrep and peer Go images once they are a week old, a version
rebuilt in place included (GitHub refuses Dependabot's image updates). Relock
Dependabot updates rebuilds a lock Dependabot changed with the lock's own uv or
dotnet restore command. Minor and patch updates and the YARA and image pull
requests merge once the three gates pass; third-party major versions wait for
review.

The Update .NET SDK workflow proposes the newest SDK patch in the pinned feature
band weekly, with the same cooldown, waived when Microsoft marks the release as
a security release. It checks the SDK archive against the SHA-512 in Microsoft's
releases.json before running it and restores with a read-only token. In separate
pull requests it proposes the newest workload set for the pinned SDK patch,
checked against nuget.org's catalog SHA-512, and moves the conformance runner's
pinned SDK image to the SDK global.json names once MCR publishes that tag. The job
that writes accepts only the pin and lock-file changes an SDK move can make.
Because a pull request opened with the workflow's own token starts no workflows,
it starts CI, Security and Malware scan on the branch itself. These pull requests
stay open for review of every exact-head check, and a workload-set pull request
opens as a draft labelled `needs owner decision`, because a workload set can move
the Apple packs to a newer Xcode. The .NET SDK canary builds and
tests with the newest SDK weekly, ahead of the pin; it is informational.

### Semgrep image

Semgrep runs from the `semgrep/semgrep` image pinned by digest in
`.github/security/semgrep/Dockerfile`, as in the other repositories, and runs as
the checkout's user. The image carries Semgrep's own dependencies, so none of
them is a pinned Python lock here. Semgrep is not a runtime dependency of shipped
EmbedIO packages.

## Releases

A pushed version tag runs Publish: it checks the shared version against the tag,
builds the four packages with locked restore, runs Security and Malware scan,
attests the package files, and creates a GitHub release with reports and signed
provenance. The development placeholder 3.5.0 cannot be released. The publish job
uses the `nuget` environment and NuGet/login for the WilliamSmithE profile. The
owner must register the policy for WilliamSmithEdward/embedio-neo, publish.yml,
environment nuget, with glob `EmbedIO-Neo*` and permission to publish new packages
and versions. The approved IDs are EmbedIO-Neo and its JsonServer, Testing and Cli
companions. Both tags and GitHub release titles use `vX.Y.Z`.

Starting Publish by hand is always a dry run. It builds and scans, then stores the
packages, reports and changelog notes as a release-preview artifact. It creates
no release, tag, attestation or NuGet upload.

### Verifying a download

After a release exists, verify its package against GitHub build provenance:

```sh
gh attestation verify path/to/package.nupkg --repo WilliamSmithEdward/embedio-neo
```

The release's `embedio-neo-VERSION.sigstore.json` bundle also supports verification
with `--bundle`. Local development packages have no CI provenance.

## Repository settings

<!-- repo-standards:begin security-settings. Copied from WilliamSmithEdward/repo-standards, templates/security/settings-block.md. Change it there; the weekly rescan fails a copy that differs. -->
- `main` accepts changes only through a pull request that passes
  **CI passed**, **Security passed** and **Malware scan passed**. The
  ruleset has no bypass, for the owner either, and refuses force-pushes and
  deleting the branch.
- A `v*` release tag cannot be moved or deleted once pushed, except by a
  repository admin.
- A workflow that uses an action not pinned to a full commit SHA fails to
  run. Workflow tokens are read-only unless a job is granted more for
  itself.
- Secret scanning with push protection, Dependabot alerts and security
  updates, and private vulnerability reporting are on.
<!-- repo-standards:end -->
