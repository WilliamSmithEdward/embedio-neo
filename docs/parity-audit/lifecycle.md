# Listener lifecycle parity

Each HTTP listener starts, serves the comparison workload, survives the tested
request/controller failures, and completes `RunAsync` after caller cancellation
with state `Stopped`. Managed HTTPS also completes after cancellation. All three
consumer builds agree, and independent assertions require the stopped state.

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
See the [audit method](README.md).
