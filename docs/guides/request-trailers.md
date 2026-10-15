# Request trailers (Neo v2)

The built-in HTTP/1, HTTP/2 and HTTP/3 request adapters implement the optional
`IHttpRequestTrailers` interface. Existing `IHttpRequest` implementations are
unchanged. Read the body normally, then inspect the separate trailer snapshot:

```csharp
await context.Request.InputStream.CopyToAsync(destination, context.CancellationToken);
if (context.Request is IHttpRequestTrailers request && request.TryGetTrailers(out var trailers))
{
    foreach (var field in trailers)
    {
        // Recognize the field's definition and verify it before application use.
        Console.WriteLine(field.Key + ": " + field.Value);
    }
}
```

`TryGetTrailers` never reads, drains or waits. It returns false, with an empty list,
until complete valid input has been consumed. A successful empty snapshot means
input completed without trailers. A failed, reset or abandoned input cannot publish
a success snapshot. HTTP/2 waits until queued DATA has been consumed; HTTP/3 also
waits for FIN after the trailer section. A recoverable canceled HTTP/2 read can
resume normally; subsequent complete valid consumption makes the snapshot available.

The snapshot preserves field order and repeated lines. Its strings and list cannot
be modified, and it owns no transport buffers. Applications can retain it after
request completion. Trailers never merge into initial `Request.Headers`, routing,
authentication or message framing. An incoming `Trailer` declaration is advisory;
declaration or receipt does not authorize a field. Undeclared and security-sensitive
metadata remain untrusted. Recognize field semantics and verify integrity explicitly.
Existing framing checks, rejection scopes and metadata limits remain enforced.

This additive interface requires no migration for consumers or third-party request
implementations. It does not cause early body consumption and does not change
existing middleware decisions based on initial headers.

The separate snapshot follows [RFC 9110 section 6.5](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.5)
and [RFC 9112 section 7.1.2](https://www.rfc-editor.org/rfc/rfc9112.html#section-7.1.2).
HTTP/2 uses END_STREAM on its trailer block ([RFC 9113 section 8.1](https://www.rfc-editor.org/rfc/rfc9113.html#section-8.1));
HTTP/3 completion also depends on stream closure ([RFC 9114 section 4.1](https://www.rfc-editor.org/rfc/rfc9114.html#section-4.1)).