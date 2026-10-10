# Native QUIC lifetime campaigns on Apple Silicon

This record covers lifetime and resource validation of the internal MsQuic
provider for [program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181),
under the umbrella [draft PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182).
It adds test tooling, regressions and documentation only. No production code,
workflow or CI floor changes.

Application HTTP/3 (`HttpListenerMode.EmbedIOHttp3`) still uses
System.Net.Quic. Every result here drives the internal native provider
(`MsQuicApi`, `MsQuicNativeListener`, `MsQuicNativeConnection`,
`MsQuicNativeStream`) through reflection, with a System.Net.Quic peer in the
same process. None of it is evidence about application HTTP/3.

## Revisions and capabilities

| Revision | Source | Abort | CompleteWrites | Direction tasks |
| --- | --- | --- | --- | --- |
| `codex/managed-http-engine`, includes PR #238 | `a4f710574748090ef5bad0871d68f28bcf9f03be` | no | no | yes |
| Draft PR #239 head (stream control) | `ba4991b187c8844c62c77bff9bbe2ca7c7278980` | yes | no | yes |
| Draft PR #240 head (stream completion, includes #239) | `ac81b200e5e530d758aef1bbeafabdde4c5564b5` | yes | yes | yes |

Capabilities are detected by reflection at run time and recorded in every
campaign file. Cases needing an absent capability are reported as skipped with
the reason, so the same test file runs on all three revisions. The PR heads
were tested in detached local checkouts; their branches were not changed.

## Environment

All results are from real Apple Silicon hardware, not a hosted runner or a
Linux container:

- MacBook Pro, Apple M5 Pro (18 cores, 48 GiB), macOS 27.0.1 (26A434),
  Darwin 27.0.0 arm64; process architecture Arm64.
- .NET SDK 10.0.401, runtime 10.0.12 (osx-arm64 archive verified against the
  published SHA-512).
- MsQuic 2.6.2 from the Homebrew bottle that CI pins
  (`sha256:c7d7fbdaec216ced0ed5b9b7b58d3d6b251e96dc4d666409e3fb211cf8c3bf55`),
  relocated exactly as the CI step does. Loaded image:
  `libmsquic.2.6.2.dylib`, SHA-256
  `2DB15FC5D1CA50D794556735889DD82285C00F3D4322534FC69EF56B0E385207`
  after relocation and ad hoc signing; `QUIC_PARAM_GLOBAL_LIBRARY_VERSION`
  2.6.2.0, git hash `819ab74f851ee168504cbc392ec32e7bed1d82e9`.
- OpenSSL 3.6.5 from the `arm64_golden_gate` bottle
  (`sha256:fd8ea89de8c9d5390eb17041609842f61986b43932fabdcba9b43bacddc63c84`),
  isolated beside MsQuic. Nothing was installed system-wide, and no firewall,
  OS limit or runtime replacement was changed.

The host was shared with another agent's datagram work in a separate checkout.
No `BENCHMARK-LOCK.txt` existed. Campaigns took this checkout's lock
atomically and recorded the foreign processes present at start. The measured
counters are per process, so another process's load affects timing, not counts.

Intel hardware was not available; no Intel runtime result is claimed.

## Coverage

`test/EmbedIO.Tests/MsQuicNativeLifetimeCampaignTest.cs` is owned by this
work. Its ordinary cases run in the normal suite (19 discovered, a few seconds
each):

- Repeated listener/configuration/connection/stream creation and disposal in
  seeded random orders, including parents before children and the peer first.
- A pending read and a committed, flow-blocked 8 MiB write on the same stream,
  resolved by each of: cancellation of both, stream disposal (with late peer
  bytes), connection shutdown, connection disposal while the stream lives,
  peer abort of both directions, and listener disposal (which must not reach
  accepted streams). A sibling or fresh connection must then transfer data.
