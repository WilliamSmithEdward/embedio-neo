# Semgrep MCP Dependabot alert review

Dependabot alert
[#16](https://github.com/WilliamSmithEdward/embedio-neo/security/dependabot/16)
reports GHSA-84m7-p3x7-pcfv / CVE-2026-59951 against `mcp` 1.29.0. The
advisory is valid for applications that expose the MCP Python SDK's stateful
Streamable HTTP server: sessions can accumulate without expiry or a maximum
count, allowing remote resource exhaustion. Patched releases are `mcp` 1.30.0
and 2.2.0.

## Repository use

The affected package is not a production or test dependency of EmbedIO-Neo. It
is part of the complete, wheel-only Python closure for Semgrep 1.180.0 under
`.github/security/semgrep/requirements.txt`. Semgrep's published package
metadata requires `mcp==1.29.0`, so updating this transitive package alone would
make the scanner environment inconsistent with its direct dependency.

The security workflow builds that pinned scanner image and invokes only
`semgrep scan` over the repository. It does not import MCP application APIs,
start FastMCP, create a `StreamableHTTPSessionManager`, bind an MCP HTTP
listener, or expose any container port. The container is removed after the
scan. No untrusted client can create an MCP session in this use.

## Acceptance boundary

William approved dismissing alert #16 as `not_used` on 2026-10-15. This is a
narrow acceptance of the unreachable Streamable HTTP server path in the
Semgrep 1.180.0 CI scanner environment. It is not a claim that `mcp` 1.29.0 is
generally safe and does not accept use of that version by production code,
tests, repository services, or any workflow that starts an MCP transport.

The alert must be reopened and re-reviewed if the repository begins invoking
MCP APIs, exposes a service from the scanner container, or uses this dependency
outside the Semgrep image. The acceptance expires when Semgrep publishes a
compatible closure containing `mcp` 1.30.0 or later; the normal Semgrep
Dependabot update must then replace the vulnerable closure. Other dependency
alerts and scanner failures remain actionable.

Before this review is merged, the matching GitHub alert must be verified as
dismissed with reason `not_used` and a comment carrying the same restricted-use
rationale.
