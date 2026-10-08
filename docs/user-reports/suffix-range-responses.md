# Suffix byte-range responses

[Issue #170](https://github.com/WilliamSmithEdward/embedio-neo/issues/170) tracks
the inherited defect found by the [parity audit](../parity-audit/static-files.md).
Before this correction, `Range: bytes=-3` selected offsets 0 through 3: a ten-byte
`0123456789` file returned `0123`. William requested this focused correction using
existing APIs. It is unreleased; published Neo 1.0.3 still has the old behavior.

## Corrected behavior

A positive suffix selects the final N bytes, clamped to the file's size. The
response is 206 with the actual selected offsets in Content-Range. For example:

```sh
curl -i -H "Range: bytes=-3" http://127.0.0.1:9696/file.txt
```

For an existing static-folder/ZIP mount serving `0123456789`, the corrected body
is `789`, with `Content-Range: bytes 7-9/10`. This is a request example for an
already running server, not a complete program.

| Request/resource | Corrected result |
| --- | --- |
| Ten-byte file, `bytes=-1` | 206, final byte `9` |
| Ten-byte file, `bytes=-10` or `bytes=-11` | 206, entire file, `bytes 0-9/10` |
| Positive suffix up to Int64 maximum | Clamp without subtraction overflow |
| `bytes=-0` | 416, `Content-Range: bytes */<length>` |
| Empty file, positive suffix | Ignore Range: 200, empty body, no Content-Range |
| HEAD with a suffix header | Full GET metadata, no body |
| Nonmatching If-Range validator | Full 200 response |

[RFC 9110 section 14.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-14.1.2)
defines suffix selection and clamping. [Section 14.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-14.2)
permits ignoring Range for an empty representation. Zero suffixes are
unsatisfiable. Existing malformed/multipart ignored-range behavior and explicit
out-of-bounds 416 policy remain unchanged. No public API, target, dependency,
compression preference or opt-in setting is added.

## Validation and migration

Eighty of the first 88 real HTTP cases failed before the parser correction; all
88 passed locally afterward. Two additional synthetic-resource HTTP cases then
reproduced Int32 truncation of a non-seekable skip offset beyond 2 GiB. The corrected path preserves
the offset as Int64 and bounds each buffer read to its Int32 size. All 90 focused
cases then passed locally using the documented MTP command. They cover both listeners, folders/ZIPs, cache on/off,
repeated cold/warm requests, boundaries, one-byte/empty files, HEAD, validators,
explicit/open-ended ranges, existing malformed/multipart policies and healthy
full requests afterward. The two large-resource cases avoid allocating a 2 GiB
file and do not establish actual large-archive decompression performance. Required
cross-platform checks apply before merge.

The differential audit holds the old wrong body/range and Neo's exact corrected
values as an explained difference. Clients using explicit-offset workarounds may
continue doing so; clients relying on wrong leading bytes must adopt correct
suffix semantics. See the [migration notes](../compatibility/migration.md#suffix-byte-ranges-unreleased).
