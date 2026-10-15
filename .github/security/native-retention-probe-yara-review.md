# Native retention probe YARA review

PR #305 head `9fd82de1a08d53485a343ef93be8555442a842b8`, malware run
[38149406055](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38149406055),
synthetic merge `6eca0d0642fac9586a857f7233c61f1393daf843`.

The combined gate has one unexpected finding:
`SIGNATURE_BASE_WEBSHELL_ASP_Runtime_Compile` on
`test/EmbedIO.LoadBenchmark/NativeQuicRetentionProbe.cs`. ClamAV reports zero
matches. Existing accepted findings remain unchanged.

Canonical committed source SHA-256:
`55921e5eab48f17040e087e94d7234e95ae6b7758fb478d1126484f00fa7f377`.
The exact source was exported with git archive and reproduced in a
network-disabled container with YARA-X 1.20.0 and YARA Forge 20261004.
Both download hashes match `.github/security/yara.json`; Forge archive SHA-256
is `809fe0c2e6c58dd9dc74afad0cdae21a1ed3bd5e68e539504d211dbf4d872f8d`.
The complete rule and every printed match were reviewed. Raw artifacts, source,
scanner version and match offsets are retained under ignored
`TestResults/retention-review/yara-repro`.

The rule combines a small text file, ordinary request/type words, reflection
names and a method lookup. The printed matches include namespace/type terms,
the fixed helper's lookup, its invocation, and the request-data validation
message. It does not require actual source compilation to match.

The whole executable behavior was reviewed. This development-only probe uses
the already-loaded local engine assembly and fixed in-source targets for API,
registration, configuration, listener, connection and stream ownership. Its
helper's name argument comes only from those fixed call sites. Operator input
selects a known provider, bounded stream/warm-up counts and optional progress
logging; network input supplies neither code, assembly paths nor method names.
The listener is ephemeral loopback, the certificate is pinned, response bytes
and lengths are validated, and native stream owners are disposed. The probe
reads heap counts and reports library metadata. It compiles no supplied source,
executes no shell and loads no operator-supplied assembly. No code was renamed,
padded or obscured to avoid this heuristic.

A paginated open code-scanning-alert query returned zero alerts. The malware
workflow emits artifacts rather than matching GitHub code-scanning alerts, so
no matching dismissal can be performed or claimed.

Proposed acceptance is limited to this one rule and source path. Changed
executable behavior, reflection targets, input origins or scanner pins require
re-review. Other findings, tool errors and stale acceptance entries remain
fatal. Full fresh scans and every exact-head check remain required before merge.

William explicitly approved this one-rule/one-path acceptance and the matching-alert
exception on 2026-10-11 after reviewing the explanation. A fresh paginated
open-alert query still returned zero. No alert dismissal is claimed. The acceptance
entry is now recorded; every check on the new exact head must pass before merge.