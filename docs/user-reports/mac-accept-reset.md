# macOS socket accept crashes after client resets

Issue [#38](https://github.com/WilliamSmithEdward/embedio-neo/issues/38) tracks a
process crash in the managed listener on macOS. An immediate TCP reset can make
.NET throw `ArgumentException` from `IPEndPoint.Create` inside socket accept
completion. This happens before EmbedIO's completion handler runs, so catching
an exception in that handler cannot protect the process.

The relevant runtime report is
[dotnet/runtime #121848](https://github.com/dotnet/runtime/issues/121848).
Its associated [runtime fix](https://github.com/dotnet/runtime/pull/131869)
guards empty peer addresses after macOS IPv6 accepts. A merged runtime change
alone does not establish that a particular installed .NET servicing release
contains it. Our observed crashes occurred with .NET 10.0.12.

## Mitigation and tradeoff

EmbedIO uses a dedicated background worker for blocking `Socket.Accept()` on
macOS IPv6 listening endpoints. Address construction happens on that worker's
managed call stack; the specific invalid-`socketAddress` exception can be caught
and the next connection accepted. The first such failure is logged once per
endpoint. Socket errors are retried with a small delay to avoid a busy loop.
Closing the listening socket interrupts the worker on Stop/Dispose.

This costs one background thread per bound macOS IPv6 endpoint, shared by its
prefixes. IPv4 and other platforms retain the asynchronous accept path. HTTP
reads, TLS authentication, and request dispatch remain asynchronous; an idle or
incomplete TLS handshake does not occupy the accept worker. Public APIs,
framework targets, and IPv6 availability are unchanged. No new dependency or
runtime patch is required. This is an application mitigation, not a correction
to .NET or the macOS kernel.

The accepted socket is now also disposed if connection construction fails,
and registration is coordinated with endpoint disposal to avoid retaining
connections after shutdown.

## Regression coverage

The original incomplete-header/reset test keeps its immediate reset timing.
New cases issue resets without waiting for server-side acceptance, both with
and without partial bytes, over IPv4/IPv6 and HTTP/HTTPS, including IPv4 clients
on wildcard dual-stack IPv6 endpoints. Each batch then makes
a fresh healthy connection; keep-alive cannot hide a stopped accept loop.
Separate shutdown cases observe the internal worker completing within five
seconds after idle Stop/Dispose. CI additionally requires five independent
macOS stress executions; any failure fails the job rather than being retried.

See the linked issue and PR for actual platform execution results. A Windows
pass alone cannot establish that the macOS mitigation works, and these tests
do not constitute a throughput or exhaustive runtime conformance measurement.
