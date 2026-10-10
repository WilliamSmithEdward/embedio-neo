# HTTP/3 fixture YARA review

Reviewed PR #222 head `8440e311526801c050c428053fddef45e0f2965a`, malware run
[38031152126](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38031152126),
synthetic merge `47db064a0b5f1203bb85bb1ddfa023dd7f44f232`.
ClamAV reports no matches. YARA-X reports `SIGNATURE_BASE_WEBSHELL_ASP_Runtime_Compile`
in exactly these two new test fixtures:

- `test/EmbedIO.Tests/Http3DirectionWatcherTest.cs`, canonical source SHA-256
  `c9b2a2253d22b5992b92362799333bee924daa5cb9a6d3541a0faf3f99ea8ca6`.
- `test/EmbedIO.Tests/Http3RequestInputEndTest.cs`, canonical source SHA-256
  `cf8d199977cdb103e6c45dd7db0dd8e24111b2da459949c97e5fe1a8b78d80ba`.

The exact committed sources were exported with git archive and scanned independently
in a read-only, network-disabled container. YARA-X 1.20.0 and YARA Forge 20261004
were verified; archive SHA-256 is
`809fe0c2e6c58dd9dc74afad0cdae21a1ed3bd5e68e539504d211dbf4d872f8d`.
The full pinned bundle reproduced both matches and no additional rule on either file.
The matching condition combines ordinary namespace/type text, the word request,
and a method lookup into the already loaded engine assembly. Printed match evidence
and the original workflow summary are retained under ignored TestResults/http3-review.

The entire executable behavior was reviewed. The watcher fixture invokes two fixed
internal request-direction helpers with in-source completion tasks, cancellation
sources and QUIC exceptions. The input-end fixture creates an ephemeral loopback
QUIC listener with a synthetic certificate, sends fixed independently encoded
request bytes and invokes the fixed connection runner and response helper. The
reflection targets are fixed in source; no network input supplies method names,
assembly locations or code. Neither fixture compiles source, loads a supplied
assembly, executes a shell or exposes a runtime execution endpoint. This is a
heuristic false positive in test code. No fixture was renamed, padded or obscured
to avoid detection.

Proposed acceptance is limited to this rule and these two fixture paths. Any change
to executable behavior, reflection targets, input origins or scanner pins requires
re-review. Other findings, scanner errors and stale acceptances remain fatal.
The accepted list has not been changed; fresh full scans remain required.

A paginated query of open GitHub code-scanning alerts found no corresponding alert.
The malware workflow supplies artifacts rather than creating code-scanning alerts,
so no matching dismissal can be performed or claimed. The owner's matching-alert
pre-merge requirement remains outstanding. PR #222 must remain unmerged until the
owner resolves that specific requirement and every check on the final head is green.