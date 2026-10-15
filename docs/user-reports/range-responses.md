# Serve byte ranges from any module

[Neo #47](https://github.com/WilliamSmithEdward/embedio-neo/issues/47) evaluates
the roadmap item from [upstream #546](https://github.com/unosquare/embedio/issues/546):
move range handling out of `FileModule` so other modules can answer partial GET
requests. The Neo v2 engine branch already carries that facility as the opt-in
`SendRepresentationAsync` helper. This guide maps the issue's acceptance criteria
onto it, shows how a controller or action uses it, and records the decision not
to apply ranges automatically.

## What the helper is

```csharp
Task SendRepresentationAsync(this IHttpContext context, Stream content, string contentType,
    string? entityTag = null, DateTimeOffset? lastModified = null, bool leaveOpen = false,
    bool lastModifiedIsStrong = false, int maximumRanges = 16)
```

It takes an already selected, readable and seekable representation whose
`Length` is known, evaluates request preconditions, advertises `Accept-Ranges:
bytes`, selects the requested byte ranges for GET and QUERY, and streams the
result with an exact `Content-Length`. It is opt-in: a module calls it where a
download, media or export response is appropriate. Nothing else in the pipeline
applies ranges, so JSON, unknown-length streams and ordinary modules are
unaffected. The reference section is in the
[getting-started requests guide](../guides/getting-started/requests.md#stream-a-selected-representation-with-multiple-ranges).

## Example

```csharp
using var server = new WebServer(o => o.WithUrlPrefix("http://localhost:9696/").WithMode(HttpListenerMode.EmbedIO))
    .WithAction("/download", HttpVerbs.Get, async context =>
    {
        // Select the representation first: authorization, negotiation, lookup.
        var path = Path.Combine(exportRoot, "report.bin");
        var info = new FileInfo(path);
        var tag = "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture)
            + "-" + info.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        // The helper owns and disposes the stream; pass leaveOpen: true to keep it.
        await context.SendRepresentationAsync(file, "application/octet-stream", tag, info.LastWriteTimeUtc);
    });
```

The source represents the whole result from byte zero. Call the helper before
anything is written to the response; it sets status, `Content-Type`,
`Content-Length`, `Content-Range`, `ETag` and `Last-Modified` itself.

## Acceptance criteria and observed behavior

| Criterion | Behavior | Coverage |
| --- | --- | --- |
| Valid range | 206 with `Content-Range: bytes a-b/N`, exact `Content-Length`, the selected bytes only | `Issue47_RangeResponses`, HTTP/1.1 and HTTP/2 |
| Suffix range `bytes=-5` | Last five bytes, 206 | same |
| Open-ended `bytes=99990-` | From the offset to the end, 206 | same |
| Range past the end on one side | Clamped to the last byte, 206 | same |
| Unsatisfiable range | 416 with `Content-Range: bytes */N`; an owned source is disposed without being read | `Issue47_RangeResponses`, `RepresentationStreamingTest` |
| `If-Range` with the current strong tag | 206 | `Issue47_RangeResponses`, `ByteRangeSelectionTest` |
| `If-Range` with a stale tag or a weak date | 200 and the whole representation | same |
| `If-None-Match`, `If-Match`, `If-Modified-Since` | 304 or 412 decided before range selection, with no source read | `Issue47_RangeResponses`, `RequestPreconditionsTest` |
| HEAD | 200 metadata with the full length and `Accept-Ranges`; the source is never read | `Issue47_RangeResponses`, `RepresentationStreamingTest` |
| Methods other than GET and QUERY (HEAD metadata as above) | `Range` ignored; the full representation is sent | same |
| `Accept-Encoding` on the request | Ignored; ranges address stored bytes and no coding is applied | `Issue47_RangeResponses` |
| Multiple ranges | 206 `multipart/byteranges` with per-part headers and exact total length; overlapping and adjacent parts coalesce; more than `maximumRanges` specifications fall back to the whole representation | `RepresentationRangeWireTest`, `RepresentationStreamingTest` |
| Unknown length or non-seekable source | Rejected with `ArgumentException` before anything is committed, so the exception handler still owns the response | `Issue47_RangeResponses` |
| Large range | Streamed through a 64 KiB rented buffer; reads never exceed it and only the selected bytes are read | `Issue47_RangeResponses` with a 64 MiB pattern source |
| Cancellation and client disconnect | The copy stops at the transport failure and an owned source is disposed | `Issue47_RangeResponses`, `RepresentationStreamingTest` |

## Compression

Byte ranges apply to the bytes the server holds. The helper never negotiates a
content coding and ignores `Accept-Encoding`. To serve a compressed
representation, store or produce the compressed bytes, set
`Content-Encoding` on the response before calling the helper, and let ranges
address the compressed bytes; `RepresentationRangeWireTest` covers that case.
Do not wrap the response output in a compressing stream around the helper: the
declared `Content-Length` and `Content-Range` describe the stored bytes.

## What is not provided

- Automatic range handling for every module. `FileModule` keeps its own range
  logic for static files; everything else opts in by calling the helper.
- Ranges over unknown-length or dynamically generated bodies. Buffer or spool the
  result to a seekable stream first if ranges are required, and accept the cost
  knowingly; the helper itself never buffers.
- Range units other than `bytes`. Other units are ignored and the whole
  representation is sent.

## Decision

No new API is needed for this request. The helper the roadmap asked for exists,
is opt-in, reuses the shared range and precondition parsing, and now has
end-to-end coverage of every acceptance criterion over real HTTP/1.1 and HTTP/2
connections from an ordinary action module. The helper is new in Neo v2;
published 1.0.3 has only `FileModule` ranges and the `IsRangeRequest` parser.
