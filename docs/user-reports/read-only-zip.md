# Sharing a read-only ZIP archive

[Upstream #573](https://github.com/unosquare/embedio/issues/573), reported by jswigart, identified that `WithZipFile` opened an immutable archive with read/write file access. On Windows this prevented a second instance from opening the same ZIP and required unnecessary write permission.

## Current source correction

The path-based `ZipFileProvider` constructor now explicitly uses `FileMode.Open`, `FileAccess.Read` and `FileShare.Read`. Multiple provider instances and external read-only tools can share an archive. If ZIP validation fails after opening the file, the constructor disposes its owned handle before rethrowing the original exception; a rejected archive no longer requires garbage collection before it can be reopened.

This correction is included starting with EmbedIO-Neo 1.0.2. Public APIs, target frameworks and dependencies are unchanged. The provider remains immutable, and its stream overload keeps the existing `leaveOpen` ownership contract. Archive access/sharing enforcement ultimately follows the host OS and .NET runtime; read-only attributes do not restrict privileged Unix processes in the same way as ordinary users.

When building the corrected source, the usual registration works. This is a partial registration snippet for an existing server:

```csharp
server.WithZipFile("/assets", "site.zip");
```

Keep the archive unchanged while it is being served. This is not a live ZIP update or writer-sharing API. To replace content, stop and dispose the servers/providers using that archive before replacing it, then create fresh providers. Serving requests use the archive's entry metadata; changing the underlying file in place is unsupported.

## Published 1.0.1 workaround: pass a read-only stream

The existing `WithZipFileStream` overload already accepts a stream you open with the correct access/sharing. This complete example works with the published package and avoids its path-constructor defect.

```sh
dotnet new console --framework net10.0 --name ZipDemo
cd ZipDemo
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace `Program.cs` completely:

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using EmbedIO;

const string archivePath = "site.zip";
if (!File.Exists(archivePath))
{
    using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
    using var writer = new StreamWriter(archive.CreateEntry("index.html").Open());
    writer.Write("<h1>Hello shared ZIP</h1>");
}

var port = args.Length == 0 ? 8877 : int.Parse(args[0]);
using var server = new WebServer(o => o
    .WithUrlPrefix($"http://127.0.0.1:{port}/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithZipFileStream("/assets", new FileStream(
        archivePath, FileMode.Open, FileAccess.Read, FileShare.Read));
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine($"Open http://127.0.0.1:{port}/assets/index.html; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);
```

Start the first instance with `dotnet run -- 8877`. After it creates `site.zip` and starts listening, open a second terminal in the same `ZipDemo` directory and run `dotnet run --no-build -- 8878`. Both read the same archive from that working directory. Open either URL or use:

```sh
curl http://127.0.0.1:8877/assets/index.html
curl http://127.0.0.1:8878/assets/index.html
```

Both responses should be `<h1>Hello shared ZIP</h1>`. Ctrl+C stops each instance gracefully; disposing the server closes the stream owned by its ZIP module. The example creates the archive only for demonstration: start the first instance before the second to avoid racing that creation step. `/assets` is the module's base path; the ZIP contains `index.html`, not an `assets` folder.

If retaining a caller-owned stream is necessary, the existing explicit provider supports `new ZipFileProvider(stream, leaveOpen: true)` inside a `FileModule`; the caller must then dispose the stream. Do not close a stream while a provider still uses it.

## Validation

Regression cases exercise shared readers in both opening orders, an archive marked read-only, invalid/empty ZIP constructor cleanup, read-only stream ownership, fluent configuration failure cleanup, and two real servers reading the same stored/deflated archive. HTTP checks cover exact binary contents, encoded nested paths, HEAD, ranges, missing resources and independent shutdown. Hosting leaves archive bytes unchanged, and owned handles can be reopened exclusively after disposal without a forced GC cycle.

See [fork issue #86](https://github.com/WilliamSmithEdward/embedio-neo/issues/86) for the attributed report and confirmed results. The original Slack conversation was not supplied; its content is represented only by rdeago's linked public summary, not an invented transcript.
