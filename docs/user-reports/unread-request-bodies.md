# Returning a response without reading the request body

The unreleased [modern engine increment](../project/http-engine.md) supersedes
this report's managed chunked-input and synchronous-read limitations, and tightens
ambiguous framing. Measurements below describe the earlier source; see the
[migration notes](../compatibility/migration.md#managed-http-framing-unreleased).

[Upstream #558](https://github.com/unosquare/embedio/issues/558), reported by bdurrer, described the fourth request in a .NET Framework HttpClient sequence failing with EmbedIO 3.4.3 when a POST controller ignored its body. Current source completes that sequence with both listeners. Applications do not need to read a fixed-length body merely to make the response work.

## Read when the application needs the data

Use the body helpers or `Request.InputStream` when the application needs the payload. Do not add a whole-body string allocation to every handler as a connection-reuse workaround. The managed listener already discards remaining fixed-length bytes after the response when reusing a connection; it uses temporary pooled storage rather than buffering the entire upload into a string. The native listener handles its own request lifecycle.

Early responses remain possible. A handler can return a response before the full fixed-length upload arrives. [RFC 9112 section 9.3](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.3) requires consuming the request body or closing the connection after the response. Reusing that connection therefore requires receiving/discarding the remaining body bytes; bytes cannot be skipped without receiving them from a network stream. A separate client can continue making requests, and stopping the server interrupts an incomplete upload. These checks do not establish an upload-size limit, a drain deadline or immunity to resource-exhaustion attacks.

The maintained behavior preserves streaming access and does not implement the upstream proposals to buffer every body before routing, spool uploads, remove listener access, or introduce new body-size defaults.

## Complete example using current source

This source-checkout example matches the maintained controller behavior. The regression coverage and investigation guide are new; they do not represent a newly repaired production defect or a new NuGet release.

```sh
dotnet new console --framework net10.0 --name UnreadBodyDemo --output TestResults/UnreadBodyDemo
dotnet add TestResults/UnreadBodyDemo/UnreadBodyDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/UnreadBodyDemo/Program.cs` completely:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(o => o
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager()
    .WithWebApi("/api", m => m.WithController<BodyController>());
Console.WriteLine("Listening on http://127.0.0.1:8877/; press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class BodyController : WebApiController
{
    [Route(HttpVerbs.Any, "/reading")]
    public async Task<string> Reading()
    {
        await HttpContext.GetRequestBodyAsStringAsync();
        return "response1";
    }

    [Route(HttpVerbs.Any, "/ignored")]
    public Task<string> Ignored() => Task.FromResult("response2");
}
```

Run `dotnet run --project TestResults/UnreadBodyDemo`. In another terminal:

```sh
curl http://127.0.0.1:8877/api/ignored
curl http://127.0.0.1:8877/api/reading
curl -H "Content-Type: application/json" --data "{}" http://127.0.0.1:8877/api/reading
curl -H "Content-Type: application/json" --data "{}" http://127.0.0.1:8877/api/ignored
```

Expect HTTP 200 and JSON strings `"response2"`, `"response1"`, `"response1"`, `"response2"`. The controller routes are exact paths underneath the `/api` mount. Separate curl processes illustrate the endpoints; the regression fixture and supplied legacy HttpClient exercise all four requests through one client. Press Ctrl+C in the server terminal to stop gracefully.

## Chunked uploads and framing limits

This fixed-length report must not be confused with chunked request support. The managed listener currently recognizes request bodies through a positive Content-Length and does not decode chunked request input. For chunked uploads, use `HttpListenerMode.Microsoft` or send a known Content-Length. The native listener's chunked-body reading and ignored-body response cases are covered separately. This guide does not claim managed chunked input has been implemented.

When reading data is necessary, use the declared body framing and the appropriate helper; do not assume every POST has data or every GET is bodyless. A successful response does not prove the application consumed or validated the upload. Do not read the underlying connection directly, and do not dispose a request stream to force protocol behavior.

## Investigation and validation

The [supplied proof of concept](https://github.com/bdurrer/embedio_post_test/tree/445088d571328b55aba822b8daa86e4310c761ae) was inspected at its pinned commit. Its controller and four-request HttpClient sequence passed against current source with both listeners. The legacy client was compiled against installed Windows .NET Framework assemblies, with diagnostic output redirected to Console, a shorter client timeout and fail-fast exceptions. The available runtime reports registry Version `4.8.09221`, Release `533509`; this is not a reproduction on the reporter's exact 4.7.2 runtime. The original 3.4.3 failure was not reproduced or attributed to a specific historical commit.

Twenty-six real-listener cases cover the original sequence with and without Expect: 100-continue, unread/partial/full fixed-length consumption, successful and early 403 responses, multiple requests, raw same-connection delayed uploads, independent healthy requests while an upload is incomplete, disconnect/stop, and native chunked uploads. Production code, defaults, public APIs and package dependencies are unchanged. Cross-platform CI verifies the cases before merge. If a maintained version still fails, provide the complete exception, package/runtime version, listener mode, request framing and a minimal client/server example so the investigation can be reopened.
