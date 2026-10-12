# Controller compression and response length

[Upstream #394](https://github.com/unosquare/embedio/issues/394) reports an empty
integer JSON response with `Accept-Encoding: gzip` on Xamarin Forms / Android
4.4.3 and EmbedIO 3.0.8. Scobie supplied [PR #397](https://github.com/unosquare/embedio/pull/397),
including a controller returning `12345` and an automatically decompressing HTTP
client. Desktop tests passed in the original discussion; Android reproduction
results differed. The original maintainers' investigation is preserved in
[fork issue #327](https://github.com/WilliamSmithEdward/embedio-neo/issues/327).

## Current behavior

Current integration source returns `12345` under identity, gzip and deflate.
The compression wrapper must finish before the response is completed. The built-in
serializers own and dispose that wrapper; applications writing directly through
`OpenResponseStream` or `OpenResponseText` must dispose their returned stream or
writer too. Merely flushing a compressor does not complete its encoded body.

Default JSON serialization streams the response. HTTP/1.1 uses chunked framing;
HTTP/2 uses DATA frames. Neither requires a Content-Length. An absent length is
not zero bytes. With existing opt-in buffering, Content-Length describes the
**encoded** bytes, including compression framing, rather than the JSON length:

```csharp
server.WithWebApi("/api", ResponseSerializer.Json(true),
    module => module.WithController<TestController>());
```

Buffering uses memory proportional to the response and should be selected for
bounded payloads when a known length is useful. Do not set Content-Length to the
uncompressed JSON size when sending compressed bytes. Enable client automatic
decompression, or explicitly decode the negotiated Content-Encoding; do not treat
compressed bytes as UTF-8 text.

## Verification and limits

The 24 controlled cases cover the reported integer, an empty JSON string, text
and a 70,000-character value; identity/gzip/deflate; streamed/buffered serialization;
HTTP/1.1, TLS HTTP/1.1 and TLS HTTP/2. They validate headers before body buffering,
encoded length when declared, complete decoding, repeated responses and automatic
client decompression. They pass without a production change.

Muhomorik's separate WinForms / .NET Framework 4.6.2 POST sample did not request
gzip. Additional controls preserve its `{123}` JSON-content request and empty
action response, both with the request body unread and explicitly consumed,
followed by healthy controller requests. This does not establish a shared cause
with the Android report. madnik7's file-size observation in upstream #460 likewise
requires distinguishing absent streamed length from an empty body.

These are modern desktop-runtime controls, not a reproduction of Xamarin Forms,
Android API 19, an old WebView, or the original .NET Framework 4.6.2 application.
MAUI CI is paused at the owner's request, and a modern MAUI pass would not establish
historical Xamarin behavior. No historical Android defect is claimed repaired.
For recurrence, provide the minimal application, runtime/package versions, full
response headers and undecoded response bytes, plus a comparison with compression
disabled. Keep the listener task observed so startup/handler faults are visible.