- Disposal, connection shutdown or peer reset while one receive indication
  is only partly consumed, so MsQuic still lends the remaining bytes.
- Stream admission overflow: 136 peer streams against the 128-stream
  unclaimed queue. Exactly eight are rejected with H3_REQUEST_CANCELLED
  (0x10c), all 128 admitted IDs are accepted with their bytes, and a later
  stream echoes.
- Connection admission overflow: 256 queued connections, four refused with
  CONNECTION_REFUSED, a drain that shuts each queued connection down before
  disposal, no re-admission, then a healthy connection.
- Concurrent, repeated disposal of streams, connection, listener,
  configuration, registration and API while reads are pending, writes are
  flow-blocked and peers keep sending.
- Parent retention: listener and configuration release at once; registration
  and API stay open while a connection lives; the connection keeps exactly one
  lease while its stream lives; everything releases after the last child.
- Abandoned stream and connection wrappers released only by finalization while
  their peer stays connected.
- With Abort (PR #239): a local read abort fails only the pending read and
  sends STOP_SENDING with the code; a later write abort ends the blocked write
  and resets the peer with its code.
- With CompleteWrites (PR #240): after a canceled read, FIN queued behind a
  committed flow-blocked 1 MiB write delivers every byte, then the read
  direction still receives.

Each ordinary case takes a resource baseline first and, after a bounded wait
for quiescence, requires MsQuic's active connection and stream counters, the
process's UDP sockets and the count of unobserved native task exceptions to
return to it.

Five explicit campaigns (`TestCategory=NativeQuicLifetimeCampaign`) repeat the
same bodies with seeded random choices, and two explicit defect reproductions
(`TestCategory=NativeQuicLifetimeDefect`) are described below. Explicit cases
are not discovered by the ordinary suite.

### Resource measurement

- MsQuic `QUIC_PARAM_GLOBAL_PERF_COUNTERS`: `CONN_ACTIVE`, `STRM_ACTIVE`,
  `CONN_CREATED`, `CONN_APP_REJECT`. They are process-wide and include the
  System.Net.Quic peer, which shares the loaded library.
- Open descriptors (`/dev/fd`), UDP sockets (`lsof -a -p <pid> -i UDP`),
  thread count, managed bytes and heap size after two full collections with
  finalizers, and resident size.
- `TaskScheduler.UnobservedTaskException` entries whose message names the
  native provider, after forced finalization.
- Growth is the least-squares slope per iteration across samples taken every
  tenth of a campaign, excluding the first sample. A plateau with zero slope
  is pooling or warm-up; a persistent positive slope is accumulation.

Darwin closes a released UDP socket on a later kqueue turn
([QUIC lifetime and rebind](quic-lifetime.md)), so socket and counter checks
poll for up to ten seconds before failing.

## Confirmed defect: native connection disposal silently aborts the close

`MsQuicNativeConnection.Dispose` calls `ConnectionShutdown(0, 0x100)` and,
when no stream holds a lease, `ReleaseHandle` calls `ConnectionClose`
immediately. `ConnectionShutdown` only queues the shutdown. The
[ConnectionClose contract](https://github.com/microsoft/msquic/blob/v2.6.2/docs/api/ConnectionClose.md)
makes a close before shutdown completes an abortive, silent shutdown. The
requested CONNECTION_CLOSE is usually lost. System.Net.Quic's
[`QuicConnection.DisposeAsync`](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicConnection.cs)
waits for SHUTDOWN_COMPLETE, "the last event", before disposing its handle.

Established connections disposed without awaiting `ShutdownAsync`
(probe output retained under `TestResults/apple-lifetime/diag-established-*`):

| Connections | Disposal | Peer outcome | Slowest peer |
| --- | --- | --- | --- |
| 1 | `Dispose` | `ConnectionAborted`, 0x100 | 2 ms |
| 2 | parallel `Dispose` | 1 × 0x100, 1 × `InternalError` | 25 ms |
| 4 | parallel `Dispose` | 4 × `InternalError` (run 2: 3 × and 1 idle timeout) | 26 ms / 30 s |
| 16 | parallel or sequential `Dispose` | 15 × `InternalError`, 1 × `ConnectionIdle` | 30 s |
| 16 | `ShutdownAsync`, then `Dispose` | 16 × `ConnectionAborted`, 0x100 | under 1 ms |

The peer's `InternalError` carries `QUIC_STATUS_ABORTED` instead of the
application code. Its exact cause is not confirmed: MsQuic's process-wide
`SEND_STATELESS_RESET` counter stayed at zero in these runs, so it is not a
stateless reset sent by this process (`TestResults/apple-lifetime/stateless-reset-check`
in the probe checkout). Either way the application close is not delivered. The
16-connection rows were identical in six runs.

Queued, unconfigured connections drained by `Dispose` while the listener runs
(`TestResults/apple-lifetime/diag-queued-drain*`):

| Clients | Drain | Server admissions | Clients refused promptly | All settled |
| --- | --- | --- | --- | --- |
| 1 | `Dispose` | 1 | 1 | 57 ms |
| 16 | `Dispose` | 69 to 80 | 1 to 3 | 16 s (idle timeout) |
| 64 | `Dispose` | 320 | 0 | 16 s |
| 256 | `Dispose` | 552 to 660 | 0 | 16 s |
| 16 | `ShutdownAsync`, then `Dispose` | 16 | 16 | 3.08 s |
| 64 | `ShutdownAsync`, then `Dispose` | 64 | 64 | 3.08 s |

Each lost close lets the client's retransmitted Initial create a new server
connection, so the listener admits the same client several times and the
client waits for its idle timeout. The shutdown-then-dispose rows also show
that MsQuic takes about three seconds to complete shutdown of an unconfigured
connection.

The hang first surfaced in the connection-overflow regression's drain phase
(`TestResults/apple-lifetime/base-new-attempt1`); that regression now shuts
queued connections down before disposal so it tests admission rather than
this defect.

### Reproduction

Two explicit cases fail on `a4f7105` in three of three runs each (a third,
for the stream defect below, is in the same category):

```sh
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter "TestCategory=NativeQuicLifetimeDefect"
```

- `DisposedEstablishedConnectionsNotifyEveryPeerWithTheApplicationCode`: four
  established connections disposed without `ShutdownAsync`. Observed
  `QuicException: An internal error has occurred. Status code:
  QUIC_STATUS_ABORTED` instead of `ConnectionAborted` with 0x100.
- `ConnectionOverflowDrainByDisposalRefusesEachPeerOnce`: sixteen queued
  connections drained by `Dispose`. Observed "peers were not refused within
  10 s", with more than sixteen admissions.

The listener paths that already stop the listener first are less exposed: the
listener's own `ReleaseHandle` closes before disposing queued connections, so
peers see an unreachable host after about one second instead of re-admission.

## Confirmed defect: disposal after a completed final write resets the stream

`WriteAsync(payload, completeWrites: true)` completes at SEND_COMPLETE, when
MsQuic returns buffer ownership, not when the peer has the bytes.
`MsQuicNativeStream.Dispose` then calls `StopReads`, which records a send
failure for any unfinished send direction, and shuts the stream down with
abortive send and receive flags (`AbortFlags`, 0x10c). RESET_STREAM discards
every unacknowledged byte, so a response written with FIN and then disposed
is lost.

| Payload | Peer received everything | Peer saw `StreamAborted` 0x10c |
| --- | --- | --- |
| 1 KiB | 3 of 20 | 17 of 20 |
| 64 KiB | 0 of 20 | 20 of 20 |
| 1 MiB | 0 of 20 | 20 of 20 |

`DisposalAfterACompletedFinalWriteDeliversEveryByte` (explicit, defect
category) writes 64 KiB with FIN and disposes immediately, eight times per
run. All 24 trials in three runs on `a4f7105` ended with `StreamAborted` 0x10c
(`TestResults/apple-lifetime/fin-dispose-repro-base-run*`). The probe found it
because its server disposed each echo stream right after the final write; the
existing send cases read everything before disposal. Application HTTP/3 over
this provider would truncate responses that are disposed this way.
PR #240's `CompleteWrites` queues FIN without waiting, and disposal there takes
the same abortive path.

## Proposed fixes for the native owner

Two changes, both validated only in scratch checkouts on top of `a4f7105`
(diffs under `TestResults/apple-lifetime` and the probe directory):

1. **Connection.** Do not call `ConnectionClose` until SHUTDOWN_COMPLETE once a
   shutdown has been requested. A non-blocking form in
   `MsQuicNativeConnection.ReleaseHandle`:

   ```csharp
   if (Volatile.Read(ref _shutdown) != 0 && !_signals.Closed.Task.IsCompleted)
   {
       var native = handle;
       // MsQuic holds only a function pointer: root the callback until close.
       var root = GCHandle.Alloc(this);
       _ = _signals.Closed.Task.ContinueWith(_ =>
       {
           try { CloseNative(native); }
           finally { root.Free(); }
       }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
       return true;
   }
   CloseNative(handle);
   return true;
   ```

   `CloseNative` is the existing body (`ConnectionClose`, rejected-stream
   close, signal completion, registration release). `Closed` runs
   continuations asynchronously, so the close never runs on an MsQuic
   callback thread.

2. **Stream.** When a FIN was submitted and no send failure exists, decide
   that before `StopReads`, leave the send direction untouched (abort only an
   unfinished receive direction, flag 4), do not fault `WritesClosed`, and
   defer `StreamClose` and the connection lease release to SHUTDOWN_COMPLETE
   with the same rooting. Connection shutdown still bounds a peer that never
   reads.

The rooting is required. The first connection candidate deferred the close
without a `GCHandle`; the callback delegate became unreachable and the test
host aborted in `CallbackOnCollectedDelegate` under `QuicConnOnShutdownComplete`
(crash report in `TestResults/apple-lifetime/fix-candidate1-crash-unrooted-callback`).
The first stream candidate checked for a send failure after `StopReads` had
already recorded one, so it still reset; its runs are retained as
`defects-fix2-run*`.

With both changes on `a4f7105`:

- all three defect reproductions pass in three of three runs;
- all 86 native cases pass (84 run, two capability skips);
- every peer receives `ConnectionAborted` 0x100 within 2 ms for 2 to 16
  established connections, parallel or sequential;
- sixteen queued connections drained by `Dispose` give exactly sixteen
  admissions and all clients refused within 57 ms;
- stopping the listener and then disposing a queued connection refuses the
  client in 1 ms instead of an unreachable-host error after about one second.

Parent registration and API release become asynchronous after the last
connection, and a gracefully finished stream holds its connection until the
peer acknowledges FIN. The new cases therefore wait, bounded, for eventual
release instead of asserting it immediately. When the corrections are
integrated, the three reproductions should become ordinary cases and the
drain in `ConnectionOverflowOnce` can return to plain disposal.

## Memory growth investigation

Resident size rose about 24 KB per iteration in every base campaign while
managed memory, descriptors, UDP sockets, threads and native counters
returned to baseline. Separate probes (physical footprint from `vmmap`,
400 iterations per layer) attribute it to the test fixture rather than the
provider:

| Churned per iteration | Footprint, start to 400 | Steady growth |
| --- | --- | --- |
| API open/close | 84.5 to 84.7 MB | about 0 |
| + registration | 84.9 to 85.5 MB | about 0 |
| + configuration | 85.1 to 85.7 MB | about 0 |
| + new certificate and credential load | 85.7 to 109.3 MB | about 31 KB after warm-up |
| + listener start/stop | 109.3 to 111.7 MB | noisy, about 0 |
| + one native connection | 112.0 to 129.5 MB | about 23 KB after warm-up |
| new certificate only, no QUIC | 129.6 to 139.5 MB | about 25 KB |
| BCL listener, connection and new certificate | 138.6 to 149.0 MB | about 26 KB |

Each campaign iteration creates one certificate with
`HttpsSmoke.CreateCertificate`, and that alone costs the observed rate, with
or without QUIC. With one setup and 6,400 connections (200 rounds of 32, each
with a 1 KiB echo), the native provider's footprint warmed to 111.8 MB by
round 100, was 112.2 MB at round 200, and fell to 77.1 MB when the listener
closed; System.Net.Quic plateaued at 87.6 MB. That is pooling, not
accumulation. macOS `leaks` reported small unattributed blocks (100 KB native,
305 KB cumulative after the BCL run in the same process) with no MsQuic or
EmbedIO frame; attribution would need malloc stack logging, which was not
used.

## Other observations

- Threads grew to about 60 during admission overflow and returned to the
  thirties; their slope was zero in every campaign's second half (thread-pool
  warm-up).
- `ShutdownAsync` on an accepted but unconfigured connection completes after
  about 3.08 s; the client receives the TLS `user_canceled` alert at that time.
- Draining queued connections only after stopping the listener avoids
  re-admission, because the listener socket is gone.
- No unobserved native task exception was recorded in any ordinary case or
  campaign listed below.

## Campaign results

CAMPAIGN-RESULTS

## Running the campaigns

```sh
scripts/native_quic_lifetime_campaign.sh --iterations 1000 --seed 20261010 --label base --library-dir <MsQuic lib directory>
```

The script refuses to start while another owner holds
`TestResults/BENCHMARK-LOCK.txt` beside the common git directory, creates its
own lock with `noclobber`, records the environment and foreign processes,
builds, and runs the campaigns once with `EMBEDIO_REQUIRE_QUIC=1`. It never
retries a failure. Each attempt has its own directory under
`TestResults/native-quic-lifetime`, containing `environment.txt`, `run.log`,
the TRX file and one JSON evidence file per campaign (seed, iteration counts,
source SHA, OS, runtime, loaded MsQuic path/hash/version, capabilities, every
resource sample, growth slopes, unobserved exceptions and, on failure, the
iteration and phase).

Pass the library directory to the script rather than through
`DYLD_FALLBACK_LIBRARY_PATH`: macOS removes `DYLD_*` variables when a protected
binary such as `/usr/bin/env` starts the script. The first smoke attempt lost
the variable that way and every campaign skipped; it is retained and marked
`INVALID`, and the cases now fail rather than skip when `EMBEDIO_REQUIRE_QUIC=1`.

The campaigns can also be selected directly:

```sh
EMBEDIO_REQUIRE_QUIC=1 EMBEDIO_NATIVE_QUIC_CAMPAIGN_ITERATIONS=200 EMBEDIO_NATIVE_QUIC_CAMPAIGN_SEED=1 \
  dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter "TestCategory=NativeQuicLifetimeCampaign"
```

## Untested gaps

- Hosted macOS runners, Intel Macs and other Apple Silicon generations; only
  one M5 Pro was used.
- Windows and Linux runs of the new cases; the Linux resource probe uses
  `/proc` and was not exercised here.
- Native datagrams, WebTransport and application HTTP/3 through the native
  provider, none of which are integrated.
- Fault injection inside native calls, low-memory behavior and 32-bit
  processes.
- Packet loss and reordering; all traffic is loopback.
- Physical-footprint (`phys_footprint`) memory; resident size is reported.
- Durations beyond the bounded campaigns.

The 19 new ordinary cases raise discovery from 4903 to 4922. The CI and
CONTRIBUTING floors are unchanged here and still pass; the integrating branch
should set the floor from actual discovery.
