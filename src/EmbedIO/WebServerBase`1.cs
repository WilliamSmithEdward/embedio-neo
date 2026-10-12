using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.Configuration;
using EmbedIO.Diagnostics;

namespace EmbedIO
{
    /// <summary>
    /// Base class for <see cref="IWebServer" /> implementations.
    /// </summary>
    /// <typeparam name="TOptions">The type of the options object used to configure an instance.</typeparam>
    /// <seealso cref="IHttpContextHandler" />
    /// <seealso cref="IWebServer" />
    public abstract class WebServerBase<TOptions> : ConfiguredObject, IWebServer, IHttpContextHandler
        where TOptions : WebServerOptionsBase, new()
    {
        private static readonly double MillisecondsPerTimestamp = 1000.0 / Stopwatch.Frequency;

        private readonly WebModuleCollection _modules;

        private readonly MimeTypeCustomizer _mimeTypeCustomizer = new MimeTypeCustomizer();

        private ExceptionHandlerCallback _onUnhandledException = ExceptionHandler.Default;
        private HttpExceptionHandlerCallback _onHttpException = HttpExceptionHandler.Default;

        private WebServerState _state = WebServerState.Created;
        private int _runStarted;
        private int _disposed;
        private int _lifetimeArrivals;

        // Owned by the server: linked to the RunAsync token and canceled when the
        // server is disposed or RunAsync completes, so modules, session purging and
        // in-flight contexts observe every way the server can end.
        private CancellationTokenSource? _lifetime;

        // Opt-in request admission (WebServerOptionsBase.MaximumConcurrentRequests).
        // Created when the server starts; null keeps the original unlimited dispatch.
        private SemaphoreSlim? _requestSlots;
        private int _waitingRequests;

        private ISessionManager? _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServerBase{TOptions}" /> class.
        /// </summary>
        protected WebServerBase()
            : this(new TOptions(), null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServerBase{TOptions}" /> class.
        /// </summary>
        /// <param name="options">A <typeparamref name="TOptions"/> instance that will be used
        /// to configure the server.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        protected WebServerBase(TOptions options)
            : this(Validate.NotNull(nameof(options), options), null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebServerBase{TOptions}" /> class.
        /// </summary>
        /// <param name="configure">A callback that will be used to configure
        /// the server's options.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        protected WebServerBase(Action<TOptions> configure)
            : this(new TOptions(), Validate.NotNull(nameof(configure), configure))
        {
        }

        private WebServerBase(TOptions options, Action<TOptions>? configure)
        {
            Options = options;
            LogSource = GetType().Name;
            _modules = new WebModuleCollection(LogSource);

            configure?.Invoke(Options);
            Options.Lock();
            if (Options.EnableTelemetry) _telemetry = EmbedIO.Internal.ServerTelemetry.Create(Options.TelemetryServerName);
        }

        private readonly EmbedIO.Internal.ServerTelemetry? _telemetry;

        /// <summary>
        /// Finalizes an instance of the <see cref="WebServerBase{TOptions}"/> class.
        /// </summary>
        ~WebServerBase()
        {
            Dispose(false);
        }

        /// <inheritdoc />
        public event WebServerStateChangedEventHandler? StateChanged;

        /// <inheritdoc />
        public IComponentCollection<IWebModule> Modules => _modules;

        /// <summary>
        /// Gets the options object used to configure this instance.
        /// </summary>
        public TOptions Options { get; }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The server's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">this property is being set to <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>The default value for this property is <see cref="ExceptionHandler.Default"/>.</para>
        /// </remarks>
        /// <seealso cref="ExceptionHandler"/>
        public ExceptionHandlerCallback OnUnhandledException
        {
            get => _onUnhandledException;
            set
            {
                EnsureConfigurationNotLocked();
                _onUnhandledException = Validate.NotNull(nameof(value), value);
            }
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The server's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">this property is being set to <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>The default value for this property is <see cref="HttpExceptionHandler.Default"/>.</para>
        /// </remarks>
        /// <seealso cref="HttpExceptionHandler"/>
        public HttpExceptionHandlerCallback OnHttpException
        {
            get => _onHttpException;
            set
            {
                EnsureConfigurationNotLocked();
                _onHttpException = Validate.NotNull(nameof(value), value);
            }
        }

        /// <inheritdoc />
        public ISessionManager? SessionManager
        {
            get => _sessionManager;
            set
            {
                EnsureConfigurationNotLocked();
                _sessionManager = value;
            }
        }

        /// <inheritdoc />
        public WebServerState State
        {
            get => _state;
            private set
            {
                if (value == _state) return;

                var oldState = _state;
                _state = value;

                if (_state != WebServerState.Created)
                {
                    LockConfiguration();
                }

                StateChanged?.Invoke(this, new WebServerStateChangedEventArgs(oldState, value));
            }
        }

        /// <summary>
        /// Gets a string to use as a source for log messages.
        /// </summary>
        protected string LogSource { get; }

        /// <inheritdoc />
        public Task HandleContextAsync(IHttpContextImpl context)
        {
            if (State > WebServerState.Listening)
                throw new InvalidOperationException("The web server has already been stopped.");

            if (State < WebServerState.Listening)
                throw new InvalidOperationException("The web server has not been started yet.");

            return DoHandleContextAsync(context);
        }

        /// <inheritdoc />
        public string? GetMimeType(string extension)
            => _mimeTypeCustomizer.GetMimeType(extension);

        /// <inheritdoc />
        public bool TryDetermineCompression(string mimeType, out bool preferCompression)
            => _mimeTypeCustomizer.TryDetermineCompression(mimeType, out preferCompression);

        /// <inheritdoc />
        public void AddCustomMimeType(string extension, string mimeType)
            => _mimeTypeCustomizer.AddCustomMimeType(extension, mimeType);

        /// <inheritdoc />
        public void PreferCompression(string mimeType, bool preferCompression)
            => _mimeTypeCustomizer.PreferCompression(mimeType, preferCompression);

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The method was already called.</exception>
        /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);

            // A second invocation must not reprepare or change the state of an active server.
            // A stopped server is final: its listener, modules and token are spent.
            if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            {
                throw new InvalidOperationException(State == WebServerState.Stopped
                    ? "The web server has been stopped and cannot be restarted; create a new instance."
                    : "The web server has already been started.");
            }

            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lifetime = lifetime;
            if (Volatile.Read(ref _disposed) != 0)
                lifetime.Cancel();
            var lifetimeToken = lifetime.Token;

            var startupCompleted = false;
            try
            {
                State = WebServerState.Loading;
                lifetimeToken.ThrowIfCancellationRequested();
                Prepare(lifetimeToken);

                if (Options.MaximumConcurrentRequests is int slots)
                    _requestSlots = new SemaphoreSlim(slots, slots);

                lifetimeToken.ThrowIfCancellationRequested();
                _sessionManager?.Start(lifetimeToken);
                _modules.StartAll(lifetimeToken);

                lifetimeToken.ThrowIfCancellationRequested();
                State = WebServerState.Listening;
                startupCompleted = true;
                await ProcessRequestsAsync(lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                "Operation canceled.".Debug(LogSource);
            }
            catch (Exception)
            {
                // Preparation may have bound a port before a module or callback failed.
                // Release the listener without replacing the error the caller needs to diagnose.
                if (!startupCompleted)
                {
                    try { OnFatalException(); }
                    catch (Exception cleanupError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(cleanupError)) { cleanupError.Log(LogSource, "Exception while stopping the failed server."); }
                }
                throw;
            }
            finally
            {
                "Cleaning up".Info(LogSource);
                // Work the accept loop left behind, such as a handler awaiting its
                // context token, ends with the server rather than running unobserved.
                CancelLifetime(lifetime);
                State = WebServerState.Stopped;
                ArriveAtLifetimeEnd();
            }
        }

        private void CancelLifetime(CancellationTokenSource? lifetime)
        {
            if (lifetime == null) return;
            try { lifetime.Cancel(); }
            catch (AggregateException error) when (error.InnerExceptions.All(EmbedIO.Internal.ExceptionPolicy.IsRecoverable))
            {
                error.Log(LogSource, "Exception thrown by a server lifetime cancellation callback.");
            }
        }

        // Disposal and run completion each arrive here once, after their own cancel
        // call returned. The second arrival releases the link to the caller's token,
        // so neither path can dispose the source while the other is still canceling
        // it. Tokens already handed out keep reporting cancellation after release.
        private void ArriveAtLifetimeEnd()
        {
            if (Interlocked.Increment(ref _lifetimeArrivals) == 2)
                _lifetime?.Dispose();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Asynchronously handles a received request.
        /// </summary>
        /// <param name="context">The context of the request.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected Task DoHandleContextAsync(IHttpContextImpl context)
        {
            // Preserve the asynchronous failure the async method reported for a null context.
            if (context is null) return Task.FromException(new System.NullReferenceException());
            // Context creation can precede admission by a keep-alive idle interval.
            // Measure this handling operation without changing the public Age clock.
            var requestStarted = Stopwatch.GetTimestamp();
            context.SupportCompressedRequests = Options.SupportCompressedRequests;
            RequestDecompressionPolicy.Associate(context, Options.DecompressionPolicy);
            context.MimeTypeProviders.Push(this);
            // Without telemetry the request runs in a single state machine, as before.
            return _telemetry is { } telemetry
                ? HandleWithTelemetryAsync(context, requestStarted, telemetry)
                : HandleContextCoreAsync(context, requestStarted, null);
        }

        // Telemetry spans the whole request, through response completion and context close.
        private async Task HandleWithTelemetryAsync(IHttpContextImpl context, long requestStarted, EmbedIO.Internal.ServerTelemetry telemetry)
        {
            var scope = telemetry.Start(context);
            try { await HandleContextCoreAsync(context, requestStarted, scope).ConfigureAwait(false); }
            finally { telemetry.Stop(context, scope); }
        }

        private async Task HandleContextCoreAsync(IHttpContextImpl context, long requestStarted, EmbedIO.Internal.RequestTelemetry? telemetry)
        {
            var admitted = false;
            try
            {
                $"[{context.Id}] {context.Request.SafeGetRemoteEndpointStr()}: {context.Request.HttpMethod} {context.Request.Url.PathAndQuery} - {context.Request.UserAgent}"
                    .Debug(LogSource);

                if (SessionManager != null)
                    context.Session = new SessionProxy(context, SessionManager);

                try
                {
                    if (context.CancellationToken.IsCancellationRequested)
                        return;

                    var serverToken = context.CancellationToken;
                    // A matching route policy replaces the server-wide body and deadline values.
                    var routeLimits = Options.FindRouteRequestLimits(context.RequestedPath);
                    CancellationTokenSource? deadline = null;
                    try
                    {
                        try
                        {
                            // The deadline covers queueing and handling. The context token is
                            // restored before completion so a late deadline cannot abort it.
                            if (RequestLimitPolicy.Resolve(routeLimits?.RequestTimeout, Options.RequestTimeout) is TimeSpan timeout)
                            {
                                deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                                deadline.CancelAfter(timeout);
                                context.CancellationToken = deadline.Token;
                            }

                            CheckHeaderLimits(context);
                            admitted = await AdmitAsync(context).ConfigureAwait(false);
                            ApplyBodyPolicies(context, routeLimits);

                            // RFC 10008 requires QUERY media-type information before processing.
                            if (context.Request.HttpMethod == "QUERY"
                                && !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out _))
                                throw HttpException.BadRequest("QUERY requires a valid Content-Type header.");

                            // Return a 404 (Not Found) response if no module handled the response.
                            await _modules.DispatchRequestAsync(context).ConfigureAwait(false);
                            if (!context.IsHandled)
                            {
                                $"[{context.Id}] No module generated a response. Sending 404 - Not Found".Error(LogSource);
                                throw HttpException.NotFound("No module was able to serve the requested path.");
                            }
                        }
                        finally
                        {
                            if (deadline != null)
                            {
                                context.CancellationToken = serverToken;
                                deadline.Dispose();
                            }
                        }
                    }
                    catch (Exception exception) when (EmbedIO.Internal.ServerTelemetry.Observe(telemetry, exception))
                    {
                        throw; // Never reached: the filter only records the exception type.
                    }
                    // Exception filters run before the inner finally, so the deadline token is
                    // still current here; this clause must precede the general cancellation one.
                    catch (OperationCanceledException) when (deadline != null && deadline.IsCancellationRequested && !serverToken.IsCancellationRequested)
                    {
                        await HttpExceptionHandler.Handle(LogSource, context,
                            new HttpException(503, "The request exceeded its configured deadline."), _onHttpException).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                    {
                        throw; // Let outer catch block handle it
                    }
                    catch (HttpListenerException)
                    {
                        throw; // Let outer catch block handle it
                    }
                    catch (Exception exception) when (context.Request is Net.Internal.HttpListenerRequest request && request.IsBodyFramingError(exception) && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                    {
                        if (context is Net.Internal.HttpListenerContext owned && owned.HttpListenerResponse.HeadersSent)
                            owned.HttpListenerResponse.Abort();
                        else
                        {
                            context.Response.KeepAlive = false;
                            context.Response.StatusCode = 400;
                            await HttpExceptionHandler.Handle(LogSource, context,
                                HttpException.BadRequest("Invalid or incomplete request body."), _onHttpException).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (context is Net.Internal.MultiplexedContext tunnel && tunnel.HasAcceptedTunnel && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                    {
                        await tunnel.AbortTunnelAsync(exception).ConfigureAwait(false);
                        exception.Log(LogSource, $"[{context.Id}] Tunnel application failed.");
                    }
                    catch (Exception exception) when (context is Net.Internal.HttpListenerContext tunnel && tunnel.HasAcceptedTunnel && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                    {
                        tunnel.HttpListenerResponse.Abort();
                        exception.Log(LogSource, $"[{context.Id}] Tunnel application failed.");
                    }
                    catch (Exception exception) when (exception is IHttpException && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                    {
                        await HttpExceptionHandler.Handle(LogSource, context, exception, _onHttpException)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                    {
                        await ExceptionHandler.Handle(LogSource, context, exception, _onUnhandledException, _onHttpException)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Request cleanup and context closure must run even when flushing fails.
                    try
                    {
                        await context.Response.OutputStream.FlushAsync(context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            await HttpContextExtensions.CompleteRequestAsync(context).ConfigureAwait(false);
                        }
                        finally
                        {
                            try
                            {
                                // Completion callbacks must run even if flushing or cleanup fails.
                                // Multiplexed FIN writes must not block a worker waiting for I/O.
                                if (context is Net.Internal.MultiplexedContext multiplexed)
                                    await multiplexed.CloseAsync().ConfigureAwait(false);
                                else if (context is Net.Internal.HttpListenerContext managed && managed.HasAcceptedTunnel)
                                    await managed.CloseTunnelAsync().ConfigureAwait(false);
                                else context.Close();
                            }
                            finally
                            {
                                // A slot covers the whole request, including its response.
                                if (admitted) ReleaseRequestSlot();
                            }
                        }
                    }

                    var handlingMilliseconds = (long)((Stopwatch.GetTimestamp() - requestStarted) * MillisecondsPerTimestamp);
                    var statusCode = context.Response.StatusCode;
                    var statusDescription = context.Response.StatusDescription;
                    var sendChunked = context.Response.SendChunked;
                    var contentLength = context.Response.ContentLength64;
                    $"[{context.Id}] {context.Request.HttpMethod} {context.Request.Url.AbsolutePath}: \"{statusCode} {statusDescription}\" sent in {handlingMilliseconds}ms ({(sendChunked ? "chunked" : contentLength.ToString(CultureInfo.InvariantCulture) + " bytes")})"
                        .Info(LogSource);
                }
            }
            catch (Exception exception) when (EmbedIO.Internal.ServerTelemetry.ObserveCleanup(telemetry, exception))
            {
                throw; // Never reached: the filter only records the exception type.
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                $"[{context.Id}] Operation canceled.".Debug(LogSource);
            }
            catch (HttpListenerException ex)
            {
                ex.Log(LogSource, $"[{context.Id}] Listener exception.");
            }
            catch (Exception ex) when (context.Request is Net.Internal.HttpListenerRequest request && request.HasBodyFramingFailure && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                // Completing an invalid body can encounter the aborted connection.
                // That request cannot take down the listener shared by other clients.
                ex.Log(LogSource, $"[{context.Id}] Invalid request body connection closed.");
            }
            catch (Exception ex) when (context is Net.Internal.HttpListenerContext tunnel && tunnel.HasAcceptedTunnel && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(LogSource, $"[{context.Id}] HTTP/1 tunnel connection closed.");
            }
            catch (Exception ex) when (context is Net.Internal.MultiplexedContext && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                // A failed or reset request stream must not dispose the listener
                // shared by unrelated HTTP/2 and HTTP/3 connections. Terminal
                // context cleanup above still closes and completes that stream.
                ex.Log(LogSource, $"[{context.Id}] Multiplexed request stream failed.");
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(LogSource, $"[{context.Id}] Fatal exception.");
                OnFatalException();
            }
        }

        // Returns true when a slot was taken and must be released after the request
        // completes. Throws HTTP 503 when every slot is busy and the queue is full.
        private async Task<bool> AdmitAsync(IHttpContext context)
        {
            var slots = _requestSlots;
            if (slots == null) return false;
            try
            {
                if (slots.Wait(0)) return true;
                if (Interlocked.Increment(ref _waitingRequests) > Options.RequestQueueLimit)
                {
                    _ = Interlocked.Decrement(ref _waitingRequests);
                    throw new HttpException(503, "The server is handling its maximum number of requests.");
                }

                try { await slots.WaitAsync(context.CancellationToken).ConfigureAwait(false); }
                finally { _ = Interlocked.Decrement(ref _waitingRequests); }
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The server was disposed; its lifetime token is already canceled.
                throw new OperationCanceledException("The web server has stopped.", context.CancellationToken);
            }
        }

        // Disposal may race with a request that is still finishing; the slot no longer matters.
        private void ReleaseRequestSlot()
        {
            try { _requestSlots?.Release(); }
            catch (ObjectDisposedException) { }
        }

        // Applies MaximumRequestHeaderBytes and MaximumRequestHeaderCount before admission,
        // so an oversized request never takes a slot. The parsers count received field lines.
        private void CheckHeaderLimits(IHttpContext context)
        {
            var maximumBytes = Options.MaximumRequestHeaderBytes;
            var maximumCount = Options.MaximumRequestHeaderCount;
            if ((maximumBytes == null && maximumCount == null)
                || !(context.Request is EmbedIO.Internal.IRequestPolicyTarget request))
                return;
            if (request.HeaderFieldBytes > maximumBytes || request.HeaderFieldCount > maximumCount)
            {
                StopReuseForUnreadBody(context);
                throw new HttpException(431, "Request header fields exceed the configured limit.");
            }
        }

        // HTTP/1 keep-alive would drain a body the server refused to read; close instead.
        // HTTP/2 and HTTP/3 reset the stream when the response completes before its body.
        private static void StopReuseForUnreadBody(IHttpContext context)
        {
            if (context is Net.Internal.HttpListenerContext && context.Request.HasEntityBody
                && !EmbedIO.Internal.ResponseCommitment.IsCommitted(context))
                context.Response.KeepAlive = false;
        }

        // Applies MaximumRequestBodyBytes and RequestBodyReadTimeout. A declared length over
        // the limit is rejected before dispatch, and the body stream counts what is actually
        // read and bounds how long each read waits. HTTP/1 stops connection reuse on any
        // rejection, because keep-alive would otherwise drain the unread remainder, which is
        // unbounded for chunked bodies. HTTP/2 and HTTP/3 reset the stream when the response
        // completes before its body.
        private void ApplyBodyPolicies(IHttpContext context, RequestLimitPolicy? routeLimits)
        {
            var limit = RequestLimitPolicy.Resolve(routeLimits?.MaximumRequestBodyBytes, Options.MaximumRequestBodyBytes);
            var readTimeout = RequestLimitPolicy.Resolve(routeLimits?.RequestBodyReadTimeout, Options.RequestBodyReadTimeout);
            if (limit == null && readTimeout == null) return;
            var request = context.Request;
            // CONNECT carries tunnel data, not a request body.
            if (request.HttpMethod == "CONNECT") return;
            var http1 = context is Net.Internal.HttpListenerContext;
            void StopReuse()
            {
                if (http1 && !EmbedIO.Internal.ResponseCommitment.IsCommitted(context))
                    context.Response.KeepAlive = false;
            }

            if (limit is long maximum && request.HasEntityBody && request.ContentLength64 > maximum)
            {
                StopReuse();
                throw new HttpException(413, EmbedIO.Internal.LimitedRequestStream.TransferredLimitMessage);
            }

            if (!(request is EmbedIO.Internal.IRequestPolicyTarget target)) return;
            if (limit is long bytes) target.LimitBody(bytes, StopReuse);
            if (readTimeout is TimeSpan timeout) target.TimeBodyReads(timeout, StopReuse);
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            base.OnBeforeLockConfiguration();

            _mimeTypeCustomizer.Lock();
            _modules.Lock();
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            // Repeated or concurrent disposal is a no-op after the first call.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CancelLifetime(_lifetime);
            // Waiters observe the canceled lifetime token; late releases tolerate disposal.
            _requestSlots?.Dispose();
            _modules.Dispose();
            // A run that never started cannot start now (RunAsync throws), so count
            // its arrival here and release whatever lifetime exists.
            if (Volatile.Read(ref _runStarted) == 0) ArriveAtLifetimeEnd();
            ArriveAtLifetimeEnd();
        }

        /// <summary>
        /// Prepares a web server for running.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> used to stop the web server.</param>
        protected virtual void Prepare(CancellationToken cancellationToken)
        {
        }

        /// <summary>
        /// <para>Asynchronously receives requests and processes them.</para>
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> used to stop the web server.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected abstract Task ProcessRequestsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// <para>Called when an exception is caught in the web server's request processing loop.</para>
        /// <para>This method should tell the server socket to stop accepting further requests.</para>
        /// </summary>
        protected abstract void OnFatalException();
    }
}
