# Concurrent ZIP resources

[Upstream #491](https://github.com/unosquare/embedio/issues/491), reported by
madnik7, describes intermittent failures when concurrent requests use one
`ZipFileProvider`. The attributed discussion includes rdeago's serialization
suggestions, bdurrer's experience with many archives and jswigart's alternative
SharpZipLib provider. The fix retains built-in .NET compression; no attachment
code, third-party ZIP library or new public API is imported.

## Confirmed race and resulting behavior

Entry streams share the archive's backing cursor. Locking only `GetEntry` or
`OpenFile` cannot prevent a later read from another entry seeking that cursor.
Four controlled pre-fix cases reproduce wrong stored-entry bytes and compressed
entry corruption, covering synchronous and asynchronous reads.

A provider now serializes archive lookup/open and each entry read through one
shared gate. It releases the gate between reads; it does not hold a lease for an
entire HTTP response or materialize every entry into memory. Stored and compressed
entries remain streams. Independent providers have independent gates. Application
MIME callbacks execute outside archive synchronization and can re-enter the
provider safely.

The existing .NET Standard 2.0 and .NET 10 targets, constructors, URL decoding,
case-sensitive entry names, metadata, stream capabilities, argument errors,
resource immutability and caching configuration are preserved. The returned
stream's concrete runtime implementation is internal; consume its `Stream` API.
This correction is on source until an owner-approved release ships it; do not
assume the already published 1.0.3 package contains the fix.

## Use existing file-module configuration

This is a partial snippet for an existing server; `archivePath` is an absolute
path to an immutable archive, and `server` is your existing `WebServer`:

```csharp
using EmbedIO;
using EmbedIO.Files;

server.WithModule(new FileModule("/assets", new ZipFileProvider(archivePath))
{
    Cache = new FileCache { MaxFileSizeKb = 0 },
});
```

An archive entry named `index.html` is requested at `/assets/index.html`.
`MaxFileSizeKb = 0` disables file-body caching, not metadata or hash handling.
Omit that initializer to retain the normal cache policy. See
[Getting started with files](../guides/getting-started/files.md) for a complete
host and shutdown program.

Serialization is per provider and can limit throughput for one busy archive.
The fix claims byte correctness and bounded streaming, not faster throughput.
Existing `FileCache` settings can help repeated reads. Application-owned extraction
to a directory followed by `SystemFileProvider` remains an application design
choice; this provider does not extract files or add cache/worker policies.

## Ownership, cancellation and shutdown

Dispose each stream returned by `OpenFile`. Async read cancellation interrupts
waiting for the gate without consuming another entry's bytes or stopping its
reader. Read and open failures release their operation references.

`FileModule` disposes an owned disposable provider on shutdown. Provider disposal
stops queued archive lookup/open operations and disposes the archive without
waiting for an active entry read. Closing an owned backing stream may unblock
that read; it can finish or fail under concurrent disposal. Synchronization stays
alive until existing streams and operations release their references.

With the stream constructor, `leaveOpen: true` retains the caller's original
stream. Previously opened entry streams retain the underlying runtime's behavior
rather than being forcibly invalidated by a new provider policy. For a
nonseekable source, `ZipArchive` may make its own seekable copy; disposal of that
copy is its existing behavior. This fix does not introduce archive copying.

Keep exclusive control of the backing stream while the provider is active.
Sharing one stream among multiple providers, changing its position externally or
modifying the immutable archive is outside provider synchronization. The path
constructor retains read-only, shared-read access. Create a fresh provider for a
replacement archive.

## Validation scope

The regressions check stored/compressed sync and async cursor races, queued-read
cancellation, provider disposal/borrowed ownership, queued metadata shutdown,
read-error recovery, independent providers, MIME re-entry, native stream
capability/error parity and opening a 128KiB entry without a whole-entry buffer.
Real HTTP tests verify all bytes with 32 concurrent clients, caching enabled and
disabled; file-backed tests also verify parallel reads and handle release.

The actual .NET Standard 2.0 asset passed the focused tests on a modern host.
An additional Windows .NET Framework probe targeting 4.7.2 checked 48 concurrent
byte/stream-error/ownership assertions using the installed CLR. This does not
claim exact original application, SharpZipLib or Raspberry Pi validation. Required
cross-platform and security gates remain necessary before merge.

## Constrained worker pools

The Neo CI follow-up found a 20-second request timeout with 32 ZIP HTTP clients
on .NET 10.0.12 under a two-processor limit. Synchronous semaphore waits could
occupy workers while an async read needed another worker to finish and release
the same gate. Synchronous entry points now wait on semaphore tasks, allowing
modern runtime task-blocking compensation. Archive reads remain serialized; this
does not configure the global thread pool, start dedicated workers, buffer whole
entries, or extend request timeouts.

All four existing ZIP HTTP cases passed five consecutive constrained-host runs
after the change. CI also runs the full ZIP fixture with a two-processor limit.
The .NET Standard asset uses the host runtime's thread-pool policy; do not infer
modern compensation behavior for .NET Framework or other legacy hosts. Existing
cancellation, disposal, cursor and stream-ownership regressions still apply.
This follow-up is unreleased and requires all PR checks to pass before merge.
