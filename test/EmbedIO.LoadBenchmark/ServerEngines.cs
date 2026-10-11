using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using EmbedIO;
using EmbedIO.Actions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

internal enum Protocol
{
    Http1,
    Http2,
    Http3,
}

internal sealed record ServerSettings(string Engine, Protocol Protocol, bool Tls, int Port, X509Certificate2? Certificate);

internal interface IBenchmarkServer : IAsyncDisposable
{
    Task StartAsync();

    Task StopAsync();
}

// The same application work for both engines: route parse, cached body or
// validated upload, then an asynchronous write. No engine-specific middleware.
internal static class Application
{
    internal const int UploadBufferBytes = 16384;

    // Reads and validates the whole upload. Returns the acknowledgement or null on mismatch.
    internal static async Task<byte[]?> ReadUploadAsync(Stream body, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(UploadBufferBytes);
        try
        {
            long offset = 0;
            var valid = true;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, UploadBufferBytes), cancellation).ConfigureAwait(false)) > 0)
            {
                valid &= Payloads.Matches(buffer.AsSpan(0, read), offset);
                offset += read;
            }

            return valid ? Payloads.UploadAcknowledgement(offset) : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class EmbedIOServer(ServerSettings settings) : IBenchmarkServer
{
    private readonly CancellationTokenSource _stop = new();
    private WebServer? _server;
    private Task? _running;

    public Task StartAsync()
    {
        EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Off;
        var scheme = settings.Tls ? "https" : "http";
        var mode = settings.Protocol == Protocol.Http3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO;
        var options = new WebServerOptions()
            .WithUrlPrefix($"{scheme}://localhost:{settings.Port}/")
            .WithMode(mode)
            .WithoutAutoLoadCertificate()
            .WithoutAutoRegisterCertificate();
        if (settings.Tls) options = options.WithCertificate(settings.Certificate);
        _server = new WebServer(options)
            .PreferNoCompressionFor("text/*")
            .PreferNoCompressionFor("application/octet-stream")
            .WithModule(new ActionModule("/", HttpVerbs.Any, HandleAsync));
        _running = _server.RunAsync(_stop.Token);
        return WaitForListeningAsync(_server, _running);
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_running is not null) await _running.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _server?.Dispose();
        _stop.Dispose();
    }

    private static async Task WaitForListeningAsync(WebServer server, Task running)
    {
        // RunAsync reports Listening once the listener has bound its endpoints.
        while (server.State is not WebServerState.Listening)
        {
            if (running.IsCompleted) await running.ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal static async Task HandleAsync(IHttpContext context)
    {
        var response = context.Response;
        if (!BenchmarkRoute.TryParse(context.Request.Url.AbsolutePath, out var route)) throw HttpException.NotFound();
        var cancellation = context.CancellationToken;
        response.ContentType = route.Kind is RouteKind.Plaintext or RouteKind.Upload ? "text/plain" : "application/octet-stream";
        switch (route.Kind)
        {
            case RouteKind.Plaintext:
                await WriteAsync(Payloads.Plaintext).ConfigureAwait(false);
                break;
            case RouteKind.Bytes:
                await WriteAsync(Payloads.Get(route.Length)).ConfigureAwait(false);
                break;
            case RouteKind.Stream:
                var body = Payloads.Get(route.Length);
                response.SendChunked = true;
                for (var offset = 0; offset < body.Length; offset += route.Chunk)
                {
                    await response.OutputStream.WriteAsync(body.AsMemory(offset, Math.Min(route.Chunk, body.Length - offset)), cancellation).ConfigureAwait(false);
                    await response.OutputStream.FlushAsync(cancellation).ConfigureAwait(false);
                }

                break;
            case RouteKind.Inspect:
                var failure = InspectFailure(context.Request);
                if (failure is not null) response.StatusCode = 400;
                await WriteAsync(failure is null ? Payloads.Plaintext : InspectRequest.Rejection(failure)).ConfigureAwait(false);
                break;
            default:
                if (context.Request.HttpVerb != HttpVerbs.Post) throw HttpException.MethodNotAllowed();
                var acknowledgement = await Application.ReadUploadAsync(context.Request.InputStream, cancellation).ConfigureAwait(false);
                if (acknowledgement is null) response.StatusCode = 400;
                await WriteAsync(acknowledgement ?? Payloads.UploadRejected).ConfigureAwait(false);
                break;
        }

        async Task WriteAsync(byte[] payload)
        {
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        }
    }

    // Returns the name of the first property that does not match, or null.
    private static string? InspectFailure(IHttpRequest request)
    {
        if (request.HttpVerb != HttpVerbs.Get) return "method";
        if (request.Url.Host != "localhost") return "host";
        if (request.IsSecureConnection != (request.Url.Scheme == Uri.UriSchemeHttps)) return "scheme";
        if (request.QueryString["id"] != "42" || request.QueryString["name"] != "neo bench") return "query";
        if (request.QueryString.GetValues("tag") is not ["a", "b"]) return "repeated-query";
        if (request.UserAgent != InspectRequest.UserAgent) return "user-agent";
        if (request.Headers["Accept"] != InspectRequest.Accept || request.Headers["X-Request-Id"] != InspectRequest.RequestId) return "headers";
        if (request.Cookies.Count != 2 || request.Cookies["theme"]?.Value != "dark") return "cookies";
        if (request.UrlReferrer?.AbsolutePath != "/origin") return "referrer";
        // Body framing is read but not validated: an HTTP/3 GET without content-length
        // reports an unknown length and HasEntityBody true in this engine.
        if (request.ContentType is not null || request.ContentLength64 < -1 || (request.HasEntityBody && request.ProtocolVersion.Major < 3)) return "body";
        if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address)) return "remote";
        if (!request.IsLocal) return $"is-local ({request.LocalEndPoint} {request.RemoteEndPoint})";
        return null;
    }
}

