# Customize the Server response header

[Upstream #579](https://github.com/unosquare/embedio/issues/579), reported by
`mirror222`, asks how to replace the default `Server` header with `MYOWN`.
`etherealbacon` asks about applying it across responses; `zhangzhezh` suggests
setting the response header. The original report combines APIs, static files,
sessions, CORS, IP banning and WebSocket modules.

For an individual HTTP response, assign the header **before writing its body**:

```csharp
context.Response.Headers["Server"] = "MYOWN";
```

Use assignment rather than `Headers.Add` to replace a previously configured
value instead of accumulating values. The managed listener only adds
`WebServer.Signature` when no `Server` value is present. You do not need to
modify that read-only field or rebuild the library.

## Set it before API and static-file handlers

Create a .NET 10 console application with the published package:

```sh
dotnet new console --framework net10.0 --name CustomServerHeader
cd CustomServerHeader
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace `Program.cs` with this complete program:

```csharp
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

// Paths are relative to the process working directory, not the executable.
var files = Path.GetFullPath("public");
Directory.CreateDirectory(files);
var page = Path.Combine(files, "index.html");
if (!File.Exists(page))
    File.WriteAllText(page, "<!doctype html><title>Demo</title><h1>Hello</h1>");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO));

// First: set the header, then let later modules produce the response.
server.WithModule(new ServerHeaderModule());
server.WithModule(new ActionModule("/api/hello", HttpVerbs.Any,
    context => context.SendStringAsync("hello", "text/plain", Encoding.UTF8)));
// Specific API routes precede the catch-all static folder.
server.WithStaticFolder("/", files, true);

Console.WriteLine("Listening at http://127.0.0.1:8877/; press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

sealed class ServerHeaderModule : WebModuleBase
{
    public ServerHeaderModule() : base("/") { }
    public override bool IsFinalHandler => false;

    protected override Task OnRequestAsync(IHttpContext context)
    {
        context.Response.Headers["Server"] = "MYOWN";
        return Task.CompletedTask;
    }
}
```

Run `dotnet run`. In another terminal:

```sh
curl -i http://127.0.0.1:8877/api/hello
curl -i -X POST http://127.0.0.1:8877/api/hello
curl -I http://127.0.0.1:8877/index.html
curl -i http://127.0.0.1:8877/missing
```

The first two responses return `200` and `hello`. The static HEAD response has
status `200` and no body; the missing path returns `404`. Each includes
`Server: MYOWN`. Ctrl+C cancels the listener and allows `RunAsync` to finish.

The module's `/` base route matches all paths. `IsFinalHandler = false` and
leaving the context unhandled allow later modules to run. Do **not** replace
this module with an `ActionModule` solely to set a header: `ActionModule` marks
the request handled after its callback and would prevent subsequent handlers
from producing their normal responses. Put the header module before CORS,
authentication, IP banning and other modules that might finish a response early.

## Scope and limits

- Ordinary HTTP responses that reach this module keep the custom header unless
  a later handler replaces it or clears headers. This includes API/static
  responses and normal pipeline error responses. Assigning a different value
  later is an intentional override; this is not an enforced server-wide policy.
- On Windows, the native Microsoft listener uses HTTP.sys. Local validation
  observed `MYOWN` alongside `Microsoft-HTTPAPI/2.0`; setting the application
  header did not remove the operating system product token. Header policy may
  depend on the host configuration. This example deliberately selects the
  managed listener and does not change machine-wide HTTP.sys settings. See
  [Microsoft’s HTTP.sys settings](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/health-diagnostic-performance/httpsys-registry-windows)
  for the host-controlled `DisableServerHeader` policy.
- WebSocket upgrade responses use a separate handshake path. The managed
  handshake clears ordinary response headers, so this module does **not** set
  a custom `Server` header on its HTTP 101 response. The native Windows
  upgrade retained `MYOWN` alongside the HTTP.sys token in local validation. Do not claim that a normal
  HTTP middleware covers every handshake or listener backend.
- Malformed requests rejected before module dispatch, hosting layers and reverse
  proxies are outside this module's control. A proxy may replace or add its own
  header; inspect the final response observed by the client.
- Removing `Server` from the collection is not a way to suppress the managed
  default: the listener adds its signature if the value is absent. This guide
  covers replacement, not guaranteed removal of the header.
- Use a trusted constant or validated configuration value. A different header
  does not conceal all server characteristics or provide a security boundary.

The example compiles against published EmbedIO-Neo 1.0.1 and current source.
Real-listener validation checks managed and native modes, API GET/POST, static
GET/HEAD/conditional requests, missing routes and pipeline exceptions, with a
single custom managed header, recorded native HTTP.sys differences, and
unchanged response content/status. It also checks
later overrides and successful WebSocket communication to establish the
handshake limit. No defaults, public APIs or dependencies are changed.
