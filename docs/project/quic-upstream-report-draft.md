# Draft upstream report: QUIC listener endpoint release after disposal

**Status: the MsQuic report was posted on 2026-10-11 as
[microsoft/msquic#6374](https://github.com/microsoft/msquic/issues/6374) after
William's explicit approval; the dotnet/runtime note below remains unposted until
the MsQuic maintainers answer the contract question.** This document is the
reviewable source of that report, requested by
[issue #202](https://github.com/WilliamSmithEdward/embedio-neo/issues/202). Any
further external post needs the same approval and a re-read against the exact
MsQuic and .NET versions current at that time. The maintained evidence is in
[QUIC listener disposal, rebind and workaround retirement](quic-lifetime.md).

Every source statement below was checked against the named revision of the
public repositories on 2026-10-11: MsQuic `v2.6.2` (`819ab74f`) and `main`, and
dotnet/runtime `release/10.0`. Quotations are short; line numbers are omitted
because the files move. Nothing was copied from MsQuic into EmbedIO.

## Ownership and the contract in question

Two layers are involved. .NET's `QuicListener.DisposeAsync` calls
`ListenerStop`, waits for `QUIC_LISTENER_EVENT_STOP_COMPLETE`, disposes the
handle (`ListenerClose`), cancels pending handshakes and drains its accept
queue. Its documentation says it "releases all resources associated with the
listener". MsQuic's `ListenerClose` documentation says the call frees all
resources tied to the handle and is blocking; neither `ListenerClose.md` nor
`ListenerStop.md` says anything about the bound UDP port.

Inside MsQuic, `QuicListenerStopAsync` unregisters the listener and releases the
listener's binding reference before the stop-complete indication, and
`MsQuicListenerClose` waits for `StopEvent`. So when `DisposeAsync` returns, the
listener's own reference is gone. Whether the UDP endpoint is free at that
moment is decided by two things the managed runtime cannot observe: the
datapath's socket close ordering (behavior A) and binding references still held
by connections (behavior B). The managed runtime has no event for either, so an
application-side fix is impossible without a sleep or a retry. The question for
the MsQuic maintainers is therefore: **after `ListenerClose` returns, and after
`ConnectionClose` has returned for every connection accepted through it, is the
application entitled to bind the same local endpoint again?** If yes, both
behaviors below are defects. If no, that limit should be documented in
`ListenerClose.md`, and dotnet/runtime should mirror it on `DisposeAsync`.

## Behavior A: deferred socket close on the kqueue and epoll datapaths

`CxPlatSocketDelete` calls `CxPlatSocketContextUninitialize` for every socket
context and returns. When I/O has started, that function waits only for in-flight
upcalls ("Block until all upcalls complete"), removes the read filter and enqueues
`ShutdownSqe`. The `close()` of the descriptor happens later in
`CxPlatSocketContextUninitializeComplete`, when the event-queue worker processes
that shutdown entry. This is the same in `src/platform/datapath_kqueue.c` at
`v2.6.2` and on `main` as inspected, and `src/platform/datapath_epoll.c` has the
same structure.

On macOS the kqueue datapath sets no `SO_REUSEADDR` or `SO_REUSEPORT`, so a
replacement `bind()` of the same port fails with `EADDRINUSE` (48) until that
deferred `close()` has run. On Linux the epoll datapath sets `SO_REUSEPORT` on
UDP server sockets when `PartitionCount > 1` ("Only set SO_REUSEPORT on a server
socket"), which lets the replacement bind succeed while the old descriptor is
still open; that explains why the Linux campaigns did not reproduce the failure
on multi-core runners. A single-partition Linux host has not been tested and is
not claimed immune. On Windows `closesocket` runs synchronously inside
`CxPlatSocketContextUninitialize` after the rundown wait, so the idle-listener
case does not reproduce there.

Evidence (all retained in the EmbedIO-Neo repository):

- `QuicRuntimeRebindTest.DisposedRuntimeListenerRebindsSameEndpoint` is a raw
  System.Net.Quic loop: 32 bind, dispose and rebind cycles of one loopback
  endpoint, with and without an authenticated accepted connection, no retry and
  no delay. On macOS 15 Apple Silicon runners with the Homebrew `libmsquic`
  2.6.2 bottle and .NET 10.0.12 it fails at `stage=bind` with
  `AddressAlreadyInUse`; PR #200 recorded roughly one ordinary run in four, and
  its repeated experiment runs failed 8 to 12 of 20 attempts.
- An independent native probe (`EmbedIO.RuntimeCloseProbe --quic-rebind 2048`)
  traced `bind` and `close` through dyld interposition. In all twelve failures
  captured on Apple Silicon and Intel, the replacement bind began before the
  previous descriptor's close completed; the close landed 25 to 230 microseconds
  after managed disposal returned.
- A deterministic native fixture (`kqueue-lifetime-test.inc`) fails on the
  unmodified `v2.6.2` source and passes with the one-hunk test-only patch below.

Candidate fix for A (`test/EmbedIO.RuntimeCloseProbe/msquic-kqueue-close.patch`,
kqueue only): in `CxPlatSocketContextUninitialize`, after deleting the read
filter, call `close(SocketContext->SocketFd)` and set it to `INVALID_SOCKET`
before enqueueing `ShutdownSqe`, leaving context reclamation queued. With that
patch the raw test passed 80 paired runs and forty 4,096-cycle probes, the
MsQuic datapath tests passed, and an AddressSanitizer build passed 100
repetitions of the lifetime fixtures. The patch is a test-only experiment; the
epoll datapath would need the same review and the maintainers may prefer a
different mechanism.

## Behavior B: connection-held binding and the unlink-before-close window

Listeners obtain their binding with `CXPLAT_SOCKET_FLAG_SHARE |
CXPLAT_SOCKET_SERVER_OWNED` ("Listeners always share the binding"), and
`QuicLibraryGetBinding` shares an existing server binding that matches the local
port. A connection takes its own reference in `QuicBindingCreateConnection`
(`QuicLibraryTryAddRefBinding`) and releases it only in `QuicConnFree`
(`QuicLibraryReleaseBinding(Path->Binding)`). `ConnectionClose` from the
application queues the close to the worker and releases only the handle owner
reference; the worker, timer wheel, lookup table and stream references can keep
the connection alive, so `QuicConnFree` commonly runs on a worker thread after
`ConnectionClose` has returned. This holds for `v2.5.10` (bundled with .NET
10.0.12 on Windows) and `v2.6.2` alike.

While the old binding is still listed, a new listener simply shares it and no
error occurs. The failure window opens when the last connection reference drops
concurrently with the new listener start: `QuicLibraryReleaseBinding` decrements
to zero and unlinks the binding under `DatapathLock`, then calls
`QuicBindingUninitialize` after releasing the lock, and `QuicBindingUninitialize`
calls `CxPlatSocketDelete`. A `QuicLibraryGetBinding` that runs between the unlink
and the completed socket close finds no binding, creates a new one, and its
`bind()` fails with `ADDRESS_IN_USE` because the old descriptor is still open.
On Windows that window is the rundown wait plus `closesocket` on the releasing
thread; on the kqueue and epoll datapaths it also includes the deferred close
from behavior A.

Evidence: under four-process contention on Windows Server 2025 with .NET 10.0.12
(MsQuic 2.5.10, Schannel), restarting an EmbedIO HTTP/3 server after it served a
request failed with error 10048 in 4 of 7,040 restarts, once on port 20423,
outside the UDP dynamic range, so ephemeral reuse by another socket does not
explain it. Pinned Linux MsQuic 2.6.2 did not reproduce this in the same campaign;
that is not proof of immunity.

Possible directions for B, untested and offered for discussion only: complete
the socket close before unlinking the binding while lookups treat a binding
that is uninitializing as unavailable until closed, or have `QuicLibraryGetBinding`
wait for an in-progress uninitialize of a same-port binding instead of creating a
colliding socket.

## Issue text for microsoft/msquic

Posted as [microsoft/msquic#6374](https://github.com/microsoft/msquic/issues/6374)
on 2026-10-11 in the repository's bug-report structure, with the source
references above and commit-pinned links to the raw test, the native probe, the
kqueue fixture, the one-hunk patch and the evidence guide. The outline it
followed:

1. Environment: MsQuic 2.6.2 `819ab74f` (Homebrew bottle, QuicTLS) on macOS 15
   arm64 and macOS 27 Apple M5 Pro; MsQuic 2.5.10 bundled with .NET 10.0.12 on
   Windows Server 2025; libmsquic 2.6.2 on Ubuntu 24.04 (no reproduction).
2. Expected: after `ListenerClose` and `ConnectionClose` return, binding the same
   endpoint succeeds. Actual: `ADDRESS_IN_USE` from `ListenerStart` in the two
   situations above.
3. Reproduction: the raw rebind loop (System.Net.Quic, 32 cycles, no retry) and
   the native probe with traced bind/close ordering; the deterministic kqueue
   fixture.
4. Analysis: behavior A and behavior B as above, each with the function names.
5. Candidate fix for A (the one-hunk kqueue patch) and the validation it passed;
   open question for B.
6. Question: what disposal contract do the maintainers intend, and should
   `ListenerClose.md` document it?

Link the EmbedIO-Neo evidence guide and the probe sources rather than attaching
patched binaries. Do not attach the patched library.

## Suggested note for dotnet/runtime

If MsQuic confirms that endpoint release is not synchronous, request a remark on
`QuicListener.DisposeAsync` (and `QuicConnection.DisposeAsync`) stating that the
local endpoint may remain bound briefly after the call returns and that an
immediate rebind can fail with `SocketError.AddressAlreadyInUse`. If MsQuic
treats it as a defect, note the dependency on the fixed MsQuic version in the
runtime's bundled `msquic.dll` and in the Linux/macOS prerequisite guidance.
`System.Net.Quic` already maps `QUIC_STATUS_ADDRESS_IN_USE` to that socket error
(`ThrowHelper.GetExceptionInternal`), which is the error EmbedIO's bounded retry
recognizes.

## What this draft does not claim

- Before microsoft/msquic#6374 no upstream issue described either behavior as
  far as searches on 2026-10-11 found; the closest upstream discussion (#2961)
  concerns a kqueue wake-up deadlock on FreeBSD, not this ordering.
- No MsQuic release after 2.6.2 existed on 2026-10-11, and `main` retained the
  deferred kqueue close. The release/10.0 move to MsQuic 2.5.11 is a security
  servicing update (CVE-2026-105794, OpenSSL and QuicTLS hostname validation)
  and changes neither behavior.
- The patched library is a test-only experiment. EmbedIO-Neo ships stock
  dependencies and works around the window with a bounded retry; see the
  evidence guide for its limits and the retirement gates.
