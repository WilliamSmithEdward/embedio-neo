# Choose controller-route case matching

[Upstream #521](https://github.com/unosquare/embedio/issues/521) asks why a
`/GetTest/{id?}` controller route returns 404 for `/gettest/1`. Routing remains
case-sensitive by default. The additive `WebApiModule.CaseInsensitiveRoutes`
setting lets an application opt one API module into case-insensitive literal
controller-route matching without rewriting its request data.

gabriele-ricci-kyklos supplied the original example. rdeago
[recommended a careful module-local option](https://github.com/unosquare/embedio/issues/521#issuecomment-846011557)
because filesystem names, culture and IDs must not be treated as interchangeable.
captainjono [requested configuration flexibility](https://github.com/unosquare/embedio/issues/521#issuecomment-855552609)
instead of a source fork. MIME-provider injection and pre-request callbacks are
tracked separately in [the customization issue](https://github.com/WilliamSmithEdward/embedio-neo/issues/129).

## Enable the option before controller registration

The new setting is available in the source change; it is **not in an existing
NuGet release yet**. Until an authorized release includes it, build against this
repository's source. With the repository checked out and its selected .NET SDK
installed, run from the repository root:

```sh
dotnet new console --framework net10.0 --output TestResults/route-case-demo --no-restore
dotnet add TestResults/route-case-demo/route-case-demo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace that project's `Program.cs` with this complete program:

```csharp
using System;
using System.Globalization;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var api = new WebApiModule("/api") { CaseInsensitiveRoutes = true };
api.RegisterController<TestController>();
using var server = new WebServer(options => options
    .WithUrlPrefix("http://localhost:8877/").WithMode(HttpListenerMode.EmbedIO))
    .WithModule(api);
Console.WriteLine("Try /api/gettest/1. Press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class TestController : WebApiController
{
    [Route(HttpVerbs.Get, "/GetTest/{id?}")]
    public string GetTest(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "GetTest success [id=]!";
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw HttpException.BadRequest("id must be an integer.");
        return $"GetTest success [id={number}]!";
    }
}
```

The example binds the optional value as text and validates it explicitly, giving
bad input a deliberate 400 response. That is application validation, not a new
library binding policy. The original valid `int? id` handler also works with the
opt-in setting; automatic invalid nullable-integer conversion currently returns
500 in both default and opted-in routing, and this change does not repair that
separate behavior.

Run the program, then issue requests from another terminal:

```sh
dotnet run --project TestResults/route-case-demo/route-case-demo.csproj
```

```sh
curl http://localhost:8877/api/gettest/1
curl http://localhost:8877/api/GETTEST
curl -i http://localhost:8877/api/gettest/not-an-integer
```

Expect JSON strings `"GetTest success [id=1]!"` and
`"GetTest success [id=]!"`, followed by 400 for the invalid input. On Windows
PowerShell, use `curl.exe`. Stop with Ctrl+C; cancellation is passed to RunAsync
and the app-owned server is disposed.

## What changes, and what stays case-sensitive?

| Surface | Behavior |
| --- | --- |
| Literal controller routes in this opted-in module | `/GetTest`, `/gettest` and `/GETTEST` match |
| Module mount `/api` | Still case-sensitive; `/API/gettest/1` does not match this module |
| Captured values and parameter names | Retain existing decoding, spelling and binding; no lowercasing |
| Query names/values, request body, raw URL | Not rewritten by the option |
| Other API modules, public RouteMatcher.Parse and attributes | Retain their existing default/cache behavior |
| Static file names and filesystem lookup | Unchanged; this is not a filesystem case policy |

Matching uses the existing regex route grammar with IgnoreCase and CultureInvariant,
as described in [Microsoft's regex options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options).
It is independent of the current thread's culture. It is not ordinal string
comparison, Unicode normalization or a promise that every Unicode character is
interchangeable across all runtime Unicode tables. Values such as `AbC`, `İIıi`
and `XyZ` reach handlers with their existing spelling.

Optional segments, controller BaseRoute attributes, escaped literal punctuation
and HTTP verb selection retain their existing semantics. Configure the setting
before registering any controller; changing it afterward throws, and startup
locks it like other module configuration. A module-local compiled matcher is
used, leaving shared attribute/public parser instances case-sensitive.

## Avoid ambiguous routes and align authorization

When opted in, case-equivalent textual templates for overlapping verbs must not
select different handlers or controller factories. Registration rejects these
conflicts before adding any of that controller's handlers. Case-only aliases on
one method with identical parameter names are deduplicated; distinct GET/POST
handlers remain supported. Aliases may not silently rename captured parameters.

This guard does not solve every possible overlap between different templates.
Existing broader parameter-versus-literal overlaps still use registration order.
Prefer one unambiguous handler for each route/verb.

Protect the entire API mount with appropriate authentication middleware, or enforce
authorization in the controller's pre-handler hook using the selected operation.
Do not allow case-insensitive endpoints while protecting only one case-sensitive
spelling of a terminal path. The option does not automatically rewrite an
application's raw-path authorization comparisons. See
[authorization by route and verb](route-authorization.md).

If you need only a few alternate spellings with already released APIs, explicit
Route attributes on the same method remain an option. They match those declared
spellings, not every combination of letter case. Global lowercase middleware is
not required and could alter IDs, query values or file paths.

## Validation

Real managed/native listener tests cover default and opted-in matching, the original
optional integer handler, mixed-case captured/query/body values and raw URLs,
Unicode values under English/Turkish cultures, escaped punctuation, base routes,
wrong verbs, other-module/mount isolation and base-scoped authentication. Additional
cases cover cache identity, configuration locking, case/Any-verb conflicts,
same-handler aliases, renamed captures and inherited factories. The default
parser and existing signatures/targets/dependencies are preserved.
