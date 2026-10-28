# Semgrep OpenTelemetry Dependabot alert review

Dependabot alert
[#19](https://github.com/WilliamSmithEdward/embedio-neo/security/dependabot/19)
reports GHSA-xcq7-p3pf-p88h / CVE-2026-114603 against `opentelemetry-api`
1.37.0. `TraceState.from_header` splits an inbound W3C `tracestate` value with a
regular expression whose cost grows with the square of a whitespace run, and
nothing limits the value's length first, so one oversized header can hold a
worker for seconds. The advisory is valid for services that extract trace
context from untrusted carriers, such as instrumented HTTP servers. Its impact
is limited to availability. The patched release is `opentelemetry-api` 1.45.1.

## Repository use

The affected package is not a production or test dependency of EmbedIO-Neo. It
is part of the complete, wheel-only Python closure for Semgrep 1.182.0 under
`.github/security/semgrep/requirements.txt`. Every Semgrep release through
1.182.1 requires `opentelemetry-api~=1.37.0`, and `opentelemetry-sdk` 1.37.0
requires `opentelemetry-api==1.37.0`, so the patched release cannot enter the
closure that the image's `pip check` verifies. Semgrep's request to raise those
pins, [semgrep/semgrep#11863](https://github.com/semgrep/semgrep/issues/11863),
targets 1.44.0, which is still affected. This review was written for Semgrep
1.181.0 and rechecked for 1.182.0 on 2026-10-27.

The parser runs only when `TraceContextTextMapPropagator.extract` receives a
carrier with a `tracestate` entry. Semgrep 1.182.0 calls
`opentelemetry.propagate.extract` in one place, `Telemetry.extract`. That runs
only when a scan starts with `--trace`, and its carrier holds a single
`traceparent` built from two environment variables, never a `tracestate`. The
closure holds no OpenTelemetry server instrumentation, the usual source of
inbound carriers; its requests and threading instrumentations only inject
context into outgoing calls.

The security workflow builds that pinned scanner image and runs `semgrep scan`
without `--trace`, in a container that receives only `HOME`, exposes no port
and is removed after the scan. No trace context, from any source, is parsed in
this use.

## Acceptance boundary

William approved dismissing alert #19 as `not_used` on 2026-10-22. This is a
narrow acceptance of the unreachable `tracestate` parsing path in the Semgrep CI
scanner environment. It is not a claim that `opentelemetry-api` 1.37.0 is
generally safe and does not accept that version in production code, tests,
repository services or any other workflow.

The alert must be reopened and re-reviewed if the security workflow starts
Semgrep with `--trace` or sets its trace-parent environment variables, if any
workflow runs `semgrep mcp` or another Semgrep service, if OpenTelemetry server
instrumentation enters the scanner image, or if `opentelemetry-api` is used
outside it. The acceptance expires when Semgrep permits `opentelemetry-api`
1.45.1 or later; the normal Semgrep Dependabot update must then replace the
vulnerable closure. Other dependency alerts and scanner failures remain
actionable.

Before this review is merged, the matching GitHub alert must be verified as
dismissed with reason `not_used` and a comment carrying the same restricted-use
rationale.
