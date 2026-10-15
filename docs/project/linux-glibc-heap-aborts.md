# Linux glibc heap aborts

This record covers two Linux test-host aborts raised by glibc's heap checks during
validation of [program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181)
and [draft PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182).

**Status: inconclusive.** No component or cause is established. There are no
production, test, workflow or quarantine changes. The `FormatHeaders`
NullReferenceException is a separate, diagnosed runtime race
([Linux test-host terminations](linux-test-host-terminations.md)). The
intermittent large-JSON timeout is a separate failure and not a process crash.

This investigation records sources and observations through `5a0507f`. The
subsequent engine merge of PR #269 removes the Microsoft backend and its
FormatHeaders regression cases, and enables owner-approved Unix crash-report-only
capture. It does not establish a cause or correction for the glibc aborts.
The later `2ba2113` CI run 38101741496 recorded a host crash after 359 passes in
nine seconds, without a malloc message or native stack. That termination is
unclassified; it is not counted as a confirmed third glibc abort.

## Occurrences

| Where | Revision | Host | Message | Reported cases | Last completed result | Termination |
| --- | --- | --- | --- | --- | --- | --- |
| Local container | `b8f38ba` | pinned image `cad57be` | `free(): double free detected in tcache 2` | 356 | `ConnectionLifetimeRegressionTest.UnregisteredIdleOrHandshakeConnectionReleasesResources(True,True)`, 22:20:38.638 | host crash recorded 22:20:38.686 |
| CI run 37997672083, job 114047748068 | `556022ec` | GitHub `ubuntu-24.04` image 20261004.327.1 | `free(): invalid pointer` | 413 | `DeflateFramingValidatorTest.ExactEndAcrossBlocksAndFragmentedInput(NoCompression,True,31,4096)`, 22:10:56.537 | host crash recorded 22:10:58.263 |

Both hosts ran SDK 10.0.401 and runtime 10.0.12 with coverage enabled. Neither run
had a failed test or a dump. The pinned image is Ubuntu 24.04.5 with glibc
2.39-0ubuntu8.9 and OpenSSL 3.0.13-0ubuntu3.16. `556022ec` is an ancestor of
`b8f38ba`. The managed connection, endpoint listener, lifetime test and
`HttpsSmoke` helper are byte-identical between `b8f38ba` and the integration head
`5a0507f`.

Both runs were sequential. In the local run the next case,
`WebSocketUpgradeKeepsItsTransportUntilCloseOrShutdown(False,False)` (plain HTTP),
reported no result. In the CI run the Brotli fixtures ended at 48.0 s,
`ConnectionLifetimeRegressionTest` ran from 48.6 s to 53.9 s and
`CrossPlatformHttpsTest` (TLS) until 56.3 s. JSON and deflate-framing tests
followed until the abort. Both aborts therefore surfaced under unrelated,
single-threaded tests, after the same TLS lifecycle and Brotli fixtures.

A scan of 158 failed Ubuntu and macOS test jobs from 189 failed CI runs since
2026-10-05 found no other glibc or macOS malloc abort. The crashed hosts in that
scan were six `FormatHeaders` NullReferenceExceptions, four older macOS
`IPEndPoint.Create` failures, and 15 exits with code 7 and no error output. The
last group matches the known `--timeout` behavior and was not inspected further.

## What the messages establish

