Operating Model(s) -->

1.) F:\GitHub\RIDM_Recursive_Invariant_Discovery_Model\RIDM.MD

High Level Guardrail(s) -->

1.) F:\GitHub\AI_Best_Practices\docs\agentic_ai_programming_best_practices.md
2.) F:\GitHub\AI_Best_Practices\docs\ai_smells_for_agents_to_avoid.md

# Fork maintenance rules

- Preserve compatibility by default. Any breaking change requires discussion with William and his explicit approval before implementation, plus release notes and migration documentation.
- Follow CONTRIBUTING.md for development and validation. Preserve existing license and third-party notices.

## Documentation and generated files

- Maintain the docs/README.md index when adding or reorganizing guides. Keep usage guides under docs/guides, platform guidance under docs/platforms, migration guidance under docs/compatibility, and baseline/project documents under docs/project. Preserve existing public entry-point and section links where practical, and include all guides in DocFX navigation/content.
- Write local build/test logs under ignored TestResults; do not clutter the repository root. Archived pre-baseline logs are under TestResults/root-logs. Keep licenses, active build configuration, and repo instructions intact.

## Pull request completion

- William gives standing authorization to enable squash auto-merge on completed, validated PRs handled in this repository, once the proposed change is ready for review. Use the required repository checks; never bypass them. No separate auto-merge confirmation is needed. Preserve human credits and remove AI Co-authored-by trailers from the final merge message. This does not authorize releases, new backlog issues, or unapproved breaking changes.

