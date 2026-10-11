# Performance README YARA review

[Malware run 37823335370](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37823335370)
flagged `SIGNATURE_BASE_Powershell_Case_Anomaly` in
`test/EmbedIO.Performance/README.md` at PR head
`dc7daab917cbc2f91252a3dce608c97c7064a313` (tested merge
`bf813af7c031843fe70036c490a8150d89f3f311`). ClamAV 1.5.4 with official
signatures reported zero matches across 857 files. This review covers only the
named rule and documentation path.

## Evidence and finding

The pinned YARA-X 1.20.0 / YARA Forge full 20261004 rule was read in full.
Its [upstream source](https://github.com/Neo23x0/signature-base/blob/94a1c48d7ab499879287ff611dfe7f9c56376030/yara/gen_case_anomalies.yar#L11-L60)
compares case-insensitive word counts with permitted spellings. It is a casing
heuristic, not a determination of executable behavior.

The full bundle reproduces the CI finding on exact committed LF source bytes.
Printed `$s1` matches occur at offsets `0x1a14` and `0x1a26`: the canonical
shell product name in the sentence preceding the command example and the
lowercase Markdown code-fence language tag. The full bundle also prints `$sn3`
at `0x1a14`, but does not print the permitted lowercase `$sn1` match. The isolated
rule produces no match on the identical source. This difference is observed;
no confirmed scanner-engine defect is claimed.

The complete README was reviewed, including the executable examples it describes.
It documents local allocation/transport benchmarks, explicit runner paths and
bounded workload arguments. The matched paragraph invokes the repository's
comparison script with baseline/candidate assembly paths and a local results
directory. There is no randomized command casing, encoded payload, remote script,
execution-policy modification or covert command in the matched material. The
new scheduler section adds a local Python benchmark invocation and measurement
limitations; it does not introduce the matched words. Documentation is not part
of a shipped runtime execution endpoint.

SHA-256 evidence:

- Committed README: `c30bfdf6fadb5d9ccd214e1aa5bf9b34e1679e38978e885dbcb9f0a13ceac8ee`
- Extracted matched rule: `747c317b2279259808c6384df40a07513f59b61899ac0f47122ced4c00bc7ebb`
- Full rule bundle: `91b65681fa7c79dfb9422d0bdb31a51c06b2e8ab2a3b205a2d597eea28f7ed90`

The exact source, rule, printed full/isolated outputs and downloaded CI scan
artifact are retained under ignored `TestResults/http-engine/performance-readme-yara`.

## Acceptance boundary

Accept only this rule on this README. Other paths/rules, tool failures and stale
acceptances remain fatal. Re-review changed command examples, input origins,
matched text or scanner pins. The README, rule and scanning scope are unchanged
by this acceptance.

The GitHub code-scanning open-alert API returned an empty array on 2026-10-08;
this workflow reports artifacts/checks and exposes no matching alert to dismiss.
No dismissal is claimed. The owner's matching-alert dismissal requirement remains
a pre-merge limitation; this acceptance does not authorize merging with red checks.