From the [glibc 2.39 `malloc.c`](https://sourceware.org/git/?p=glibc.git;a=blob;f=malloc/malloc.c;hb=refs/tags/glibc-2.39):

- The tcache is per-thread (line 3125). `free` reports "double free detected in
  tcache 2" only when the chunk being freed is already in the freeing thread's own
  bin (lines 4520-4541). Tcache holds up to seven chunks per size, for requests up
  to 1,032 bytes on x86_64 (lines 295-313). No `GLIBC_TUNABLES` were set.
- "free(): invalid pointer" (line 4507) means the address fails glibc's alignment
  or size checks. One way to produce it is a destructor running a second time over
  an already-freed structure and freeing the stale fields it finds. Freeing any
  address that malloc did not return is another.

Inference: the first abort is most directly a second free of one allocation of at
most 1 KB on the same thread, with no same-size allocation on that thread reusing
the chunk in between. A cross-thread double free can also reach this message after
the chunk is reallocated. Both messages fit a small native object destroyed twice;
neither names the component.

Anchors in the pinned image: a deliberate double free of a 64-byte block aborts
with "double free detected in tcache 2" under the default allocator. Under glibc's
malloc checker it aborts with "free(): invalid pointer".

## Diagnostics performed

- One ordinary run of the exact pre-abort sequence. This was the `b8f38ba` archive in
  the pinned image: all 15 fixtures through `ConnectionLifetimeRegressionTest`, 360
  cases including the four WebSocket cases, with coverage. A control run and a run
  with `LD_PRELOAD=libc_malloc_debug.so.0` and `GLIBC_TUNABLES=glibc.malloc.check=3`
  both passed (357 passed, 3 skipped). A loader trace confirmed the checker
  initialized in the test host. This sequence does not misuse the heap on every
  run; the abort is rare or timing-dependent.
- The CI scan described above.
- Source review of EmbedIO at `b8f38ba` and of the
  [v10.0.12 runtime](https://github.com/dotnet/runtime/tree/v10.0.12) TLS and
  certificate paths.

No crash or stress campaign was run and no dump capture was enabled.

## Source review findings

EmbedIO:

- Outside its HTTP/3 MsQuic layer, the core frees no native memory. Its low-level
  code reads vectors, uses `stackalloc` scratch space and reinterprets managed
  memory.
- `BrotliRequestStream` releases its decoder exactly once, and defers release
  while a read is active (`BrotliRequestStream.cs` lines 60-72). Response
  compression uses `BrotliStream` and `DeflateStream` normally.
- `HttpsSmoke` disposes its key and generated certificate, loads the server
  certificate through PKCS#12, and targets `127.0.0.1`.
- Accepted connections queue `BeginReadRequest` to the thread pool
  (`EndPointListener.cs` lines 216-228). It checks `_resourcesDisposed` under
  `_connectionSync` (`HttpConnection.cs` lines 105-107), then starts
  `AuthenticateAsServerAsync` outside the lock (line 118).
- Endpoint disposal calls `HttpConnection.Dispose` from the stopping thread
  (`EndPointListener.cs` lines 109-127). `DisposeTransportResources` sets the flag
  under the lock and disposes the `SslStream` after releasing it
  (`HttpConnection.cs` lines 566-580).
- So a stop can dispose a server `SslStream` while its handshake or a read is
  pending. A stop that lands between the check and the call starts a handshake on
  an already-disposed stream.

Runtime 10.0.12:

- `AuthenticateAsServerAsync` applies its options before checking for disposal
  (`SslStream.cs` lines 433-437, `SslStream.IO.cs` lines 110-112).
- With a `ServerCertificate`, applying the options builds an owned per-stream
  certificate context with a duplicated key handle and an up-referenced
  certificate handle (`SslAuthenticationOptions.cs` lines 141-164 and 194-198,
  `SslStreamCertificateContext.Linux.cs` lines 63-113).
- In the late-start interleaving above, that context is created after the stream's
  cleanup ran, so finalizers release it later. This is inferred from source; each
  SafeHandle still releases once.
- `Dispose` releases the remote certificate, security context, credentials and
  owned certificate context through SafeHandles (`SslStream.Protocol.cs` lines
  168-180, `SslAuthenticationOptions.cs` lines 230-240,
  `SslStreamCertificateContext.Linux.cs` lines 420-432). `SafeSslHandle` transfers
  its BIOs to the SSL object and frees them once with it (`Interop.Ssl.cs` lines
  416-494).
- The [dotnet/runtime #109689](https://github.com/dotnet/runtime/issues/109689)
  double free (SslStream disposed during a handshake) was fixed by
  [PR #113124](https://github.com/dotnet/runtime/pull/113124), present in
  `Interop.Ssl.cs` lines 143-151. Maintainers there called such concurrent
  disposal unsupported usage that should still not crash.
- Client TLS session caching is disabled for IP-literal hosts
  (`Interop.OpenSsl.cs` lines 351-367), so the session callbacks did not run for
  these clients. Server contexts are cached by certificate thumbprints
  (lines 39-80) and keep sessions in OpenSSL's own cache.
- No path that frees a small native allocation twice was identified in the
  reviewed code.

## Hypotheses

1. Leading, untested: deferred cleanup of native objects from earlier TLS work
   frees something twice inside the runtime or OpenSSL. The cleanup could run on
   the finalizer thread or as a late continuation.
   - For: both aborts surfaced under unrelated tests after the TLS lifecycle
     fixture. Both messages fit a double destruction of small objects. EmbedIO's
     stop sequencing leaves TLS state for late or finalizer cleanup.
   - Against: no concrete double-free path was found. Native-heavy fixtures run
     early in every suite, so timing alone does not discriminate. These fixtures
     pass in almost every run.
2. Untested, less likely: Brotli or zlib state. EmbedIO's and the tests' ownership
   is correct. The Brotli fixtures ended about 1.3 s and 10.2 s before the two
   aborts. The CI abort happened while a deflate test was compressing with
   `DeflateStream` in a plain `using` block. The local run had no compression in
   its last second.
3. Cannot be excluded: another runtime or native component unrelated to these
   tests.

Limits of exclusions:

- MsQuic: no QUIC or HTTP/3 fixture had started, and EmbedIO loads MsQuic only
  from HTTP/3 paths. This does not prove the library was absent and cannot fully
  exclude it as a native component. No observed stack attributes either abort to it.
- The #109689 path and the client session cache.
- DNS: the tests connect to `127.0.0.1`.
- A deterministic heap misuse in the pre-abort sequence.

## Diagnostics and remaining proposals

William approved crash-report-only capture on 2026-10-10; PR #269 implements it
in the ordinary Linux/macOS regression step. The configuration uses
`DOTNET_DbgEnableMiniDump=1`, `DOTNET_EnableCrashReportOnly=1` and a
process-specific name under `RUNNER_TEMP/embedio-crash-reports`. Failed-job JSON
reports are retained for seven days. It adds no forced crashes or extra campaign,
and full heap dumps are not enabled. The first complete capture-enabled run
(38102290875, `dbe36c0`) passed on Linux, so no crash report was generated there.
Actual capture remains unproven until a fatal termination produces a report.

Historical recommendations, with their current status:

1. Crash reports for the Linux and macOS test steps (now approved and configured).
   The original proposal set `DOTNET_DbgEnableMiniDump=1`,
   `DOTNET_EnableCrashReportOnly=1` and
   `DOTNET_DbgMiniDumpName=$RUNNER_TEMP/crash/crash.%p`, and upload
   `$RUNNER_TEMP/crash` when the job fails.
   - The JSON report lists every thread's native and managed frames and stays
     small.
   - For heap analysis, use `DOTNET_EnableCrashReport=1` with
     `DOTNET_DbgMiniDumpType=4` instead; a full dump is several hundred megabytes.
   - CoreCLR writes these from its SIGABRT handler, which glibc's abort reaches
     (`signal.cpp` lines 184 and 429-436). The variables are read in
     `process.cpp` lines 2615-2663.
   - Generation occurs on a fatal termination; a passing run is not proof of capture.
2. Candidate EmbedIO risk reduction, only if wanted before attribution: make
   handshake start and stream disposal mutually exclusive. One way is to abort a
   pending handshake by closing the socket, and dispose the `SslStream` only after
   `BeginReadRequest` has left it. This is not implemented: no cause is established,
   and it would need its own before and after evidence.

## Remaining gaps

- No dump or native stack exists for either abort.
- One checked run cannot exclude a timing-dependent race.
- The macOS exit-7 jobs without error output were classified, not analyzed.
- Ubuntu's glibc patches were not compared with upstream 2.39. The anchors show
  the same messages in the pinned image.

## Evidence locations

Under the ignored `TestResults/linux-double-free/evidence` folder of the primary
checkout:

- `malloc-check/` and `preload-check/`: anchors, both runs and the loader trace.
- `ci-scan/` and `ci-37997672083/`: scanned logs and the second abort's TRX.
- `runtime-v10.0.12/` and `glibc-2.39/`: reviewed sources.
- `image-environment.txt`: package versions.
- `tools/`: the scripts used.

The original b8f38ba run is retained under
`TestResults/native-close-completion/worktree/TestResults/native-reconciled-full`.
