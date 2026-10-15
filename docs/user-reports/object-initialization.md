# Object initialization and fluent setup

[Neo #50](https://github.com/WilliamSmithEdward/embedio-neo/issues/50) evaluates
the roadmap item from [upstream #546](https://github.com/unosquare/embedio/issues/546):
replace fluent setup methods with object initialization. Both styles already work
today and produce the same server. This guide shows them side by side, lists the
helpers that stay method-based and why, and records the decision to keep both.

## The same server, two ways

```csharp
// Fluent
using var fluent = new WebServer(o => o.WithUrlPrefix("http://localhost:9696/").WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager(sessions => sessions.CookieName = "app")
    .WithModule("api", new ActionModule("/api", HttpVerbs.Get, Api), module => module.OnUnhandledException = ExceptionHandler.EmptyResponse)
    .WithModule("files", new FileModule("/", new FileSystemProvider(root, true)).WithDefaultDocument("index.html").WithoutContentCaching());
fluent.OnHttpException = HttpExceptionHandler.PlainTextResponse;

// Object and collection initializers
var options = new WebServerOptions { Mode = HttpListenerMode.EmbedIO };
options.AddUrlPrefix("http://localhost:9696/");
using var initialized = new WebServer(options)
{
    SessionManager = new LocalSessionManager { CookieName = "app" },
    OnHttpException = HttpExceptionHandler.PlainTextResponse,
    Modules =
    {
        { "api", new ActionModule("/api", HttpVerbs.Get, Api) { OnUnhandledException = ExceptionHandler.EmptyResponse } },
        { "files", new FileModule("/", new FileSystemProvider(root, true)) { DefaultDocument = "index.html", ContentCaching = false } },
    },
};
```

`Modules` is enumerable and has an `Add(name, module)` method, so C# accepts the
nested collection initializer. Unnamed modules use the single-argument form,
`Modules = { module }`. `BasicAuthenticationModule.Accounts` accepts an index
initializer, `Accounts = { ["user"] = "password" }`.

## Comparison

| Aspect | Fluent | Initializers | Measured |
| --- | --- | --- | --- |
| Validation | Each helper calls the same public setter or `Modules.Add` | Calls the setter or `Add` directly | Same exception type, message and parameter name; the fluent trace adds one helper frame |
| Defaults | Untouched properties keep their defaults | Same | Equal property values; `Without*` helpers equal assigning `null` |
| Registration order | Order of `WithModule` calls | Order of initializer entries | First registered module handles overlapping routes in both |
| Duplicate registration | `ArgumentException` for a repeated module or name | Same | Same `ParamName` (`component`, `name`) |
| Locking | Settings lock at the same points | Same | See [configuration locking](configuration-locking.md) |
| Debugging | A failing chain points at the line of the whole expression | A failing initializer member points at the member | Stack traces show `set_Property` in both |
| Discoverability | `With` completion lists module-specific helpers, including registration helpers such as `WithWebApi` and `WithAction` | Completion lists settable properties | Both are discoverable; neither is a superset |

`Issue50_ObjectInitialization` builds the server above both ways and requires
identical status, content type, body and session cookie for an API route, an API
failure handled by the module, the default document, a static file and a missing
file. It also covers registration order, duplicates, validation through a setter
and through its helper, defaults, and Basic authentication with and without
credentials.

## Helpers that stay methods

Some setup is registration rather than assignment, and has no property to
initialize:

- URL prefixes: `AddUrlPrefix` validates each prefix and rejects duplicates.
  `WebServerOptions` is not a collection, so prefixes cannot appear inside its
  initializer; call `AddUrlPrefix` or use `WithUrlPrefix(es)`, or pass prefixes to
  a `WebServer` constructor.
- Controllers: `WebApiModule.RegisterController` and its `WithController` helpers.
- Composite registrations that construct a module for you: `WithAction`,
  `WithWebApi`, `WithStaticFolder`, `WithRouting`, `WithBasicAuthentication`,
  `WithIPBanning`.
- Custom MIME types: `AddCustomMimeType` and `WithCustomMimeType`.

These are as reachable from an initializer-style program as from a chain: create
the module, configure it, and add it to `Modules`.

## Decision

No API is added or removed. The concrete task the issue asked for, a setup that
remains difficult after the current guides, was not found: every configurable
property is settable, modules and accounts accept initializers, and the
method-only helpers are registrations that a property could not express without a
second, parallel API. Fluent helpers remain the style the getting-started guides
use, and object initializers remain fully supported alongside them.
