# Coverage-guided URL parser fuzzing

`test/EmbedIO.SharpFuzz` is a nonpackable .NET 10 executable. SharpFuzz 2.3.0
instruments a **copy** of `EmbedIO.dll`; the native libFuzzer driver uses its
managed branch counters to retain interesting inputs and guide mutations.
This complements the deterministic mutation and property harness in `EmbedIO.Fuzz`.
It does not exercise sockets, HTTP framing, TLS, QUIC or WebSocket sessions.

The targets are the public `UrlEncodedDataParser.Parse` and `UrlPath.Normalize`
APIs. Query checks compare keys and ordered values with mutable and immutable
results, with both flag-grouping policies. Path checks compare against a separate
character-by-character slash-collapse oracle and check idempotence. Arbitrary
bytes decode as UTF-8 with replacement; malformed percent escapes remain inputs.
Paths receive an initial slash to meet the documented precondition. No exceptions
are swallowed: unexpected exceptions and failed invariants become crash inputs.

## Run and replay

With the SDK pinned in `global.json` and PowerShell 7, on Windows x64 or Ubuntu x64:

```powershell
./scripts/run-sharpfuzz.ps1 -Seconds 60 -Seed 1729
```

Observe the repository's shared build-slot rules when running locally. This pure
parser campaign does not bind network endpoints. The runner downloads the official
`v2025.05.02.0904` driver and verifies its platform-specific SHA-256, restores the
locked harness dependencies and installs the pinned SharpFuzz.CommandLine 2.3.0
tool. The instrumenter targets .NET 8 and is run with temporary major-version
roll-forward on the repository's .NET 10 runtime.

Each invocation uses a new `TestResults/sharpfuzz/<id>` directory. Only the
published copy of the two target types is instrumented. The runner checks that
the copy changed and the ordinary build assembly did not. Neither the harness
nor its instrumentation belongs in production packages or the ordinary solution.

The five checked-in seeds are replayed before mutation. Inputs are capped at
4096 bytes, each execution has a ten-second timeout and campaigns use one driver
process. The workflow runs sixty seconds on affected PRs/main and ten minutes
weekly. Its twenty-minute job limit bounds setup or driver stalls. Nonzero driver
exits fail the job. Logs must also show managed extra counters and completed
coverage-feature reporting; a successful process alone is insufficient.

The artifact retains source/tool/seed metadata, logs, the resulting corpus and
crash files even on failure. A crash input is the reliable reproducer; a numeric
mutation seed alone is not a guarantee across runtime/platform changes. To replay
a downloaded crash, build the exact source revision, then run:

```powershell
dotnet build test/EmbedIO.SharpFuzz -c Release
dotnet test/EmbedIO.SharpFuzz/bin/Release/net10.0/EmbedIO.SharpFuzz.dll path/to/crash-file
```

Standalone replay uses the same callback without requiring instrumentation or the
native driver. Preserve the original crash and logs, add a focused regression and
promote useful minimized inputs into the checked-in corpus. Do not catch an
unexpected exception merely to make a campaign pass.

## Validation and limits

Initial Windows validation replayed all five seeds, then completed 688,180 inputs
in a bounded sixteen-second run. Managed features increased from 109 to 212 and
the retained corpus grew from four effective seeds to 28 entries. These are
coverage-feedback observations, not performance benchmarks or coverage percentages.
A temporary, uncommitted exception triggered by a mutated `!` input made the
same runner fail with driver exit 77 and retain a crash artifact. Standalone
replay reproduced that exception. The source was restored and rebuilt afterward.
The driver's `cov` field includes native driver edges; managed feedback appears
through the extra counters and `ft` features. The driver warns about absent native
sanitizer stack-trace hooks on Windows; this integration detects managed exceptions
and invariant failures, and does not claim native memory-sanitizer coverage.

The production APIs, defaults, dependencies and packages are unchanged. Passing a
bounded campaign does not establish exhaustive parser correctness.

## Upstream tooling

- [SharpFuzz](https://github.com/Metalnem/sharpfuzz), MIT license, and its
  [libFuzzer integration](https://github.com/Metalnem/sharpfuzz/blob/master/docs/libFuzzer.md).
- [Pinned libfuzzer-dotnet release](https://github.com/Metalnem/libfuzzer-dotnet/releases/tag/v2025.05.02.0904), MIT-licensed bridge to LLVM libFuzzer.
- [LLVM libFuzzer documentation](https://llvm.org/docs/LibFuzzer.html) describes
  corpus growth, crash artifacts, timeout and mutation controls.
