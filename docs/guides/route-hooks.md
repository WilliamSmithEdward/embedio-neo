# Route hooks and enum statuses

These additions are unreleased after 2.2.0. Existing applications do not need to
change: unannotated actions still call `OnBeforeHandler()` exactly as before,
and all existing integer status APIs remain available.

## Select preprocessing per action

```csharp
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

[BeforeHandler(nameof(Prepare))]
public sealed class ItemsController : WebApiController
{
    public async Task Prepare(CancellationToken cancellationToken)
    {
        await Task.Yield(); // Replace with application work.
        cancellationToken.ThrowIfCancellationRequested();
        Response.Headers["X-Prepared"] = "yes";
    }

    public void PrepareHealth() => Response.Headers["Cache-Control"] = "no-store";

    [Route(HttpVerbs.Get, "/items")]
    public string Items() => "prepared";

    [Route(HttpVerbs.Get, "/health")]
    [BeforeHandler(nameof(PrepareHealth), Behavior = BeforeHandlerBehavior.Replace)]
    public void Health() => Response.SetEmptyResponse(HttpStatusCode.NoContent);

    [Route(HttpVerbs.Get, "/public")]
    [BeforeHandler(Behavior = BeforeHandlerBehavior.Skip)]
    public string Public() => "public";
}
```

| Selection | What runs before parameter binding |
| --- | --- |
| No attribute on action or class | Existing virtual `OnBeforeHandler()` |
| `Append` (attribute default) | `OnBeforeHandler()`, then named hook |
| `Replace` | Named hook only |
| `Skip` | Neither hook |

An action attribute replaces the class attribute, including inherited class
attributes. Attributes on overridden actions are inherited unless the override
supplies its own. `Append` therefore means append to `OnBeforeHandler()`, not
append another class attribute hook. The controller hook's default disables
response caching. `Replace` and `Skip` omit it; explicitly configure cache headers
appropriate to the endpoint, especially for user-specific responses.

Authentication, controller authorization, and preceding modules still run.
Authorization occurs before activation. After activation and setting `HttpContext`
and `Route`, the selected hooks complete before body/parameter binding and the
action. Throw `HttpException` to reject through normal error handling; other hook
errors follow the existing exception pipeline. Cancellation is checked before and
after hooks, and the optional token is the context's cancellation token. Hooks
must cooperate with cancellation; the framework does not forcibly stop user code.

Name one unambiguous public instance method using `nameof`. It must return `void`
or `Task`, and accept no parameters or one `CancellationToken`. Overloads,
generic methods, `async void`, `Task<T>`, `ValueTask`, ref parameters and null tasks
are unsupported. Use Skip without a method name. Invalid declarations fail at
reflection registration or with generator diagnostic `EMBEDIO014`; null tasks
fail when invoked. Hook declarations are read once at registration/build time.

Generated bindings call hooks directly and support trimming/Native AOT without
request-time reflection. Register the generated binding as described in the
[AOT guide](trimming-and-native-aot.md). Existing generated-binding constructors
retain their original controller preprocessing behavior.

Controller release follows existing registration semantics, including hook
failure/cancellation: ordinary registration disposes after binding/action invocation,
before awaiting asynchronous action completion/serialization. Use
`RegisterControllerWithContext` (or the dependency injection integration) for
awaited release after the complete request. Async hooks complete before either
release point; they do not change the legacy action lifetime contract.

## Use System.Net.HttpStatusCode

```csharp
context.Response.SetStatusCode(HttpStatusCode.Created);
context.Response.SetEmptyResponse(HttpStatusCode.NoContent);
context.Redirect("/items", HttpStatusCode.SeeOther);
await context.SendStandardHtmlAsync(HttpStatusCode.NotFound);
await context.SendStandardHtmlAsync(HttpStatusCode.Conflict, writer => writer.Write("Conflict details"));

// Only when the response implements the optional capability:
if (context.Response is IHttpResponseSections sections)
    await sections.SendInformationalAsync(HttpStatusCode.EarlyHints,
        new WebHeaderCollection { ["Link"] = "</site.css>; rel=preload" }, context.CancellationToken);
```

Configure status and headers before committing the response. These are separate
examples, not a sequence to send on one response. `ProblemDetails(HttpStatusCode)`,
`[OpenApiResponse(HttpStatusCode.Created)]` and
`operation.Response(HttpStatusCode.Created, description: "Created")` also accept
the enum. Exception/redirect-exception APIs already accepted it.

`IHttpResponse.StatusCode`, metadata status properties and the informational
capability's interface method remain integer APIs. Enum overloads delegate to the
existing integer path; they do not relax redirect/error/interim-status validation
or restrict custom numeric codes on APIs that already accept them. `SetStatusCode`
also accepts an integer and delegates to the response's existing setter.
