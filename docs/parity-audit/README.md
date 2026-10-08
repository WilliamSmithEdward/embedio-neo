# Upstream parity audit

This audit compares the same consumer program against published
[EmbedIO 3.5.2](https://www.nuget.org/packages/EmbedIO/3.5.2), pinned SWAN 3.1.0,
and both current Neo library assets. It measures specific compatibility behavior;
it does not establish that Neo has no bugs or that every upstream application is
binary compatible. Findings are kept in one file per major functional area.

## Findings by area

| Area | Finding |
| --- | --- |
| [Public API](public-api.md) | No unclassified missing entry in the inventoried surface; 12 SWAN-related changes |
| [Utilities](utilities.md) | All 72 value/exception/parameter-name comparisons match |
| [Routing](routing.md) | Successful binding and matching agree; approved invalid-number status change |
| [Request binding](request-binding.md) | Ordinary form/query/body behavior agrees; documented JSON input boundaries differ |
| [Serialization](serialization.md) | Ordinary DTOs agree; fields, derived data, references, cycles and timestamps differ |
| [Static files](static-files.md) | Tested bytes/ranges/compression agree; inherited suffix-range defect remains |
| [Authentication and CORS](authentication-cors.md) | Tested challenges, credentials and origin/preflight behavior agree |
| [Sessions](sessions.md) | Cookie persistence and independent-client isolation agree |
| [WebSockets](websockets.md) | Ready-then-send text/binary and fragmented messages agree |
| [Lifecycle](lifecycle.md) | Windows/managed cancellation matches; old Unix native response cleanup fails while Neo stays live |
| [HTTPS](https.md) | Managed certificate-pinned access and negative trust/hostname checks agree |

## Baseline and evidence

The local Windows validation on October 7, 2026 used Windows 10.0.26300 and .NET
10.0.11 against Neo source `c58dbae1c77089ac0b2ec2a03c806714be2294ac` plus this
test/documentation change. All 207 cases completed for each of the three builds:
414 upstream-to-Neo comparisons, with no unexplained difference. Both Neo assets
had identical tested outcomes and inventoried API surfaces. Seven negative checks
prove the comparator rejects injected status/body/JSON-type/message/API/discovery changes
and a removed reviewed-difference entry. These are fixture counts, not additional
NUnit test counts or a quantified probability of correctness.

There are 152 reviewed field differences per Neo asset across 112 cases, including
112 omissions of `Content-Encoding: identity`. The remaining differences are
the existing approved typed-route correction and characterized serializer/input
migration boundaries. The other 95 cases match without those differences.
The contract stores exact old/new values and reasons; new or stale differences fail.

The Unix profile has 143 cases and 286 comparisons: 72 utility cases, the full
managed HTTP/WebSocket workload, managed HTTPS, API inventory and an explicit
native response-lifetime probe. A pinned Linux .NET 10.0.12 container passed this
profile with no unexplained difference. It records 80 reviewed field differences,
including 56 identity-encoding omissions and four fields characterizing an
existing native listener correction. The full upstream native workload cannot
complete on the tested modern Unix runtime: after its first DTO response,
upstream faults the accept loop with `ObjectDisposedException` and its cancellation
callback throws `AggregateException`. Neo serves a second DTO and cancels cleanly.
This failure is preserved and asserted as a baseline finding, rather than silently
counted as a passing native workload. See [Lifecycle](lifecycle.md).

The upstream assembly SHA-256 is
`652807CC55278507A00080698872D0A03EF2A38076D91F5D4333C57C9E223EBC`.
The pinned NuGet lock files also record package content hashes. Raw per-implementation
JSON, assembly/runtime metadata, build/run logs, differences and API additions
are written under ignored `TestResults/parity-audit`. The summary records the
Git commit and working-tree state. Required desktop CI runs the audit and uploads
that directory with its other test results; cross-platform success must be verified
from those actual artifacts.

Initial Linux/macOS PR jobs aborted in the upstream native workload. Linux logs
and the minimal probe reproduce disposal of the native response stream, which
Neo already caches. The corrected fixture separates the constrained Unix profile
from Windows's full native comparison; neither production code nor a test timeout
was changed to obtain parity. Initial failures are retained under ignored
`TestResults/ci-initial-linux` and `TestResults/ci-initial-macos`. The revised
macOS profile must pass CI; completion is recorded in the PR results and artifacts.

## Run and interpretation

From the repository root, with the SDK in `global.json` and Python 3:

```sh
python scripts/compatibility_audit.py
```

The runner restores with `--locked-mode`, audits each consumer's direct/transitive
packages using the existing fail-on-any-finding dependency checker, builds three separate processes from
`test/EmbedIO.Compatibility/Program.cs`, checks the loaded assembly targets, and
compares their reports. The baseline dependencies are test-only, outside the
solution and shipped package dependency groups. No production changes are made.

Successful non-JSON response bodies are compared as bytes; JSON is compared as
parsed values, retaining array order and member/value differences while ignoring
spacing and object member order. Error-page prose, generated cookie IDs, Date,
Content-Length, ETags and transport packet timing are outside this initial
projection. HTTP status, media type, selected routing/range/auth/CORS/compression
headers and error recovery are included. SWAN's process-specific `$circref` IDs
are normalized only for comparison; raw IDs remain in the reports.

## Remaining scope

Exact Mono/Xamarin/Unity/WebGL behavior, physical devices, native HTTP.sys TLS
registration, binary loading of unrecompiled upstream consumers, full parser/wire
fuzzing, load/race behavior, custom providers, ZIP archives, JsonServer and CLI
contracts are not established by this suite. Existing focused Neo regression tests
provide additional evidence, but are not a side-by-side upstream comparison.
Add cases and document findings in the corresponding area before expanding a
claim. The suffix-range finding is concrete inherited work; it is not silently
accepted as correct merely because both implementations match.
