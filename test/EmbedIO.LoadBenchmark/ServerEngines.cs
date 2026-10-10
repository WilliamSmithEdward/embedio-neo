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

    private static async Task HandleAsync(IHttpContext context)
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

    private static async Task HandleAsync(HttpContext context)
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
}
