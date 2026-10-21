# Coverage-guided URL, multipart and QUIC fuzzing

`test/EmbedIO.SharpFuzz` is a nonpackable .NET 10 executable. SharpFuzz 2.3.0
instruments **copies** of `EmbedIO.dll`, `EmbedIO.Quic.dll` and
`EmbedIO.Quic.Development.dll`; the native libFuzzer driver uses their
managed branch counters to retain interesting inputs and guide mutations.
This complements the deterministic mutation and property harness in `EmbedIO.Fuzz`.
It exercises URL/multipart parsing and EmbedIO.Quic packet, stream, recovery,
lifetime and H3 bridge code without sockets or native TLS. Dedicated native TLS
and wire campaigns are described in the QUIC development record.

The URL targets are the public `UrlEncodedDataParser.Parse` and `UrlPath.Normalize`
APIs. Query checks compare keys and ordered values with mutable and immutable
results, with both flag-grouping policies. Path checks compare against a separate
character-by-character slash-collapse oracle and check idempotence. Arbitrary
bytes decode as UTF-8 with replacement; malformed percent escapes remain inputs.
Paths receive an initial slash to meet the documented precondition. Documented malformed-input exceptions are handled only at their target boundary;
unexpected exceptions and failed invariants become crash inputs.

## Run and replay

With the SDK pinned in `global.json` and PowerShell 7, on Windows x64 or Ubuntu x64:

```powershell
./scripts/run-sharpfuzz.ps1 -Seconds 60 -Seed 1729
```

Observe the repository's shared build-slot rules when running locally. This managed
parser/transport campaign does not bind network endpoints. The runner downloads the official
`v2025.05.02.0904` driver and verifies its platform-specific SHA-256, restores the
locked harness dependencies and installs the pinned SharpFuzz.CommandLine 2.3.0
tool. The instrumenter targets .NET 8 and is run with temporary major-version
roll-forward on the repository's .NET 10 runtime.

Each invocation uses a new `TestResults/sharpfuzz/<id>` directory. Only the
published copies of the selected parser and QUIC implementation types are instrumented. The runner checks that
the copy changed and the ordinary build assembly did not. Neither the harness
nor its instrumentation belongs in production packages. The ordinary fuzz harness
references the nonpackable QUIC development host.

All checked-in URL, multipart and QUIC seeds are replayed before mutation. Inputs are capped at
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

## AFL++ campaigns

The same parser callback also runs through SharpFuzz's out-of-process AFL
forkserver. With Docker's Linux engine and PowerShell 7 available, run:

```powershell
./scripts/run-afl.ps1 -Seconds 60 -Seed 1729
```

The runner builds AFL++ 5.03c from a fixed source commit and checks its archive
hash, on a digest-pinned .NET SDK image. One CPU and 4 GiB bound the container.
The source is mounted read-only and copied into the disposable container; only
the published library copy is instrumented. Production outputs are unchanged.
Keep this Dockerfile's SDK tag and digest aligned with `global.json` when moving
the SDK; the container obeys the same exact-version requirement as other builds.
The hosted workflow temporarily sets file-based core-dump delivery on its
disposable runner and restores the previous value afterward, so AFL's normal
crash-delivery check remains enabled. The local runner does not change host
settings; a local host with piped core-dump delivery will fail that check.
Container-created result files are made readable for host artifact collection
even when the campaign fails.

Before mutation, `afl-showmap` must report nonempty, distinct coverage maps for
each of the URL, multipart and QUIC seed pairs. The dotnet executable has no native AFL signature, so the
official SharpFuzz bridge requires `AFL_SKIP_BIN_CHECK`; this does not disable
managed instrumentation or the forkserver. Non-instrumented mode is not used.

CI runs one worker for sixty seconds per affected change and ten minutes weekly.
The result must report executed cases, and any saved crash or hang fails the job
even if the fuzzer exits zero. Artifacts retain coverage maps, run parameters,
logs, the generated corpus and failure inputs. Replay a saved AFL input with the
same standalone replay command shown above; it uses the shared callback.
Hosted evidence is packaged as `afl-evidence.tar.gz` because AFL's original
queue/crash filenames contain colons that GitHub artifact upload rejects.
Extract those names on Linux; when replaying on Windows, copy the selected
input's bytes to a filename such as `repro.bin` first.

The initial five-second local Linux-container validation completed 1,696 cases,
retained 24 new corpus entries and reported 68 edges with no crashes or hangs.
These bounded observations verify feedback, not exhaustive correctness or a
performance comparison with libFuzzer. The corpus-directory mode of `afl-showmap`
is required to start the forkserver used by SharpFuzz; single-input mode does not
supply its communication pipes.
A temporary, uncommitted exception for a mutated `!` input was retained as a
crash and made the runner fail. After a successful standalone build, replay of
that saved input reproduced the same exception. The temporary predicate was
then removed before the clean validation campaign.
The final clean sixty-second container run completed 21,393 executions, retained
25 new corpus entries and saved no crashes or hangs.

## Upstream tooling

- [SharpFuzz](https://github.com/Metalnem/sharpfuzz), MIT license, and its
  [libFuzzer integration](https://github.com/Metalnem/sharpfuzz/blob/master/docs/libFuzzer.md).
- [Pinned libfuzzer-dotnet release](https://github.com/Metalnem/libfuzzer-dotnet/releases/tag/v2025.05.02.0904), MIT-licensed bridge to LLVM libFuzzer.
- [LLVM libFuzzer documentation](https://llvm.org/docs/LibFuzzer.html) describes
  corpus growth, crash artifacts, timeout and mutation controls.
- [AFL++ 5.03c source](https://github.com/AFLplusplus/AFLplusplus/tree/dbaf11913c1b2702dee5b4d3dcfffd52f1defe50)
  supplies the AFL engine and coverage-map validator used by the container.

## EmbedIO.Quic

The QUIC development core participates in the existing fuzz campaigns. See the
[development and testing record](../project/quic-development-and-testing.md) for
all eight targets, scheduled socket dispatch/output ownership and asynchronous application wake-ups, multi-connection
traces, managed coverage evidence, native TLS
mutation scope and extensive local commands. This does not activate a production
provider or remove existing HTTP/3/MsQuic coverage.
