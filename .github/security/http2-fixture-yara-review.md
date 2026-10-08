# HTTP/2 request-header fixture YARA review

Reviewed on 2026-10-08 for PR #182, head `65d82b5`. Malware run
[37773860699](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37773860699)
scanned merge commit `58e37bdf5e169908531d15da382ef4712c0077c6` and reported
`SIGNATURE_BASE_WEBSHELL_ASP_Runtime_Compile` in
`test/EmbedIO.Tests/Http2RequestHeadersTest.cs`. ClamAV found no matches.

The repository pins YARA-X 1.20.0 and YARA Forge 20261004. The downloaded archive
hashes matched `.github/security/yara.json`. Both the isolated rule and full bundle
reproduced the match against the exact committed fixture bytes using printed
strings in a network-disabled, read-only Debian container.

- Source SHA-256: `ac3746a78e75cb4f36ded3473e014c48355b5c7eaedac58c26818e49816b4dab`
- Extracted rule SHA-256: `7b65eb6e49948dd18297ce8b16e0842696f9593ceef69a037cdca3e5cc703e4d`
- Rule logic hash: `6699a44e396eedebb3bafa0e89c3b6d080586a158ed056ec7220bdf2ad764444`

The matching condition combines a small source file, the word `request` at offset
0x7b5, namespace/type words at offsets 0x9, 0x68 and 0x19c, and method lookup at
0x566. The rule mistakes the fixture's reflection helper for a payload loader.
The entire source was reviewed: it constructs internal header-field objects and
invokes the fixed parser or trailer validator using in-source NUnit test cases.
It does not compile code, load a supplied assembly, execute commands, expose an
ASP endpoint, or derive executable method names from network input. Reflection
is required here to exercise internal APIs without adding public production APIs.

Acceptance is restricted to that exact rule and source path. Re-review if the
fixture's reflection targets, input origins, executable behavior, or pinned scanner
rules change. Other matches, scanner errors and stale acceptances remain fatal.
The fixture was not modified to evade detection. Printed-match logs and original
scanner artifacts are retained under ignored `TestResults/http-engine`.

The malware workflow emits report artifacts, not SARIF/code-scanning alerts. A
paginated query of open repository code-scanning alerts found no corresponding
alert to dismiss. No dismissal is claimed. The owner's matching-alert requirement
therefore remains an explicit pre-merge limitation; PR #182 stays draft and
unmerged. A fresh scan is still required before claiming green checks.
