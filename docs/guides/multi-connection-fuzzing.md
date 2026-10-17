# Multi-connection state-machine fuzzing

The test-only `EmbedIO.Fuzz` executable can generate command traces for several
actors sharing one server or resource. Scenarios translate each command into a
specific operation and compare observed results with their own invariants.
Production packages do not acquire a fuzzing dependency.

From the repository root, with the pinned SDK and dependencies:

```sh
dotnet restore test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj --locked-mode
dotnet run --project test/EmbedIO.Fuzz -c Release --no-restore -- --state-machines 1729 100 websocket-budget
```

The arguments are the campaign seed and total number of cases (1â€“100,000).
Select an explicit scenario for local campaigns. Without a name, the
runner sorts the registered names and cycles through them. Each generated case
contains 48 commands for three actors. Actor IDs are 0â€“2, command IDs 0â€“11 and
values 0â€“63; their interpretation belongs to the scenario.

| Scenario | Exercised surface |
| --- | --- |
| `websocket-budget` | Shared WebSocket delivery budgets and connection generations |
| `rate-limits` | Partitioned permits, held requests, cancellation and FIFO admission |
| `routes` | Shared route registration and retained snapshots |
| `tunnels` | Shared tunnel lifetime and ownership |
| `sessions` | Shared session identities and mutations |
| `auth-state` | Bearer identities, roles/policies, cancellation and application body-binding isolation |
| `auth-jwt` | Explicit-key JWT trust, lifetime, cancellation and concurrent principal isolation |
| `auth-http1` | Real HTTP/1 authentication across overlapping and reused connections |
| `auth-http2` | Real HTTP/2 authentication across overlapping and reused connections |
| `response-http1`, `response-http2`, `response-http3` | File ranges/validators, compression, HEAD, cookies/trailers and response cancellation over real protocol connections |
| `http1-connections` | Real HTTP/1 connection isolation |
| `http2-connections` | Real HTTP/2 connection isolation |
| `http3-connections` | Real HTTP/3 connection isolation; requires working native QUIC support |

An unfiltered run includes HTTP/3. Missing QUIC support is a validation limitation,
not evidence that HTTP/3 passed; use an explicit supported scenario locally and
run the HTTP/3 scenario on a prepared host.

Each case has its own seed, calculated independently of previous scenarios'
random-number consumption. The runner prints total transition counts when the
campaign completes. Scenarios record named transitions with `trace.Cover(...)`;
these counts describe exercised operations, not line or branch coverage. They
complement the existing parser fuzzers and do not replace their coverage feedback.
The state-machine campaigns use generated traces and explicit invariant checks;
they are not coverage-guided fuzzers.

See [response-pipeline fuzzing](response-pipeline-fuzzing.md) for the response
oracles, command mapping, boundary corpus and negative self-test.

## CI campaigns

The Fuzz workflow runs shared models, runner self-tests, the native-independent corpus
traces, and real HTTP/1 and HTTP/2 connection cases on Ubuntu 24.04 and Windows
2025. Pull-request and other nonscheduled runs execute 500 cases per model and
32 cases per network protocol; scheduled runs execute 5,000 and 128 respectively.
The shared-state job allows 45 minutes on ordinary runs and three hours on the
full scheduled campaign. The physical request-abort model alone took 6m24s for
500 Linux cases in run 39057735937; 5,000 cases cannot fit the old 45-minute
whole-job allowance. This changes only the scheduled job allowance: every case
still has its 15-second deadline and five-second cleanup grace, and all cases,
assertions and negative oracles remain required.
A separate Ubuntu job requires HTTP/3, using the SHA-256-verified libmsquic 2.6.2
package pinned by the repository's main CI. It also replays QUIC-dependent traces under `state-corpus/quic` and runs the
response HTTP/3 campaign. Each network campaign executes 32 HTTP/3 cases normally
and 128 on the schedule. Unsupported QUIC does not silently skip this job.

These are configured validation jobs, not a claim that a particular run passed.
Each job retains logs, source SHA, transition counts and failure traces as an
artifact, including when a campaign fails.

## Failures, replay and shrinking

A failure produces a JSON artifact under ignored
`TestResults/fuzz/state-machines`. It contains the exact command list, scenario,
case seed, transition counts, exception and invariant identity, runtime and OS,
plus the core assembly's name, module ID and SHA-256. The assembly identity records
the actual tested binary, including uncommitted local builds. Keep build and CI
logs alongside the artifact when reporting a failure.

