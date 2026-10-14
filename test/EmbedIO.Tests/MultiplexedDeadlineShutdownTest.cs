using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A request deadline replaces the HTTP/2 or HTTP/3 context token and the server restores
    // the original afterwards. The deadline token and the restored token must both still
    // hear server shutdown. The server runs its request pipeline over a stream the test
    // accepts itself: a real listener also cancels its streams on stop, which would hide
    // whether the request heard the server's own shutdown token.
    public class MultiplexedDeadlineShutdownTest
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public Task Http2ServerShutdownReachesRequest(bool deadline, bool afterHandler)
            => AssertShutdownReachesRequestAsync(WithHttp2ExchangeAsync, deadline, afterHandler);

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public async Task Http3ServerShutdownReachesRequest(bool deadline, bool afterHandler)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (CoreType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", false) == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await AssertShutdownReachesRequestAsync(WithHttp3ExchangeAsync, deadline, afterHandler);
        }

        // With afterHandler, the wait runs in a request completion callback, after the server
        // restored the token that the deadline replaced and before the context closes.
        private static async Task AssertShutdownReachesRequestAsync(
            Func<Func<IHttpContextImpl, Task>, Func<HttpClient, Uri, Task>, Task> transport, bool deadline, bool afterHandler)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task AwaitShutdownAsync(IHttpContext context)
            {
                waiting.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            }

            // The deadline is far away, so only server shutdown can end the wait.
            using var server = new PipelineServer(deadline ? TimeSpan.FromMinutes(10) : null);
            // Shutdown cancels the deadline and the captured server token alike, so only the
            // server-token check keeps a shutdown from being answered as an expired deadline.
            var answered = new ConcurrentQueue<int>();
            server.OnHttpException = (context, exception) =>
            {
                answered.Enqueue(exception.StatusCode);
                return HttpExceptionHandler.Default(context, exception);
            };
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
            {
                if (!afterHandler) return AwaitShutdownAsync(context);
                context.OnRequestCompleted(() => AwaitShutdownAsync(context));
                return context.SendStringAsync("handled", "text/plain", WebServer.Utf8NoBomEncoding);
            }));
            using var shutdown = new CancellationTokenSource();
            var running = server.RunAsync(shutdown.Token);
            try
            {
                var serverToken = await server.Running.WaitAsync(Deadline);
                var handling = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                await transport(context =>
                {
                    var pipeline = server.HandleAsync(context, serverToken);
                    handling.TrySetResult(pipeline);
                    return pipeline;
                }, async (client, uri) =>
                {
                    using var abandon = new CancellationTokenSource();
                    var request = client.GetAsync(uri, abandon.Token);
                    try
                    {
                        await waiting.Task.WaitAsync(Deadline);
                        shutdown.Cancel();
                        Assert.That(await Task.WhenAny(canceled.Task, Task.Delay(Deadline)), Is.SameAs(canceled.Task),
                            "Server shutdown did not reach the request.");
                        await (await handling.Task.WaitAsync(Deadline)).WaitAsync(Deadline);
                        Assert.That(answered, Is.Empty, "Server shutdown was answered with an HTTP error, such as an expired-deadline 503.");
                    }
                    finally
                    {
                        // Resetting the stream earlier would cancel the request on its own.
                        abandon.Cancel();
                        try { using var response = await request; }
                        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                    }
                });
            }
            finally
            {
                shutdown.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        private static async Task WithHttp2ExchangeAsync(Func<IHttpContextImpl, Task> application, Func<HttpClient, Uri, Task> verify)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var local = (IPEndPoint)listener.LocalEndpoint;
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                var remote = (IPEndPoint)(socket.Client.RemoteEndPoint ?? throw new AssertionException("Missing remote endpoint."));
                using var stream = socket.GetStream();
                var accepting = (Task)(Method("EmbedIO.Net.Internal.Http2.Http2Connection", "AcceptAsync", BindingFlags.Static)
                    .Invoke(null, new object[] { stream, stop.Token }) ?? throw new AssertionException("Missing connection."));
                await accepting;
                var connection = accepting.GetType().GetProperty("Result")?.GetValue(accepting) ?? throw new AssertionException("Missing connection.");
                using var connectionLifetime = (IDisposable)connection;
                var dispatcher = Activator.CreateInstance(CoreType("EmbedIO.Net.Internal.Http2.Http2Dispatcher"),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { connection }, null)
                    ?? throw new AssertionException("Missing dispatcher.");
                using var dispatcherLifetime = (IDisposable)dispatcher;
                var dispatch = Adapt("EmbedIO.Net.Internal.Http2.Http2Exchange", exchange => application(Context(exchange, local, remote, false)));
                await (Task)(Method("EmbedIO.Net.Internal.Http2.Http2Dispatcher", "RunAsync", BindingFlags.Instance)
                    .Invoke(dispatcher, new object[] { dispatch, stop.Token }) ?? throw new AssertionException("Missing dispatch."));
            });
            // The client stays open until the server has stopped. Closing it first can reset the
            // connection under the dispatcher's pending read, which RunAsync reports as a failure.
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            try
            {
                await verify(client, new Uri($"http://127.0.0.1:{local.Port}/shutdown"));
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                try { await server.WaitAsync(Deadline); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task WithHttp3ExchangeAsync(Func<IHttpContextImpl, Task> application, Func<HttpClient, Uri, Task> verify)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { SslApplicationProtocol.Http3 },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 8,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = new() { SslApplicationProtocol.Http3 },
                        ServerCertificate = certificate,
                    },
                }),
            }, stop.Token);
            var local = listener.LocalEndPoint;
            var server = Task.Run(async () =>
            {
                var connection = await listener.AcceptConnectionAsync(stop.Token);
                var remote = connection.RemoteEndPoint;
                var dispatch = Adapt("EmbedIO.Net.Internal.Http3.Http3QuicExchange", exchange => application(Context(exchange, local, remote, true)));
                await (Task)(Method("EmbedIO.Net.Internal.Http3.Http3QuicConnection", "RunAsync", BindingFlags.Static)
                    .Invoke(null, new object[] { connection, dispatch, stop.Token }) ?? throw new AssertionException("Missing connection."));
            });
            // As over HTTP/2, the client stays open until the server has stopped.
            using var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString(),
                },
            };
            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version30,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            try
            {
                await verify(client, new Uri($"https://127.0.0.1:{local.Port}/shutdown"));
            }
            finally
            {
                stop.Cancel();
                try { await server.WaitAsync(Deadline); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }

        private static Type? CoreType(string name, bool required)
            => typeof(WebServer).Assembly.GetType(name, required);

        private static Type CoreType(string name)
            => CoreType(name, true) ?? throw new AssertionException($"Missing {name}.");

        private static MethodInfo Method(string type, string name, BindingFlags binding)
            => CoreType(type).GetMethod(name, binding | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new AssertionException($"Missing {type}.{name}.");

        private static IHttpContextImpl Context(object exchange, IPEndPoint local, IPEndPoint remote, bool secure)
            => (IHttpContextImpl)(Activator.CreateInstance(CoreType("EmbedIO.Net.Internal.MultiplexedContext"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { exchange, local, remote, secure }, null)
                ?? throw new AssertionException("Missing context."));

        // Builds the internal Func<TExchange, Task> callback a transport dispatches requests to.
        private static Delegate Adapt(string exchangeType, Func<object, Task> application)
        {
            var type = CoreType(exchangeType);
            var exchange = Expression.Parameter(type);
            return Expression.Lambda(typeof(Func<,>).MakeGenericType(type, typeof(Task)),
                Expression.Invoke(Expression.Constant(application), Expression.Convert(exchange, typeof(object))), exchange).Compile();
        }

        // Runs the server's request pipeline over contexts the test accepts itself.
        private sealed class PipelineServer : WebServerBase<TestWebServerOptions>
        {
            private readonly TaskCompletionSource<CancellationToken> _running = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal PipelineServer(TimeSpan? deadline)
                : base(options => options.WithRequestTimeout(deadline))
            {
            }

            internal Task<CancellationToken> Running => _running.Task;

            // The steps WebServer takes for each accepted context before handling it.
            internal Task HandleAsync(IHttpContextImpl context, CancellationToken serverToken)
            {
                context.CancellationToken = serverToken;
                context.Route = RouteMatch.UnsafeFromRoot(UrlPath.Normalize(context.Request.Url.AbsolutePath, false));
                return DoHandleContextAsync(context);
            }

            protected override void Prepare(CancellationToken cancellationToken)
            {
            }

            protected override async Task ProcessRequestsAsync(CancellationToken cancellationToken)
            {
                _running.TrySetResult(cancellationToken);
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }

            protected override void OnFatalException()
            {
            }
        }
    }
}
