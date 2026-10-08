# Session parity

The same cookie-enabled client receives session counts 1 and then 2. A separate
client receives 1. These behaviors agree across both listeners and Neo assets.
The shared consumer uses the existing session indexer and registers the local
session manager explicitly; no new convenience API or default is introduced.

Counts are checked independently of the upstream comparison, so two equally
broken session implementations cannot pass simply by returning the same value.
Random cookie/session identifiers are not compared. The suite does not establish
cookie security flags, timeout/expiry behavior, regeneration, concurrent session
mutations, persistence across process restarts, custom stores or WebSocket session
association.

Evidence: `session-first`, `session-second` and `session-independent` cases.
See the [audit method](README.md).
