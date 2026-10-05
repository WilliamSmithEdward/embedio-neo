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

Neo has no published release yet. Fixes currently target the latest source on
main. Once released, only the latest Neo release receives security fixes;
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
- **OpenSSF Scorecard** rates the repository's security practices on every change
  to main and weekly, and the README badge shows the result. It is not a merge gate.
  No dedicated C# fuzzing workflow is configured yet; request and protocol parsers
  remain candidates for a future fuzz harness, beyond existing regression tests.

## Accepted findings

A finding is fixed, or accepted with a written reason in
`.github/security/accepted.toml` for security and
`.github/security/malware-accepted.toml` for malware. Security entries match tool,
rule, path and flagged source text; malware entries match tool, rule, path and
SHA-256. An entry that no longer matches fails the report. zizmor keeps its
exceptions in `.github/zizmor.yml` or inline beside the line they excuse, each
with its reason. No scanner findings have been accepted at onboarding preparation.

## Pinning and updates

Actions use full commit SHAs, runners named OS releases, scanner images digests,
Python tools hash-locked files, and NuGet packages committed `packages.lock.json`
files. The SDK floor is recorded in global.json with latestFeature roll-forward.
YARA Forge rules and the YARA-X engine use releases and SHA-256 digests. ClamAV's
signatures change too often to pin, so freshclam fetches and verifies them.

Dependabot proposes actions, Docker, Python, NuGet and SDK updates with a seven-day
cooldown, except security updates and the owner's Python packages. The Update YARA
rules workflow proposes pin updates weekly. Minor and patch updates and YARA
updates merge once the three gates pass; third-party major versions wait for review.

## Releases

A pushed version tag runs Publish: it checks the shared version against the tag,
builds the four packages with locked restore, runs Security and Malware scan,
attests the package files, and creates a GitHub release with reports and signed
provenance. The development placeholder 3.5.0 cannot be released. The publish job
uses the `nuget` environment and NuGet/login for the WilliamSmithE profile. The
owner must register the policy for WilliamSmithEdward/embedio-neo, publish.yml,
environment nuget, and approve fork-specific package identities before release.

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
