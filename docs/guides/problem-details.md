# Problem details for API errors

By default Neo v2 answers unhandled application exceptions with an empty 500 and
application-authored HTTP errors with the configured HTTP error handler. For an HTTP API, `ProblemDetailsHandler`
answers them with [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details instead: a
JSON object sent as `application/problem+json`, which API clients and tools understand.

It is opt-in. Without it, nothing changes.

This is new in Neo v2; the published 1.0.x packages do not include it.

## A complete program

```csharp
using System;
using System.Text.Json.Nodes;
using System.Threading;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

using var server = new WebServer("http://localhost:9696/")
    .WithProblemDetails()
    .WithModule(new WebApiModule("/api").WithController<OrdersController>());

Console.WriteLine(new Uri("http://localhost:9696/api/orders/1"));
await server.RunAsync(cancellation.Token);

public sealed class OrdersController : WebApiController
{
    [Route(HttpVerbs.Get, "/orders/{id}")]
    public string Get(int id) => id switch
    {
        1 => "order 1",
        2 => throw new ProblemDetailsException(new ProblemDetails(409)
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Out of stock",
            Detail = "Order 2 cannot be filled.",
        }.WithExtension("missing", new JsonArray("widget"))),
        3 => throw new InvalidOperationException("Database password rejected"),
        _ => throw HttpException.NotFound($"No order {id}."),
    };
}
```

| Request | Response |
| --- | --- |
| `GET /api/orders/1` | 200, `"order 1"` (the result, serialized as JSON) |
| `GET /api/orders/9` | 404, `{"title":"Not Found","status":404,"detail":"No order 9.","requestId":"..."}` |
| `GET /api/orders/abc` | 400, `{"title":"Bad Request","status":400,"detail":"Invalid route parameter value.","requestId":"..."}` |
| `GET /api/orders/2` | 409, the thrown problem with its `type`, `title`, `detail`, `missing` and a `requestId` |
| `GET /api/orders/3` | 500, `{"title":"Internal Server Error","status":500,"detail":"An unexpected error occurred.","requestId":"..."}` |

The 500 response says nothing about the exception. EmbedIO logs it, with the same ID as
`requestId`, so a client's report leads to the log entry.

## Installing the handlers

`WithProblemDetails(options)` sets a web server's `OnHttpException` and `OnUnhandledException`. To
use problem details for part of a server only, set them on a module instead:

```csharp
var api = new WebApiModule("/api").WithController<OrdersController>();
api.HandleHttpException(ProblemDetailsHandler.ForHttpExceptions());
api.HandleUnhandledException(ProblemDetailsHandler.ForUnhandledExceptions());
```

## What becomes a problem

- **HTTP exceptions** (`HttpException`, `throw HttpException.NotFound(...)` and the like) become a
  problem with the exception's status, the status's reason phrase as `title`, and the
  exception's message as `detail`.
- **Binding failures** in Web API controllers are HTTP exceptions with status 400, so they become
  400 problems whose `detail` names the field: `Cannot convert field limit to Int32.`,
  `Missing query field name.`, or for a JSON body that does not parse,
  `Expected request body to be deserializable to MyApp.Order.`
- **`ProblemDetailsException`**, or any HTTP exception whose data object is a `ProblemDetails` with
  the same status, is sent as is. Use it for problem types of your own, with extension members
  such as validation errors. The thrown instance is not changed, so it can be a shared value.
- **Unhandled exceptions** become a 500 problem saying only that an unexpected error occurred.
- **Redirects** and other HTTP exceptions with a status below 400 keep their headers, such as
  `Location`, and get no body.

Other data objects attached to HTTP exceptions are not sent: they would need reflection to
serialize and might hold more than the client should see.

`context.SendProblemAsync(problem)` sends a problem from a handler without throwing.

## Options

| Option | Default | Effect |
| --- | --- | --- |
| `IncludeHttpExceptionMessages` | `true` | Send HTTP exception messages as `detail`. They are written for clients, as with EmbedIO's other handlers, though binding messages name .NET types. |
| `IncludeExceptionDetails` | `false` | For unhandled exceptions, send the message as `detail` and an `exception` member with the type, message and stack trace. For development only. |
| `Customize` | `null` | A callback that adjusts each problem, for example to set `Type` or add extensions. It gets a copy. |
| `HtmlHttpExceptionHandler`, `HtmlExceptionHandler` | EmbedIO's HTML pages | What clients that prefer HTML get; `null` sends problem details to everyone. |

## Content negotiation

A request gets problem details unless its `Accept` header prefers HTML over every JSON media
range (`application/problem+json`, `application/json`, `application/*`, `*/*`), or accepts no JSON
at all. So API clients, and requests without `Accept`, get JSON, while a browser, whose
`Accept` puts `text/html` first, gets the HTML page.

## Problem objects

`new ProblemDetails(status)` takes a status from 400 to 599. `Type`, `Title`, `Detail` and `Instance`
are optional. Leaving `Type` unset means `about:blank`, and the status's reason phrase becomes
the `title`; with a `Type`, set your own `Title`. `WithExtension(name, value)` adds members, whose
values are `JsonNode`s, so writing them needs no reflection and works in trimmed and Native AOT
applications. The names RFC 9457 defines cannot be used for extensions, and a name can be added
once.

Every problem the handlers send carries `requestId`, unless it already has one.

## Responses already sent

A problem replaces the response only while nothing of it has been sent. If a handler fails after
the status and part of the body have gone out, for example while streaming, EmbedIO aborts the
response instead, as it does without problem details; the client sees an incomplete response,
not a problem. Neither the handlers nor EmbedIO can turn a response already under way into an
error.

## Migrating

Nothing changes until you install the handlers. Clients that parse the HTML error pages should
not see a difference while their `Accept` header prefers HTML. Clients that receive JSON errors
now get `application/problem+json` instead of the HTML page; check that they read the status
code, which is unchanged.
