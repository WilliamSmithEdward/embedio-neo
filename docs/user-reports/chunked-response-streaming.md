# Stream one response with chunked transfer

[Upstream #510](https://github.com/unosquare/embedio/issues/510), reported by
eli-darkly, describes HTTP/1.1 requests receiving an HTTP/1.0 managed response
and only the first string write. The attached project uses EmbedIO 3.4.3 on
.NET Core 2.1; the reported OS was macOS 10.15.5.

Original maintainer rdeago [identified the early protocol-version copy](https://github.com/unosquare/embedio/issues/510#issuecomment-812980578)
and supplied [upstream PR #511](https://github.com/unosquare/embedio/pull/511).
It merged in 2021. Neo already includes that request-backed protocol getter,
including published version 1.0.3. markmeeus later asked about the planned 3.5
release; that historical release plan was discontinued. Neo's release history
is separate, and no v4 backend rewrite is required for this example.

## Keep one writer open until the response is complete

`SendStringAsync` sends a complete response: it opens and disposes its writer.
Calling it twice does not append two chunks to one still-open response. Use
one `OpenResponseText` writer, await its writes, and flush when a part should
be delivered. Dispose it only after the last part.

This complete example uses existing APIs. The HTTP/1.1 chunked-writing pattern
works in 1.0.3. The separate managed keep-alive initialization correction described
below is available from source and is not in a published release yet.
From the source checkout with its selected SDK installed:

```sh
dotnet new console --framework net10.0 --output TestResults/chunked-demo --no-restore
dotnet add TestResults/chunked-demo/chunked-demo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `Program.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithUrlPrefix("http://localhost:8877/").WithMode(HttpListenerMode.EmbedIO))
    .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
    {
        context.Response.ContentType = "text/plain";
        context.Response.SendChunked = true;
        using var writer = context.OpenResponseText(
            WebServer.Utf8NoBomEncoding, buffered: false, preferCompression: false);
        await writer.WriteAsync("chunk1,");
        await writer.FlushAsync();
        await Task.Delay(250, context.CancellationToken);
        await writer.WriteAsync("chunk2");
        await writer.FlushAsync();
    }));
Console.WriteLine("Try / with curl. Press Ctrl+C to stop.");
await server.RunAsync(stop.Token);
```

Run it, then use another terminal:

```sh
dotnet run --project TestResults/chunked-demo/chunked-demo.csproj
curl --http1.1 -i --raw http://localhost:8877/
curl --http1.0 -i --raw http://localhost:8877/
```

Use `curl.exe` in Windows PowerShell. The HTTP/1.1 response has
`Transfer-Encoding: chunked`; its raw body is:

```text
7
chunk1,
6
chunk2
0

```

Wire delimiters are CRLF. HTTP/1.0 does not support chunked transfer: the managed
listener sends `chunk1,chunk2` without chunk-size lines and closes the connection
to delimit this unknown-length body. A normal HTTP client decodes chunk framing;
`--raw` is used to inspect it. Chunk sizes count encoded bytes, not characters.
The explicit UTF-8 encoding without a BOM avoids an extra preamble; the original
`Encoding.UTF8` can emit one. The attached archive writes `chunk2.` while the
issue's inline snippet writes `chunk2`; those have different byte counts.
Compression is disabled here to make wire bytes easy to inspect. Normal content
negotiation and existing compression APIs remain available.

Ctrl+C cancels the listener and the application disposes its owned server.
Writes remain ordered; do not concurrently write one response. Headers must be
set before writing starts. HTTP chunks describe transport framing, not a stable
application message format or guaranteed TCP packet boundaries. Use the
[SSE guide](progress-events.md) for browser progress events and
[streaming lifetime guidance](streaming-response-close.md) for cancellation and
transport errors.

## The related keep-alive correction

The investigation found another early read in the managed response constructor:
it accessed request `KeepAlive` before the protocol and headers were parsed.
That also cached the request's premature decision. A client requesting closure
could instead get a persistent connection, and an unknown-length HTTP/1.0 body
could wait for a connection that remained open.

The source correction derives the default response policy from the parsed
request when needed. An explicit application `Response.KeepAlive` assignment
still takes precedence. Existing request token parsing, response reuse limits,
error-close rules, writer ownership, APIs, targets and dependencies are preserved.
The native Microsoft adapter is unchanged. Explicitly forcing keep-alive for an
HTTP/1.0 response still requires a known content length; this fix is not a new
framing policy for arbitrary application overrides.

Real HTTP/HTTPS wire regressions cover the original two-helper behavior, complete 7/6/0
framing, empty writes, UTF-8 byte lengths, HTTP/1.0 fallback, parsed keep-alive
decisions, explicit overrides, same-connection follow-up and terminal closure.
The exact published 1.0.3 assembly reproduces the premature keep-alive failure;
the corrected source passes the focused cases. This does not claim execution
on the original macOS 10.15.5 / .NET Core 2.1 environment.

Thank you to eli-darkly for the reproduction and diagnosis, markmeeus for the
release follow-up, and rdeago and the original EmbedIO maintainers for the
protocol fix and module/streaming foundation.
