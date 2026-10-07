# Concurrent controllers and response ownership

[Upstream #570](https://github.com/unosquare/embedio/issues/570), reported by derole1, describes disposed contexts during rapid JSON requests involving sqlite-net. Returning a DTO with JsonData binding avoided the problem; manual SendDataAsync and binary responses were affected. The complete SQLite/controller application was not supplied, so its specific cause remains unconfirmed.

## Confirmed connection-lifetime correction

Current managed-listener source reproduced a related defect: disposing an already completed response could close a newer request on the same reused TCP connection. Response close/dispose now atomically runs only once. This protects the newer request and preserves first-close behavior, public APIs, defaults, supported frameworks and dependencies. No garbage-collection setting or thread-pool adjustment is needed for this correction.

This correction is included starting with EmbedIO-Neo 1.0.2. The usage patterns below already work with 1.0.1. Regression tests distinguish a stale response from the next context by actual TCP port reuse and exercise repeated/concurrent disposal while the next request is awaiting work.

## Choose one response owner

| Handler return type | Response owner |
| --- | --- |
| DTO / Task of DTO | WebApiModule passes the returned value to its configured serializer. The default is JSON. |
| byte array / Task of byte array | The configured serializer still applies. Default JSON encodes bytes as a JSON Base64 string; use an explicit raw serializer for binary output. |
| Task (non-generic) / synchronous void | The handler owns writing the response; WebApiModule does not serialize a return value. Await the write before returning. |

Do not manually write a response and also return a value that the module will serialize. In the reported pattern, the second serializer tries to change a response whose headers/body are already committed or closed. The managed listener rejects this; native implementations may throw or silently ignore a subsequent write. An HTTP 200 from the first write does not prove the handler completed correctly, and a committed response cannot be replaced with a new error body.

Manual writes from Web API controllers are supported when the handler returns a non-generic Task and awaits the write. The archived maintainer's blanket prohibition is not the current contract. Do not use async void or launch response/database work without awaiting it: those patterns can let the request complete while work still holds its context.

## Complete JSON and binary example (published 1.0.1)

```sh
dotnet new console --framework net10.0 --name ConcurrentApi
cd ConcurrentApi
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace Program.cs completely:

```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithWebApi("/api", m => m.WithController<EchoController>())
    .WithWebApi("/binary", ResponseSerializer.None(false), m => m.WithController<BinaryController>());
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Listening on http://127.0.0.1:8877; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);

public sealed class Input { public int Id { get; set; } }
public sealed class EchoController : WebApiController
{
    [Route(HttpVerbs.Post, "/echo")]
    public async Task<Input> Echo([JsonData] Input input)
    {
        if (input.Id < 0) throw HttpException.BadRequest("Id must be nonnegative.");
        // Demonstration of awaited work. Replace this with an awaited database call.
        await Task.Delay(25, CancellationToken);
        return input;
    }

    [Route(HttpVerbs.Post, "/manual")]
    public async Task Manual([JsonData] Input input)
    {
        await Task.Delay(25, CancellationToken);
        await HttpContext.SendDataAsync(input);
    }
}
public sealed class BinaryController : WebApiController
{
    [Route(HttpVerbs.Get, "/value/{id}")]
    public async Task<byte[]> Value(int id)
    {
        await Task.Delay(25, CancellationToken);
        Response.ContentType = MimeType.Default;
        return Encoding.UTF8.GetBytes("binary:" + id);
    }
}
```

Start with `dotnet run`. POST JSON `{"Id":7}` to `http://127.0.0.1:8877/api/echo` or `/api/manual`, using `Content-Type: application/json`; both return the JSON object with Id 7. An Id of -1 sent to `/api/echo` returns HTTP 400. `curl http://127.0.0.1:8877/binary/value/7` returns raw `binary:7` bytes with `application/octet-stream`, without JSON quotes or Base64 encoding. Ctrl+C cancels the server and exits its RunAsync loop gracefully.

For concrete POST commands, create `input.json` containing `{"Id":7}` and run:

```sh
curl -H "Content-Type: application/json" --data-binary '@input.json' http://127.0.0.1:8877/api/echo
curl -H "Content-Type: application/json" --data-binary '@input.json' http://127.0.0.1:8877/api/manual
```

Change the file to `{"Id":-1}` and repeat the echo request to see HTTP 400.

The /api and /binary paths are module base paths; the route attributes are relative to those paths. A fresh controller is created for each request. The delay demonstrates async execution, not a database, persistence, or SQLite thread-safety guarantee.

For a manually streamed binary response inside a JSON module, this partial method replaces a value-returning handler:

```csharp
[Route(HttpVerbs.Get, "/download")]
public async Task Download()
{
    var bytes = Encoding.UTF8.GetBytes("binary response");
    Response.ContentType = MimeType.Default;
    await HttpContext.SendDataAsync(ResponseSerializer.None(false), bytes);
}
```

## Controller and database resource lifetimes

Do not return the same mutable controller instance from a factory for overlapping requests: HttpContext and Route belong to each invocation. Share appropriately synchronized services, rather than the controller's context, and do not capture a request context for background work after the handler completes. Database connection concurrency and ownership still follow the database library's contract.

Legacy WithController/RegisterController overloads retain their inherited IDisposable timing. In that path, disposal occurs when the compiled handler invocation returns its Task, before the asynchronous operation necessarily completes. Existing compatibility tests intentionally preserve it; this correction does not change that lifetime contract. A controller that owns disposable resources across awaits should use the existing RegisterControllerWithContext API with a fresh instance and an awaited release callback. The module awaits handler and serialization completion before calling release, including failure paths. This core API requires no dependency-injection package.

Partial registration for a controller type that implements IDisposable and has a parameterless constructor:

```csharp
module.RegisterControllerWithContext(
    typeof(MyController),
    _ => new MyController(),
    (_, controller) =>
    {
        ((IDisposable)controller).Dispose();
        return Task.CompletedTask;
    });
```

An application retaining ownership can supply a no-op release callback instead. Keep only one disposal owner. See [dependency-injection lifetimes](../architecture/dependency-injection.md) for optional scoped activation and [streaming response closure](streaming-response-close.md) for the distinction between server cancellation and remote disconnect.

The test suite verifies isolated overlapping contexts, typed JSON/manual Task/raw binary routes on both listeners, preserved legacy disposal timing, awaited request-aware release, invalid double-write behavior and safe late response disposal during real keep-alive reuse. See [tracking issue #90](https://github.com/WilliamSmithEdward/embedio-neo/issues/90) for the attributed discussion. A remaining SQLite-specific failure needs the controller method, registration, return type, exact versions and a minimal reproduction; it is not diagnosed as GC disposal merely from this stack.
