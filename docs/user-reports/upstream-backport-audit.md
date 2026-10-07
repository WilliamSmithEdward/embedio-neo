# Selective audit of upstream PR #534

[Upstream PR #534](https://github.com/unosquare/embedio/pull/534), contributed by
radioegor146 and reviewed by rdeago, combined fixes for default ports, response
charset, directory links and WebSocket concurrency with maintenance changes.
It merged into a divergent upstream branch in January 2022. This audit checks
the individual behaviors in Neo rather than importing the aggregate commit or
its obsolete CI configuration. Credit for the original findings and patch
remains with those contributors.

## Patch decisions

The source PR changes thirteen files. Paths below omit its old `src/packages`
and `src/tests` layout where the maintained location differs.

| Historical file | Current decision and evidence |
| --- | --- |
| `.github/workflows/build.yml` | Superseded. The maintained CI builds the current solution and runs Microsoft.Testing.Platform tests, desktop/platform probes and the required gates; the old .NET Core 3.1 path change is not imported. |
| `README.md` | Superseded. The historical chat-class rename concerns an old inline example. Current onboarding uses maintained runnable guides rather than that example. No public class is renamed. |
| `Files/Internal/HtmlDirectoryLister.cs` | Present. File links omit a directory separator; directory links retain their separate behavior. Existing directory-browser tests exercise generated listings. |
| `Files/ZipFileProvider.cs` | Formatting only: the historical change adds a final newline. Retain current archive lifetime/thread-safety work and notices. |
| `Net/Internal/HttpListenerRequest.cs` | Present. Host parsing distinguishes the closing IPv6 bracket from an explicit port separator; dual-stack URL/HTTP regressions cover current reconstruction. |
| `Net/Internal/HttpListenerResponse.cs` | Present and subsequently enhanced. Charset follows the selected encoding, supports a null encoding and respects an explicit charset. The maintained charset regressions cover those policies. |
| `Net/Internal/ListenerPrefix.cs` | Constructor superseded by `System.Uri` parsing with explicit wildcard preservation. A remaining validator defect was reproduced and corrected: authority-local port separators must exclude IPv6 brackets and path colons. |
| `Net/Internal/ListenerUri.cs` | Not imported. The proposed parser duplicates maintained URI parsing and rejects port 65535. The existing constructor plus corrected validator preserve supported prefixes and current argument-validation behavior. |
| `Net/Internal/SystemHttpResponse.cs` | Formatting only: the historical change adds a final newline. Native header/lifetime fixes remain intact. |
| `WebServerOptions.cs` | Not selected. The historical patch wraps `netsh` in `cmd` to force code page 437. No reproduction establishes a required fix through that wrapper; keep direct process invocation. This audit does not establish localized `netsh` output correctness or solve translated certificate-label parsing. Applications can supply a certificate explicitly; see [HTTPS guidance](../guides/https.md). |
| `WebSockets/Internal/WebSocket.cs` | Remaining concurrency/cancellation defect corrected as described below, including control-frame and cleanup coverage. The historical unconditional semaphore release after a cancelled wait is not copied. |
| `WebSockets/WebSocketModule.cs` | Present. Only a successful context removal disposes/notifies that context. Real close/reconnect/shutdown cases verify one disconnect notification per client. |
| `Issues/Issue531_DefaultPort.cs` | Replaced by compatible prefix coverage and the maintained listener suite. Validate default HTTP/HTTPS ports, IPv4/IPv6, explicit port 65535, wildcards and invalid prefixes without requiring a privileged port-80 bind in every developer environment. |

This is a patch audit, not a claim to implement every proposal in every linked
issue. In particular, the historical charset change is not an arbitrary raw-header
API, and the code-page change is not evidence of a localized Windows certificate
fix. Existing APIs, defaults, targets, dependencies and license notices remain.

## Managed WebSocket sends

Concurrent sends previously wrote fragments from separate data messages without
a whole-message gate. A new text/binary message could start before the previous
one finished, and the cancellation argument was not passed to transport writes.

The corrected managed listener serializes complete data messages. A separate
frame-write gate prevents data and ping/pong/close bytes from overlapping while
allowing control frames between completed data fragments. A cancelled waiter
releases no permit it did not acquire. Cancellation before writing leaves the
connection usable; cancellation after a partial message or an interrupted
transport write terminates the connection because a partial frame cannot safely
be followed by another message. Close/dispose stop remaining continuations and
cleanup is idempotent. Gates are disposed after their active and queued users
finish, not while they still need to release them.

Applications still own lifetime/cancellation policy for a peer that stops reading.
This correction does not introduce a write timeout, guarantee scheduling order
between independently started tasks, or change native listener implementation.

## Prefix validation

The URI constructor already handles implicit ports, but validation independently
treated address/path colons as port separators. `http://[::1]/`,
`https://[::1]/` and `http://localhost/files:archive/` were rejected through the
public managed listener's `AddPrefix` method. Locate a port only inside the
authority and outside IPv6 brackets. HTTP/HTTPS defaults, wildcard hosts, port
65535 and existing invalid-port/scheme/trailing-slash checks are retained.

## Validation and availability

Three deterministic WebSocket cases and three public prefix-validation cases
failed before their respective corrections and pass afterward. The controlled
transport invokes the real managed WebSocket constructor and send methods; it
makes overlapping writes, cancellation and cleanup timing observable.

Twenty-eight focused cases cover whole-message ordering, queued/pre/active and
between-fragment cancellation, transport failure, ping/pong/close serialization,
cancelled close, disposal and gate cleanup, prefix parsing, and real connections
under both listener modes. Real clients receive 24 concurrent messages
with distinct UTF-8 payloads and alternating text/binary types. Repeated
close/reconnect/server shutdown verifies one disconnect callback per client.

The corrections require a build containing this work; they are not in the
published 1.0.3 package. Required cross-platform and security gates must pass
before merge. No execution on the original browsers, exact application or a
Russian Windows installation is claimed. A concrete remaining reproduction is
welcome with runtime, OS, listener mode and a minimal server/client.
