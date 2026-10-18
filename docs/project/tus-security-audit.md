# Adversarial security audit of resumable uploads

Review date: 2026-10-18. Scope: [PR #489](https://github.com/WilliamSmithEdward/embedio-neo/pull/489),
including the five `src/EmbedIO/Tus` files, authentication/owner selection,
browser-protection integration, body ownership, disk recovery, completion,
and the new tests, fuzz harness, compatibility fixtures and workflow changes.
The review started at `ee3a305b0a518fadabb964969819304c63dad80f`.
This is a source review with owned local adversarial tests, not an independent
penetration test or a guarantee that the implementation has no vulnerabilities.
The feature and correction remain unreleased.

## Confirmed finding: method override bypassed token checks

**Moderate / defense in depth.** Browser protection exempted a physical GET,
HEAD or OPTIONS without considering `X-HTTP-Method-Override`. The tus handler
then honored an override selecting POST, PATCH or DELETE. Nine reproductions
created, appended to or deleted the authenticated owner's upload without a CSRF
token. Three more real HTTP/1 cases reproduced mutation after supplying WebSocket
upgrade headers and a trusted Origin; the previous WebSocket exemption also
ignored overrides.

Authentication, upload permission selection and owner isolation were still
applied. No unauthenticated or cross-owner access was demonstrated. A normal
cross-origin browser cannot freely set these custom headers, and the independent
Origin policy still rejects untrusted origins. These tests demonstrate a bypass
of the configured token requirement, not a complete cross-origin browser exploit.
See [OWASP's CSRF guidance](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html)
for the separate roles of tokens, custom headers and origin controls.

The correction requires a token when an override is unsafe, unknown or ambiguous,
even on safe physical verbs and WebSocket upgrades. It also preserves the token
requirement for unsafe physical verbs when their override is safe. Three reverse
cases protect that rule. The owner selector still receives the effective method;
[tus requires override interpretation](https://tus.io/protocols/resumable-upload#x-http-method-override).
Normal HTTP/1 GET and HTTP/2/3 extended CONNECT WebSocket handshakes retain their
trusted-Origin behavior. Explicit host token exemptions remain explicit policy.

The original 12 failing reproductions are retained under ignored
`TestResults/tus-security-before` and `TestResults/tus-security-upgrade-before`.
The corrected 15 cases are in `TusUploadTest.Security.cs` and
`TusUploadTest.SecurityWire.cs`. Each `tus-uploads` fuzz trace now generates a
safe physical verb and unsafe effective method, checks denial without a valid
token and unchanged storage, then checks acceptance with a valid session-bound
token. The usual upload ledger and crash-recovery models remain enabled.

## Other reviewed boundaries

| Boundary | Review and validation |
| --- | --- |
| Authentication and ownership | Authenticated principal and current owner selector required on every operation; effective method supplied for permission checks. Foreign and unknown resources disclose no offset. SHA-256 owner scope is computed from strict UTF-8; scope construction remains application policy. Existing auth/owner regression cases retained. |
| Input and response headers | Single-value bounded protocol headers; unsigned bounded decimal parsing; unsupported modes rejected; bounded printable metadata with unique keys and validated Base64. Metadata remains encoded when returned, and never selects a path. Malformed/duplicate header and metadata regressions retained. |
| Paths and directory ownership | Server-generated exact 32-character IDs; fixed extensions inside one canonical private directory; exclusive store ownership; reparse-point checks during recovery/open; bounded file enumeration and orphan cleanup. Hostile local writers and unprotected parent directories are outside the supported trust boundary. |
| Storage and memory denial of service | Declared-length reservation, global/per-owner count and byte quotas, bounded PATCH and transfer buffer, bounded active data operations, fixed expiry and explicit idle cleanup. Overflow was checked against global subset bounds. Disk headroom and request/read deadlines remain required host controls. |
| Mutation concurrency | Per-upload mutation ownership, committed immutable snapshots, stale-offset rejection, ownership checked before body access, whole-PATCH rollback on checksum/body/cancellation failures, disposal retains directory ownership during active I/O. Deterministic overlap and cancellation regressions retained. |
| Recovery and corrupt records | Bounded record and string-prefix parsing before allocation; validate owner/length/offset/metadata; reject missing or short committed data; truncate uncommitted tails; recover uncertain replacements from authoritative disk state; fail closed on incompatible recovered quotas. Corruption, tail recovery, lock-release and killed-process fixtures retained. |
| Completion and publication | Completion runs after durable commit, with request context and owner checks intact. Failure leaves completed data; startup does not replay callbacks. No durable/exactly-once business delivery is promised. Applications must validate private staged content, publish idempotently and reconcile known owner scopes. |
| Existing functionality and packaging | No new runtime dependency, target or public API removal; module remains opt-in. Existing multipart/controller requests do not acquire a store. Browser protection changes only the token exemption decision when an override is supplied. |

No additional security defect was confirmed in those reviewed boundaries. This
statement is limited to the source and test evidence above; external validators,
reverse proxies, browser apps, content scanners and filesystem administration
were not independently audited.

## Local validation

- Final-source Windows suite: 7,287 cases, 7,274 passed, 13 expected platform/opt-in
  skips and no failures. All 89 tus cases passed, including the 15 new security
  cases. The .NET Framework compatibility host passed all 50 assertions.
- The 15 new security regressions cover missing/tampered tokens, all nine safe
  physical verb / unsafe override pairs, reverse overrides and real HTTP/1
  upgrade headers. Normal HTTP/1-3 WebSocket coverage remains enabled.
- Both 500-case campaigns (`tus-uploads` and `browser-protection`, seed 84919)
  passed. The tus campaign covered every generated override pair and exact byte
  and owner ledgers. Separate fixtures passed retained replay, injected-offset
  rejection and killed-process recovery.
- Release builds for both core targets, NUnit, fuzzing, Framework and AOT hosts
  have zero active compiler/analyzer diagnostics in changed source. Debug
  solution builds and PrettyCSharp 1.1.5 checks pass. Full-severity whitespace,
  style and analyzer checks pass after resolving workspace-loading warnings.
- Compatibility-only informational suppressions are exact member/field scoped.
  Compiler probes against actual .NET Standard 2.0 references confirm ranges and
  System.Threading.Lock are unavailable; no error or warning is suppressed.

## Deployment assumptions that remain necessary

- Use HTTPS, current authentication/authorization, trusted issuer/tenant/subject
  owner scopes, deliberate CORS origins and browser protection for cookie hosts.
- Provision host-only permissions on the store and protected parent directories,
  outside static-file mounts, on a local filesystem supporting atomic replacement.
  Reparse-point checks do not promise race resistance against a hostile local
  writer who can already modify this private directory.
- Configure admission/rate limits, body/read/request deadlines and operational
  disk headroom. One authorized owner may occupy the configured concurrency
  allowance until its bounded requests finish or time out.
- Reclaim expired idle sessions periodically, move completed data before fixed
  expiry, and validate content before publication. Callback delivery and business
  publication require application-owned reconciliation and idempotency.

See [the upload guide](../guides/resumable-uploads.md) for configuration and replay
commands. Final changed-source validation and PR checks determine merge readiness;
this report does not mark an in-progress check as passed.