- William gives standing authorization to close issues once resolution is confirmed and there is no clear remaining reason to keep them open. Use a PR closing reference (for example, Closes #13) when appropriate, or close manually after verified resolution. Keep issue descriptions and existing outcome replies current with actual merge/release status; do not leave a resolved configuration question open solely for optional reporter confirmation. Record concrete unresolved work when an issue stays open. Avoid duplicate public replies and notifications.

- GitHub parses closing keywords even in negated prose: PR #15's phrase "does not close #10" inadvertently closed #10 at merge. It was reopened and the PR description corrected. Never place close/closes/closed, fix/fixes/fixed, or resolve/resolves/resolved directly before an issue reference unless closure is intended; say "must remain open" instead. Verify closingIssuesReferences before enabling auto-merge and verify issue state after merge.

## Upstream backlog workflow

- Use the title format `Upstream #<number>: <specific description>` consistently for migrated backlog issues and their associated PRs. Keep the upstream number at the beginning; use sentence case for the description. Align existing related titles when correcting a mismatch.

- Keep public issue descriptions, PR descriptions, and contributor replies specific to the individual issue and its relevant technical evidence. Do not include unrelated backlog status, other issues' validation gaps, or internal agent workflow narration. Store broader work-selection and coordination context in repo memory; link another issue only when it is technically relevant or a genuine dependency.

- Work on one issue from https://github.com/unosquare/embedio/issues at a time. William selects the issue; do not select or start another without his explicit approval. William selected upstream #600, tracked as WilliamSmithEdward/embedio-neo#13. PR #14 merged and #13 was closed as answered at William's request. William subsequently selected upstream #599, tracked as fork #16; PR #17 merged and #16 is closed as answered. William selected upstream #598 next, tracked as fork #18 with PR #19; no other issue is selected. #601/#10 remains open specifically because the original Sonoma 14.5 / M1 Pro environment is unavailable for validation. PR #11 merged after its conflicts were resolved and all required checks, including the Mac Catalyst smoke, passed.
- Fully triage every issue and PR: assign WilliamSmithEdward, set relevant labels, select the appropriate milestone, and link its development branch/PR. Add meaningful parent/sub-issue or blocking relationships when they exist; explicitly record when none apply. Do not invent dependencies or release commitments. William does not use GitHub Projects; leave Projects intentionally unused and do not request project access. If other access prevents a field from being set, report the exact limitation and keep it as outstanding work.
- Create the tracking issue and fix PR in WilliamSmithEdward/embedio-neo. The archived upstream repository is read-only; do not attempt to post there.
- Aggregate the relevant original report, reproduction details, discussion, user comments, and proposed solutions into the tracking issue. Preserve authorship, distinguish quotations from summaries, and link to the original issue and individual comments. Do not present migrated comments as new posts by their original authors.
- Follow CONTRIBUTING.md and F:\GitHub\AI_Best_Practices\docs\agentic_ai_programming_best_practices.md. Reproduce the concern, implement a focused fix, add meaningful regression coverage, validate, and document the outcome. Preserve compatibility; breaking changes still require William's explicit approval.
- Once a substantive outcome is ready, post the fix information in a comment on the fork's tracking issue, in William's voice. William authorizes tagging all human participants and reaction authors on the original issue and its comments except the former maintainers. Read paginated reaction lists, include every emoji type, and deduplicate users already mentioned. Establish the participants and former-maintainer identities from evidence before mentioning anyone; exclude bots and avoid duplicate notifications.
- Explain respectfully that the original project was archived and William is carrying it forward through EmbedIO-Neo. Offer support in a spirit of community and goodwill, thank participants for their work, and invite them to try the fix and report whether it resolves their concern. Do not imply endorsement by the former maintainers.
- Use William's public contributor reply format: open with participant @mentions and a warm, specific thank-you; state the outcome early; insert `---` before the technical explanation and again before the closing; thank them again and sign `William` on its own line. Link the fork issue/PR and relevant validation. Distinguish proposed, awaiting checks, merged, and released status; name a release or NuGet availability only when verified.
- For platform-specific reports, configure CI that exercises the relevant runtime and app model where practical. For #601, add a dedicated MAUI Mac Catalyst smoke app on a compatible GitHub macOS runner, with pinned SDK/workload/package versions, sandbox/network entitlements, and validated signing/launch steps. Start the real EmbedIO listener and fetch local HTML; capture failures and test artifacts. Keep test-only platform dependencies outside production packages and the ordinary solution build.
- Docker downloads and the installed Docker engine are authorized for reproduction and testing. Linux containers cannot reproduce Apple runtimes or sandbox behavior. Existing macOS desktop regression tests are not a substitute for MAUI Mac Catalyst validation; do not describe a configured job as passing until its run succeeds. Do not claim reproduction of Sonoma 14.5 / M1 Pro unless that environment was actually tested.
- Previous #601 status: fork issue #10 exists, two focused Windows tests passed, and the full Windows suite passed with 367 successes and two platform skips (369 total). The sandboxed MAUI Mac Catalyst HTTP/WebView smoke passed in GitHub run 37256447885 on macos-26 with runtime 10.0.12. PR #11 merged after reconciliation with main: the combined Windows suite passed with 375 successes and two skips (377 total). Required CI/security/malware checks passed, including the final Mac Catalyst smoke in run 37258096948. #10 stays open at William's direction because the original Sonoma 14.5 / M1 Pro hardware/runtime environment has not been tested. William subsequently authorized #600; do not start any further issue without approval. GitHub Projects are intentionally unused by William.
- Current #600 status: fork issue #13 aggregates the report; no original comments or reactions were present at migration. PR #14 adds a guide and eight regression cases without production/default changes. The full Windows suite passed (373 successes, two skips, 375 total); Windows/Linux/macOS CI tests passed in run 37257136628. The outcome reply was posted as William at issuecomment-5987320253, mentioning andraschris and asking for reporter confirmation. PR #14 merged into main as df1ba28; #13 was closed as answered after the issue body and existing outcome reply were updated. The original payload was not supplied; invite a minimal example if the reporter needs the issue reopened. Do not start another issue without approval.
- Current #599 status: fork issue #16 attributes WillCrystian's multiple-static-folder question; no comments/reactions existed at migration. Eight focused cases passed using real temporary directories and the in-process HTTP pipeline. Full Windows suite passed: 383 successes, two existing skips, 385 total. Existing APIs support nested children, specific mounts before a catch-all, and HandleMappingFailed(FileRequestHandler.PassThrough) overlays. Documentation/tests only; defaults, dependencies, and production APIs unchanged. PR #17 merged as d994177 after required CI/security/malware gates passed, including Windows/Linux/macOS and Mac Catalyst tests in run 37259224248. #16 closed automatically; its body and existing outcome reply (5987551892) now state merged and answered status. Stable-main release dry run 37259458132 succeeded; its release-preview was downloaded and all four packages passed checks against exact Git source bytes (the Windows working tree uses CRLF). The earlier branch preview lost its ref during a CodeQL upload after auto-merge deletion. No release was published. Do not start another issue without William's approval.
- Local Windows test firewall approval: William authorized narrowly scoped rules for the current root and issue-600/601 EmbedIO.Tests.exe apphosts. Three enabled rules named EmbedIO-Neo-Local-Tests-0/1/2 allow inbound TCP only with local and remote address 127.0.0.1, on all profiles. A real localhost listener regression passed after setup. Do not disable the firewall or global notifications. New worktree executable paths may need their own scoped approval; do not assume these rules cover future paths. Setup, original-rule snapshot, and validation artifacts are under ignored TestResults/firewall-approval.
- An upstream report is not resolved merely because it was copied. If required reproduction or confirmation still needs input, state the concrete limitation and keep the issue open; optional feedback alone does not prevent closure after confirmed resolution. Do not merge or publish merely to claim resolution. After completing the selected issue, wait for William's approval before moving to another.

- Current #598 status: migrated to fork #18; PR #19 adds an async response guide and six real-listener cases. All six and the full Windows suite passed (389 successes, two existing skips; 391 total). PR #19 merged as a8e1830 after CI 37261711613, Security and Malware gates passed; Windows/Linux/macOS and Mac Catalyst passed. No closing references were present; #18 remains open. Stable-main Publish dry run 37261901938 succeeded; all four downloaded packages passed checks against exact merged-source README/license bytes and the security/malware reports identify a8e1830 with no unexpected findings. No release was published. Upstream issue/PR titles for #598-601 were aligned to the approved Upstream #<number>: format. Reply 5987865745 asks andraschris for the missing controller/DTO/outbound reproduction. The original {} diagnosis is unconfirmed; #18 must remain open.

<!-- repo-standards:begin. Copied from WilliamSmithEdward/repo-standards, templates/agents/AGENTS-block.md. Change it there; the weekly rescan fails a copy that differs. -->
## Releases, CI and security

These rules are the same in every WilliamSmithEdward repository.

- **How a release happens here:** pushing a `vX.Y.Z` tag runs Publish, which builds the release files in CI and creates the GitHub release with them, their signed provenance and the security reports. Any other step, such as a marketplace upload, is described elsewhere in this file.
- **Starting a workflow by hand never releases anything.** Publish and every
  release report are dry runs when started with `gh workflow run` or the Run
  workflow button. They build, scan and assemble the release files exactly
  as a release would, and upload them as the `release-preview` artifact
  instead. Run one after changing anything on the release path:
  `gh workflow run <file> --ref main`, then
  `gh run download <run-id> -n release-preview`.
- **Do not create, publish, edit or delete a release or a `v*` tag** unless
  the owner asks for it. A `v*` tag cannot be moved or deleted once pushed.
- **Every change to `main` goes through a pull request** that passes CI
  passed, Security passed and Malware scan passed. No one can push to `main`
  directly or skip the checks, admins included. Push a branch, open a pull
  request, and let it merge itself: `gh pr merge --auto --squash <number>`.
- **Pins.** Actions by full commit SHA with the version as a comment. Images
  by digest, in `.github/security/<tool>/Dockerfile`. Python tools from the
  hash-locked `.github/requirements/<purpose>.txt`, compiled from the `.in`
  beside it with
  `uv pip compile <purpose>.in --universal --generate-hashes --python-version 3.12 -o <purpose>.txt`.
  Runners are named releases, never `-latest`.
- **Updates merge themselves.** Dependabot and the Update YARA rules workflow
  open pull requests that merge once the three checks pass, except a
  third-party major version, which waits for the owner. Leave them alone
  unless asked.
- **A scanner finding is fixed or accepted with a written reason** in the
  repository's accepted list. Never silence a scanner without one.
<!-- repo-standards:end -->
