# Native QUIC explicit abort disposal

The native candidate must preserve an application's explicit stream reset code
when the stream is subsequently disposed. The original binding queued the
requested abort, then disposal queued another send/read abort with `0x10c`
(`H3_REQUEST_CANCELLED`). This could replace the requested error and, when
reliable reset is enabled, downgrade the reset that protects a stream header.

The binding now remembers each abort direction only after the native shutdown
call succeeds. Disposal shuts down remaining directions and retains the normal
native callback/handle lifetime, but does not reissue an already queued abort.
A failed shutdown call does not set the remembered bits, so cleanup can still
request shutdown. Graceful FIN handling is unchanged.

This is an original managed-binding correction. It changes no public API,
default provider, target, dependency, certificate policy or native library.
It does not address the distinct native wire-format/final-size gaps tracked in
[#303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303), and does not
enable or advertise WebTransport.

## Reproduction and validation

Three new real-connection cases cover explicit read, write and both-direction
abort followed immediately by asynchronous disposal. They forward every native
call through an observer at EmbedIO's own binding, assert that the original
direction/code is requested exactly once, and verify the remote peer's read and
write completion errors. Other directions still receive the normal disposal
code when they have not already been aborted.

With unchanged production source from engine `5b014a9`, all three cases fail:
disposal issues an extra abort. With the correction, all three pass, and the
complete Windows native lifetime fixture passes **78/78**, zero skips. Builds
complete with zero warnings/errors. Evidence is retained in the worktree's
ignored `TestResults/abort-disposal/{before,after}` TRX and corresponding logs.
The control harness initially duplicated the source BOM during staging; its
build failure is retained, and the corrected control was run with every
assertion unchanged. The candidate source was restored in `finally`.

A separate unfinished reset prototype also verifies the correction against an
independent quic-go peer and a test-only native reset diagnostic. Its immediate
disposal case initially captured an extra `0x10c` send abort after `0x1234`;
with this correction both prefix/reset cases pass. The plain peer-only test
passed before the correction, so it did not independently expose the call race;
the forwarding observer establishes the duplicate request. No stock MsQuic
current-reset interoperability success is claimed.

The same compiled source passes **78/78** native lifetime cases, zero skips,
on Linux .NET 10.0.12 with stock MsQuic 2.6.2 in immutable image
`sha256:46f173c7a38ab18c682bd071376c9eb2b1350cc9b202a48b52ec38748427e0c3`
(10.532 seconds). The source mount is read-only; core hash, log and TRX are
retained under `TestResults/abort-disposal`.

The complete Windows suite reports **4720 cases: 4713 passed, seven expected
skips, zero failures**, in 3m 52.539s, with QUIC and the independent datagram
peer required. Both library targets build warning-free; the C# parser guard
and suppression guard pass. The discovery floor is increased by the three
ordinary regressions from 4717 to 4720 in all six maintained locations.
All ten existing resource gates pass unchanged: hot, queue, cold, HTTP,
allocations, full/partial/unread request bodies, wire framing and chunked output.

macOS and exact-head hosted checks remain required before integration.
Program #181 remains open.
