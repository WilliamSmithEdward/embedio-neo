using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.PlatformTests;

var engine = args.FirstOrDefault() ?? "kestrel";

if (args.Contains("--trace", StringComparer.Ordinal))
{
    EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.All;
    EmbedIO.Diagnostics.Log.Source.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
}

if (args.Length > 1 && args[1] == "advanced")
{
    await AdvancedCases.RunAsync(engine);
    return;
}

if (args.Length > 1 && args[1] == "protocols")
{
    await ProtocolCases.RunAsync(engine);
    return;
}

if (args.Length > 1 && args[1] == "lifecycle")
{
    await LifecycleCases.RunAsync(engine);
    return;
}

if (args.Length > 1 && args[1] == "tls")
{
    await TlsCases.RunAsync(engine);
    return;
}

if (args.Length > 1 && args[1] == "startup")
{
    await StartupCases.RunAsync();
    return;
}

if (engine is not ("kestrel" or "embedio")) throw new ArgumentException("Choose kestrel or embedio.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var prefix = "http://127.0.0.1:" + LoopbackPorts.Free() + "/";
var options = new WebServerOptions().WithUrlPrefix(prefix);
options.MaximumRequestBodyBytes = 4;

if (engine == "kestrel") options.WithKestrel();

using var server = new WebServer(options);
IHttpContextImpl? retained = null;
var closeCount = 0;
var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var cases = new List<string>();
var streamedBody = Encoding.ASCII.GetBytes(new string('x', 1024 * 1024));
server.WithModule(new ActionModule(async context =>
    {
        switch (context.Request.Url.AbsolutePath)
        {
            case "/fixed":
                context.Response.ContentLength64 = 2;
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("ok"), context.CancellationToken);
                break;

            case "/chunked13":
                context.Response.SendChunked = true;
                context.Response.ContentLength64 = 99;
                await context.Response.OutputStream.WriteAsync("hello"u8.ToArray(), context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(" chunks!"u8.ToArray(), context.CancellationToken);
                break;

            case "/chunked-large":
                context.Response.SendChunked = true;

                for (var offset = 0; offset < streamedBody.Length; offset += 16384)
                {
                    await context.Response.OutputStream.WriteAsync(streamedBody.AsMemory(offset, 16384), context.CancellationToken);
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                }

                break;

            case "/empty":
                context.SetHandled();
                break;

            case "/raw-content-type":
                context.Response.Headers["Content-Type"] = "text/custom; charset=utf-8";
                context.Response.ContentLength64 = 3;
                await context.Response.OutputStream.WriteAsync("raw"u8.ToArray(), context.CancellationToken);
                break;

            case "/text":
                await context.SendStringAsync("hello", "text/plain", Encoding.UTF8);
                break;

            case "/echo":
                using (var reader = new StreamReader(context.OpenRequestStream()))
                    await context.SendStringAsync(await reader.ReadToEndAsync(context.CancellationToken), "text/plain", Encoding.UTF8);

                break;

            case "/query":
                await context.SendStringAsync(context.Request.QueryString["x"] + "|" + context.Request.RawTarget, "text/plain", Encoding.UTF8);
                break;

            case "/cookies":
                context.Response.SetCookie(new Cookie("a", "one", "/"));
                context.Response.SetCookie(new Cookie("b", "two", "/"));
                await context.SendStringAsync("cookies", "text/plain", Encoding.UTF8);
                break;

            case "/head":
                context.Response.ContentLength64 = 4;
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("body"), context.CancellationToken);
                break;

            case "/no-content":
                context.Response.StatusCode = 204;
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("must-not-appear"), context.CancellationToken);
                break;

            case "/throw":
                throw new InvalidOperationException("Expected uncommitted handler failure.");

            case "/retain":
                retained = (IHttpContextImpl)context;
                context.OnClose(_ => Interlocked.Increment(ref closeCount));
                await context.SendStringAsync("first", "text/plain", Encoding.UTF8);
                break;

            case "/stale":
                if (retained == null) throw new InvalidOperationException("Missing previous request.");

                retained.Response.Close();
                retained.Close();
                var refused = false;

                try
                {
                    retained.Response.OutputStream.WriteByte(120);
                }

                catch (ObjectDisposedException)
                {
                    refused = true;
                }

                if (!refused) throw new InvalidOperationException("A stale output accepted a write.");

                await context.SendStringAsync("second", "text/plain", Encoding.UTF8);
                break;

            case "/flush":
                await context.Response.OutputStream.WriteAsync(new byte[] { 97 }, context.CancellationToken);
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                await release.Task.WaitAsync(context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(new byte[] { 98 }, context.CancellationToken);
                break;

            default:
                throw HttpException.NotFound();
        }
    }));
var running = server.RunAsync(deadline.Token);
using var client = new HttpClient
{
    BaseAddress = new Uri(prefix),
    Timeout = TimeSpan.FromSeconds(5)
};

