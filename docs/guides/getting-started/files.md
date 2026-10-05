# Serve HTML and files

Start with the app from [Your first JSON endpoint](README.md). This page replaces
the JSON-only server with a server that serves a folder.

## Create one HTML file

In the `HelloEmbedIO` project folder, create a folder named `wwwroot`. Inside it,
create `index.html`:

```html
<!doctype html>
<html lang="en">
  <head><meta charset="utf-8"><title>Hello EmbedIO</title></head>
  <body><h1>Hello from EmbedIO-Neo</h1></body>
</html>
```

## Replace Program.cs

```csharp
using System;
using System.IO;
using System.Threading;
using EmbedIO;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .WithStaticFolder("/", Path.GetFullPath("wwwroot"), isImmutable: false);

Console.WriteLine("Open http://localhost:9696/index.html");
Console.WriteLine("Press Ctrl+C to stop.");
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
```

Run `dotnet run` from the project folder, then open
[localhost:9696/index.html](http://localhost:9696/index.html).

`WithStaticFolder` maps URL paths to files. For example,
`wwwroot/css/site.css` is served at `/css/site.css`.
The same mapping serves images, JavaScript, PDFs, and other files:

| File you create | URL |
| --- | --- |
| `wwwroot/logo.png` | `/logo.png` |
| `wwwroot/app.js` | `/app.js` |
| `wwwroot/manual.pdf` | `/manual.pdf` |

Link to them from your HTML using those URL paths, for example
`<a href="/manual.pdf">Read the manual</a>`. The browser decides whether to display
or download a file based on its type and browser settings.

`Path.GetFullPath` resolves `wwwroot` from your current working directory, so
run the command from the folder where you created it. Put only files you intend
to serve in that folder.

`isImmutable: false` lets the server track file changes while you develop.
On macOS, this provider treats files as immutable; it also falls back to that
behavior if file watching is unsupported. Restart the server after editing in
those cases. Use `true` when files will stay unchanged during the server's lifetime.

## Generate HTML in C#

You can also return HTML directly. In the same program, replace the
`using var server` statement with:

```csharp
using var server = new WebServer("http://localhost:9696/")
    .OnGet("/hello", context => context.SendStringAsync(
        "<!doctype html><html lang=\"en\"><title>Hello</title><h1>Hello!</h1></html>",
        "text/html", WebServer.Utf8NoBomEncoding));
```

Restart and open [localhost:9696/hello](http://localhost:9696/hello).
`SendStringAsync` writes text with the content type and encoding you specify.
If you insert user-provided text into HTML, encode it with
`System.Net.WebUtility.HtmlEncode` first.

## Keep the JSON endpoint too

In the program above, replace just the `using var server` statement with:

```csharp
using var server = new WebServer("http://localhost:9696/")
    .OnGet("/api/status", context =>
        context.SendDataAsync(new { status = "ok" }))
    .WithStaticFolder("/", Path.GetFullPath("wwwroot"), isImmutable: false);
```

Restart the app. Both `/api/status` and `/index.html` now work.

Registration order matters. Keep API handlers before the root static folder:
the folder handles unmatched paths too, returning 404 when a file is missing.
An API handler registered after it would not get that request.

Next: [Use a controller](controllers.md), or see
[multiple static folders](../multiple-static-folders.md) for more than one directory.
