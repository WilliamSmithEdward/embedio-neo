# Uncompressed responses and Content-Encoding

[Upstream #566](https://github.com/unosquare/embedio/issues/566), reported by a-schade, identified `Content-Encoding: identity` on MP4 responses used with COG/WPE. The response should omit that field when no content coding has been applied. [RFC 9110 section 8.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.4) reserves identity for Accept-Encoding negotiation and advises against including it in Content-Encoding.

## Current source correction

The shared `IHttpRequest.TryNegotiateContentEncoding` response-preparation callback now removes Content-Encoding when negotiation selects `CompressionMethod.None`. This includes an absent or empty Accept-Encoding header and explicit identity requests. It clears stale coding metadata instead of replacing it with identity. `Vary: Accept-Encoding` remains, as do gzip/deflate selection, their response headers, file bytes, range handling, compression-specific ETags and rejection of unacceptable encodings.

This is an **unreleased** source correction, unavailable in NuGet 1.0.1. Public APIs, target frameworks, dependencies and compression preferences are unchanged. The intentional wire-header correction also applies to text/JSON and other responses using the shared helper. Applications inspecting an uncompressed response should accept an absent Content-Encoding field rather than depend on the legacy identity value. `Response.ContentEncoding` describes a text charset; it is separate from the Content-Encoding HTTP field used for compression. See [static-file charset](static-file-charset.md).

## Serve a video from the corrected checkout

From a checkout containing the correction:

```sh
dotnet new console --framework net10.0 --name VideoDemo --output TestResults/VideoDemo
dotnet add TestResults/VideoDemo/VideoDemo.csproj reference src/EmbedIO/EmbedIO.csproj
mkdir TestResults/VideoDemo/media
```

Copy your video to `TestResults/VideoDemo/media/video.mp4`. Replace `TestResults/VideoDemo/Program.cs` completely:

```csharp
using System;
using System.IO;
using System.Threading;
using EmbedIO;

var directory = Path.GetFullPath(args.Length == 0 ? "media" : args[0]);
if (!File.Exists(Path.Combine(directory, "video.mp4")))
{
    Console.Error.WriteLine($"Copy video.mp4 into {directory}.");
    Environment.ExitCode = 1;
    return;
}

using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithStaticFolder("/", directory, false);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Open http://127.0.0.1:8877/video.mp4; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);
```

Run from the repository directory, passing the media path explicitly:

```sh
dotnet run --project TestResults/VideoDemo -- TestResults/VideoDemo/media
```

In another terminal in the same directory:

```sh
curl -I http://127.0.0.1:8877/video.mp4
curl -H "Accept-Encoding: identity" -D TestResults/VideoDemo/full.headers -o TestResults/VideoDemo/copy.mp4 http://127.0.0.1:8877/video.mp4
curl -H "Range: bytes=0-15" -D TestResults/VideoDemo/range.headers -o TestResults/VideoDemo/range.bin http://127.0.0.1:8877/video.mp4
```

For a nonempty video larger than 16 bytes, HEAD returns `200`, the `video/mp4` media type, the full file length and no body. Existing Content-Type charset metadata is unchanged; changing it is covered in the linked charset guide. The full GET returns `200` and exactly the original bytes. The range request returns `206`, `Content-Range: bytes 0-15/<file length>` and exactly 16 bytes. None includes Content-Encoding. The `/` module base route includes child paths; it is the supplied directory that determines which files are served. Relative directory arguments are resolved against the process working directory at startup. Ctrl+C cancels the listener and lets `RunAsync` finish.

## Compression, caches and client limits

- Identity remains valid in **request** Accept-Encoding. The lower-level `QValueList` negotiation result and `CompressionMethodNames.None` retain their existing identity name; only response preparation omits the field. Do not change those public values or send an empty field as a substitute.
- The correction applies with either listener mode, normal folders or ZIP providers, content caching enabled or disabled, and GET/HEAD/304/206 responses. Compressed representations retain their gzip/deflate headers; conditional requests use the ETag for the selected representation.
- Existing range/compression policy is preserved: FileModule prefers no compression for byte ranges. If the client excludes identity and forces a supported compression, FileModule sends a complete compressed `200` representation rather than a compressed byte range. No multipart-range support is added.
- Do not remove gzip/deflate headers from bytes that actually use those codings. Doing so makes clients interpret compressed data as original media. A reverse proxy or a later application callback may also change response headers; inspect the final response received by the browser.
- Regression fixtures use deterministic binary data named `.mp4` to exercise MIME selection and exact transfer bytes. They are not playable media, and the tests do not establish COG/WPE decoding or playback compatibility. If playback still fails with the corrected headers, provide the browser/runtime version, a minimal server configuration, request/response headers and an appropriately shareable media sample.

Validation covers both listeners, absent/empty/explicit identity negotiation, cached/uncached folder and ZIP responses, cold and warm ranges, HEAD and conditionals, rejected encodings, stale-header cleanup, buffered/streaming text and JSON, gzip/deflate integrity and switching between compressed and uncompressed representations.