internal sealed class KestrelServer(ServerSettings settings) : IBenchmarkServer
{
    private WebApplication? _application;

    public Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.ListenLocalhost(settings.Port, listen =>
            {
                listen.Protocols = settings.Protocol switch
                {
                    Protocol.Http3 => HttpProtocols.Http3,
                    // Mirrors EmbedIO: TLS offers both by ALPN; cleartext HTTP/2 is prior knowledge.
                    _ when settings.Tls => HttpProtocols.Http1AndHttp2,
                    Protocol.Http2 => HttpProtocols.Http2,
                    _ => HttpProtocols.Http1,
                };
                if (settings.Tls) listen.UseHttps(settings.Certificate ?? throw new InvalidOperationException("TLS requires a certificate."));
            });
        });
        if (settings.Protocol == Protocol.Http3) builder.WebHost.UseQuic();
        _application = builder.Build();
        _application.Run(HandleAsync);
        return _application.StartAsync();
    }

    public Task StopAsync() => _application?.StopAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_application is not null) await _application.DisposeAsync().ConfigureAwait(false);
    }

    internal static async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        if (!BenchmarkRoute.TryParse(context.Request.Path.Value ?? string.Empty, out var route))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var cancellation = context.RequestAborted;
        response.ContentType = route.Kind is RouteKind.Plaintext or RouteKind.Upload ? "text/plain" : "application/octet-stream";
        switch (route.Kind)
        {
            case RouteKind.Plaintext:
                await WriteAsync(Payloads.Plaintext).ConfigureAwait(false);
                break;
            case RouteKind.Bytes:
                await WriteAsync(Payloads.Get(route.Length)).ConfigureAwait(false);
                break;
            case RouteKind.Stream:
                var body = Payloads.Get(route.Length);
                for (var offset = 0; offset < body.Length; offset += route.Chunk)
                {
                    await response.Body.WriteAsync(body.AsMemory(offset, Math.Min(route.Chunk, body.Length - offset)), cancellation).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }

                break;
            case RouteKind.Inspect:
                var failure = InspectFailure(context);
                if (failure is not null) response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteAsync(failure is null ? Payloads.Plaintext : InspectRequest.Rejection(failure)).ConfigureAwait(false);
                break;
            default:
                if (!HttpMethods.IsPost(context.Request.Method))
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }

                var acknowledgement = await Application.ReadUploadAsync(context.Request.Body, cancellation).ConfigureAwait(false);
                if (acknowledgement is null) response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteAsync(acknowledgement ?? Payloads.UploadRejected).ConfigureAwait(false);
                break;
        }

        async Task WriteAsync(byte[] payload)
        {
            response.ContentLength = payload.Length;
            await response.Body.WriteAsync(payload, cancellation).ConfigureAwait(false);
        }
    }

    // Returns the name of the first property that does not match, or null.
    private static string? InspectFailure(HttpContext context)
    {
        var request = context.Request;
        var remote = context.Connection.RemoteIpAddress;
        if (!HttpMethods.IsGet(request.Method)) return "method";
        if (request.Host.Host != "localhost") return "host";
        if (request.IsHttps != (request.Scheme == Uri.UriSchemeHttps)) return "scheme";
        if (request.Query["id"] != "42" || request.Query["name"] != "neo bench") return "query";
        if (request.Query["tag"] is not ["a", "b"]) return "repeated-query";
        if (request.Headers.UserAgent != InspectRequest.UserAgent) return "user-agent";
        if (request.Headers.Accept != InspectRequest.Accept || request.Headers["X-Request-Id"] != InspectRequest.RequestId) return "headers";
        if (request.Cookies.Count != 2 || request.Cookies["theme"] != "dark") return "cookies";
        if (!Uri.TryCreate(request.Headers.Referer, UriKind.Absolute, out var referer) || referer.AbsolutePath != "/origin") return "referrer";
        if (request.ContentType is not null || request.ContentLength is not (null or 0)) return "body";
        if (remote is null || !IPAddress.IsLoopback(remote)) return "remote";
        if (!remote.Equals(context.Connection.LocalIpAddress)) return $"is-local ({context.Connection.LocalIpAddress} {remote})";
        return null;
    }
}
