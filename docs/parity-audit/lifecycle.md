# Listener lifecycle parity

On Windows, each HTTP listener starts, serves the comparison workload, survives the tested
request/controller failures, and completes `RunAsync` after caller cancellation
with state `Stopped`. Managed HTTPS also completes after cancellation. All three
consumer builds agree, and independent assertions require the stopped state.

## Modern Unix native baseline

Published upstream 3.5.2 serves a first ordinary native JSON DTO, then its response
cleanup reacquires `System.Net.HttpListenerResponse.OutputStream` after the
response has been disposed. The fatal path disposes the listener; `RunAsync`
faults with `ObjectDisposedException`, and subsequent caller cancellation throws
an `AggregateException` from its Stop callback. The pinned Linux .NET 10.0.12
container reproduces this, preserving the logs. The initial Linux/macOS CI
workloads aborted in this baseline cleanup path.

Neo's existing `SystemHttpResponse` caches the acquired stream. Both Neo assets
remain live after the first DTO, serve a second DTO, and complete cancellation
without either exception. The Unix profile independently requires those exact
Neo outcomes and the characterized upstream errors. A changed error or weakened
Neo liveness fails the audit. This is verified improvement on the tested host,
not evidence that the complete old native workload passes on Unix. The Windows
profile continues to compare that complete native workload.

This validates caller-owned cancellation, not every shutdown mechanism. Upstream's
idle Stop/Dispose accept hang was separately reproduced and corrected in Neo;
[listener-stall guidance](../guides/listener-stalls.md) records that evidence.
This suite does not deliberately wait forever on an old defective shutdown path.

Listener restart, shared endpoints, shutdown races, pending TLS handshakes,
disconnected clients under load, event counts and platform activity recreation
require additional differential cases. Existing Neo coverage does not establish
that upstream and Neo match in those situations. Each client/start/shutdown wait
is bounded and fixture failure fails the audit, rather than being recorded as a
passing parity result.

Evidence: `lifecycle/`, `https/cancel` and `healthy-after-errors` cases.
The Unix observation is `native/unix-response-lifetime`, with its original
exception details in the Upstream run log.
See the [audit method](README.md).
