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

The original audit found that a ten-byte `0123456789` file with `Range: bytes=-3` returned 206,
`Content-Range: bytes 0-3/10`, and `0123` under both implementations before this correction. A three-byte
suffix should be `789`, with `bytes 7-9/10`, under
[RFC 9110 section 14.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-14.1.2).
The match therefore identifies a
concrete inherited defect, not correct range behavior and not a newly introduced
Neo regression. The owner-requested follow-up in [issue #170](https://github.com/WilliamSmithEdward/embedio-neo/issues/170) corrects Neo to `789` and `bytes 7-9/10`, while upstream remains wrong. This unreleased correction is an exact explained audit difference, not a parity match. [Suffix guidance](../user-reports/suffix-range-responses.md) records boundaries, empty/zero suffixes, HEAD, validators and migration effects.

ZIP/resource/custom providers, symlinks/traversal, cache invalidation, ETag
semantics, multi-ranges and large/concurrent transfers are not compared here.
Evidence: `file*` and `index` HTTP cases, including exact Base64 bytes and range
metadata. See the [audit method](README.md).
