# Typed HTTP results review (2.14.0)

This opt-in surface adds explicit controller responses and named Location generation.
It does not change the return handling of ordinary object/string/Task actions,
serializer defaults, authentication, authorization, target frameworks or runtime
package dependencies. Existing controller lifetime behavior remains covered by the
ordinary regression suite; explicit results await body completion before release.

## Trust boundaries

- Locations reject controls, backslashes, credentials, protocol-relative URLs and
  non-HTTP schemes. Application code must authorize external redirect destinations.
  Named routes use a copied, bounded registry and escaped, bounded parameter values;
  they never derive an origin from client-controlled Host/forwarding headers.
- Download names cannot include separators or controls and use UTF-8 filename
  encoding. File paths are trusted application input; this API is not a sandbox.
  Applications must authorize storage access and avoid modifying an active source.
- JSON uses configured serializer metadata; Problem Details takes a snapshot and
  reuses existing client-safe error formatting. Do not put secrets in payloads.
- Stream ownership transfers only after successful construction. A stream result
  claims execution once. Typed alternatives unwrap before cancellation and claiming, so the selected response owns cleanup. Cancellation and ordinary read/write/cleanup failures
  release owned input once. Borrowed inputs stay open. The host owns output closure.
- Files and streams bypass output-cache publication and reject idempotency
  recording, including when hidden inside an alternative. Finite responses retain
  existing caching/idempotency behavior. Security policies still control headers;
  nonce-bearing responses remain fresh and uncacheable.

## Validation and limits

The focused suite covers generated and reflection actions, async controller
lifetime, ordinary return compatibility, null results, header/path injection,
HEAD/no-body responses, ranges and preconditions, owned/borrowed stream cleanup,
concurrent execution, cancellation, cache replay, idempotency and fresh security
nonces. Real HTTP/1, HTTP/2 and HTTP/3 cases exercise response completion.

The typed-results state machine uses three overlapping clients with an independent
status/body/Location oracle, cache replay and file bypass checks, and nonce
uniqueness checks. Its negative self-test must reject an injected actor mismatch.
The Native AOT host exercises generated synchronous/Task/ValueTask actions, typed
alternatives, named URLs and OpenAPI without reflection controller registration.

OpenAPI inference describes concrete generated result alternatives. Broad or
variable-status results retain a default response; explicit response annotations
are still needed for an exhaustive application contract. Named registries do not
register endpoints. Nonseekable streams do not promise range or conditional support.
Applications remain responsible for producer timeouts and resource budgets.

Local evidence is retained under ignored `TestResults/typed-results`. CI supplies
cross-platform, native AOT, platform-host, security and malware evidence on the
exact PR head; configured checks alone are not passing validation.
