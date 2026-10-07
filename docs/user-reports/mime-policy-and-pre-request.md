# Customize MIME policy and pre-request processing

captainjono's [upstream #521 follow-up](https://github.com/unosquare/embedio/issues/521#issuecomment-855552609)
asks for application configuration rather than a source fork: injectable MIME
policy and a handler before request dispatch. The additive APIs tracked in
[issue #129](https://github.com/WilliamSmithEdward/embedio-neo/issues/129) address
those two concerns. [Controller route case matching](controller-route-case.md)
is a separate opt-in setting; these callbacks do not rewrite URLs.

## Run a file server with your own MIME policy

These APIs are available from source and **are not in a published NuGet version
yet**. From this repository with its selected SDK installed:

```sh
dotnet new console --framework net10.0 --output TestResults/customization-demo --no-restore
dotnet add TestResults/customization-demo/customization-demo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace that project's `Program.cs` with this complete program:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Files;

var directory = Path.Combine(AppContext.BaseDirectory, "public");
Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, "hello.note"), "Hello from EmbedIO-Neo!");
File.WriteAllText(Path.Combine(directory, "hello.txt"), "Built-in MIME mapping.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var files = new FileModule("/files", new FileSystemProvider(directory, false))
{
    MimeTypeProvider = new NoteMimePolicy()
};
using var server = new WebServer(options => options
    .WithUrlPrefix("http://localhost:8877/").WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new PreRequestModule(context =>
    {
        context.Response.Headers["X-App-Policy"] = "notes";
        return Task.CompletedTask;
    }))
    .WithModule(files);
Console.WriteLine("Try /files/hello.note. Press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class NoteMimePolicy : IMimeTypeProvider
{
    public string GetMimeType(string extension)
        => string.Equals(extension, ".note", StringComparison.OrdinalIgnoreCase)
            ? "text/plain" : null!;

    public bool TryDetermineCompression(string mimeType, out bool preferCompression)
    {
        preferCompression = false;
        return false;
    }
}
```

Run it, then use another terminal:

```sh
dotnet run --project TestResults/customization-demo/customization-demo.csproj
curl -i http://localhost:8877/files/hello.note
curl -i http://localhost:8877/files/hello.txt
```

Both responses are 200 with `Content-Type: text/plain` and `X-App-Policy: notes`.
The first body is `Hello from EmbedIO-Neo!`; the second is `Built-in MIME mapping.`
On Windows PowerShell use `curl.exe`. Files live beside the executable, so the
current working directory does not determine their location. The mutable file
provider checks for changes; this demo overwrites its two files at startup.
Ctrl+C cancels the listener, then the application disposes the server.

## Choose the smallest configuration you need

For a few extensions, existing `files.WithCustomMimeType(".note", "text/plain")`
is sufficient. Injection is useful when an application owns a shared or
computed policy. Set `FileModule.MimeTypeProvider` before startup; its default
is null and configuration locks when the server starts.

Lookups use explicit file-module overrides first, then the injected provider,
then server overrides, then built-in mappings and the existing unknown-extension
fallback (`application/octet-stream`). Return null for an unanswered MIME lookup
and false from `TryDetermineCompression` for an unanswered compression preference.
Returning true with `preferCompression = false` is an explicit preference against
compression; client content-encoding negotiation still applies. Returning a MIME
type for a known extension deliberately overrides its server/built-in mapping.

To supply an application-specific unknown-extension default while retaining
built-in types, a provider can return null when `MimeType.Associations` contains
the extension, and its selected default otherwise. This is module policy; it
does not change `MimeType.Default` or another module's behavior. Such a provider
also precedes server overrides, so return null for extensions that should be
answered at server scope.

The injected object remains application-owned, including disposal. Its methods
can run concurrently. Keep its policy stable while mapping/content caches are
warm; mutating the object does not invalidate cached MIME metadata. Prefer a
fixed policy per server lifetime. `ClearCache()` exists for explicit cache
maintenance, not as synchronization for concurrent policy mutation. Direct or
indirect delegation cycles through FileModule providers are rejected; an
application-defined provider must also avoid calling back into its own module.

## Run a callback before later modules

Register `PreRequestModule` before the modules it should precede. The default
constructor matches `/`; `new PreRequestModule("/api", callback)` limits it to
that existing, case-sensitive base route. Callbacks are awaited in registration
order and normal dispatch continues afterward. Request URL, query data, body and
controller binding stay unchanged unless the application itself reads the body.

For request-scoped data, use `context.Items`; avoid sharing mutable state between
requests. For asynchronous work, use `context.CancellationToken`. A callback may
reject a request by throwing the appropriate `HttpException`. To supply an early
response, write it and call `context.SetHandled()`; simply writing a body does
not make this non-final module terminal. Exceptions retain existing module error
handling. A callback registered after a final handler will not run for a request
already handled by that module.

The helper does not introduce arbitrary URL rewriting or a different middleware
ordering model. Use the approved per-API route-case option for case-insensitive
controller literals, with the authorization and mount boundaries explained in
the linked routing guide.

## Validation and attribution

Regression coverage uses real managed and Microsoft listeners for local/provider/
server/built-in precedence, unknown defaults, content-encoding negotiation,
cache isolation, provider ownership/configuration, callback ordering, untouched
request data, early responses, rejection, failure recovery and cancellation.
Defaults, target frameworks and production dependencies are retained. New APIs
will require a release before ordinary NuGet consumers can use them.

Thanks to captainjono for explaining the configuration gaps, and to Unosquare and
the original EmbedIO maintainers for the extensible module and MIME-provider
foundation that makes this compatible extension possible.
