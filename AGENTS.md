Operating Model(s) --> 

1.) F:\GitHub\RIDM_Recursive_Invariant_Discovery_Model\RIDM.MD

High Level Guardrail(s) -->

1.) F:\GitHub\AI_Best_Practices\docs\agentic_ai_programming_best_practices.md
2.) F:\GitHub\AI_Best_Practices\docs\ai_smells_for_agents_to_avoid.md

# Fork maintenance rules

- Preserve compatibility by default. Any breaking change requires discussion with William and his explicit approval before implementation, plus release notes and migration documentation.
- Follow CONTRIBUTING.md for development and validation. Preserve existing license and third-party notices.

## Upstream backlog workflow

- Work on one issue from https://github.com/unosquare/embedio/issues at a time. William selects the issue; do not select or start another without his explicit approval. The sole active item is upstream #601, tracked as WilliamSmithEdward/embedio-neo#10; keep it active pending Mac Catalyst confirmation.
- Fully triage every issue and PR: assign WilliamSmithEdward, set relevant labels, select the appropriate milestone, and link its development branch/PR. Add meaningful parent/sub-issue or blocking relationships when they exist; explicitly record when none apply. Do not invent dependencies or release commitments. William does not use GitHub Projects; leave Projects intentionally unused and do not request project access. If other access prevents a field from being set, report the exact limitation and keep it as outstanding work.
- Create the tracking issue and fix PR in WilliamSmithEdward/embedio-neo. The archived upstream repository is read-only; do not attempt to post there.
- Aggregate the relevant original report, reproduction details, discussion, user comments, and proposed solutions into the tracking issue. Preserve authorship, distinguish quotations from summaries, and link to the original issue and individual comments. Do not present migrated comments as new posts by their original authors.
- Follow CONTRIBUTING.md and F:\GitHub\AI_Best_Practices\docs\agentic_ai_programming_best_practices.md. Reproduce the concern, implement a focused fix, add meaningful regression coverage, validate, and document the outcome. Preserve compatibility; breaking changes still require William's explicit approval.
- Once a substantive outcome is ready, post the fix information in a comment on the fork's tracking issue, in William's voice. William authorizes tagging all human participants in the original issue except the former maintainers. Establish the participants and former-maintainer identities from evidence before mentioning anyone; exclude bots and avoid duplicate notifications.
- Explain respectfully that the original project was archived and William is carrying it forward through EmbedIO-Neo. Offer support in a spirit of community and goodwill, thank participants for their work, and invite them to try the fix and report whether it resolves their concern. Do not imply endorsement by the former maintainers.
- Use William's public contributor reply format: open with participant @mentions and a warm, specific thank-you; state the outcome early; insert `---` before the technical explanation and again before the closing; thank them again and sign `William` on its own line. Link the fork issue/PR and relevant validation. Distinguish proposed, awaiting checks, merged, and released status; name a release or NuGet availability only when verified.
- For platform-specific reports, configure CI that exercises the relevant runtime and app model where practical. For active #601, add a dedicated MAUI Mac Catalyst smoke app on a compatible GitHub macOS runner, with pinned SDK/workload/package versions, sandbox/network entitlements, and validated signing/launch steps. Start the real EmbedIO listener and fetch local HTML; capture failures and test artifacts. Keep test-only platform dependencies outside production packages and the ordinary solution build.
- Docker downloads and the installed Docker engine are authorized for reproduction and testing. Linux containers cannot reproduce Apple runtimes or sandbox behavior. Existing macOS desktop regression tests are not a substitute for MAUI Mac Catalyst validation; do not describe a configured job as passing until its run succeeds. Do not claim reproduction of Sonoma 14.5 / M1 Pro unless that environment was actually tested.
- Current #601 status: fork issue #10 exists, two focused Windows tests passed, and the full Windows suite passed with 367 successes and two platform skips (369 total). The sandboxed MAUI Mac Catalyst HTTP/WebView smoke passed in GitHub run 37256447885 on macos-26 with runtime 10.0.12. PR #11 is open; remaining checks and reporter confirmation are outstanding. Do not move to another issue without approval. GitHub Projects are intentionally unused by William.
- An upstream report is not resolved merely because it was copied. If reproduction or confirmation requires input, state the limitation and keep the issue active. Do not merge or publish merely to claim resolution. After completing the selected issue, wait for William's approval before moving to another.

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
