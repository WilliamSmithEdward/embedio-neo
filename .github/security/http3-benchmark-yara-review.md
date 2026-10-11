# HTTP/3 write benchmark YARA review

Reviewed on 2026-10-08 for draft PR #182 before pushing the bounded-DATA change.
The local pinned YARA-X 1.20.0 / YARA Forge 20261004 scan reported
`SIGNATURE_BASE_WEBSHELL_ASP_Runtime_Compile` in
`test/EmbedIO.Performance/Http3WriteBenchmark.cs.txt`.
The full bundle matched the working file; the isolated complete rule reproduced
the finding against canonical staged Git bytes. No source text or scanner rule
was rewritten to evade detection. A fresh CI malware run is still required.

- Canonical source SHA-256: `7ebfed9e87d9a0beb1d6aaf06434f3f5b74386660cdfb09fcdc99b0684fdef5a`
- Extracted rule SHA-256: `a73cfc183c1be7bce95066e5d587f87822474532be1cb285cf222cac7f85f2c9`
- Full bundle SHA-256: `91b65681fa7c79dfb9422d0bdb31a51c06b2e8ab2a3b205a2d597eea28f7ed90`
- Rule logic hash: `6699a44e396eedebb3bafa0e89c3b6d080586a158ed056ec7220bdf2ad764444`

The [complete pinned rule](https://github.com/Neo23x0/signature-base/blob/94a1c48d7ab499879287ff611dfe7f9c56376030/yara/gen_webshells.yar#L5088-L5186)
combines a small file, the ordinary word `request`, namespace/type words and
`GetMethod` with an identifier argument. Printed matches include the fixed
`nameof(Handle)` lookup and ordinary reflection invocation. Its runtime-compilation
heuristic therefore matches this benchmark without compilation of supplied code.

The entire runner and comparison driver were reviewed. The runner creates an
exportable synthetic certificate, binds a loopback-only QUIC listener, pins that
certificate in its own client, serves fixed seeded response buffers and checks
exact bytes and protocol metadata. Reflection targets the already loaded EmbedIO
assembly and fixed internal exchange/connection methods; neither target names
nor executable code come from HTTP input. The runner loads no supplied assembly,
compiles no network input, launches no command and exposes no ASP execution
endpoint. The companion Python driver builds source snapshots using explicit
subprocess argument lists, stores evidence under TestResults and records source
and binary hashes. Git revision selection is local command-line input, not a
network request parameter. Production packages do not include this benchmark.

Acceptance is limited to this exact rule and source path. Re-review is required
if reflection targets, input origins, executable behavior or scanner pins change.
Other files/rules, scanner failures and stale acceptances remain fatal. Original
printed-match logs, canonical bytes and extracted rule remain under ignored
`TestResults/http-engine/h3-write-yara`; the changed-file full-bundle scan is in
`h3-backpressure-yara.log`.

The repository's open code-scanning alert query returned an empty array. This
artifact-based YARA finding has no corresponding alert to dismiss, and no
dismissal is claimed. The owner's matching-alert dismissal requirement remains
an explicit pre-merge limitation; PR #182 remains draft and unmerged.
