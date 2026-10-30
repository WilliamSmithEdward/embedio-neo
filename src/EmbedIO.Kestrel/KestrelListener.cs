using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmbedIO.Kestrel
{
    internal sealed class KestrelListener(WebServerOptions options) :
        IHttpListener,
        IDispatchingHttpListener,
        IGracefulHttpListener,
        IHttpBackendListener,
        IHttpApplication<KestrelContext>
    {
        private readonly object _sync = new();
        private readonly Channel<KestrelContext> _pending = Channel.CreateUnbounded<KestrelContext>();
        private Func<IHttpContextImpl, Task>? _dispatcher;
        private IHost? _application;
        private IHost? _http2Application;
        private KestrelConnectionRouter? _router;
        private RequestPolicyResolver? _policy;
        private ServerTelemetry? _telemetry;
        private Task? _shutdown;
        private bool _disposed;
        private int _applicationsDisposed;
        private bool _draining;
        private volatile bool _listening;
        private ListenerPrefix[] _bindings = [];

        public bool IgnoreWriteExceptions { get; set; } = true;
        public List<string> Prefixes { get; } = [];
        public bool IsListening => _listening;
        public string Name => "Kestrel";

        void IHttpBackendListener.Configure(RequestPolicyResolver? policy, ServerTelemetry? telemetry)
        {
            _policy = policy;
            _telemetry = telemetry;
        }

        public void AddPrefix(string listenerPrefix)
        {
            lock (_sync)
            {
                if (_application != null) throw new InvalidOperationException("The listener has already started.");

                ListenerPrefix.CheckUri(listenerPrefix);

                if (Prefixes.Contains(listenerPrefix, StringComparer.Ordinal))
                    throw new ArgumentException("The prefix is already registered.", nameof(listenerPrefix));

                Prefixes.Add(listenerPrefix);
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_listening) return;

                if (_application != null)
                    throw new InvalidOperationException("Create a new server after stopping Kestrel.");

                if (options.WebTransport != null)
                    throw new NotSupportedException("The Kestrel backend does not expose WebTransport sessions.");

                if (options.RequireHttp3AddressValidation)
                    throw new NotSupportedException(
                        "Kestrel does not expose forced stateless HTTP/3 address validation."
                    );

                _bindings = [.. Prefixes.Select(prefix => new ListenerPrefix(prefix))];

                if (_bindings.Length == 0 || _bindings.Any(prefix => !prefix.IsValid()))
                    throw new ArgumentException("At least one valid URL prefix is required.");

                if (
                    _bindings
                        .GroupBy(prefix => prefix.Port)
                        .Any(
                            group => group
                                .Select(prefix => prefix.Secure)
                                .Distinct()
                                .Count()
                                != 1
                        )
                )
                    throw new ArgumentException("A TCP port cannot mix HTTP and HTTPS bindings.");

                options.TlsPolicy?.EnsureSupported(
                    options.Mode is HttpListenerMode.EmbedIOHttp3 or HttpListenerMode.EmbedIOCombined
                );

                if (options.Mode is HttpListenerMode.EmbedIOHttp3 or HttpListenerMode.EmbedIOCombined)
                {
                    if (!QuicListener.IsSupported)
                        throw new PlatformNotSupportedException(
                            "Kestrel HTTP/3 requires System.Net.Quic and a supported MsQuic runtime."
                        );

                    if (_bindings.Any(prefix => !prefix.Secure))
                        throw new ArgumentException("HTTP/3 hosting requires HTTPS prefixes.");
                }

                _router = new KestrelConnectionRouter(options, _telemetry);
                _application = CreateApplication(false);

                try
                {
                    _application
                        .StartAsync()
                        .GetAwaiter()
                        .GetResult();

                    if (options.Mode == HttpListenerMode.EmbedIO && _bindings.Any(prefix => !prefix.Secure))
                    {
                        _http2Application = CreateApplication(true);
                        _http2Application
                            .StartAsync()
                            .GetAwaiter()
                            .GetResult();
                    }

                    _listening = true;
                }

                catch
                {
                    _http2Application?.Dispose();
                    _application.Dispose();
                    _router
                        .DisposeAsync()
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    throw;
                }
            }
        }

        private IHost CreateApplication(bool cleartextHttp2)
        {
            var builder = new HostBuilder();
            builder.ConfigureLogging(logging => logging
                .SetMinimumLevel(LogLevel.Trace)
                .AddProvider(new KestrelTraceLoggerProvider()));
            builder.ConfigureWebHost(
                web =>
                {
                    web
                        .UseSetting(WebHostDefaults.PreventHostingStartupKey, bool.TrueString)
                        .UseKestrelCore()
                        .ConfigureKestrel(
                            serverOptions =>
                            {
                                serverOptions.AddServerHeader = false;
                                serverOptions.AllowSynchronousIO = true;
                                serverOptions.Limits.MaxConcurrentConnections = null;
                                serverOptions.Limits.MaxRequestBodySize = null;
                                serverOptions.Limits.MinRequestBodyDataRate = null;
                                serverOptions.Limits.MinResponseDataRate = null;
                                serverOptions.Limits.MaxRequestHeadersTotalSize = Math.Max(
                                    1,
                                    Math.Min(
                                        WebServerOptionsBase.DefaultMaximumRequestHeaderBytes,
                                        options.MaximumRequestHeaderBytes
                                            ?? WebServerOptionsBase.DefaultMaximumRequestHeaderBytes
                                    )
                                );
                                serverOptions.Limits.MaxRequestLineSize =
                                    WebServerOptionsBase.DefaultMaximumRequestHeaderBytes;
                                // No valid header block can contain more fields than bytes. Keep
                                // headroom for Kestrel's HTTP/2 pseudo-header count arithmetic.
                                serverOptions.Limits.MaxRequestHeaderCount = Math.Max(
                                    1,
                                    Math.Min(
                                        WebServerOptionsBase.DefaultMaximumRequestHeaderBytes,
                                        options.MaximumRequestHeaderCount
                                            ?? WebServerOptionsBase.DefaultMaximumRequestHeaderBytes
                                    )
                                );
                                serverOptions.Limits.RequestHeadersTimeout = options.RequestHeadersTimeout
                                    ?? TimeSpan.FromSeconds(30);
                                serverOptions.Limits.KeepAliveTimeout = options.KeepAliveTimeout
                                    ?? TimeSpan.FromSeconds(15);

                                foreach (var group in _bindings.GroupBy(prefix => (prefix.Port, prefix.Secure)))
                                {
                                    if (cleartextHttp2 && group.Key.Secure) continue;

                                    void Configure(ListenOptions endpoint)
                                    {
                                        endpoint.DisableAltSvcHeader = options.Mode != HttpListenerMode.EmbedIOCombined
                                            || !options.AdvertiseHttp3;
                                        endpoint.Protocols = cleartextHttp2
                                            ? HttpProtocols.Http2
                                            : options.Mode switch
                                            {
                                                HttpListenerMode.EmbedIOHttp3 => HttpProtocols.Http3,
                                                HttpListenerMode.EmbedIOCombined => HttpProtocols.Http1AndHttp2AndHttp3,
                                                _ when group.Key.Secure => HttpProtocols.Http1AndHttp2,
                                                _ => HttpProtocols.Http1
                                            };
                                        ((IMultiplexedConnectionBuilder)endpoint).Use(next => async connection =>
                                        {
                                            var lifetime = _telemetry?.ConnectionStarted("quic", (connection.LocalEndPoint as IPEndPoint)?.Port ?? group.Key.Port);

                                            try
                                            {
                                                await next(connection).ConfigureAwait(false);
                                            }

                                            finally
                                            {
                                                lifetime?.Stop();
                                            }
                                        });

                                        if (group.Key.Secure) endpoint.UseHttps(ConfigureHttps);
                                    }

                                    if (group.Any(prefix => prefix.Host is "*" or "+"))
                                        serverOptions.ListenAnyIP(group.Key.Port, Configure);

                                    else
                                    {
                                        var addresses = group.SelectMany(prefix => ResolveAddresses(prefix.Host));

                                        foreach (var address in addresses.Distinct())
                                            serverOptions.Listen(address, group.Key.Port, Configure);
                                    }
                                }
                            }
                        );

                    if (
                        !cleartextHttp2
                        && options.Mode is HttpListenerMode.EmbedIOHttp3 or HttpListenerMode.EmbedIOCombined
                    )
                        web.UseQuic();

                    web.ConfigureServices(services =>
                    {
                        services.RemoveAll<IConnectionListenerFactory>();
                        services.AddSingleton((_router ?? throw new InvalidOperationException("Missing transport router.")).Factory(cleartextHttp2));
                    });
                    web.Configure(application => application.Run(async request =>
                        {
                            var context = CreateContext(request.Features);

                            try
                            {
                                await ProcessRequestAsync(context).ConfigureAwait(false);
                            }

                            finally
                            {
                                DisposeContext(context, null);
                            }
                        }));
                },
                hostOptions => hostOptions.SuppressEnvironmentConfiguration = true
            );
            return builder.Build();
        }

        private static IPAddress[] ResolveAddresses(string host)
        {
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return [IPAddress.Loopback, IPAddress.IPv6Loopback];

            return IPAddress.TryParse(host.Trim('[', ']'), out var address) ? [address] : Dns.GetHostAddresses(host);
        }

        private void ConfigureHttps(HttpsConnectionAdapterOptions https)
        {
            var policy = options.TlsPolicy;
            https.ServerCertificate = options.CertificateSource?.Current
                ?? options.Certificate
                ?? throw new InvalidOperationException("HTTPS requires a server certificate.");
            https.ServerCertificateSelector = (_, name) => policy?.ServerCertificateSelector?.Invoke(name)
                ?? options.CertificateSource?.Current
                ?? https.ServerCertificate;
            https.SslProtocols = Net.Internal.ServerTls.GetProtocols(policy);

            if (policy == null) return;

            https.ClientCertificateMode = policy.ClientCertificateMode switch
            {
                Net.ClientCertificateMode.Require => ClientCertificateMode.RequireCertificate,
                Net.ClientCertificateMode.Allow => ClientCertificateMode.AllowCertificate,
                _ => ClientCertificateMode.NoCertificate
            };
            https.CheckCertificateRevocation = policy.CheckCertificateRevocation;
            https.ClientCertificateValidation = (certificate, chain, errors) => policy.ValidateClientCertificate(
                this,
                certificate,
                chain,
                errors
            );
        }

        public KestrelContext CreateContext(IFeatureCollection contextFeatures)
        {
            if (
                contextFeatures
                    .Get<KestrelConnectionRouter.ForwardedConnection>()
                    ?.AdmitHead()
                == false
            )
                throw new ConnectionAbortedException("The initial request header deadline expired.");

            return new(contextFeatures, IgnoreWriteExceptions, options.KeepAliveTimeout ?? TimeSpan.FromSeconds(15));
        }

        public async Task ProcessRequestAsync(KestrelContext context)
        {
            try
            {
                if (context.RequestImpl.InitializationError != null)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var request = context.Request;

                if (
                    !_bindings.Any(
                        prefix => prefix.Port == request.LocalEndPoint.Port
                            && prefix.Port == request.Url.Port
                            && prefix.Secure == request.IsSecureConnection
                            && (prefix.Host is "*"
                                or "+"
                                || prefix.Host.Equals(request.Url.Host, StringComparison.OrdinalIgnoreCase))
                            && request.Url.AbsolutePath.StartsWith(prefix.Path, StringComparison.Ordinal)
                    )
                )
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                _policy?.Apply(context);
                Func<IHttpContextImpl, Task>? dispatch;

                lock (_sync)
                {
                    dispatch = _dispatcher;

                    if (dispatch == null && !_pending.Writer.TryWrite(context))
                        throw new InvalidOperationException("The listener is stopping.");
                }

                if (dispatch != null)
                {
                    var handling = dispatch(context);

                    if (
                        handling.IsCompleted
                        || !context.RequestImpl.CanReleaseAfterResponse
                        || await Task
                            .WhenAny(handling, context.Completion)
                            .ConfigureAwait(false)
                            == handling
                    )
                        await handling.ConfigureAwait(false);
                }

                else
                    await context.Completion
                        .WaitAsync(context.PeerAborted)
                        .ConfigureAwait(false);
            }

            finally
            {
                try
                {
                    await context
                        .CloseAsync()
                        .ConfigureAwait(false);
                }

                finally
                {
                    await context
                        .RetireAsync()
                        .ConfigureAwait(false);
                }
            }
        }

        public void DisposeContext(KestrelContext context, Exception? exception) => context.Retire();

        void IDispatchingHttpListener.SetDispatcher(Func<IHttpContextImpl, Task> dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);

            lock (_sync)
            {
                if (_dispatcher != null) throw new InvalidOperationException("The dispatcher is already installed.");

                _dispatcher = dispatcher;

                while (_pending.Reader.TryRead(out var context)) _ = DispatchPendingAsync(dispatcher, context);
            }
        }

        private static async Task DispatchPendingAsync(Func<IHttpContextImpl, Task> dispatch, KestrelContext context)
        {
            try
            {
                await dispatch(context).ConfigureAwait(false);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                await context
                    .AbortAsync(error)
                    .ConfigureAwait(false);
            }

            finally
            {
                await context
                    .CloseAsync()
                    .ConfigureAwait(false);
            }
        }

        public async Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _pending.Reader
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            catch (ChannelClosedException)
            {
                if (_draining) throw new ListenerDrainedException();

                throw new HttpListenerException(995, "The listener stopped.");
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _listening = false;
                _pending.Writer.TryComplete();
                _shutdown ??= StopServerAsync(TimeSpan.Zero, CancellationToken.None);
            }
        }

        public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _draining = true;
                return _shutdown ??= StopServerAsync(timeout, cancellationToken);
            }
        }

        private async Task StopServerAsync(TimeSpan timeout, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);

            try
            {
                await Task
                    .WhenAll(
                        _application?.StopAsync(deadline.Token) ?? Task.CompletedTask,
                        _http2Application?.StopAsync(deadline.Token) ?? Task.CompletedTask
                    )
                    .ConfigureAwait(false);
            }

            finally
            {
                _listening = false;
                _pending.Writer.TryComplete();

                if (_disposed) await DisposeApplicationsAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Task shutdown;

            lock (_sync)
            {
                if (_disposed) return;

                _disposed = true;
                Stop();
                shutdown = _shutdown ?? Task.CompletedTask;
            }

            try
            {
                shutdown
                    .GetAwaiter()
                    .GetResult();
            }

            finally
            {
                DisposeApplicationsAsync()
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private async Task DisposeApplicationsAsync()
        {
            if (Interlocked.Exchange(ref _applicationsDisposed, 1) != 0) return;

            _http2Application?.Dispose();
            _application?.Dispose();

            if (_router != null)
                await _router
                    .DisposeAsync()
                    .ConfigureAwait(false);
        }
    }
}
