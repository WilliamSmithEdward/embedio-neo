# Request binding fixture YARA review

Reviewed on 2026-10-13 against audit source
`f1d4d75ade6c04d8a7eb65b93dec1f1b67775235`. The current pinned
YARA-X 1.21.0 / YARA Forge 20261011 full bundle reports
`SIGNATURE_BASE_WEBSHELL_ASP_Runtime_Compile` on
`test/EmbedIO.Tests/RequestBindingCleanupTest.cs`.

Both downloaded archives matched their committed SHA-256 pins. A raw Git-blob
export was scanned in a network-disabled, read-only container. The full scan
and a separate printed-string scan completed successfully. Scanner execution
success is distinct from acceptance of findings.

- Canonical source SHA-256: `e95a07c1b4d11655cc8eceeac8f91b25bfcf9bff171105955788a1931587d41a`.
- Complete extracted rule SHA-256: `457e3ccfa7686bae71aa2dc58f8432325708ad4abeb2754b50002dca53f4086f`.
- Rule logic hash: `6699a44e396eedebb3bafa0e89c3b6d080586a158ed056ec7220bdf2ad764444`.

The complete rule and all 78 source lines were read. Its reflection branch
matches `System`, `Reflection`, `Assembly`, and `GetMethod(n`; the input word
`Request` is in the assertion message `Request encoding helper was not found.`
Printed canonical offsets include 0x9, 0x61, 0x128, 0xfdc and 0x685 respectively.
The current rule changes only quality metadata from the previous pinned rule;
its strings and detection condition are unchanged.

The fixture has 13 deterministic NUnit cases for optional field lookup,
required-field case sensitivity, and charset fallback. Its private lookup
receives only fixed in-source method names, targets the already referenced
engine assembly, and creates delegates to internal binding/encoding helpers.
It downloads or loads no supplied assembly, compiles no supplied source,
executes no command, and exposes no network execution endpoint. External input
cannot choose a reflection target or executable method. The test project is
nonpackable. This is a lexical false positive, not a runtime payload compiler.
The fixture has not been rewritten to evade detection.

A paginated read of open and all-state GitHub code-scanning alerts found no
matching rule/path alert. No dismissal is claimed. The owner's requirement for
a matching alert dismissal remains unresolved unless an explicit exception is
recorded for this exact finding. This written review grants no exception and
adds no scanner acceptance.

Any acceptance must remain limited to this rule and source path. Re-review
changed executable behavior, reflection targets, input origins, matched bytes,
or scanner pins. Other findings, tool errors, stale entries and all exact-head
merge gates remain enforced. Raw exports, archive checks, complete rules,
printed matches and alert responses are retained under ignored
`TestResults/audit/request-binding-yara-review` and the associated scan folders.

The written review itself also matches the same rule because it quotes the
fixture's reported words and method-lookup fragment. Current pinned printed
matches place those quoted strings at 0x3bc, 0x3c6, 0x3d4 and 0x3e4; the heading
supplies the input word at 0x2. This Markdown file contains evidence and policy
boundaries only; it is not executable source. Any acceptance of the review text
must name this exact document path separately. Its absent matching GitHub alert
also requires an explicit owner exception; the fixture's exception cannot be
implicitly extended to another file. Printed proof is retained in
`TestResults/audit/scan-policy-boundary-printed-02`.

## Owner acceptance decision

On 2026-10-13 William explicitly approved this exact ASP rule on
RequestBindingCleanupTest.cs and this review document separately, waiving only
the unavailable matching-alert dismissal for these two paths. Earlier pending
statements are historical. Changed behavior, targets, inputs, bytes or scanner
pins require review; full scans and all other exact-head gates remain enforced.
No matching GitHub alert dismissal is claimed.
