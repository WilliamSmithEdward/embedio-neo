using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;
using EmbedIO.Routing;
using EmbedIO.Utilities;
using EmbedIO.Diagnostics;

namespace EmbedIO
{
    /// <summary>
    /// <para>EmbedIO's web server. This is the default implementation of <see cref="IWebServer"/>.</para>
    /// <para>This class also contains some useful constants related to EmbedIO's internal working.</para>
    /// </summary>
    public partial class WebServer : WebServerBase<WebServerOptions>
    {
        private int _disposeStarted;

        private readonly CancellationTokenSource _drainStarted = new CancellationTokenSource();

        private int _drainSignaled;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer"/> class,
        /// that will respond on HTTP port 80 on all network interfaces.
        /// </summary>
        public WebServer()
            : this(80)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer"/> class,
        /// that will respond on the specified HTTP port on all network interfaces.
        /// </summary>
        /// <param name="port">The port.</param>
        public WebServer(int port)
            : this($"http://*:{port}/")
        {
        }

        /// <summary>Initializes a server with one URI prefix.</summary>
        /// <param name="urlPrefix">The URI prefix to configure.</param>
        public WebServer(Uri urlPrefix)
            : this(Validate.NotNull(nameof(urlPrefix), urlPrefix).OriginalString)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer"/> class
        /// with the specified URL prefixes.
        /// </summary>
        /// <param name="urlPrefixes">The URL prefixes to configure.</param>
        /// <exception cref="ArgumentNullException"><paramref name="urlPrefixes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is the empty string.</para>
        /// <para>- or -</para>
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is already registered.</para>
        /// </exception>
        public WebServer(params string[] urlPrefixes)
            : this(new WebServerOptions().WithUrlPrefixes(urlPrefixes))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer" /> class.
        /// </summary>
        /// <param name="mode">The type of HTTP listener to configure.</param>
        /// <param name="urlPrefixes">The URL prefixes to configure.</param>
        /// <exception cref="ArgumentNullException"><paramref name="urlPrefixes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is the empty string.</para>
        /// <para>- or -</para>
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is already registered.</para>
        /// </exception>
        public WebServer(HttpListenerMode mode, params string[] urlPrefixes)
            : this(new WebServerOptions().WithMode(mode).WithUrlPrefixes(urlPrefixes))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer" /> class.
        /// </summary>
        /// <param name="mode">The type of HTTP listener to configure.</param>
        /// <param name="certificate">The X.509 certificate to use for SSL connections.</param>
        /// <param name="urlPrefixes">The URL prefixes to configure.</param>
        /// <exception cref="ArgumentNullException"><paramref name="urlPrefixes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is the empty string.</para>
        /// <para>- or -</para>
        /// <para>One or more of the elements of <paramref name="urlPrefixes"/> is already registered.</para>
        /// </exception>
        public WebServer(HttpListenerMode mode, X509Certificate2 certificate, params string[] urlPrefixes)
            : this(new WebServerOptions()
                .WithMode(mode)
                .WithCertificate(certificate)
                .WithUrlPrefixes(urlPrefixes))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer"/> class.
        /// </summary>
        /// <param name="options">A <see cref="WebServerOptions"/> object used to configure this instance.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public WebServer(WebServerOptions options)
            : base(options)
        {
            DrainStarted = _drainStarted.Token;
            Listener = CreateHttpListener();
        }
        internal WebServer(WebServerOptions options, bool nativeHttp3)
            : base(options)
        {
            DrainStarted = _drainStarted.Token;
            if (nativeHttp3 && options.Mode is not HttpListenerMode.EmbedIOHttp3 and not HttpListenerMode.EmbedIOCombined)
                throw new ArgumentException("Native HTTP/3 requires an HTTP/3 hosting mode.", nameof(options));
            Listener = CreateHttpListener(nativeHttp3);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServer"/> class.
        /// </summary>
        /// <param name="configure">A callback that will be used to configure
        /// the server's options.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public WebServer(Action<WebServerOptions> configure)
            : base(configure)
        {
            DrainStarted = _drainStarted.Token;
            Listener = CreateHttpListener();
        }

        /// <summary>
        /// Gets the underlying HTTP listener.
        /// </summary>
        public IHttpListener Listener { get; }

        /// <summary>
        /// <para>Gets a token that is canceled when <see cref="DrainAsync"/> is first called, before the listener stops
        /// accepting new work.</para>
        /// <para>Applications can register on it to end long-lived work themselves, such as event streams or
        /// WebSockets they manage outside a <see cref="WebSockets.WebSocketModule"/> configured with
        /// <see cref="WebSockets.WebSocketModule.CloseOnDrain"/>, so the drain need not wait for its deadline.</para>
        /// </summary>
        /// <remarks>
        /// <para>Callbacks registered on the token run synchronously in the call to <see cref="DrainAsync"/>; keep them
        /// short. An exception thrown by a callback is logged and does not stop the drain.</para>
        /// <para>The token is not canceled by <c>RunAsync</c> cancellation or disposal, which remain immediate stops.</para>
        /// </remarks>
        public CancellationToken DrainStarted { get; }

        /// <summary>Stops accepting new work and lets accepted responses finish within a deadline.</summary>
        /// <param name="timeout">The maximum drain interval before remaining connections are aborted.</param>
        /// <param name="cancellationToken">Cancellation aborts remaining connections immediately.</param>
        /// <returns>A task completing when listener transport cleanup finishes.</returns>
        /// <remarks>Supported by HTTP/3, exclusively owned managed TCP endpoints, and their combined listener. RunAsync cancellation and disposal remain immediate.
        /// Concurrent calls share the first drain deadline. Await this operation outside request callbacks.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The timeout is nonpositive or exceeds the timer range.</exception>
        /// <exception cref="NotSupportedException">The selected listener does not support graceful drain.</exception>
        public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            cancellationToken.ThrowIfCancellationRequested();
            if (Listener is not IGracefulHttpListener graceful)
                throw new NotSupportedException("The selected listener does not support graceful drain.");
            SignalDrainStarted();
            return graceful.DrainAsync(timeout, cancellationToken);
        }

