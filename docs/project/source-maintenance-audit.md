# Production source maintenance audit

The 2026-10-15 audit reviewed the implementation of every tracked production C#
file under `src` at `eccd1c65b5c3841c4214df93e2cd837fcb368de0`: **434 files**.
The [coverage manifest](evidence/source-maintenance-audit.json) records the exact
paths. Generated `bin`/`obj` output is excluded. This is an implementation review,
not a complete XML-documentation audit or proof that no defects remain.

Review covered ownership and cancellation, concurrent cleanup, protocol and
header handling, duplicated logic, unreachable branches and unused members.
Suspected defects were reproduced with regression tests before correction;
observable corrections were approved by the owner. Public API signatures,
defaults, target frameworks and production dependencies are preserved.

## Findings

| Area | Reproduced failure | Correction |
| --- | --- | --- |
| Request-body rate monitor | An earlier caller can cancel the next read; retirement can race heartbeat publication. | Detach the previous token before source reuse and remove late-published registrations. |
| HTTP/2 output | Combining three cancellation sources disconnects earlier sources before the batch finishes. | Retain each linked source until transport completion. |
| HTTP/3 request input | Canceling a pending body read sends a request-canceled wire code that can hide the early response. | Ordinary admitted System.Net.Quic body reads use graceful input abandonment for local cancellation, settle borrowed buffers, and preserve genuine failure codes. |
| Testing adapter | Content header casing changes behavior; repeated closure repeats callbacks; negative resource ranges throw. | Compare header names without case, claim callbacks once, reject negative ranges with null. |
| Range helper | Unknown range units become byte selections or 416 responses. | Ignore unsupported units and retain case-insensitive bytes support. |
| SameSite cookies | Explicit SameSite metadata rejects built-in HTTP/2 and HTTP/3 responses. | Accept the multiplexed response implementation using its existing cookie serializer. |
| Rate limiting | Failure to register the release callback leaks an acquired concurrency permit. | Dispose the lease when ownership transfer fails. |
| Context cleanup | A diagnostic listener throwing while reporting callback failure interrupts cleanup. | Keep recoverable diagnostic failures from interrupting remaining cleanup. |
| Quality values | Splitting equal-quality values across header lines changes the documented preference order. | Preserve cumulative ordinals across header values. |
| IP-ban cleanup | Expiry cleanup removes a renewed ban; regex cleanup discards a concurrent match. | Remove only the inspected expired entry and synchronize match accumulation with cleanup. |

The quality-value change deliberately updates legacy reference expectations that
preserved the inconsistency. It restores the library's documented client-order
tie break; choosing either equal-quality coding is not itself claimed to violate
HTTP.

## Safe simplifications

- Removed two unused internal request-rate diagnostic accessors after checking
  source, tests, tools and reflection string references.
- Reused the existing Web API result-casting helper while retaining the private
  reflection entry point and synchronous wait behavior.
- Removed a CIDR check that cannot fail after applying its mask.
- Removed two later duplicate HTTP-date format entries, preserving first-match
  ordering and accepted formats.

Public types were retained even when their namespace contains `Internal`.
Native layout fields and reflection entry points were not classified as dead
merely because ordinary C# callers were absent. Similar stream wrappers were
retained where ownership, accounting or target-framework behavior differs.

## Validation and delivery

The first combined production corrections passed the Windows suite with 6,376
reported cases: 6,355 succeeded, 21 skipped and zero failures. The suite includes
the 600-case quality-value corpus with unchanged inputs. Later HTTP/3 wire
controls and fixture corrections bring combined discovery to 6,395. The complete
production source, including the HTTP/3 correction and planned file organization,
passed that Windows suite with 6,374 successes, 21 expected skips and no failures.
These local results do not establish cross-platform success.

The rate-monitor correction merged in [PR #453](https://github.com/WilliamSmithEdward/embedio-neo/pull/453)
after its exact-head checks passed. Its first macOS attempt exposed a refusal
upload fixture that returned after fewer bytes than its declared length; the
fixture was changed to unknown length. Subsequent main CI and
[PR #454](https://github.com/WilliamSmithEdward/embedio-neo/pull/454) then exposed
a distinct production issue: local HTTP/3 body cancellation sent
`H3_REQUEST_CANCELLED` before the early response.

Five runtime-provider wire cases reproduced that wrong code; native-provider and
precanceled-token controls passed. The owner-approved correction is restricted to
ordinary admitted System.Net.Quic request bodies. Initial fields, control streams,
CONNECT tunnels and native-provider behavior remain unchanged. All 16 wire cases
passed on macOS in the next run, including graceful stop codes, genuine
request-wide failures and buffer-settlement controls. That run still failed the
existing HttpClient trickle fixture: swallowing its upload exception bypassed
HttpContent's normal error wrapping and triggered a buffered reflush. A further
test-only correction propagates HTTP/3 upload errors to the client's graceful
stop handling. All 23 focused content/wire cases passed locally after that
adjustment. The original response status, complete body, timing and subsequent
connection-health assertions remain required.

Both failed CI attempts are retained. The latter also encountered the documented
raw listener-rebind `AddressAlreadyInUse` failure; no unrelated failure was
accepted under that exception. PR #454 subsequently merged after all 33 checks
completed successfully or were intentionally skipped, with an overall passing
result. Its fresh macOS suite reported 6,357 cases: 6,336 passed, 21 skipped and
zero failures, including the original trickle fixture and both raw rebind
variants. This does not claim the underlying runtime rebind issue was repaired.
The policy corrections merged in [PR #456](https://github.com/WilliamSmithEdward/embedio-neo/pull/456)
after all exact-head checks and the overall result passed. Testing-adapter
corrections and safe simplifications accompany this report; their own
cross-platform checks remain required before merge. No release or publication
is part of this audit.
