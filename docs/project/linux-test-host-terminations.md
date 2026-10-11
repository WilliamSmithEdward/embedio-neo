# Linux test-host terminations

This record covers two Linux test-host terminations seen while validating
[program #181](https://github.com/WilliamSmithEdward/embedio-neo/issues/181) and
[draft PR #182](https://github.com/WilliamSmithEdward/embedio-neo/pull/182). It is
documentation only: no production code, test, workflow or quarantine changes. The
first termination is a .NET runtime defect in the managed `System.Net.HttpListener`.
The second remains unattributed, and the evidence needed to attribute it is listed.

## Native listener stop race

### Occurrences

| Where | Revision | Last reported result | Next fixture |
| --- | --- | --- | --- |
| CI run 37847220707 (Ubuntu) | `f6f69b4` | `ModuleGroupTest`, 21:34:50.819 | `NativeWebSocketShutdownTest` |
| PR #260 CI run 38088528912, job 114319998041 | `2339085` | `NativeResponseDisposalTest`, 21:44:58.633 | `NativeWebSocketShutdownTest` |
| Local pinned container | `620e1f2` | not applicable | focused run |

Both CI hosts ended within four seconds of their last result. Each log shows an
unhandled NullReferenceException in `HttpListenerResponse.FormatHeaders`, reached
from `HttpConnection.OnRead` through `HttpConnection.Close(Boolean)`,
`HttpResponseStream.DisposeCore` and `SendHeaders`. The first case of
`NativeWebSocketShutdownTest` is
`CancellationDuringUpgradeReleasesAcceptAndAllConnectedTransports(Microsoft)`.

The local run used the pinned SDK 10.0.401 image (runtime 10.0.12) and ran that
test in fresh processes. Seven processes passed; the eighth ended with the same
stack and wrote `crash.712.dmp`, whose faulting frame is
`NameValueCollection.GetKey`. The adapter and test files involved are identical
at `620e1f2` and at the integration head `ba253fa`.

### What the dump shows

The dump was read with ClrMD from the nuget.org `dotnet-dump` 10.0.745401 package,
whose SHA-512 matches the published value. Its CoreLib is file version
10.0.1226.42308, the 10.0.12 build.

- Exactly one of 247 `HttpListenerResponse` objects has a damaged header list. Its
  slots are `Server`, `Content-Type`, an empty slot, then `Transfer-Encoding`, with
  a list size of four. The lookup table holds a `Date` entry that is missing from
  the list.
- That response belongs to a connection whose upgrade request was fully parsed but
  never bound to a listener: no listener, not bound, status 404.
- The heap retains the matching exceptions. One NullReferenceException passed
  through `SystemHttpListener.Stop`, `HttpEndPointManager.RemoveListener`,
  `HttpEndPointListener.Close` and `HttpConnection.Close`. The test's
  `stop.Cancel()` raised an AggregateException from that callback. A second passed
  through `HttpConnection.SendError` and `HttpResponseStream.BeginWrite`. Two more
  passed through `HttpConnection.Close` from `OnRead`; the second escaped.
- The response has four `HttpResponseStream` objects. Three are closed, one for
  each failed closing serialization. The fourth is the stream `SendError` tried
  to write through.
- That round's native listener is `Stopped` and still holds one pending accept,
  EmbedIO's `GetContextAsync`. `Stop` threw before its cleanup completes waiters.
- The 32 upgraded responses in the heap are intact. The WebSocket handshake did
  not touch the damaged response.

### Mechanism

Source references are to the
[v10.0.12 managed implementation](https://github.com/dotnet/runtime/tree/v10.0.12/src/libraries/System.Net.HttpListener/src/System/Net/Managed),
which .NET uses on Linux and macOS.

1. EmbedIO's `RunAsync` cancellation callback calls `HttpListener.Stop()`. Its
   `Close` removes the listener's prefixes and then closes the endpoint
   (`HttpListener.Managed.cs` line 154, `HttpEndPointListener.cs` lines 290-318).
   For every connection not yet bound to a listener, the stopping thread calls
   `HttpConnection.Close(true)`. That serializes a closing response:
   `SendHeaders` adds `Server`, `Date` and `Transfer-Encoding` under
   `_headersLock`, then calls `FormatHeaders` (`HttpListenerResponse.Managed.cs`
   lines 162-290).
2. On another thread, the same connection's read completes the request when the
   client's final CRLF arrives. `BindContext` fails because the prefixes are gone,
   so the connection calls `SendError(404)` (`HttpConnection.cs` lines 301-305).
   Its `ContentType` assignment adds `Content-Type` through `Headers.Set` without
   `_headersLock` (`HttpConnection.cs` line 452, `HttpListenerResponse.cs` line 48).
3. `NameValueCollection` is not safe for concurrent writers. Two simultaneous
   additions lost `Date` and left an empty slot inside the list's size.
   `BaseGetKey` dereferences that slot (`NameObjectCollectionBase.cs` line 328).
4. `SentHeaders` is set only after `FormatHeaders` succeeds, so every later close
   retries serialization and fails again. `OnRead`'s catch-all handler calls
   `Close(true)` again (`HttpConnection.cs` line 250). That exception leaves a
   thread-pool continuation and terminates the process. The stopping thread's
   exception leaves `Stop()` before `Cleanup` runs.

The request being a WebSocket upgrade is incidental. Any request that completes
while `Stop` closes its unbound connection can take this path. The test hits it
because it releases partial requests and then cancels immediately in some rounds.

### What EmbedIO can and cannot change

`Stop`, `Abort`, `Close`/`Dispose` and removing the last prefix all reach
`HttpEndPointListener.Close`, which closes unbound connections from the calling
thread. None of them waits for those connections' read callbacks, and the header
lock is private to the runtime. No public API avoids the race. A private-field
hook is not authorized for this, so no production change is made.

EmbedIO's own managed listener (`HttpListenerMode.EmbedIO`, the default) does not
share this path. It closes an unbound request without writing a 404, and endpoint
disposal aborts transports through `ForceClose` before any response is
serialized. The EmbedIO case of the same test passed in all seven completed
local processes.

Source review finds a second candidate race that the dump does not show. `Stop`'s
cleanup also serializes closing responses for registered connections from the
stopping thread. The runtime's handshake (`HttpWebSocket.Managed.cs` lines 48-50)
and application header writes on in-flight requests change headers without
`_headersLock`, so they can overlap that serialization. No handshake response in
the dump is damaged.

Residual risk, derived from source and the pending accept seen in the dump: if
only the stopping thread fails, `Stop` throws into the caller of `Cancel()` and
leaves the accept pending, so `RunAsync` would not complete until the server is
disposed. This has not been observed.

### Recommendations

- Report the defect to dotnet/runtime once the owner authorizes it. Possible
  corrections are serializing `SendError`'s header changes under `_headersLock`,
  synchronizing endpoint close with each connection's read callback, or not
  retrying serialization from `OnRead`'s error handler.
- `CancellationDuringUpgradeReleasesAcceptAndAllConnectedTransports(Microsoft)`
  deliberately overlaps request completion with `Stop`. Its occasional host loss
  on Unix is this runtime defect, not an EmbedIO regression. The owner can keep
  it, run that case in a separate process so a runtime abort cannot discard the
  rest of the suite, or restructure it with an explicit coverage decision.
- Applications on Linux or macOS whose shutdown can overlap request arrival should
  prefer `HttpListenerMode.EmbedIO`.

## Unattributed glibc abort at b8f38ba

### Evidence

The full Linux suite at `b8f38ba`, run with coverage in the same pinned image,
ended with `free(): double free detected in tcache 2` after 356 reported cases,
4.7 seconds into execution. Minidumps were not enabled, so there is no dump.

- The 356 results never overlap; the run was sequential. The last completed case
  was `ConnectionLifetimeRegressionTest.UnregisteredIdleOrHandshakeConnectionReleasesResources(True,True)`
  at 22:20:38.638. The controller recorded the termination at 22:20:38.675. The
  next case by fixture order, `WebSocketUpgradeKeepsItsTransportUntilCloseOrShutdown(False,False)`,
  reported no result.
- The coverage report was written after the abort and contains no hits.
- Before the abort the process ran Brotli request and response fixtures and the
  TLS lifetime cases (SslStream and X509 over OpenSSL), including a listener
  stopped during a pending handshake and concurrent `Close`/`Stop`/`Dispose`.
- No QUIC or HTTP/3 fixture had started. EmbedIO loads its MsQuic layer only from
  HTTP/3 listener and test paths. This evidence does not implicate MsQuic; it
  does not prove the library was absent from the process.
- Outside its HTTP/3 MsQuic layer, EmbedIO's core frees no native memory. Its
  low-level code reads vectors, uses `stackalloc` scratch space and reinterprets
  managed memory. Native frees on these paths come from runtime-owned handles
  (OpenSSL, Brotli, zlib) or the runtime itself.
- [dotnet/runtime #109689](https://github.com/dotnet/runtime/issues/109689)
  reports the same glibc message on Ubuntu 24.04, caused by disposing SslStream
  during a handshake. [PR #113124](https://github.com/dotnet/runtime/pull/113124)
  fixed it before .NET 10 by tying the peer chain handle to its parent SSL handle;
  that change is present in the 10.0.12 source (`Interop.Ssl.cs` lines 143-151).
  That defect is excluded. The same family of disposal races remains a candidate,
  because the TLS lifetime cases ran within the preceding second.
- Later pinned-container runs did not abort: four complete suites of 5,054 cases
  and 40 focused `ConnectionLifetimeRegressionTest` processes, all with minidumps
  enabled. Their ordinary test failures are separate from this abort.

### Remaining uncertainty and next diagnostic

The freeing frame is unknown, so no cause is claimed. CoreCLR handles SIGABRT,
which glibc raises for this error, and writes a crash dump before restoring the
default action when `DOTNET_DbgEnableMiniDump=1` is set (`signal.cpp` lines 184
and 427-438). A full dump (`DOTNET_DbgMiniDumpType=4`) from the Linux test step,
retained on failure, could identify the native stack; it is not enabled.

William approved crash-report-only capture on 2026-10-10. The Linux/macOS
regression step enables `DOTNET_DbgEnableMiniDump=1` with
`DOTNET_EnableCrashReportOnly=1` and a process-specific name under
`RUNNER_TEMP/embedio-crash-reports`. JSON reports from failed jobs are uploaded
as `test-host-crash-reports-<OS>` and retained for seven days. Full heap dumps
are not enabled. This observes ordinary CI; no forced crash or extra campaign
is added, and assertions, discovery minimums and failure handling are unchanged.
Capture is configured, not yet demonstrated by a new report from a crash.

The newer retirement-branch run 38101741496 at `2ba2113` reports a host crash
(PID 3122) after 359 passed cases in nine seconds. It has no native stack or
malloc message in the uploaded artifact, so it is not classified as a confirmed
recurrence of the glibc double free. Its incomplete discovery count is a
consequence of termination, not evidence that the minimum should be lowered.

## Evidence locations

Under the ignored `TestResults/linux-runtime-crashes` folder of the primary
checkout: `evidence/dump-results/heap-report.txt` (heap analysis),
`evidence/dump-analyzer` (reader source), `evidence/runtime-v10.0.12` (reviewed
runtime sources), `evidence/pr260-ci` and `evidence/run-37847220707` (CI logs and
TRX). The local reproduction is retained under
`TestResults/multiplexed-request-model-review/worktree/TestResults/native-callback-probe`,
and the b8f38ba run under
`TestResults/native-close-completion/worktree/TestResults/native-reconciled-full`.
