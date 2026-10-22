# Response-pipeline state-machine fuzzing

This test-only expansion is included in the 2.3.0 source tree. The unpackaged
`EmbedIO.Fuzz` executable exercises the real file/response pipeline through
`response-http1`, `response-http2` and `response-http3`. Production packages,
dependencies, APIs and defaults are unchanged.

## Run and replay

Use the pinned SDK and locked dependencies:

```sh
dotnet restore test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj --locked-mode
dotnet build test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj -c Release --no-restore
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 response-http1
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 32 response-http2
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --response-pipeline-selftest
```

HTTP/3 additionally requires a working .NET HTTP/3 client (MsQuic); an explicitly selected
HTTP/3 campaign fails when it is unavailable. Replay the matching corpus on a
prepared host:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/response-http1-large-streamed.json
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/quic/response-http3-large-cached.json
```

The existing [state-machine runner](multi-connection-fuzzing.md) records original
failures, exact command traces, runtime/core-binary identity, transition counts
and invariant-preserving shrinking. Unexpected exceptions and timeouts remain
failures. No timing sleep establishes an ordering, and a failed sample is not
silently retried. Seeded generation and checked-in replay complement one another.

## Independent checks and generated operations

The file's expected bytes are generated before the server starts. The campaign
does not use the production range parser, compression implementation, framing
helpers or ETag generator to compute its expected result. It learns ETags as
opaque validators for each representation and then checks selection, precedence
and stability. Compressed bodies are decoded with the BCL and compared byte for
byte with the known file or request-specific trailer payload.

Payload sizes include zero, one, 127, 128, 4,096, 8,192, 65,535, 65,536 and
65,537 bytes. Cases alternate cached/streamed content and compressible/seeded
incompressible data. Checked-in traces pin empty, single-byte and large
cached/streamed examples for each protocol.

| Command | Operation |
|---|---|
| 0-2 | Full identity, gzip or deflate GET |
| 3 | HEAD with representation metadata and no body |
| 4 | Closed, open-ended or suffix range; exact selected bytes and Content-Range |
| 5 | Unsatisfiable range with a 416 response and resource length |
| 6 | Matching/wildcard ETag, date validators, or conditional range returning 304 before range processing |
| 7 | Nonmatching If-None-Match overrides a newer If-Modified-Since |
| 8 | Matching/nonmatching If-Range tag or date |
| 9 | Fragmented trailer response, optional large field and copied trailer snapshot |
| 10 | Forced compression with Range ignored, or unsupported Range unit ignored |
| 11 | Complete/cancel an actor, complete all actors, or replace a client |

Checks also include negotiated status/protocol/coding, bounded encoded/decoded
body reads, declared content length, incompatible framing, absence of leaked
Content-Range/trailers, exact independent Set-Cookie lines, per-actor request
cookies, trailer separation and once-only context closure. Framing is captured
before buffering can cause HttpClient to synthesize Content-Length.

The negative self-test deliberately corrupts file bytes, cookie identity and a
trailer snapshot on HTTP/1 and HTTP/2. Each must fail with its specific invariant;
a different error does not count as successfully detecting the injected fault.

## Actual overlap, cancellation and ownership

Every trace, including a shrunk empty trace, starts three requests from separate
client handlers and holds them at application gates. Entry on all three is
observed before release. TCP remote ports must be distinct for HTTP/1 and
HTTP/2; the HTTP/3 clients also use separate handlers, without treating UDP port
numbers as QUIC connection identities.

Commands retain pending requests across actors. Selected trailer requests flush
a prefix and pause. Cancellation can then occur after the client has actually
read a wire byte, before the remaining body is released. Other cancellations
occur after application entry. A final full-response round checks healthy
subsequent requests for body/framing/state leakage.

Each case owns its clients, request tasks, cancellation sources, file cache,
temporary directory, server and certificate. Failure cleanup releases both gate
stages, cancels and observes requests, awaits entered callbacks and context
closure, joins server shutdown and disposes owned resources. Unexpected cleanup
errors are retained with the original failure. The shared runner enforces its
case deadline and cleanup grace; unresponsive work does not silently continue.

## CI and limits

The existing Windows/Ubuntu state-machine jobs are configured to run both TCP
response campaigns with their network case counts (32 on normal runs, 128 on the
schedule), alongside the existing connection campaigns. They run the negative
self-test and native-independent replay corpus. The prepared Ubuntu QUIC job
runs `response-http3` and replays the `state-corpus/quic` traces. These are
configured checks, not a claim that a particular CI head passed.

This is generated state-machine testing with named invariants and transition
counts, not additional coverage-guided AFL++/SharpFuzz coverage. It does not
establish exhaustive protocol conformance or cover every possible interleaving.
Files remain immutable during a case; resource mutation, precompressed variants,
all content negotiation grammars and general malformed-header parsing remain
separate coverage areas.

For mutable files and additional application/lifetime seams, see
[stateful application-surface fuzzing](stateful-surface-fuzzing.md).
