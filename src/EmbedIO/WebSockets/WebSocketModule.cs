using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Utilities;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.WebSockets
{
    /// <summary>
    /// A base class for modules that handle WebSocket connections.
    /// </summary>
    /// <remarks>
    /// <para>Each WebSocket server has a list of WebSocket subprotocols it can accept.</para>
    /// <para>When a client initiates a WebSocket opening handshake:</para>
    /// <list type="bullet">
    /// <item><description>if the list of accepted subprotocols is empty,
    /// the connection is accepted only if no <c>SecWebSocketProtocol</c>
    /// header is present in the request;</description></item>
    /// <item><description>if the list of accepted subprotocols is not empty,
    /// the connection is accepted only if one or more <c>SecWebSocketProtocol</c>
    /// headers are present in the request and one of them specifies one
    /// of the subprotocols in the list. The first subprotocol specified by the client
    /// that is also present in the module's list is then specified in the
    /// handshake response.</description></item>
    /// </list>
    /// If a connection is not accepted because of a subprotocol mismatch,
    /// a <c>400 Bad Request</c> response is sent back to the client. The response
    /// contains one or more <c>SecWebSocketProtocol</c> headers that specify
    /// the list of accepted subprotocols (if any).
    /// </remarks>
    public abstract class WebSocketModule : WebModuleBase, IDisposable, IDrainAwareModule
    {
        private const string DrainCloseReason = "Server is draining";

        private const int ReceiveBufferSize = 2048;

        // A finite default bounds what one client can make the server buffer per message.
        private const int DefaultMaxMessageSize = 4 * 1024 * 1024;

        private readonly bool _enableConnectionWatchdog;
        private readonly List<string> _protocols = new List<string>();
        private readonly ConcurrentDictionary<string, IWebSocketContext> _contexts = new ConcurrentDictionary<string, IWebSocketContext>();
        private bool _isDisposing;
        private bool _closeOnDrain;
        private int _draining;
        // Contexts already sent the drain close; each is closed once.
        private readonly ConcurrentDictionary<string, byte> _drainClosed = new ConcurrentDictionary<string, byte>();
        private int _maxMessageSize;
        private Internal.WebSocketDeliveryBudget? _deliveryBudget;
        private WebSocketReceiveLimits? _receiveLimits;
        internal Internal.WebSocketDeliveryBudget? DeliveryBudget => _deliveryBudget;
        /// <summary>Gets or sets optional outstanding-delivery limits; null preserves existing behavior.</summary>
        /// <remarks>
        /// Configure before the module starts. Exhaustion rejects a new message with close status 1008.
        /// This does not change MaxMessageSize, callback overlap, initialization deadlines or drain behavior.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Module configuration is locked.</exception>
        protected WebSocketReceiveLimits? ReceiveLimits
        {
            get => _receiveLimits;
            set
            {
                EnsureConfigurationNotLocked();
                var limits = value?.Limits;
                var budget = limits != null && (limits.ConnectionMessages.HasValue || limits.ConnectionBytes.HasValue
                    || limits.ModuleMessages.HasValue || limits.ModuleBytes.HasValue) ? new Internal.WebSocketDeliveryBudget(limits) : null;
                _receiveLimits = value;
                _deliveryBudget = budget;
            }
        }
        private TimeSpan _keepAliveInterval;
        private Encoding _encoding;
        private PeriodicTask? _connectionWatchdog;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketModule" /> class.
        /// </summary>
        /// <param name="requestPath">The URL path of the WebSocket endpoint to serve.</param>
        /// <param name="enableConnectionWatchdog">If set to <see langword="true"/>,
        /// contexts representing closed connections will automatically be purged
        /// from <see cref="ActiveContexts"/> every 30 seconds..</param>
        protected WebSocketModule(string requestPath, bool enableConnectionWatchdog)
            : base(requestPath)
        {
            _enableConnectionWatchdog = enableConnectionWatchdog;
            _maxMessageSize = DefaultMaxMessageSize;
            _keepAliveInterval = TimeSpan.FromSeconds(30);
            _encoding = Encoding.UTF8;
        }

        /// <inheritdoc />
        public sealed override bool IsFinalHandler => true;

        /// <summary>
        /// <para>Gets or sets the maximum size of a received message.
        /// If a message exceeding the maximum size is received from a client,
        /// the connection is closed automatically with status 1009 (message too big).</para>
        /// <para>The default value is 4 MiB (4,194,304 bytes). Zero disables message size checking.</para>
        /// </summary>
        protected int MaxMessageSize
        {
            get => _maxMessageSize;
            set
            {
                EnsureConfigurationNotLocked();
                _maxMessageSize = Math.Max(value, 0);
            }
        }

        /// <summary>
        /// <para>Gets or sets whether a graceful drain closes this module's WebSockets. The default is
        /// <see langword="false"/>: drain waits for them until its deadline, then cuts their connections.</para>
        /// <para>When <see langword="true"/>, <see cref="WebServer.DrainAsync"/> sends each open WebSocket a close
        /// with status 1001 (<see cref="CloseStatusCode.Away"/>) as the drain starts, and closes the same way any
        /// WebSocket the module accepts after that. The drain can then finish once clients complete the closing
        /// handshake, still bounded by its deadline.</para>
        /// </summary>
        /// <remarks>
        /// <para>Applies to WebSockets over HTTP/1.1, HTTP/2 and HTTP/3. Messages already being sent are sent
        /// before the close. <see cref="OnClientDisconnectedAsync"/> runs once per connection, as for any close.</para>
        /// <para>Set this before the module starts. Stopping the server through <c>RunAsync</c> cancellation or
        /// disposal is not a drain and does not depend on this setting: the server then sends each open WebSocket
        /// a 1001 close without a reason, and waits at most one second for the closes before it stops the
        /// listener.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the module's
        /// configuration is locked.</exception>
        public bool CloseOnDrain
        {
            get => _closeOnDrain;
            set
            {
                EnsureConfigurationNotLocked();
                _closeOnDrain = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets how often the server pings each WebSocket client. The default is 30 seconds.</para>
        /// <para>A connection whose client has not answered the previous ping when the next one is due is closed.
        /// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> disables keep-alive.</para>
        /// </summary>
        /// <remarks>
        /// <para>Set this before the module starts. Any pong from the client counts as an answer, and messages do
        /// not delay the pings.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">This property is being set to a value
        /// that is too small to be acceptable.</exception>
        protected TimeSpan KeepAliveInterval
        {
            get => _keepAliveInterval;
            set
            {
                EnsureConfigurationNotLocked();
                if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(value), "The specified keep-alive interval is too small.");

                _keepAliveInterval = value;
            }
        }

        /// <summary>
        /// Gets the <see cref="Encoding"/> used by the <see cref="SendAsync(IWebSocketContext,string)"/> method
        /// to send a string. The default is <see cref="System.Text.Encoding.UTF8"/> per the WebSocket specification.
        /// </summary>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        protected Encoding Encoding
        {
            get => _encoding;
            set
            {
                EnsureConfigurationNotLocked();
                _encoding = Validate.NotNull(nameof(value), value);
            }
        }

        /// <summary>
        /// Gets a list of <see cref="IWebSocketContext"/> interfaces
        /// representing the currently connected clients.
        /// </summary>
        protected IReadOnlyList<IWebSocketContext> ActiveContexts
        {
            get
            {
                // ConcurrentDictionary<TKey,TValue>.Values, although declared as ICollection<TValue>,
                // will probably return a ReadOnlyCollection<TValue>, which implements IReadOnlyList<TValue>:
                // https://referencesource.microsoft.com/#mscorlib/system/Collections/Concurrent/ConcurrentDictionary.cs,fe55c11912af21d2
                // https://github.com/dotnet/corefx/blob/master/src/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs#L1990
                // https://github.com/mono/mono/blob/master/mcs/class/referencesource/mscorlib/system/collections/Concurrent/ConcurrentDictionary.cs#L1961
                // However there is no formal guarantee, so be ready to convert to a list, just in case.
                var values = _contexts.Values;
                return values is IReadOnlyList<IWebSocketContext> list
                    ? list
                    : values.ToList();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        protected sealed override async Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new System.NullReferenceException();
            // The WebSocket endpoint must match exactly, giving a RequestedPath of "/".
            // In all other cases the path is longer, so there's no need to compare strings here.
            if (context.RequestedPath.Length > 1)
                throw RequestHandler.PassThrough();

            // A generic CONNECT carrier routed here is not a WebSocket handshake.
            // Reject it as an unsupported route before attempting socket acceptance.
            if (context is IHttpTunnelContext && context.Request.HttpMethod == "CONNECT"
                && !context.Request.IsWebSocketRequest)
                throw HttpException.NotFound("This endpoint serves WebSocket connections only.");
            var requestedProtocols = context.Request.Headers.GetValues(HttpHeaderNames.SecWebSocketProtocol)
                                         ?.SelectMany(s => s.SplitByComma(StringSplitOptions.RemoveEmptyEntries))
                                         .Select(s => s.Trim())
                                         .Where(s => s.Length > 0)
                                         .ToArray()
                                  ?? Array.Empty<string>();
            string acceptedProtocol;
            bool acceptConnection;
            if (_protocols.Count > 0)
            {
                acceptedProtocol = requestedProtocols.FirstOrDefault(p => _protocols.Contains(p)) ?? string.Empty;
                acceptConnection = acceptedProtocol.Length > 0;
            }
            else
            {
                acceptedProtocol = string.Empty;
                acceptConnection = requestedProtocols.Length == 0;
            }

            if (!acceptConnection)
            {
                $"{BaseRoute} - Rejecting WebSocket connection: no subprotocol was accepted.".Debug(nameof(WebSocketModule));
                foreach (var protocol in _protocols)
                    context.Response.Headers.Add(HttpHeaderNames.SecWebSocketProtocol, protocol);

                // Not throwing a HTTP exception here because a WebSocket client
                // does not care about nice, formatted messages.
                context.Response.SetEmptyResponse((int)HttpStatusCode.BadRequest);
                return;
            }

            var contextImpl = context.GetImplementation();
            $"{BaseRoute} - Accepting WebSocket connection with subprotocol \"{acceptedProtocol}\"".Debug(nameof(WebSocketModule));
            // The managed socket enforces the limit from its first frame.
            Internal.WebSocket.SetAcceptedMaxMessageSize(_maxMessageSize);
            Internal.WebSocket.SetAcceptedMessageConsumerPending(true);
            var delivery = _deliveryBudget?.CreateConnection();
            // Every acceptance has its own policy, including an unlimited one.
            Internal.WebSocket.SetAcceptedDeliveryBudget(delivery);
            IWebSocketContext webSocketContext;
            try
            {
                webSocketContext = await contextImpl.AcceptWebSocketAsync(
                    requestedProtocols,
                    acceptedProtocol,
                    ReceiveBufferSize,
                    KeepAliveInterval,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch { delivery?.StopAdmission(); throw; }
            finally
            {
                // Keep this async method's initialization callbacks outside the
                // acceptance scope. Its caller retains its own execution context.
                Internal.WebSocket.SetAcceptedDeliveryBudget(null);
                Internal.WebSocket.SetAcceptedMessageConsumerPending(false);
            }

            PurgeDisconnectedContexts();
            _ = _contexts.TryAdd(webSocketContext.Id, webSocketContext);

            // Accepted while a drain was starting: the drain's close may have missed this one.
            if (_closeOnDrain && Volatile.Read(ref _draining) != 0)
                CloseForDrain(webSocketContext);
            var request = EmbedIO.Internal.RequestTelemetry.Of(context);
            var session = request?.Owner?.WebSocketStarted(request);

            $"{BaseRoute} - WebSocket connection accepted - There are now {_contexts.Count} sockets connected."
                .Debug(nameof(WebSocketModule));

            try
            {
                await OnClientConnectedAsync(webSocketContext).ConfigureAwait(false);
                await ProcessEmbedIOContext(webSocketContext, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(nameof(WebSocketModule));
            }
            finally
            {
                session?.Stop();
                ((Internal.WebSocket)webSocketContext.WebSocket).AbandonPendingMessageConsumer();
                // once the loop is completed or connection aborted, remove the WebSocket
                RemoveWebSocket(webSocketContext);
            }
        }

        void IDrainAwareModule.OnDrainStarted()
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0 || !_closeOnDrain)
                return;

            foreach (var context in _contexts.Values)
                CloseForDrain(context);
        }

        // RunAsync cancellation or disposal: every open WebSocket gets a 1001 close while its
        // transport is alive, and the task completes when every listed socket is released: its
        // client answered the close, its own close timeout expired, or its transport failed. The
        // server bounds the wait, once for all sockets. A socket already closing is waited for
        // the same way, for example because its request handler saw the canceled token first and
        // disposed it. A written close frame is not enough: when the stop then ends an HTTP/2
        // connection, the client aborts the stream and drops a close frame it has received but
        // not read. The close carries no reason, so it matches the one WebSocket disposal sends.
        Task IDrainAwareModule.CloseForStopAsync()
        {
            var closing = new List<Task>();
            foreach (var context in _contexts.Values)
            {
                if (context.WebSocket is Internal.WebSocket managed)
                {
                    if (managed.State == WebSocketState.Open)
                        _ = CloseGoingAwayAsync(context, string.Empty);
                    closing.Add(managed.Released);
                }
                else if (context.WebSocket?.State == WebSocketState.Open)
                {
                    closing.Add(CloseGoingAwayAsync(context, string.Empty));
                }
            }

            return Task.WhenAll(closing);
        }

        private void CloseForDrain(IWebSocketContext context)
        {
            if (!_drainClosed.TryAdd(context.Id, 0))
                return;

            _ = CloseGoingAwayAsync(context, DrainCloseReason);
        }

        private async Task CloseGoingAwayAsync(IWebSocketContext context, string reason)
        {
            try
            {
                if (context.WebSocket is Internal.WebSocket managed)
                    await managed.CloseAfterPendingSendAsync(CloseStatusCode.Away, reason).ConfigureAwait(false);
                else if (context.WebSocket.State == WebSocketState.Open)
                    await context.WebSocket.CloseAsync(CloseStatusCode.Away, reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                // The connection may already be closing or gone; the drain deadline or the stop budget still bounds it.
                $"{BaseRoute} - Could not send the going-away close: {ex.Message}".Debug(nameof(WebSocketModule));
            }
        }

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            if (_enableConnectionWatchdog)
            {
                _connectionWatchdog = new PeriodicTask(
                    TimeSpan.FromSeconds(30),
                    ct =>
                    {
                        PurgeDisconnectedContexts();
                        return Task.CompletedTask;
                    },
                    cancellationToken);
            }
        }

        /// <summary>
        /// Adds a WebSocket subprotocol to the list of protocols supported by a <see cref="WebSocketModule"/>.
        /// </summary>
        /// <param name="protocol">The protocol name to add to the list.</param>
        /// <exception cref="ArgumentNullException"><paramref name="protocol"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="protocol"/> contains one or more invalid characters, as defined
        /// in <see href="https://tools.ietf.org/html/rfc6455#section-4.3">RFC6455, Section 4.3</see>.</para>
        /// <para>- or -</para>
        /// <para><paramref name="protocol"/> is already in the list of supported protocols.</para>
        /// </exception>
        /// <exception cref="InvalidOperationException">The <see cref="WebSocketModule"/> has already been started.</exception>
        /// <seealso cref="Validate.Rfc2616Token"/>
        /// <seealso cref="AddProtocols(IEnumerable{string})"/>
        /// <seealso cref="AddProtocols(string[])"/>
        protected void AddProtocol(string protocol)
        {
            protocol = Validate.Rfc2616Token(nameof(protocol), protocol);

            EnsureConfigurationNotLocked();

            if (_protocols.Contains(protocol))
                throw new ArgumentException("Duplicate WebSocket protocol name.", nameof(protocol));

            _protocols.Add(protocol);
        }

        /// <summary>
        /// Adds one or more WebSocket subprotocols to the list of protocols supported by a <see cref="WebSocketModule"/>.
        /// </summary>
        /// <param name="protocols">The protocol names to add to the list.</param>
        /// <exception cref="ArgumentNullException">
        /// <para><paramref name="protocols"/> is <see langword="null"/>.</para>
        /// <para>- or -</para>
        /// <para>One or more of the strings in <paramref name="protocols"/> is <see langword="null"/>.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para>One or more of the strings in <paramref name="protocols"/>
        /// contains one or more invalid characters, as defined
        /// in <see href="https://tools.ietf.org/html/rfc6455#section-4.3">RFC6455, Section 4.3</see>.</para>
        /// <para>- or -</para>
        /// <para>One or more of the strings in <paramref name="protocols"/>
        /// is already in the list of supported protocols.</para>
        /// </exception>
        /// <exception cref="InvalidOperationException">The <see cref="WebSocketModule"/> has already been started.</exception>
        /// <remarks>
        /// <para>This method enumerates <paramref name="protocols"/> just once; hence, if an exception is thrown
        /// because one of the specified protocols is <see langword="null"/> or contains invalid characters,
        /// any preceding protocol is added to the list of supported protocols.</para>
        /// </remarks>
        /// <seealso cref="Validate.Rfc2616Token"/>
        /// <seealso cref="AddProtocol"/>
        /// <seealso cref="AddProtocols(string[])"/>
        protected void AddProtocols(IEnumerable<string> protocols)
        {
            protocols = Validate.NotNull(nameof(protocols), protocols);

            EnsureConfigurationNotLocked();

            foreach (var protocol in protocols.Select(p => Validate.Rfc2616Token(nameof(protocols), p)))
            {
                if (_protocols.Contains(protocol))
                    throw new ArgumentException("Duplicate WebSocket protocol name.", nameof(protocols));

                _protocols.Add(protocol);
            }
        }

        /// <summary>
        /// Adds one or more WebSocket subprotocols to the list of protocols supported by a <see cref="WebSocketModule"/>.
        /// </summary>
        /// <param name="protocols">The protocol names to add to the list.</param>
        /// <exception cref="ArgumentNullException">
        /// <para><paramref name="protocols"/> is <see langword="null"/>.</para>
        /// <para>- or -</para>
        /// <para>One or more of the strings in <paramref name="protocols"/> is <see langword="null"/>.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para>One or more of the strings in <paramref name="protocols"/>
        /// contains one or more invalid characters, as defined
        /// in <see href="https://tools.ietf.org/html/rfc6455#section-4.3">RFC6455, Section 4.3</see>.</para>
        /// <para>- or -</para>
        /// <para>One or more of the strings in <paramref name="protocols"/>
        /// is already in the list of supported protocols.</para>
        /// </exception>
        /// <exception cref="InvalidOperationException">The <see cref="WebSocketModule"/> has already been started.</exception>
        /// <remarks>
        /// <para>This method performs validation checks on all specified <paramref name="protocols"/> before adding them
        /// to the list of supported protocols; hence, if an exception is thrown
        /// because one of the specified protocols is <see langword="null"/> or contains invalid characters,
        /// none of the specified protocol names are added to the list.</para>
        /// </remarks>
        /// <seealso cref="Validate.Rfc2616Token"/>
        /// <seealso cref="AddProtocol"/>
        /// <seealso cref="AddProtocols(IEnumerable{string})"/>
        protected void AddProtocols(params string[] protocols)
        {
            protocols = Validate.NotNull(nameof(protocols), protocols);

            var names = new HashSet<string>(_protocols, StringComparer.Ordinal);
            if (protocols.Select(p => Validate.Rfc2616Token(nameof(protocols), p)).Any(protocol => !names.Add(protocol)))
                throw new ArgumentException("Duplicate WebSocket protocol name.", nameof(protocols));

            EnsureConfigurationNotLocked();

            _protocols.AddRange(protocols);
        }

        /// <summary>
        /// Sends a text payload.
        /// </summary>
        /// <param name="context">The web socket.</param>
        /// <param name="payload">The payload.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected async Task SendAsync(IWebSocketContext context, string payload)
        {
            if (context is null) throw new System.NullReferenceException();
            try
            {
                var buffer = _encoding.GetBytes(payload ?? string.Empty);

                await context.WebSocket.SendAsync(buffer, true, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(nameof(WebSocketModule));
            }
        }

        /// <summary>
        /// Sends a binary payload.
        /// </summary>
        /// <param name="context">The web socket.</param>
        /// <param name="payload">The payload.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected async Task SendAsync(IWebSocketContext context, byte[] payload)
        {
            if (context is null) throw new System.NullReferenceException();
            try
            {
                await context.WebSocket.SendAsync(payload ?? Array.Empty<byte>(), false, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(nameof(WebSocketModule));
            }
        }

        /// <summary>
        /// Broadcasts the specified payload to all connected WebSocket clients.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected Task BroadcastAsync(byte[] payload)
            => Task.WhenAll(_contexts.Values.Select(c => SendAsync(c, payload)));

        /// <summary>
        /// Broadcasts the specified payload to selected WebSocket clients.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="selector">A callback function that must return <see langword="true"/>
        /// for each context to be included in the broadcast.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected Task BroadcastAsync(byte[] payload, Func<IWebSocketContext, bool> selector)
            => Task.WhenAll(_contexts.Values.Where(Validate.NotNull(nameof(selector), selector)).Select(c => SendAsync(c, payload)));

        /// <summary>
        /// Broadcasts the specified payload to all connected WebSocket clients.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected Task BroadcastAsync(string payload)
            => Task.WhenAll(_contexts.Values.Select(c => SendAsync(c, payload)));

        /// <summary>
        /// Broadcasts the specified payload to selected WebSocket clients.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="selector">A callback function that must return <see langword="true"/>
        /// for each context to be included in the broadcast.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected Task BroadcastAsync(string payload, Func<IWebSocketContext, bool> selector)
            => Task.WhenAll(_contexts.Values.Where(Validate.NotNull(nameof(selector), selector)).Select(c => SendAsync(c, payload)));

        /// <summary>
        /// Closes the specified web socket, removes it and disposes it.
        /// </summary>
        /// <param name="context">The web socket.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected async Task CloseAsync(IWebSocketContext context)
        {
            if (context == null)
                return;

            try
            {
                await context.WebSocket.CloseAsync(context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(nameof(WebSocketModule));
            }
            finally
            {
                RemoveWebSocket(context);
            }
        }

        /// <summary>
        /// Called when this WebSocket server receives a full message (EndOfMessage) from a client.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="buffer">The buffer.</param>
        /// <param name="result">The result.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected abstract Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result);

        /// <summary>
        /// Called when this WebSocket server receives a message frame regardless if the frame represents the EndOfMessage.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="buffer">The buffer.</param>
        /// <param name="result">The result.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected virtual Task OnFrameReceivedAsync(
            IWebSocketContext context,
            byte[] buffer,
            IWebSocketReceiveResult result)
            => Task.CompletedTask;

        /// <summary>
        /// Called when this WebSocket server accepts a new client.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected virtual Task OnClientConnectedAsync(IWebSocketContext context) => Task.CompletedTask;

        /// <summary>
        /// Called when the server has removed a connected client for any reason.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>A <see cref="Task"/> representing the ongoing operation.</returns>
        protected virtual Task OnClientDisconnectedAsync(IWebSocketContext context) => Task.CompletedTask;

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposing)
                return;

            _isDisposing = true;

            if (disposing)
            {
                _connectionWatchdog?.Dispose();
                Task.WhenAll(_contexts.Values.Select(CloseAsync)).ConfigureAwait(false).GetAwaiter().GetResult();
                PurgeDisconnectedContexts();
            }
        }

        private void RemoveWebSocket(IWebSocketContext context)
        {
            // Disposed while still listed: a server stop that runs meanwhile waits for its release.
            if (!_contexts.ContainsKey(context.Id))
                return;
            context.WebSocket?.Dispose();
            if (!_contexts.TryRemove(context.Id, out _))
                return;
            _ = _drainClosed.TryRemove(context.Id, out _);

            // OnClientDisconnectedAsync is better called in its own task,
            // so it may call methods that require a lock on _contextsAccess.
            // Otherwise, calling e.g. Broadcast would result in a deadlock.
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnClientDisconnectedAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    $"[{context.Id}] OnClientDisconnectedAsync was canceled.".Debug(nameof(WebSocketModule));
                }
                catch (Exception e) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(e))
                {
                    e.Log(nameof(WebSocketModule), $"[{context.Id}] Exception in OnClientDisconnectedAsync.");
                }
            });
        }

        private void PurgeDisconnectedContexts()
        {
            var contexts = _contexts.Values;
            var totalCount = _contexts.Count;
            var purgedCount = 0;
            foreach (var context in contexts)
            {
                if (context.WebSocket == null || context.WebSocket.State == WebSocketState.Open)
                    continue;
                // Transport closure can precede initialization and the delivery of
                // already received messages. Leave that context to its handler.
                if (context.WebSocket is Internal.WebSocket managed && !managed.IsApplicationCloseCompleted)
                    continue;

                RemoveWebSocket(context);
                purgedCount++;
            }

            $"{BaseRoute} - Purged {purgedCount} of {totalCount} sockets."
                .Debug(nameof(WebSocketModule));
        }

        private async Task ProcessEmbedIOContext(IWebSocketContext context, CancellationToken cancellationToken)
        {
            ((Internal.WebSocket)context.WebSocket).OnMessage += async (s, e) =>
            {
                try
                {
                    if (e.Opcode == Opcode.Close)
                    {
                        await context.WebSocket.CloseAsync(context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (e.DeliveryLease == null) await OnMessageReceivedAsync(
                                context,
                                e.RawData,
                                new Internal.WebSocketReceiveResult(e.RawData.Length, e.Opcode))
                            .ConfigureAwait(false);
                        else await DeliverLimitedMessageAsync(context, e).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex)) { ex.Log(nameof(WebSocketModule)); }
            };

            await ((Internal.WebSocket)context.WebSocket).WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
        }

        private Task DeliverLimitedMessageAsync(IWebSocketContext context, Internal.MessageEventArgs message)
        {
            var lease = message.DeliveryLease ?? throw new InvalidOperationException("Missing message delivery reservation.");
            return lease.DeliverAsync(() => OnMessageReceivedAsync(context, message.RawData,
                new Internal.WebSocketReceiveResult(message.RawData.Length, message.Opcode)));
        }
    }
}
