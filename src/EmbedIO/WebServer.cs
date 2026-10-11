using System;
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
            Listener = CreateHttpListener();
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
            Listener = CreateHttpListener();
        }

        /// <summary>
        /// Gets the underlying HTTP listener.
        /// </summary>
        public IHttpListener Listener { get; }

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
            return graceful.DrainAsync(timeout, cancellationToken);
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
            while (!cancellationToken.IsCancellationRequested && (Listener?.IsListening ?? false))
            {
                IHttpContextImpl context;
                try { context = await Listener.GetContextAsync(cancellationToken).ConfigureAwait(false); }
                catch (ListenerDrainedException) { return; }
                // Explicit disposal may win after the listening snapshot but
                // before accept starts. Only expected stop failures end this loop.
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0) { return; }
                catch (System.Net.HttpListenerException error) when (error.ErrorCode == 995 && Volatile.Read(ref _disposeStarted) != 0) { return; }
                context.CancellationToken = cancellationToken;
                context.Route = RouteMatch.UnsafeFromRoot(UrlPath.Normalize(context.Request.Url.AbsolutePath, false));

                _ = Task.Run(() => DoHandleContextAsync(context), cancellationToken);
            }
        }

        /// <inheritdoc />
        protected override void OnFatalException() => Listener?.Dispose();

        private static IHttpListener CreateHttp3Listener(X509Certificate2? certificate, bool combined = false)
        {
#if NET10_0_OR_GREATER
            if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && System.Net.Quic.QuicListener.IsSupported)
                return combined ? new CombinedHttpListener(certificate) : new Net.Internal.Http3.Http3Listener(certificate);
#endif
            throw new PlatformNotSupportedException("HTTP/3 requires the .NET 10 asset and native QUIC support.");
        }

        private IHttpListener CreateHttpListener()
        {
            IHttpListener DoCreate() => Options.Mode switch
            {
                HttpListenerMode.EmbedIOHttp3 => CreateHttp3Listener(Options.Certificate),
                HttpListenerMode.EmbedIOCombined => CreateHttp3Listener(Options.Certificate, true),
                _ when (int)Options.Mode == 1 => throw new NotSupportedException(
                    "Neo v2 no longer supports the Microsoft HTTP listener. Select EmbedIO, EmbedIOHttp3 or EmbedIOCombined."),
                _ => new Net.HttpListener(Options.Certificate)
            };

            var listener = DoCreate();
            $"Running HTTPListener: {listener.Name}".Info(LogSource);

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