        // Once per server: drain-aware modules first, so they can close long-lived connections while their
        // transports are alive, then the application's callbacks.
        private void SignalDrainStarted()
        {
            if (Interlocked.Exchange(ref _drainSignaled, 1) != 0)
                return;

            NotifyDrainStarted(Modules);
            try
            {
                _drainStarted.Cancel();
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(LogSource, "A DrainStarted callback threw; the drain continues.");
            }
        }

        private void NotifyDrainStarted(IEnumerable<IWebModule> modules)
        {
            foreach (var module in modules)
            {
                if (module is EmbedIO.Internal.IDrainAwareModule aware)
                {
                    try
                    {
                        aware.OnDrainStarted();
                    }
                    catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
                    {
                        ex.Log(LogSource, "A module failed to prepare for the drain; the drain continues.");
                    }
                }

                if (module is IWebModuleContainer container)
                    NotifyDrainStarted(container.Modules);
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _disposeStarted, 1);
                try
                {
                    Listener.Dispose();
                }
                catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
                {
                    ex.Log(LogSource, "Exception thrown while disposing HTTP listener.");
                }

                _drainStarted.Dispose();

                "Listener closed.".Info(LogSource);
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        protected override void Prepare(CancellationToken cancellationToken)
        {
            Listener.Start();
            "Started HTTP Listener".Info(LogSource);

            // close port when the cancellation token is cancelled
            _ = cancellationToken.Register(() => Listener?.Stop());
        }

        /// <inheritdoc />
        protected override async Task ProcessRequestsAsync(CancellationToken cancellationToken)
        {
            // TCP connections hand each admitted request straight to the handler on
            // their own thread. The loop below only serves listeners without that
            // path, currently HTTP/3, and ends when the listener stops.
            if (Listener is IDispatchingHttpListener dispatching)
                dispatching.SetDispatcher(context => DispatchAsync(context, cancellationToken));
            while (!cancellationToken.IsCancellationRequested && (Listener?.IsListening ?? false))
            {
                IHttpContextImpl context;
                try { context = await Listener.GetContextAsync(cancellationToken).ConfigureAwait(false); }
                catch (ListenerDrainedException) { return; }
                // Explicit disposal may win after the listening snapshot but
                // before accept starts. Only expected stop failures end this loop.
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0) { return; }
                // An application stopping the listener, directly or through disposal,
                // ends the run normally; other listener failures still propagate.
                catch (System.Net.HttpListenerException error) when (error.ErrorCode == 995 && (Volatile.Read(ref _disposeStarted) != 0 || !(Listener?.IsListening ?? false))) { return; }

                // Once accepted, the context must reach the handler's cleanup even
                // if shutdown wins before dispatch. The context still carries cancellation.
                _ = Task.Run(() => DispatchAsync(context, cancellationToken));
            }
        }

