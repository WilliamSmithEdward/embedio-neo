# HEAD responses: metadata without a body

[Upstream #564](https://github.com/unosquare/embedio/issues/564), reported by Warpten, showed a void HEAD controller assigning Content-Length but the managed listener sending zero. The earlier [static-file HEAD correction](https://github.com/WilliamSmithEdward/embedio-neo/issues/60) already corrected the original managed metadata-only reproduction in current source. Additional investigation found HEAD stream writes leaking body/chunk bytes into subsequent responses, native fixed-length writes throwing a protocol exception, and native header-only length assignments resetting a connection.

## Current source behavior

Both listeners now discard HEAD response body writes at the transport wrapper while preserving normal argument, cancellation, disposal and asynchronous completion behavior. No new HEAD-specific stream exception is introduced. Native HEAD responses also synchronize direct Content-Length assignments with the native framing field, preserving the last property/header assignment. Non-HEAD response handling and existing connection-close policies are retained.

These corrections are **unreleased** and unavailable in NuGet 1.0.1. Public APIs, target frameworks and dependencies are unchanged. The upstream suggestion to forbid output-stream access is not adopted: that would introduce a separate compatibility change.

[RFC 9110 section 9.3.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.3.2) requires a HEAD response to have no content. An advertised Content-Length describes the corresponding GET representation, not the number of bytes sent for HEAD. When computing representation-dependent fields would require generating the content, such fields may be omitted. [RFC 9112 section 6.3](https://www.rfc-editor.org/rfc/rfc9112.html#section-6.3) explains that HEAD has no message body even if length or transfer-coding metadata is present; no payload or terminating chunk may appear after the header section.

## A complete GET and HEAD controller

From a checkout containing the corrections:

```sh
dotnet new console --framework net10.0 --name HeadDemo --output TestResults/HeadDemo
dotnet add TestResults/HeadDemo/HeadDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/HeadDemo/Program.cs` completely:

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
    .WithWebApi("/", module => module.WithController<GreetingController>());
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Open http://127.0.0.1:8877/greeting; Ctrl+C stops the server.");
await server.RunAsync(stop.Token);

public sealed class GreetingController : WebApiController
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("Hello café");

    private void PrepareMetadata()
    {
        Response.ContentType = "text/plain; charset=utf-8";
        Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
        Response.ContentLength64 = Payload.LongLength;
    }

    [Route(HttpVerbs.Get, "/greeting")]
    public async Task Get()
    {
        PrepareMetadata();
        await Response.OutputStream.WriteAsync(Payload, 0, Payload.Length, HttpContext.CancellationToken);
    }

    [Route(HttpVerbs.Head, "/greeting")]
    public void Head() => PrepareMetadata();
}
```

Run it and make requests from another terminal:

```sh
dotnet run --project TestResults/HeadDemo
```

```sh
curl -i http://127.0.0.1:8877/greeting
curl -I http://127.0.0.1:8877/greeting
```

Both return `200`, `Content-Type: text/plain; charset=utf-8` and `Content-Length: 11`. GET sends `Hello café` as 11 UTF-8 bytes; HEAD sends no body. This example writes original bytes directly without compression, so the representation length is the same for both verbs. Ctrl+C cancels the listener and lets RunAsync finish. The `/` module base route includes child paths, while `/greeting` is the exact controller route.

The GET method returns non-generic Task because it writes the response itself. The void HEAD method supplies only metadata. Neither result is serialized by the default Web API serializer. Returning an object or Task<T> instead invokes the module serializer; buffered serializers calculate a representation length and can replace a manually assigned length. See [response ownership](concurrent-controller-responses.md) for those contracts.

## Assigning length and avoiding side effects

- Prefer `Response.ContentLength64` for explicit length metadata. In the corrected HEAD paths, direct `Headers.Set(HttpHeaderNames.ContentLength, "123")` also works, and the last property/header assignment wins. Do not infer that direct header mutation synchronizes every native framing field for other HTTP methods.
- Length is a byte count. Text character counts can differ from UTF-8 byte counts, and compressed length differs from original length. Calculate or cache the actual corresponding GET representation length; omit it when unknown instead of advertising zero merely because HEAD sends no bytes.
- Do not use `OutputStream.SetLength` to assign an HTTP header. The response stream is not a seekable file, and SetLength remains unsupported. An exception handler changing the response is not a reliable metadata-setting strategy.
- Valid HEAD body writes are discarded. Avoid unnecessary serialization and expensive body generation in a metadata-only handler; suppression prevents invalid wire content but does not eliminate work the application performs beforehand. Streaming serialization may omit unknown length, while buffering can calculate the serialized/compressed representation length.
- Existing error responses can close or reset a connection. HEAD body suppression does not change those connection policies. A healthy subsequent request can use a fresh connection when required.

Real-listener coverage verifies the original route, synchronous and async handlers, both assignment orders, raw HEAD followed by GET on the same connection, fixed/chunked writes, buffered/streaming JSON, gzip, APM completion/state, WriteByte/memory writes, argument/cancellation/disposal contracts and bodyless errors. Existing static-file HEAD tests remain in the suite. Raw socket checks are necessary because an HTTP client normally ignores a HEAD body and can hide illegal bytes that corrupt the next response.
