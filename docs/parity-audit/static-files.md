# Static-file parity

Index HTML and file bytes, HEAD body omission, explicit range `bytes=2-5`, invalid
ranges, fixed-time If-Modified-Since responses, missing files, and gzip/deflate
response bytes agree in both listeners and Neo assets. The shared fixture fixes
the file's last-modified time and disables redirects on the client.

Upstream sends `Content-Encoding: identity` without compression. Neo omits it,
retaining `Vary: Accept-Encoding`. This is the existing correction described in
[identity encoding guidance](../user-reports/identity-content-encoding.md) and
[PR #96](https://github.com/WilliamSmithEdward/embedio-neo/pull/96). The audit holds
this exact header difference while preserving comparison of other fields.

## Inherited suffix-range defect

For a ten-byte `0123456789` file, a request with `Range: bytes=-3` returns 206,
`Content-Range: bytes 0-3/10`, and `0123` under both implementations. A three-byte
suffix should be `789`, with `bytes 7-9/10`, under
[RFC 9110 section 14.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-14.1.2).
The match therefore identifies a
concrete inherited defect, not correct range behavior and not a newly introduced
Neo regression. It remains unfixed by this audit. A focused follow-up needs
boundary/zero-length/oversized-suffix and HEAD coverage before a correction.

ZIP/resource/custom providers, symlinks/traversal, cache invalidation, ETag
semantics, multi-ranges and large/concurrent transfers are not compared here.
Evidence: `file*` and `index` HTTP cases, including exact Base64 bytes and range
metadata. See the [audit method](README.md).