Replay that exact trace, without generating new commands:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay TestResults/fuzz/state-machines/original-EXAMPLE.json
```

Use the actual path printed by the failing run. Replay prints the recorded binary
identity and the replay's transition counts. Use the same source, SDK, runtime and
platform when investigating reproducibility; matching commands alone cannot
reproduce every operating-system scheduling decision. Replay does not claim the
recorded binary and current binary are identical.

Replay also accepts raw corpus JSON with just `Scenario`, `Seed` and `Steps`,
where each step contains `Actor`, `Command` and `Value`. Checked-in examples live
in `test/EmbedIO.Fuzz/state-corpus`. Raw corpus files deliberately contain no
invented historical runtime or binary provenance. For example:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/websocket-budget-overlap.json
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/rate-fifo.json
```

The runner itself has a native-free self-test mode:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machine-selftest
```

It verifies deterministic traces, exact raw/artifact replay, invariant-preserving
shrinking, retained unexpected exceptions and cooperative timeout cleanup.
Expected injected failures are retained in a unique self-test evidence directory;
the command succeeds only when its assertions pass. It never deletes earlier
campaign evidence or changes the process working directory. The unresponsive-task
process-termination branch is not executed by this in-process self-test.

The `sessions-detached-contexts.json` corpus records a corrected test-oracle
assumption. Regenerating a session unregisters and clears the old session object,
but contexts that already hold that object retain their reference. Later writes
through those detached contexts remain visible through those references; deleting
the old ID again cannot find and clear the object through the registry. The oracle
therefore clears its model only when removing a registered session succeeds.
This trace does not assert that a production defect was found or fixed.

An invariant failure retains the original artifact before attempting up to 64
chunk-deletion trials. A deletion is kept only when it reproduces the same
`StateMachineFailure.Invariant`. Other invariant failures do not count as the
original failure. The minimized trace is saved separately and the campaign still
fails. Minimization finds a smaller reproducer within its budget, not necessarily
the smallest possible trace. An unexpected exception is saved and surfaced; it is
never treated as an invariant match. Unexpected errors and timeouts are not shrunk.

## Deadlines and ownership

Every invocation has a 15-second deadline. On expiry the runner cancels the
scenario and allows five seconds for cleanup, observing both the scenario task
and cancellation callbacks. Scenarios must own and await every operation they
start and dispose their listeners, streams, connections and other resources in
`finally` blocks. The runner cannot discover tasks discarded inside a scenario.

If a scenario or cancellation callback remains unresponsive after cleanup grace,
the runner saves an `unresponsive` trace and terminates the test process. It does
not silently abandon the task and continue with contaminated shared state. No
subprocess service or production runtime dependency is introduced. A timed-out
scenario that finishes cleanup still fails the campaign.

## Scope of concurrency claims

A trace with multiple actors provides reproducible **interleavings** when it
executes and awaits one operation at a time. That alone is not a simultaneous race
test. Scenarios that exercise genuine overlap must start multiple operations,
coordinate a defined rendezvous or completion boundary, and await every operation
before checking invariants or finishing cleanup. Their transition names and
scenario documentation should identify these overlap points.

Shared in-memory models exercise ownership and state transitions without proving
wire behavior. Real network scenarios exercise the listener and protocol they
actually instantiate. Neither establishes coverage for every native backend,
HTTP/2 or HTTP/3 stream interaction, runtime or platform. Retain this distinction
when reporting campaign results, and retain focused regression tests for confirmed
bugs rather than relying on randomized rediscovery.

The current real-network scenarios hold three accepted requests on three distinct
physical connections. They validate each request body completely before opening
the application gate and commanding cancellation or response completion. They
therefore test accepted-request ownership and cleanup, not cancellation halfway
through upload reception. HTTP/2 scenarios do not substitute three streams on one
connection for three physical connections. HTTP/3 scenarios do not perform QUIC
connection migration, address rebinding or packet-loss simulation.
Post-drain listener endpoint rebinding is checked for HTTP/1 and HTTP/2 only;
HTTP/3 native listener retirement remains covered by its separate runtime tests.

Additional [stateful application campaigns](stateful-surface-fuzzing.md) cover
controller parity, persistence rollback, lifecycle, mutable files, request
codings and multipart ownership, with their own negative oracle checks.


Request-abort timeout artifacts identify the wait for handler entry, cancellation
or context close and record the public token state at the deadline. These values
can race shutdown cancellation; they locate the outstanding wait, rather than
establishing the cause. The original Windows and Linux reset-timeout traces from
main run 39054323280 are retained in the corpus. The follow-up
run 39055778078 located another timeout at the strict-token callback wait.
Cancellation-source disposal could race registration; canceled sources now remain
usable with retained tokens. Isolated replays passed even before the correction, so these traces are
historical failure inputs, not deterministic reproductions. The
15-second case deadline and all cancellation and ownership assertions remain
unchanged.
