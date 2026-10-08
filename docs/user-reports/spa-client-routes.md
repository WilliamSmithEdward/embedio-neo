# Single-page application routes without redirects

[Upstream #490](https://github.com/unosquare/embedio/issues/490), proposed by
former maintainer `rdeago` after `madnik7`'s report, describes a bookmarked
`/dashboard` failing because no dashboard file exists. `maarlo` supported the
proposal, `madnik7` used a custom module, and `osnoser1` asked for a current-version
recipe. Their discussion helped identify the important contract: serve the
entry HTML **at the requested URL**, allowing the browser's client router to
select its view.

Existing `IFileProvider` injection supports a deterministic, application-owned
route policy without a new library API. The wrapper below first checks real
files/directories, then maps an exact allowlisted client route to `/index.html`.
The request URL is never rewritten and no redirect is sent. Ordinary static
modules retain their default 404 behavior. This answers the navigation case;
it does not implement the proposed context-aware `PreProcessPath` callback.

## Complete runnable example

Create a .NET 10 console project and install the verified published package:

```sh
dotnet new console -n SpaDemo --framework net10.0
cd SpaDemo
dotnet add package EmbedIO-Neo --version 1.0.3
```

Save this complete helper as `SpaFiles.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EmbedIO;
using EmbedIO.Files;

// Application-owned policy for a fixed, immutable deployment.
public sealed class SpaFiles : IFileProvider, IDisposable
{
    private readonly FileSystemProvider _files;
    private readonly HashSet<string> _routes;

    public SpaFiles(string root, params string[] routes)
    {
        _files = new FileSystemProvider(root, isImmutable: true);
        _routes = new HashSet<string>(routes, StringComparer.Ordinal);
    }

    public bool IsImmutable => _files.IsImmutable;
    public event Action<string>? ResourceChanged
    {
        add => _files.ResourceChanged += value;
        remove => _files.ResourceChanged -= value;
    }

    public void Start(CancellationToken cancellationToken) => _files.Start(cancellationToken);
    public Stream OpenFile(string path) => _files.OpenFile(path);
    public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypes)
        => _files.GetDirectoryEntries(path, mimeTypes);
    public void Dispose() => _files.Dispose();

    public MappedResourceInfo? MapUrlPath(string path, IMimeTypeProvider mimeTypes)
    {
        var actual = _files.MapUrlPath(path, mimeTypes);
        if (actual != null || !_routes.Contains(path))
            return actual;
        return _files.MapUrlPath("/index.html", mimeTypes);
    }
}
```

Replace `Program.cs` with this complete program:

```csharp
using System.Text;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Files;

const string url = "http://localhost:9696/";
var root = Path.GetFullPath("wwwroot"); // Relative to the directory running dotnet run.
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "index.html"),
    "<!doctype html><html><title>SPA demo</title><body><h1>SPA demo</h1>" +
    "<p id='view'></p><script src='/app.js'></script></body></html>", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(root, "app.js"),
    "document.getElementById('view').textContent = 'Client view: ' + location.pathname + location.search;",
    new UTF8Encoding(false));

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new ActionModule("/api", HttpVerbs.Any, context =>
    {
        if (context.RequestedPath != "/ping") throw HttpException.NotFound();
        return context.SendStringAsync("pong", "text/plain", Encoding.UTF8);
    }))
    .WithModule(new FileModule("/", new SpaFiles(root, "/login", "/dashboard", "/dashboard/reports", "/release/v1.0")));
Console.WriteLine("Open " + url + "dashboard?tab=recent; Ctrl+C stops the server.");
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
```

Run `dotnet run`, open `http://localhost:9696/dashboard?tab=recent`, and refresh
or bookmark it. The browser should show `Client view: /dashboard?tab=recent`.
This tiny script demonstrates the preserved URL; it is not a framework-specific
router. In a real SPA, deploy the framework's generated entry file and assets
instead of writing these demonstration files at startup. Use Ctrl+C to stop.

Check the server responses independently:

```sh
curl -i 'http://localhost:9696/dashboard?tab=recent'
curl -I http://localhost:9696/dashboard
curl -i http://localhost:9696/api/ping
curl -i http://localhost:9696/missing.js
curl -i -X POST http://localhost:9696/dashboard
```

Expect HTML/200 with no `Location` header for direct navigation, HEAD/200 with
no body, `pong`/200 for the API, 404 for the missing asset and 405 for POST to
an allowlisted view. An unknown route also returns 404; this recipe does not
pretend every missing resource is a client view.

## Choose the route policy deliberately

- Register API and WebSocket modules **before** the file module. `FileModule`
  is a final handler. API modules must handle/reject their own missing routes;
  do not deliberately pass API failures into the SPA fallback.
- Route strings are module-relative, case-sensitive and do not contain query
  strings. With `new FileModule("/app", ...)`, configure `/dashboard` in the
  provider and browse `/app/dashboard`. The mount prefix keeps its existing
  base-route matching rules. The demonstration script uses root-relative
  `/app.js`; change it to `/app/app.js` when deploying that example at `/app`.
- Prefer an explicit route table (or carefully bounded application predicate).
  A missing `.js`, image, API path or misspelled URL should not receive HTML.
  Dotted client routes such as `/release/v1.0` work when explicitly listed;
  an extension-only heuristic would incorrectly reject them.
- Existing files and directories take precedence. A real `/login` file stays
  a file; a directory without an index retains normal directory handling.
  If `/index.html` is absent, mapping fails with the usual 404. Keep directory
  listing disabled. The helper delegates path containment and opening to
  `FileSystemProvider`; it never builds a filesystem path from a client route.
  Deploy only intended public files and do not use symlinks to expose secrets.
- This helper declares the deployment **immutable**, matching its fixed route
  table. Replace the server for a new deployment; editing files in place is
  outside that contract. `FileModule` owns and disposes this provider. No extra
  worker or external dependency is introduced.

`FileModule` still supplies MIME, ETag/Last-Modified, revalidation, ranges,
compression and HEAD handling using the mapped entry file. Multiple client
routes share that file's resource identity, not separate copies. Standard
cache configuration applies; disabling body caching alone does not make an
immutable deployment live-reloading. See
[multiple static folders](../guides/multiple-static-folders.md) for module
order and pass-through, and [serve files](../guides/getting-started/files.md)
for the introductory file-serving task.

For per-user HTML, authorization-dependent mapping or a changing runtime route
table, reconsider the cache/policy design explicitly rather than making this
immutable helper context-dependent. The original suggestion included those
possible extensions, but this recipe addresses the demonstrated path-routing
case using existing APIs. No new default or public callback is introduced.

## Validation and limits

Twenty real HTTP regression cases exercise both listener modes, cache enabled
and disabled, direct navigation/refresh, nested and dotted routes, query strings,
API and real-file precedence, missing assets/views, a subpath mount, HEAD,
conditional requests, byte ranges, unsupported methods, missing entry HTML,
path containment and unchanged default mapping. These are server-side contract
checks, not a claim of testing every SPA framework or the original application.
The complete program is also checked against published EmbedIO-Neo 1.0.3.