        private Task DispatchAsync(IHttpContextImpl context, CancellationToken cancellationToken)
        {
            Task handling;
            try
            {
                context.CancellationToken = cancellationToken;
                context.Route = RouteMatch.UnsafeFromRoot(UrlPath.Normalize(context.Request.Url.AbsolutePath, false));
                handling = DoHandleContextAsync(context);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // The handler never started, so nothing else will complete this
                // response. The faulted task lets the owning connection close.
                error.Log(LogSource, $"[{context.Id}] Request dispatch failed before handling.");
                return Task.FromException(error);
            }
            // The connection waits for the response, not for the handler, which may
            // outlive it. This server therefore owns the handler's task: a fault the
            // handler did not catch is observed and logged here instead of going
            // unobserved. A handler that completed inline needs no continuation.
            if (!handling.IsCompleted)
                _ = handling.ContinueWith(static (task, state) => ((WebServer)(state ?? throw new InvalidOperationException("Missing dispatch owner."))).ObserveHandlingFault(task),
                    this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return handling;
        }

        private void ObserveHandlingFault(Task handling)
        {
            try { handling.Exception?.Log(LogSource, "A request handler faulted after dispatch."); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // A failing trace listener cannot turn an observed fault into another.
            }
        }

        /// <inheritdoc />
        protected override void OnFatalException() => Listener?.Dispose();

        private static IHttpListener CreateHttp3Listener(X509Certificate2? certificate, Net.ServerCertificateSource? certificateSource,
            bool combined = false, bool native = false, EmbedIO.Internal.RequestPolicyResolver? bodyPolicy = null)
        {
#if NET10_0_OR_GREATER
            if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && System.Net.Quic.QuicListener.IsSupported)
            {
                if (certificateSource != null)
                    return combined ? new CombinedHttpListener(certificateSource, native, bodyPolicy) : new Net.Internal.Http3.Http3Listener(certificateSource, native, bodyPolicy);

                return combined ? new CombinedHttpListener(certificate, native, bodyPolicy) : new Net.Internal.Http3.Http3Listener(certificate, native, bodyPolicy);
            }
#endif
            throw new PlatformNotSupportedException("HTTP/3 requires the .NET 10 asset and native QUIC support.");
        }

        private IHttpListener CreateHttpListener(bool nativeHttp3 = false)
        {
            var bodyPolicy = EmbedIO.Internal.RequestPolicyResolver.Create(Options);
            // QUIC connections are not governed by the TCP connection limits.
            if (Options.Mode == HttpListenerMode.EmbedIOHttp3 && Options.HasConnectionLimits)
                throw new NotSupportedException(
                    "MaximumConnections, RequestHeadersTimeout and KeepAliveTimeout apply to TCP listeners. Use EmbedIO or EmbedIOCombined mode.");

            // A source replaces the fixed certificate settings, whose getter would otherwise load or register one.
            X509Certificate2? CertificateOrNull() => Options.CertificateSource != null ? null : Options.Certificate;

            IHttpListener DoCreate() => Options.Mode switch
            {
                HttpListenerMode.EmbedIOHttp3 => CreateHttp3Listener(CertificateOrNull(), Options.CertificateSource, false, nativeHttp3, bodyPolicy),
                HttpListenerMode.EmbedIOCombined => CreateHttp3Listener(CertificateOrNull(), Options.CertificateSource, true, nativeHttp3, bodyPolicy),
                _ when (int)Options.Mode == 1 => throw new NotSupportedException(
                    "Neo v2 no longer supports the Microsoft HTTP listener. Select EmbedIO, EmbedIOHttp3 or EmbedIOCombined."),
                _ => Options.CertificateSource != null
                    ? new Net.HttpListener(Options.CertificateSource, bodyPolicy)
                    : new Net.HttpListener(Options.Certificate, bodyPolicy)
            };

            // Refuse TLS settings the selected listener cannot honor before creating it.
            var tlsPolicy = Options.TlsPolicy;
            tlsPolicy?.EnsureSupported(Options.Mode is HttpListenerMode.EmbedIOHttp3 or HttpListenerMode.EmbedIOCombined, nativeHttp3);
            var listener = DoCreate();
            $"Running HTTPListener: {listener.Name}".Info(LogSource);

            // With a body limit, 100 Continue waits until a handler reads the body, so a
            // request refused before dispatch never invites its upload.
            var deferContinue = Options.HasBodyLimits;
            var connections = Options.CreateConnectionPolicy();
            if (listener is Net.HttpListener tcp)
            {
                tcp.DeferContinue = deferContinue;
                tcp.ConnectionPolicy = connections;
                tcp.TlsPolicy = tlsPolicy;
                tcp.Telemetry = Telemetry;
            }
#if NET10_0_OR_GREATER
            else if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (listener is CombinedHttpListener combined)
                {
                    combined.DeferContinue = deferContinue;
                    combined.ConnectionPolicy = connections;
                    combined.TlsPolicy = tlsPolicy;
                    combined.Telemetry = Telemetry;
                }
                else if (listener is Net.Internal.Http3.Http3Listener quic)
                {
                    quic.DeferContinue = deferContinue;
                    quic.TlsPolicy = tlsPolicy;
                    quic.Telemetry = Telemetry;
                }
            }
#endif

            foreach (var prefix in Options.UrlPrefixes)
            {
                var urlPrefix = new string(prefix?.ToCharArray());

                if (!urlPrefix.EndsWith("/")) urlPrefix += "/";

                listener.AddPrefix(urlPrefix);
                $"Web server prefix '{urlPrefix}' added.".Info(LogSource);
            }

            return listener;
        }
    }
}
