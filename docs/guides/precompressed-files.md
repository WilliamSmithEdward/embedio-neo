# Precompressed static files

A `FileModule` can send a file such as `app.js.br` or `app.js.gz`, stored next to `app.js`, to
clients that accept that encoding. The file is compressed once, when you build or deploy the site,
at a level too slow to use on every request, and the server sends its bytes as they are.

It is opt-in through `FileModule.PrecompressedVariants`. Without it, nothing changes: the module
compresses files itself as before.

This is unreleased and available only from source.

## A complete program

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using EmbedIO;
using EmbedIO.Files;

var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><script src=\"app.js\"></script>");
File.WriteAllText(Path.Combine(root, "app.js"), "console.log('hello');\n");
Precompress(Path.Combine(root, "app.js"));

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

using var server = new WebServer("http://localhost:9696/")
    .WithModule(new FileModule("/", new FileSystemProvider(root, false))
    {
        DefaultDocument = "index.html",
        PrecompressedVariants = PrecompressedVariants.All,
    });

Console.WriteLine(new Uri("http://localhost:9696/app.js"));
await server.RunAsync(cancellation.Token);

// Usually a build or deployment step: the slowest, smallest settings, paid once per file.
static void Precompress(string path)
{
    var source = File.ReadAllBytes(path);
    using (var brotli = new BrotliStream(File.Create(path + ".br"), new BrotliCompressionOptions { Quality = 11 }))
        brotli.Write(source, 0, source.Length);
    using (var gzip = new GZipStream(File.Create(path + ".gz"), CompressionLevel.SmallestSize))
        gzip.Write(source, 0, source.Length);
}
```

`curl --compressed -i http://localhost:9696/app.js` receives `app.js.br` with
`Content-Encoding: br`; `curl -i http://localhost:9696/app.js` receives `app.js` itself.

`PrecompressedVariants.Gzip` and `PrecompressedVariants.Brotli` enable one kind each. Sending a
`.br` file needs no Brotli encoder, so this works on the .NET Standard 2.0 asset too. Set the
property before the server starts; it cannot change afterwards.

## Which file a request gets

- The module looks for a variant only when it would compress the response itself: the MIME type
  prefers compression (see `PreferCompression`), the request has no `Range` header, and the
  resource is a file from the provider. The one exception is a client that refuses everything the
  module could send otherwise, such as `Accept-Encoding: br, identity;q=0`: it gets a variant it
  accepts, if there is one, instead of `406 Not Acceptable`.
- The client must accept the variant's encoding in `Accept-Encoding` with a weight above zero, at
  least as high as the encoding the module would otherwise use. `br` is preferred to `gzip` at
  equal weight. `*` counts for both; `br;q=0` refuses Brotli.
- The variant must be a file whose last write time is no earlier than the original's. A stale
  `.br` left behind after `app.js` changed is ignored, and the module compresses `app.js` itself.
- Default documents and default extensions have variants too: `/` can be answered with
  `index.html.br`.

Otherwise the request is answered exactly as before.

A variant response carries the original file's `Content-Type`, `Content-Encoding` for the
variant, `Vary: Accept-Encoding` and the variant's own `Content-Length`. `HEAD` sends the same
headers without the body. `OnPrepareResponse` sees the original file's information.

Requests for `app.js.br` itself are served as an ordinary file with the provider's MIME type for
`.br`, as before. Hide sidecars from directory listings or the provider if clients should not ask
for them directly.

## Validators, ranges and caching

Each representation has its own entity tag, derived from both files' sizes and write times and the
encoding, so `If-None-Match` from a Brotli client never matches the gzip or identity response.
`If-Modified-Since` is compared with the variant's write time.

A `Range` request gets the original file, as it already did with dynamic compression. Range
offsets apply to the identity representation only, never to a differently encoded one.

Within the module's caching limits, a variant is cached separately from the original and from
dynamically compressed copies. Larger variants are streamed from the provider.

A replaced `.br` or `.gz` file is picked up on the next request, since the variant is looked up on
every request. Noticing that the original changed, and that its variant is therefore stale, needs a
provider that reports changes. On macOS `FileSystemProvider` is always immutable, because it has no
file watcher there, so regenerate variants before the server starts or restart it after changing
originals.

## Refused encodings no longer get 406

This change also corrects content negotiation for every module that compresses responses. When a
client refuses every coding EmbedIO could produce, for example `Accept-Encoding: br;q=0`, and
does not refuse `identity`, the response is now sent uncompressed. Previously, a MIME type that
prefers compression answered `406 Not Acceptable`. This follows
[RFC 9110, section 12.5.3](https://www.rfc-editor.org/rfc/rfc9110#section-12.5.3): the
uncompressed representation stays acceptable unless `identity;q=0` or `*;q=0` (without a more
specific `identity` entry) excludes it. Headers that exclude it still get 406.

## Choosing compression levels

Measured on .NET 10.0.12 (Windows, one thread, CPU time per MiB of input, median of up to eight
samples) over two text corpora from this repository: its Markdown documentation (1.9 MB) and the
core C# sources (2.5 MB). The ratio is compressed size over original size.

| Setting | Docs ratio | Docs ms/MiB | C# ratio | C# ms/MiB |
| --- | ---: | ---: | ---: | ---: |
| gzip `Fastest` | 0.512 | 3.4 | 0.268 | 2.0 |
| gzip `Optimal` (module default) | 0.340 | 12.8 | 0.169 | 8.3 |
| gzip `SmallestSize` | 0.337 | 32.4 | 0.167 | 18.4 |
| Brotli quality 1 | 0.364 | 3.8 | 0.196 | 2.6 |
| Brotli quality 4 | 0.316 | 11.9 | 0.159 | 6.4 |
| Brotli `Optimal` (module default) | 0.316 | 11.5 | 0.159 | 6.3 |
| Brotli quality 5 | 0.297 | 22.2 | 0.146 | 11.6 |
| Brotli quality 6 | 0.291 | 25.6 | 0.142 | 13.5 |
| Brotli quality 9 | 0.282 | 57.5 | 0.135 | 32.2 |
| Brotli quality 11 | 0.259 | 1176 | 0.122 | 1012 |

What this supports:

- For precompressed files, Brotli quality 11 is about 20% smaller than gzip `Optimal` and 18%
  smaller than the module's dynamic Brotli, at roughly 100 times the CPU. Paid once per file at
  build time, that is usually worth it. For `.gz` sidecars, `SmallestSize` saves about 1% over
  `Optimal`.
- On every request, the module's existing defaults are a reasonable balance: Brotli `Optimal`
  (identical output to quality 4 here) costs about the same as gzip `Optimal` and is 6 to 7%
  smaller. Levels above that cost two to five times the CPU for a few percent. This change does
  not alter the module's levels.
- Responses written through `OpenResponseStream` and the helpers built on it, such as
  `SendDataAsync`, use Brotli quality 1 instead, as ASP.NET Core does. On these corpora it takes
  0.32 and 0.41 times the CPU of quality 4, for output 15% and 23% larger. See
  [Brotli responses](getting-started/files.md#brotli-responses-in-the-modern-engine-development-branch).

These are two text corpora on one machine. Measure your own files before relying on the numbers:
images and already compressed formats gain little or nothing, which is why the MIME policy that
decides whether to compress also decides whether to look for a variant.
