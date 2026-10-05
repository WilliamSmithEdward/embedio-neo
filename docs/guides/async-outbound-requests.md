# Awaiting an outbound HTTP request in a Web API controller

EmbedIO-Neo supports asynchronous controller methods. Declare a JSON-producing
route as `Task<TResponse>` and return the completed application response. The
Web API dispatcher awaits that task before passing its result to the configured
response serializer.

```csharp
using System;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

public sealed class TerminalsController : WebApiController
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;

    public TerminalsController(HttpClient client, Uri endpoint)
    {
        _client = client;
        _endpoint = endpoint;
    }

    [Route(HttpVerbs.Get, "/getterminals")]
    public async Task<TerminalsResponse> GetTerminals()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        using var response = await _client.SendAsync(request, CancellationToken);
        response.EnsureSuccessStatusCode();
        return new TerminalsResponse
        {
            Result = true,
            Terminal = await response.Content.ReadAsStringAsync(),
        };
    }
}

public sealed class TerminalsResponse
{
    public bool Result { get; set; }
    public string Terminal { get; set; } = string.Empty;
}
```

Register the controller with `WithWebApi("/v1", module =>
module.WithController(() => new TerminalsController(client, endpoint)))`.
Keep the injected `HttpClient` alive for the server's lifetime and give it an
appropriate timeout; dispose it after the server has stopped. Dispose each
request/response after its asynchronous work finishes. Use a trusted, configured
endpoint rather than accepting an arbitrary destination from a request.

`EnsureSuccessStatusCode()` raises `HttpRequestException` for unsuccessful
outbound HTTP statuses. Decide how your application should map that error;
for example, catch it, set `HttpContext.Response.StatusCode = 502`, and return
a response DTO with a safe error message. `SendAsync` does not itself reject
HTTP error statuses. Timeout/cancellation and transport failures need their own
application policy; do not expose credentials or raw exception details in replies.

## Return the result, rather than an asynchronous-operation wrapper

- A route declared `Task<TResponse>` gets automatic result serialization.
- A route declared `Task` must write its response explicitly, for example
  `await HttpContext.SendDataAsync(result)` after awaiting its outbound call.
- Do not use `async void` for request handlers or launch unawaited work that
  writes the response after the handler has returned.
- Do not hide a `Task<T>` behind an `object` return type or put that task in a
  DTO's `Result` property. The declared route return type drives task handling;
  a nested task is ordinary data to the serializer, not an instruction to await.
- Return your application DTO, not `HttpResponseMessage`, `HttpClient`, or a
  task object. Read or deserialize the upstream content into the intended DTO.
- Avoid `.Result` and `.Wait()` in the request path. Await the operation directly.

## Diagnosing the original empty response

[Upstream #598](https://github.com/unosquare/embedio/issues/598), reported by
andraschris, shows two completed HTTP 200 responses: a populated object when
returning before `SendAsync`, and `{}` when executing the outbound request.
The populated screenshot also contains a wrapper with a nested `Result` and
`DebuggerDisplayResultDescription` / `DebuggerDisplayMethodDescription`
properties. Inspect what object is actually being returned or sent to the
serializer. These screenshots do not establish a hang, a particular wrapper
implementation, or a confirmed EmbedIO defect.

Please include the following in a minimal reproduction:

1. The complete route signature, route registration, serializer configuration,
   DTO definitions, and all return paths around the outbound request.
2. EmbedIO and .NET versions, host/runtime, and any relevant exception/stack trace.
3. A sanitized outbound request and local mock response, including status and
   body. Remove tokens, API keys, personal data, and private endpoint details.
4. The incoming response status, headers and body, and whether the awaited
   operation completes. Log before and after the await to locate the boundary.

The fork's investigation is tracked in [issue #18](https://github.com/WilliamSmithEdward/embedio-neo/issues/18).
The support question is closed as answered after the supported async pattern was
documented and verified. The original controller and outbound service were not
provided, so the exact empty-response cause is unconfirmed. This guide does not
claim that the reporter's application was reproduced or repaired. A minimal
reproduction can support reopening the issue if the behavior persists.

Six integration cases use a real EmbedIO listener and a second HTTP request to
a gated local backend. They verify that a genuinely suspended `SendAsync` finishes
before the JSON response is sent, for both automatic `Task<T>` serialization and
explicit `Task` response writing, with buffered and chunked responses. Two of the
six cases verify explicit JSON/status mapping for an outbound HTTP 503. They do
not reproduce the reporter's external service, timeout, or cancellation behavior.
Production APIs and serialization defaults are unchanged.