try
{
    await Get("fixed", "ok");

    using (var chunked = await client.GetAsync("chunked13", HttpCompletionOption.ResponseHeadersRead, deadline.Token))
    {
        Equal(true, chunked.Headers.TransferEncodingChunked == true, "explicit chunked framing");
        Equal<long?>(null, chunked.Content.Headers.ContentLength, "chunked response overrides length");
        Equal("hello chunks!", await chunked.Content.ReadAsStringAsync(deadline.Token), "explicit chunked body");
        cases.Add("explicit-chunked13");
    }

    using (var tcp = new TcpClient())
    {
        var port = new Uri(prefix).Port;
        await tcp.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        var requests = "GET /chunked-large HTTP/1.1\r\nHost: 127.0.0.1:"
            + port
            + "\r\n\r\nGET /fixed HTTP/1.1\r\nHost: 127.0.0.1:"
            + port
            + "\r\n\r\n";
        await tcp
            .GetStream()
            .WriteAsync(Encoding.ASCII.GetBytes(requests), deadline.Token);
        var large = await WireResponse.ReadAsync(tcp.GetStream(), deadline.Token);
        Equal(new WireResponse(200, new string('x', streamedBody.Length)), large, "large chunked response");
        Equal(
            new WireResponse(200, "ok"),
            await WireResponse.ReadAsync(tcp.GetStream(), deadline.Token),
            "chunked pipeline successor"
        );
        cases.Add("explicit-large-chunked-and-pipelined-successor");
    }

    using (var empty = await client.GetAsync("empty", deadline.Token))
    {
        Equal(0L, empty.Content.Headers.ContentLength ?? -1, "empty response length");
        Equal(false, empty.Headers.TransferEncodingChunked ?? false, "empty response framing");
        cases.Add("empty-response-framing");
    }

    using (var raw = await client.GetAsync("raw-content-type", deadline.Token))
    {
        // The built-in response keeps ContentType separate from its raw fields.
        Equal(
            engine == "kestrel" ? "text/custom" : string.Empty,
            raw.Content.Headers.ContentType?.MediaType ?? string.Empty,
            "raw Content-Type header"
        );
        Equal("raw", await raw.Content.ReadAsStringAsync(deadline.Token), "raw content body");
        cases.Add("raw-content-type-header");
    }

    await Get("text", "hello");
    await Get("query?x=a%2Bb", "a+b|/query?x=a%2Bb");

    using (var response = await client.GetAsync("cookies", deadline.Token))
    {
        Equal(
            2,
            response.Headers
                .GetValues("Set-Cookie")
                .Count(),
            "two distinct Set-Cookie fields"
        );
        cases.Add("cookies");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Head, "head"))
    using (var response = await client.SendAsync(request, deadline.Token))
    {
        Equal(4L, response.Content.Headers.ContentLength ?? -1, "HEAD content length");
        Equal(0, (await response.Content.ReadAsByteArrayAsync(deadline.Token)).Length, "HEAD no body");
        cases.Add("head");
    }

    using (var response = await client.GetAsync("no-content", deadline.Token))
    {
        Equal(HttpStatusCode.NoContent, response.StatusCode, "204 status");
        Equal(0, (await response.Content.ReadAsByteArrayAsync(deadline.Token)).Length, "204 no body");
        cases.Add("no-content");
    }

    await Get("fixed", "ok");

    using (var body = new StringContent("abc"))
    using (var response = await client.PostAsync("echo", body, deadline.Token))
    {
        Equal(HttpStatusCode.OK, response.StatusCode, "POST status");
        Equal("abc", await response.Content.ReadAsStringAsync(deadline.Token), "POST body");
        cases.Add("post");
    }

    using (var body = new StringContent("abcde"))
    using (var response = await client.PostAsync("echo", body, deadline.Token))
    {
        Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, "body limit");
        cases.Add("body-limit");
    }

    using (var response = await client.GetAsync("throw", deadline.Token))
    {
        Equal(HttpStatusCode.InternalServerError, response.StatusCode, "uncommitted failure");
        cases.Add("handler-error");
    }

    await Get("retain", "first");
    await Get("stale", "second");
    Equal(1, closeCount, "completion callback exactly once");

    using (var response = await client.GetAsync("flush", HttpCompletionOption.ResponseHeadersRead, deadline.Token))
    using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token))
    {
        var first = new byte[1];
        Equal(1, await stream.ReadAsync(first, deadline.Token), "flush before handler resumes");
        Equal((byte)97, first[0], "flushed byte");
        release.TrySetResult(true);
        using var rest = new StreamReader(stream);
        Equal("b", await rest.ReadToEndAsync(deadline.Token), "stream suffix");
        cases.Add("flush-while-handler-suspended");
    }

    Console.WriteLine(
        JsonSerializer.Serialize(
            new CompatibilityReport(engine, "basic", cases.Count, cases),
            JsonSerializerOptions.Web
        )
    );
}

finally
{
    release.TrySetResult(true);
    deadline.Cancel();
    await running.WaitAsync(TimeSpan.FromSeconds(5));
}

async Task Get(string path, string expected)
{
    using var response = await client.GetAsync(path, deadline.Token);
    Equal(HttpStatusCode.OK, response.StatusCode, path + " status");
    Equal(expected, await response.Content.ReadAsStringAsync(deadline.Token), path + " body");
    cases.Add(path);
}

static void Equal<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(description + ": expected " + expected + ", received " + actual);
}
