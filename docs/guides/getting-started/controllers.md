# Use a controller

Use a controller to group related endpoints and declare exact routes. Start with
the app from [Your first JSON endpoint](README.md); no additional package is needed.

## Replace Program.cs

This complete program moves the status endpoint into a controller:

```csharp
using System;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .WithWebApi("/api", api => api.WithController<StatusController>());

Console.WriteLine("Open http://localhost:9696/api/status");
Console.WriteLine("Press Ctrl+C to stop.");
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

public sealed class StatusController : WebApiController
{
    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new { status = "ok" };
}
```

Run `dotnet run` and open
[localhost:9696/api/status](http://localhost:9696/api/status).
The response is still `{"status":"ok"}`.

`WithWebApi("/api", ...)` supplies the shared URL prefix. The route attribute
adds `/status`, producing `/api/status`. Return an object and EmbedIO writes
the JSON response for you. Add more attributed methods to add endpoints.

Keep `OnGet` for a small callback. Use controllers when you want explicit route
matching or a group of API operations. Both use the same server.

When an endpoint needs asynchronous work, return `Task<T>` and await the work
before returning your response. See [asynchronous outbound requests](../async-outbound-requests.md)
for a complete controller method and error handling.

Next: [Routes, verbs, and parameters](requests.md) for GET, POST, PUT, DELETE,
query strings, and JSON bodies. Or [serve HTML and files](files.md).
To combine a controller and static files,
register `WithWebApi` before `WithStaticFolder`, just as with the callback example.
