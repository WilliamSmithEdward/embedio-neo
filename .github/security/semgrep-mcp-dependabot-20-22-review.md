# Semgrep MCP client and authentication Dependabot alert review

Dependabot alerts
[#20](https://github.com/WilliamSmithEdward/embedio-neo/security/dependabot/20),
[#21](https://github.com/WilliamSmithEdward/embedio-neo/security/dependabot/21)
and
[#22](https://github.com/WilliamSmithEdward/embedio-neo/security/dependabot/22)
report three advisories against `mcp` 1.29.0, published on 2026-10-27. All three
are fixed in `mcp` 1.30.0 and 2.2.0.

- #20, GHSA-rwrf-2pqf-9j8j / CVE-2026-116686: when a connected server declares
  an output schema for a tool, the client validates each result with
  `jsonschema`'s default resolver. That resolver fetches any `$ref` it cannot
  resolve locally, including `http`, `https` and `file` URLs the server names,
  synchronously and without a timeout.
- #21, GHSA-5h93-6whr-6q8j / CVE-2026-116685: the Streamable HTTP and SSE client
  transports and the OAuth client providers follow redirects to any host. Custom
  headers go with them, and on 307 and 308 so does the request body.
- #22, GHSA-w4fh-qvv9-3v23 / CVE-2026-116684: the server's bearer authentication
  cannot check that a token was issued for this server, so where one
  authorization server issues tokens for several services, a token issued for
  another service is accepted. The patched releases add an opt-in check;
  upgrading alone does not turn it on.

The first two affect applications that use the SDK as an MCP client. The third
affects HTTP servers that use the SDK's bearer authentication with a token
verifier that does not check the audience itself.

## Repository use

The affected package is not a production or test dependency of EmbedIO-Neo. It
is part of the complete, wheel-only Python closure for Semgrep 1.182.0 under
`.github/security/semgrep/requirements.txt`. Semgrep 1.182.0 and 1.182.1, the
newest release, require `mcp==1.29.0`, as does Semgrep's development branch, so
the patched release cannot enter the closure that the image's `pip check`
verifies. Semgrep's request to move the pin,
[semgrep/semgrep#11992](https://github.com/semgrep/semgrep/issues/11992), is
open. This review was written for Semgrep 1.182.0 on 2026-10-28.

Semgrep 1.182.0 has no MCP client code. Nothing in the `semgrep` package uses
`mcp.client`, `ClientSession`, `ClientSessionGroup`, the SSE or Streamable HTTP
client transports, or `create_mcp_http_client`, and inside `mcp` only those
client transports call `create_mcp_http_client`. Semgrep sets up the SDK's
bearer authentication in one place, `setup_mcp_server` in
`semgrep/commands/mcp.py`, which only the `semgrep mcp` command calls. Its token
verifier decodes tokens with audience verification turned off, which is the
kind of verifier GHSA-w4fh-qvv9-3v23 describes.

The security workflow builds that pinned scanner image and runs only
`semgrep scan`, in a container that receives only `HOME`, exposes no port and is
removed after the scan. Loading Semgrep's command line imports the SDK's client,
transport and authentication modules, but a scan calls none of their functions.
A scan of one file with the workflow's flags, traced with `sys.monitoring` in
the same image, ran no function in those modules; the only SDK function it ran
was a logger lookup. A control run in the same image that called
`create_mcp_http_client`, `ClientSession._validate_tool_result` and
`BearerAuthBackend.authenticate` directly showed each of them in the trace.

## Acceptance boundary

William approved dismissing alerts #20, #21 and #22 as `not_used` on
2026-10-28. This is a narrow acceptance of the unreachable MCP client paths and
the unstarted authenticated MCP HTTP server in the Semgrep CI scanner
environment. It is not a claim that `mcp` 1.29.0 is generally safe and does not
accept that version in production code, tests, repository services or any
other workflow.

The alerts must be reopened and re-reviewed if the repository begins invoking
MCP APIs, if any workflow runs `semgrep mcp` or connects Semgrep to an MCP
server, if a service is exposed from the scanner container, or if `mcp` is used
outside the Semgrep image. The acceptance expires when Semgrep publishes a
compatible closure containing `mcp` 1.30.0 or later; the normal Semgrep
Dependabot update must then replace the vulnerable closure. That update fixes
#20 and #21 outright. For #22 it adds an audience check that Semgrep's own
server would still have to turn on, so the condition on `semgrep mcp` above
stays in force. Other dependency alerts and scanner failures remain actionable.

Before this review is merged, the matching GitHub alerts must be verified as
dismissed with reason `not_used` and comments carrying the same restricted-use
rationale.
