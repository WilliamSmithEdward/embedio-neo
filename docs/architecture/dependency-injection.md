# Dependency injection in EmbedIO-Neo

Dependency injection (DI) means giving a class the objects it needs through its
constructor. A controller asks for a service, such as a database repository,
and the application decides how to build that service. The controller does not
need to know connection strings, configuration files, or which database is used.

EmbedIO-Neo supports this through the optional
`EmbedIO-Neo.DependencyInjection` package. It adds one service scope per HTTP
request, constructor injection, explicit service arguments, and Generic Host
support. Existing servers and controller factories continue to work without it.
The integration was requested in [feature #32](https://github.com/WilliamSmithEdward/embedio-neo/issues/32).

## Availability and prerequisites

The adapter builds for .NET Standard 2.0 and .NET 10, matching the core library.
It references Microsoft DI and Hosting.Abstractions 10.0.12. Those dependencies,
including the async-interface compatibility packages needed by .NET Standard,
belong to the optional adapter. Installing only the core adds none of them.

This guide describes the source implementation. Do not assume that this package
has been published to NuGet. To try the checkout, add a project reference from
an application to `src/EmbedIO.DependencyInjection/EmbedIO.DependencyInjection.csproj`.
Once a release publishes the adapter, use its matching package version.

The examples below use a .NET 10 console app with implicit usings enabled.
Add these namespaces:

```csharp
using EmbedIO;
using EmbedIO.DependencyInjection;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Microsoft.Extensions.DependencyInjection;
```

## A complete small application

This application exposes `GET http://localhost:9696/api/greeting?name=Sam`.
The controller receives its service through its constructor. The service is
created once per request.

```csharp
var registrations = new ServiceCollection();
registrations.AddScoped<GreetingService>();

await using var services = registrations.BuildServiceProvider(
    new ServiceProviderOptions
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    });

using var server = new WebServer("http://localhost:9696/")
    .WithDependencyInjection(services)
    .WithWebApi("/api", api => api.WithControllerServices<GreetingController>());

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    stopping.Cancel();
};

Console.WriteLine("Open http://localhost:9696/api/greeting?name=Sam");
try
{
    await server.RunAsync(stopping.Token);
}
finally
{
    await server.DrainDependencyInjectionAsync();
}

public sealed class GreetingService
{
    public string Greet(string name) => $"Hello, {name}!";
}

public sealed class GreetingController : WebApiController
{
    private readonly GreetingService _greetings;

    public GreetingController(GreetingService greetings)
    {
        _greetings = greetings;
    }

    [Route(HttpVerbs.Get, "/greeting")]
    public string Get([QueryField] string name) => _greetings.Greet(name);
}
```

The response is the JSON string `"Hello, Sam!"`. `WithDependencyInjection` must
appear before modules that use DI: EmbedIO executes modules in registration
order. The helper does not rearrange existing routes or modules.

Keep the root provider alive until request cleanup finishes.
`DrainDependencyInjectionAsync` is for manual hosting: call it after the server
has stopped and its `RunAsync` task has completed. It prevents new DI scopes and
waits for already active scopes to finish cleanup. It does not stop the listener
for you. A server that has been drained should be replaced with a new server
when restarting. The provider can be reused if the application still owns it.

## Choosing service lifetimes

A service registration controls how often the container creates it:

| Registration | Lifetime | Typical use |
| --- | --- | --- |
| `AddSingleton<T>()` | One object for the provider's lifetime | Thread-safe application state, immutable settings |
| `AddScoped<T>()` | One object within each request scope | Database unit of work, request-specific calculations |
| `AddTransient<T>()` | A new object each time it is resolved | Small stateless helpers |

Two modules and a controller asking for the same scoped service during one
request get the same object. Two overlapping requests get different scoped
objects. Singleton services are shared and must support concurrent access.

Use scope validation in development. A singleton must not capture a scoped
service in its constructor. Resolving a scoped service from the root provider
also bypasses the request lifetime. Validation helps catch both mistakes.

Register the controller's dependencies, then use `WithControllerServices<T>()`
to register the controller's routes. You do not register controllers in the
container. The adapter always constructs a fresh controller with
`ActivatorUtilities`, even if somebody separately registered that controller
as a singleton. This keeps the controller's mutable `HttpContext` and `Route`
separate across requests and gives its disposal one owner.

For a controller discovered at runtime, use:

```csharp
api.WithControllerServices(typeof(GreetingController));
```

Controllers need a concrete, closed type and a public constructor that Microsoft
DI can select. An ambiguous constructor selection can fail during route
registration. A missing constructor dependency fails during activation and
passes through EmbedIO's normal error handling; it is not silently replaced
with null. Registering routes does not build controllers or execute their
constructors at startup.

## Accessing the current request

A controller already has `HttpContext` during its action. A service may also need
that context, for example to read a correlation header. Register the context
bridge before building the provider:

```csharp
registrations.AddEmbedIORequestContext();
registrations.AddScoped<RequestDetails>();

public sealed class RequestDetails
{
    public RequestDetails(IHttpContext context)
    {
        RequestId = context.Id;
    }

    public string RequestId { get; }
}
```

The bridge makes `IHttpContext` a scoped dependency. It is populated before the
pipeline resolves request services. It is not an ambient or global variable.
Resolving it outside an EmbedIO request fails. Do not store it in a singleton,
use it after the request finishes, or capture it in background work.

Without this bridge, ordinary service injection still works; only services
asking for `IHttpContext` need that registration. `AddEmbedIO` for Generic Host
adds the bridge automatically.

Custom modules have the context already. They can explicitly obtain request
services:

```csharp
var greetings = context.GetRequestServices()
    .GetRequiredService<GreetingService>();
```

Constructor injection is usually clearer for controllers because their needs
are visible in one place. For a dependency used by just one action, explicit
service-argument injection is also available:

```csharp
[Route(HttpVerbs.Get, "/greeting")]
public string Get(
    [QueryField] string name,
    [FromServices] GreetingService greetings) => greetings.Greet(name);
```

`[FromServices]` uses the same request provider as constructor injection. Existing
route, query, form, and JSON binding keeps its behavior. Unannotated arguments
are not automatically treated as services. Do not combine competing binding
attributes on the same parameter.

## What happens during a request

1. The DI module creates a scope and places its provider in `IHttpContext.Items`.
2. Other modules can resolve services from that provider.
3. The controller adapter constructs a fresh controller using that provider.
   EmbedIO then sets the controller's `HttpContext` and `Route`.
4. EmbedIO binds arguments, runs the action, and awaits its result and serializer.
   If processing fails, normal error handling runs while services are still alive.
5. After the response flush attempt, awaited cleanup releases controllers and
   then the scope. The context is subsequently closed by the existing pipeline.

Cleanup is attempted even if activation, binding, the action, serialization,
or flushing fails, or cancellation is requested. A constructor that throws
never produces a controller to dispose; any dependencies already tracked by
the request scope are still released.

The adapter prefers `IAsyncDisposable.DisposeAsync` when an object implements
it, otherwise `IDisposable.Dispose`. It does not call both. Callbacks execute in
reverse registration order, so controllers finish cleanup before their scoped
dependencies. Each registered cleanup callback is awaited once. Cleanup failures
are logged through EmbedIO diagnostics and do not prevent remaining callbacks
from running. They cannot reliably change a response that has already been sent.

Ordinary DI ownership rules still apply. A controller must not dispose an
injected service that the scope owns. If you register an existing singleton
instance yourself, you own that instance according to Microsoft's DI rules.
The DI module never disposes the root provider.

## Running with .NET Generic Host

Generic Host coordinates DI, configuration, logging, and application shutdown.
It is convenient for a console server or a worker process that already uses
Microsoft.Extensions.Hosting. The adapter depends on Hosting.Abstractions; the
application supplies the full `Microsoft.Extensions.Hosting` package. The
example can be built with version 10.0.12.

Replace the manual startup above with this code and keep the same service and
controller classes:

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped<GreetingService>();

var prefix = builder.Configuration["EmbedIO:UrlPrefix"]
    ?? "http://localhost:9696/";

builder.Services.AddEmbedIO(
    options => options.WithUrlPrefix(prefix),
    (server, rootServices) => server.WithWebApi("/api",
        api => api.WithControllerServices<GreetingController>()));

using var host = builder.Build();
await host.RunAsync();
```

`AddEmbedIO` constructs a host-owned server and installs the DI module before
calling your configuration callback. Startup waits until the listener and
modules report ready. Startup failures propagate to the host. Unexpected
listener termination asks the host to stop; faults are also logged with
`ILogger`. HTTP request errors continue through EmbedIO's existing handlers.

On shutdown, the adapter cancels the listener, awaits its run task, drains
request scopes, and disposes the server. It does not dispose the host's service
provider. The adapter's server is not separately registered as an `IWebServer`
singleton; this avoids giving the same object two disposal owners.

The configuration callback receives the root provider for long-lived
application dependencies. Do not resolve request-scoped services there. Build
modules yourself, or use `ActivatorUtilities.CreateInstance<MyModule>(rootServices)`
with dependencies suitable for application lifetime, and add them with
`WithModule`. The server owns added disposable modules. Do not also resolve a
container-owned disposable module and give it to the server: the existing module
collection would dispose it as well. Modules should not dispose their injected
container-owned dependencies.

Configuration is read when the server is constructed. This integration does not
reload listener options while running. Use normal application configuration or
`IOptions<T>` in your own services where appropriate. Core diagnostic messages
still use `TraceSource`; this adapter logs hosting faults but does not replace
all EmbedIO diagnostics with Microsoft logging.

Long-running actions should observe `WebApiController.CancellationToken`.
A host shutdown timeout can stop waiting, but it cannot safely force arbitrary
application code to finish. Final adapter disposal still waits for cleanup;
an action or disposal method that never completes can prevent orderly shutdown.

## Existing code and other containers

The new core hooks are additive. `IHttpContext`, `IWebServer`, existing public
constructors, listener defaults, and core package dependencies are unchanged.
The new package can be omitted entirely.

`RegisterController()` and the older parameterless controller-factory overloads
retain their original behavior. In particular, the old expression-based path
can dispose an `IDisposable` controller before its asynchronous action finishes
and does not perform async-only disposal. This implementation does not silently
change that lifetime contract. Use `WithControllerServices` to opt into the new
request lifetime. Do not return a shared controller from an old factory.

A different container can use the core's context-aware hook without installing
the Microsoft adapter:

```csharp
api.RegisterControllerWithContext(
    typeof(MyController),
    context => CreateControllerFromYourRequestScope(context),
    (context, controller) => ReleaseControllerAsync(controller));
```

Those two functions are application-defined. The release callback is awaited
in a `finally` after argument binding, invocation and serialization. If the
container owns the controller, return `Task.CompletedTask` there and release
its scope at request completion instead. Never give both the scope and the
release callback ownership of the same controller.

`context.OnRequestCompleted(Func<Task>)` is the core hook for awaited cleanup.
It does not require a new interface member from applications implementing
`IHttpContext`. Register callbacks during request processing; registering during
or after cleanup is rejected for contexts that have entered that cleanup path.
The base server invokes these callbacks. A custom server that replaces the
entire context-processing pipeline must implement its own completion boundary;
the extension alone cannot make a foreign pipeline execute callbacks.

## Platform and protocol boundaries

The service-registration model works independently of the selected listener.
It does not add Kestrel or change TLS configuration. Desktop regression coverage
and MAUI smoke coverage have different meanings: successful desktop tests do
not establish full DI hosting behavior inside a particular mobile app model.

For MAUI, keep the provider and server at application lifetime, and stop/drain
the server before releasing the provider. An activity or page should not own
an application-wide listener. Generic Host is optional; the manual setup works
without introducing a host into the UI application. Follow the application's
platform lifecycle and networking requirements.

The adapter creates HTTP request scopes, not a DI scope per WebSocket message.
Do not retain HTTP-scoped services or controllers in background tasks or
WebSocket handlers after the HTTP pipeline completes. A connection or message
that needs its own scope should explicitly create and dispose one with
`IServiceScopeFactory`.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| "Request services are unavailable" | Install `WithDependencyInjection` before the API/action module. Do not use the context after completion. |
| A constructor dependency cannot be resolved | Register it before building the provider, and check its public constructor and lifetime. |
| More than one constructor is applicable | Use one injectable constructor or Microsoft's `ActivatorUtilitiesConstructor` selection attribute. |
| `IHttpContext` is unavailable | Add `AddEmbedIORequestContext` before building the provider and resolve it only inside an HTTP request. |
| A scoped dependency is being resolved at the root | Move resolution into a controller or request module. Keep validation enabled. |
| A resource is disposed twice | Establish one owner. Let the adapter dispose controllers and the scope dispose their injected dependencies. |
| Shutdown waits indefinitely | Check that actions observe cancellation and that async cleanup completes. |
| Old factories still show early disposal | Opt into `WithControllerServices` or the explicit context-aware activation/release hook. |

See [Microsoft's DI guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines)
for service ownership and disposal rules, and
[ActivatorUtilities](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.activatorutilities)
for constructor selection.
