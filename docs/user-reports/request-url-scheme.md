# Request URL scheme and HTTPS

[Upstream #593](https://github.com/unosquare/embedio/issues/593), reported by
cyanfish, identified HTTPS requests whose `Request.Url` used `http`. EmbedIO-Neo's
managed listener had retained that hard-coded scheme. URL reconstruction now
uses `https` when `Request.IsSecureConnection` is true and `http` otherwise.
For example, a TLS request to `/Account?Token=AbC` reports an HTTPS URL with the
original path and query casing.

The adjacent regression investigation also corrected lowercasing of absolute
request targets and truncation of bracketed IPv6 Host values without a port.
Uppercase URI schemes remain accepted. The listener continues using its existing
host selection and local endpoint port; this change does not introduce a new
proxy authority or port policy. `RawTarget` retains the received request target.
Released packages through 1.0.3 expose this string as `RawUrl`; the unreleased
[API cleanup](../compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved)
renames it while preserving the raw text.

## Validation

Eleven real managed-listener HTTP/TLS regressions cover origin and absolute
request targets, mixed-case path/query data and escapes, uppercase URI schemes,
keep-alive, IPv4 and bracketed IPv6 hosts, HTTP/1.0 without Host, and simultaneous
HTTP/HTTPS listeners. The platform HTTPS smoke also checks that the reconstructed
scheme agrees with transport security. This is focused regression evidence,
not a claim of complete HTTP request-target RFC conformance.

## TLS termination and proxies

`IsSecureConnection` describes the connection reaching EmbedIO. When a reverse
proxy terminates TLS and forwards plain HTTP, the managed listener correctly
reports its downstream connection as HTTP. This correction does not trust
`X-Forwarded-Proto` or `Forwarded`, or infer a public URL from untrusted headers.
Configure public URL generation and proxy trust deliberately in the application.

See [HTTPS hosting](../guides/https.md) for certificates and client trust. Existing
listener modes, public APIs, supported targets, and production dependencies are
unchanged. No migration is required; applications that worked around the old
incorrect scheme can remove that workaround once using a version containing
this correction.
