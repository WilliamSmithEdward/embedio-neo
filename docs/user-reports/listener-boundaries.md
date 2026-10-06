# HTTP listener framing and accept boundaries

This audit starts at main commit `e3b7bfe` and follows the
[connection lifetime work](listener-connection-lifetimes.md). It covers fragmented
headers, request framing and synchronous socket accepts in the managed listener.
Public APIs, dependencies, supported frameworks, timeout values, request parsing
policy and TLS selection remain unchanged.

## Iterative accept completion

The previous SocketAsyncEventArgs path called ProcessAccept from Accept when an
accept completed synchronously. ProcessAccept rearmed Accept before handling the
previous socket, so a populated backlog could add one recursive call per inline
completion. This was established from the control flow; an actual stack overflow
was not reproduced.

Accept now drains inline completions in a loop, still rearming before processing
the previous socket. A pending completion returns to the existing Completed
callback. If rearming fails during shutdown, the previous accepted socket is
closed. The existing dedicated blocking macOS IPv6 worker is unchanged.

Two controlled tests queue 32 and 128 connected peers before arming any accept,
verify every distinct request is dispatched, then send another request through the
pending async accept. Four additional HTTP/HTTPS cases exercise 64-request bursts,
optional immediate client resets, Stop and same-listener restart. Existing macOS
reset and native-platform CI fixtures remain part of validation. This removes
recursive accept growth; no throughput improvement is claimed.

## Supported framing coverage

The raw-socket tests use HTTP and pinned-certificate HTTPS, including writes in
1-, 7- and 8192-byte pieces. These write boundaries exercise network fragmentation
but do not guarantee matching socket read boundaries. Six deterministic parser
cases additionally control read fragments for empty and 32,000-byte header values.

Coverage includes fixed-length POST bodies, equal duplicate Content-Length headers,
case-insensitive header names, zero-length bodies and a second request on the same
connection. Exact body bytes and response status are checked. Expect: 100-continue
must arrive before the client sends its body, followed by a successful subsequent
request. Negative Content-Length closes the connection. Truncated HTTP/HTTPS bodies
terminate the reader, and a separate healthy request verifies the listener remains
usable. EOF before Content-Length is observable to the application; this pass does
not add a new exception or automatic application-level rejection.

## Confirmed parsing-policy gaps

Controlled probes use the actual HttpConnection.ProcessInput parser, preserving
its MemoryStream state between calls and applying the same existing 32768-byte
buffer check before each call. They start with separate request-line and Host
fragments, then a single header with a 40,000-byte value. Results:

| Header fragment size | Largest buffered fragment | Header completed | Existing check triggered |
| --- | --- | --- | --- |
| 1 | 1 byte | Yes | No |
| 7 | 8 bytes | Yes | No |
| 8192 | 8192 bytes | Yes | No |
| 40000 | 40000 bytes | No | Yes |

The line builder retains text when the buffer is cleared. Consequently the check
bounds the current receive buffer, rather than total header bytes or an accumulating
line. It is not a reliable 32 KB request-header limit. These are controlled parser
results, not a guarantee about how a particular network peer's writes are split.
The receive timer is also stopped after the initial read; no total-header deadline
is introduced here.

Separate parser probes with a six-byte source body (`abcdef`) found:

| Request headers | Observed length/body | Parser error |
| --- | --- | --- |
| Content-Length 3, then Content-Length 6 | 6 / `abcdef` | None |
| Content-Length 6, then Content-Length 3 | 3 / `abc` | None |
| Content-Length `invalid` | 0 / empty | None |
| Content-Length `9223372036854775808` | 0 / empty | None |
| Content-Length -1 | -1 / empty | Invalid Content-Length |
| Transfer-Encoding chunked, no length | 0 / empty | None |
| Content-Length 6 with Transfer-Encoding chunked | 6 / `abcdef` | None |

Headers.Set overwrites duplicates. Unparseable or overflowing lengths map to zero,
and Transfer-Encoding does not select a request-body decoder. The probe does not
call FinishInitialization or application dispatch: the table describes parser and
InputStream behavior, rather than claiming every malformed request reaches a route.
These results must not be interpreted as support for chunked request bodies. Send
one valid Content-Length for bodies. If an intermediary interprets ambiguous framing
differently, these parsing policies require consideration before forwarding input.
An end-to-end request-smuggling exploit was not tested or established.

Enforcing a cumulative header limit, rejecting conflicting or malformed lengths,
rejecting unsupported transfer encodings, or adding a total-header deadline would
change previously accepted requests or timing. They are deliberately deferred under
the owner's no-breaking-changes constraint. A future policy change needs explicit
approval and a defined compatibility/migration approach; this audit does not treat
permissive behavior as a desirable contract to preserve indefinitely.

## Validation

Local Windows validation passed all 42 new cases and the full suite: 1,022 passed
and two existing platform skips (1,024 total). Both library targets and the SDK/
NUnit analyzers built with zero errors; inherited warnings remain visible. Scoped
formatting and all allocation/transport verification commands passed.

The change adds 42 cases: 24 supported fixed-length framing cases, six raw boundary
cases, four burst/reset cases, two prequeued accept cases and six deterministic
header-fragment cases. Run them with:

```sh
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ListenerBoundaryRegressionTest|FullyQualifiedName~HeadersBelowExistingBufferLimit' --report-trx --results-directory TestResults/listener-boundaries --timeout 2m --minimum-expected-tests 42
```

Local probes and logs remain under ignored TestResults/listener-boundaries. The
full regression suite, both library targets, analyzers, allocation/transport checks
and required cross-platform/security PR gates are used for acceptance. No release
or version tag is authorized by this work.
