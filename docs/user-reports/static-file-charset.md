# Controlling charset on static-file responses

[Upstream #567](https://github.com/unosquare/embedio/issues/567), reported by asesidaa, asked for control over `charset` in static-file `Content-Type` headers. EmbedIO-Neo now offers an optional `FileModule.OnPrepareResponse` callback that receives the actual mapped resource before its response is sent. The managed listener also honors `Response.ContentEncoding`: null suppresses automatic charset, and a non-null encoding supplies its own name instead of always advertising UTF-8.

These changes are **unreleased** and are not available in NuGet 1.0.1. The callback defaults to null. Unconfigured file responses retain their previous behavior, including the managed listener's default UTF-8 charset. Public interfaces, target frameworks and dependency requirements are unchanged; the callback is an additive property on `FileModule`.

## Serve UTF-8 text and binary files

From a checkout containing these changes, create an ignored demonstration project:

```sh
dotnet new console --framework net10.0 --name CharsetDemo --output TestResults/CharsetDemo
dotnet add TestResults/CharsetDemo/CharsetDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/CharsetDemo/Program.cs` completely:

```csharp
using System;
using System.IO;
using System.Threading;
using EmbedIO;

var directory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, "index.html"),
    "<meta charset=\"utf-8\"><h1>Hello café</h1>", WebServer.Utf8NoBomEncoding);
File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[] { 0, 255, 10, 128 });

using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithStaticFolder("/", directory, false, module =>
    {
        module.OnPrepareResponse = (context, resource) =>
        {
            // Use the selected MIME type, including default-document mapping.
            var mime = MimeType.StripParameters(context.Response.ContentType);
            context.Response.ContentEncoding = null;
            context.Response.ContentType = mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                ? mime + "; charset=utf-8"
                : mime;
        };
    });
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Open http://127.0.0.1:8877/; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);
```

Run it, then make requests from another terminal:

```sh
dotnet run --project TestResults/CharsetDemo
```

```sh
curl -i http://127.0.0.1:8877/
curl -I http://127.0.0.1:8877/payload.bin
curl -i -H "Range: bytes=0-1" http://127.0.0.1:8877/payload.bin
```

The first request returns `200`, `Content-Type: text/html; charset=utf-8` and the HTML above. The HEAD request returns `200`, `Content-Type: application/octet-stream`, `Content-Length: 4` and no body. The range request returns `206`, no charset, `Content-Range: bytes 0-1/4` and exactly the bytes `00 FF`. Ctrl+C cancels the listener and lets `RunAsync` complete.

The program creates or overwrites two demonstration files under its output directory, next to the executable; it does not depend on the terminal's working directory. The `/` file-module base route includes child paths, and `/` itself maps to `index.html`. This example's rule assumes its `text/*` files are UTF-8; it is an application policy, not universal MIME or encoding detection. Use the actual resource name/path to select a different charset when necessary. Do not advertise UTF-8 for a file stored in another encoding.

## Select a different charset for a known file

This partial callback replacement assumes `legacy.txt` is stored as ISO-8859-1 and all other text files are UTF-8:

```csharp
module.OnPrepareResponse = (context, resource) =>
{
    var mime = MimeType.StripParameters(context.Response.ContentType);
    context.Response.ContentEncoding = null;
    context.Response.ContentType = resource.Name.Equals("legacy.txt", StringComparison.OrdinalIgnoreCase)
        ? mime + "; charset=iso-8859-1"
        : mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            ? mime + "; charset=utf-8"
            : mime;
};
```

File bytes are served unchanged. Setting charset does not decode, encode, validate or convert a file, and it does not change compression negotiation. For binary formats, omit charset unless that format's specification and your application require it. JSON, XML and other formats have their own encoding rules; do not infer a universal whitelist from `text/*` alone.

## Callback behavior and listener differences

- The callback runs once for each successful GET/HEAD response, after MIME, cache, compression and range metadata is prepared and before headers or body bytes are sent. FileModule sets transport framing and content length afterward. It also runs for `206` ranges, `304` not-modified responses, default documents and enabled directory listings.
- It runs on every request, including populated file caches and immutable ZIP providers. The resource mapping and bytes keep their existing caching behavior; callback results are not cached because they may depend on the request. Keep the callback quick and safe for concurrent execution.
- Error responses such as `404`, `405`, `406` and `416` do not invoke it. Exceptions in the callback use the module's existing exception handling; handle expected application failures deliberately.
- Configure the callback before starting the server. The property is locked with the other module configuration. Use it for headers; do not write a body or change status, content length, ranges or compression headers. Those values describe the representation FileModule will send.
- The Microsoft listener's existing `ContentEncoding` property does not automatically add charset to its header in the tested runtimes. For the same header on both listeners, set `ContentEncoding = null` and explicitly include a charset in `ContentType`, as shown above. Native listener defaults are unchanged.
- On the corrected managed listener, a non-null `ContentEncoding` adds charset only when a valid explicit charset parameter is absent. Existing charset parameters take precedence, including mixed case and quoted values. A parameter named `x-charset` or a quoted value containing `charset=` is not a charset parameter. Null suppresses automatic addition; it does not remove a charset already present in `ContentType`.
- `OpenResponseText` selects an encoding for its writer and sets `ContentEncoding` accordingly. Header-only metadata cannot change bytes already written or the encoding chosen by another writer.

Regression coverage exercises real managed/native listeners, UTF-16 file bytes, binary files, folder and ZIP providers, repeated/cache-hit requests, HEAD, conditional requests, ranges, gzip/deflate, directory listings, error isolation, text writers and unchanged defaults. The original discussion also proposed changing default MIME policy; that breaking policy change is intentionally not adopted.

Validating the example also exposed a pre-existing cold-cache range defect: caching a whole file overwrote the selected response length, producing excess bytes or an invalid offset/count combination. The correction keeps the selected slice length while caching the full representation. Four regressions cover initial and repeated ranges at zero/nonzero offsets after HEAD, on both listeners.
