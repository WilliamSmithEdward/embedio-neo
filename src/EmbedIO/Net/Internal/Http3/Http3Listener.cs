#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3Listener : IHttpListener, IGracefulHttpListener, IDispatchingHttpListener
    {
        private readonly object _sync = new();
        private readonly ServerCertificateSource _certificates;
        private readonly bool _native;
        private readonly RequestPolicyResolver? _bodyPolicy;
        private readonly List<string> _prefixes = new();
        private Session? _session;
        private bool _disposed;
        private bool _stopping;
        private Func<IHttpContextImpl, Task>? _dispatcher;
        internal Http3Listener(X509Certificate2? certificate) : this(certificate, false) { }
        internal Http3Listener(X509Certificate2? certificate, bool native) : this(certificate, native, null) { }
        internal Http3Listener(X509Certificate2? certificate, bool native, RequestPolicyResolver? bodyPolicy)
            : this(FixedSource(certificate), native, bodyPolicy) { }
        internal Http3Listener(ServerCertificateSource certificates, bool native, RequestPolicyResolver? bodyPolicy)
        {
            _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
            _native = native;
            _bodyPolicy = bodyPolicy;
        }
        private static ServerCertificateSource FixedSource(X509Certificate2? certificate)
            => certificate != null && certificate.HasPrivateKey
                ? ServerCertificateSource.Fixed(certificate)
                : throw new ArgumentException("HTTP/3 requires a certificate with its private key.", nameof(certificate));
        public bool IgnoreWriteExceptions { get; set; } = true;
        // Set by a server with body policies: 100 Continue waits for the first body read.
        internal bool DeferContinue { get; set; }
        // TLS settings for QUIC handshakes. Set before Start.
        internal ServerTlsPolicy? TlsPolicy { get; set; }
        // Records QUIC connections. Set before Start.
        internal EmbedIO.Internal.ServerTelemetry? Telemetry { get; set; }
        public bool IsListening { get { lock (_sync) return _session?.IsRunning ?? false; } }
        public string Name => "EmbedIO HTTP/3 listener";
        public List<string> Prefixes { get { lock (_sync) return new List<string>(_prefixes); } }
        public void AddPrefix(string urlPrefix)
        {
            ListenerPrefix.CheckUri(urlPrefix);
            var prefix = new ListenerPrefix(urlPrefix);
            if (!prefix.Secure || !prefix.IsValid() || prefix.Path.Contains('?', StringComparison.Ordinal) || prefix.Path.Contains('#', StringComparison.Ordinal))
                throw new ArgumentException("HTTP/3 requires an HTTPS prefix with a valid path.", nameof(urlPrefix));
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http3Listener));
                if (_session != null) throw new InvalidOperationException("Stop the HTTP/3 listener before changing its prefixes.");
                if (_prefixes.Contains(urlPrefix)) throw new ArgumentException("The prefix is already registered.", nameof(urlPrefix));
                _prefixes.Add(urlPrefix);
            }
        }
        public void Start()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http3Listener));
                if (_session != null) return;
                if (_stopping) throw new InvalidOperationException("The previous HTTP/3 listener is still stopping.");
                if (_prefixes.Count == 0) throw new InvalidOperationException("At least one HTTPS prefix is required.");
                _session = Session.Start(_prefixes.Select(value => new ListenerPrefix(value)).ToArray(), _certificates, TlsPolicy, _native, DeferContinue, Telemetry, _bodyPolicy, _dispatcher);
            }
        }
        public void Stop()
        {
            Session? session;
            lock (_sync)
            {
                session = _session; _session = null;
                if (session == null) return;
                _stopping = true;
            }
            try { session.Dispose(); }
            finally { lock (_sync) _stopping = false; }
        }
        public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Session session;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http3Listener));
                if (_session == null) return Task.CompletedTask;
                session = _session;
                _stopping = true;
            }
            return DrainSessionAsync(session, timeout, cancellationToken);
        }
        private async Task DrainSessionAsync(Session session, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try { await session.DrainAsync(timeout, cancellationToken).ConfigureAwait(false); }
            finally
            {
                session.Dispose();
                lock (_sync)
                {
                    if (ReferenceEquals(_session, session)) { _session = null; _stopping = false; }
                }
            }
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
        }
        // The server installs its dispatcher once, before it serves. A session
        // started later begins with it, and a running one switches at once; a
        // request admitted before the install stays queued for GetContextAsync.
        void IDispatchingHttpListener.SetDispatcher(Func<IHttpContextImpl, Task> dispatcher)
        {
            lock (_sync)
            {
                _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
                _session?.SetDispatcher(dispatcher);
            }
        }
        public async Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
        {
            Session session;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http3Listener));
                session = _session ?? throw new HttpListenerException(995, "The listener is not accepting requests.");
            }
            try
            {
                while (true)
                {
                    var context = await session.Contexts.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (!context.CancellationToken.IsCancellationRequested) return context;
                }
            }
            catch (ChannelClosedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.IsDraining) throw new ListenerDrainedException();
                throw new HttpListenerException(995, "The listener stopped accepting requests.");
            }
        }
        private sealed class Session : IDisposable
        {
            internal readonly Channel<MultiplexedContext> Contexts = Channel.CreateBounded<MultiplexedContext>(new BoundedChannelOptions(256)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            private readonly CancellationTokenSource _stop = new();
            private readonly CancellationTokenSource _acceptStop = new();
            private readonly CancellationTokenSource _drain = new();
            private Task? _shutdown;
            private int _disposed;
            internal bool IsDraining { get; private set; }
            internal bool IsRunning => !_stop.IsCancellationRequested;
            private readonly Dictionary<Http3ListenerBinding, (ListenerPrefix[] Prefixes, IPEndPoint EndPoint)> _listeners = new();
            private readonly List<Task> _accepts = new();
            private readonly object _gate = new();
            private readonly Dictionary<long, Task> _connections = new();
            private long _next;
            private int _pendingHandshakes;
            private int _activeConnections;
            private bool _deferContinue;
            private RequestPolicyResolver? _bodyPolicy;
            private EmbedIO.Internal.ServerTelemetry? _telemetry;
            private Func<IHttpContextImpl, Task>? _dispatcher;
            private Session() { }
            internal void SetDispatcher(Func<IHttpContextImpl, Task> dispatcher) => Volatile.Write(ref _dispatcher, dispatcher);
            internal static Session Start(ListenerPrefix[] prefixes, ServerCertificateSource certificates, ServerTlsPolicy? policy, bool native, bool deferContinue, EmbedIO.Internal.ServerTelemetry? telemetry, RequestPolicyResolver? bodyPolicy, Func<IHttpContextImpl, Task>? dispatcher)
            {
                var session = new Session { _deferContinue = deferContinue, _telemetry = telemetry, _bodyPolicy = bodyPolicy, _dispatcher = dispatcher };
                try
                {
                    var endpoints = new Dictionary<IPEndPoint, List<ListenerPrefix>>();
                    foreach (var prefix in prefixes)
                    {
                        foreach (var address in Resolve(prefix.Host))
                        {
                            var endpoint = new IPEndPoint(address, prefix.Port);
                            if (!endpoints.TryGetValue(endpoint, out var routes))
                            { routes = new List<ListenerPrefix>(); endpoints.Add(endpoint, routes); }
                            routes.Add(prefix);
                        }
                    }
                    foreach (var binding in endpoints)
                    {
                        var listener = QuicEndpointReleases.Shared.Bind(binding.Key,
                            () => Http3ListenerBinding.Bind(binding.Key, certificates, policy, native));
                        session._listeners.Add(listener, (binding.Value.ToArray(), binding.Key));
                    }
                    // Publish accept loops only after all bindings succeeded.
                    foreach (var listener in session._listeners)
                        session._accepts.Add(Task.Run(() => session.AcceptAsync(listener.Key, listener.Value.Prefixes)));
                    return session;
                }
                catch { session.Dispose(); throw; }
            }
            private static IEnumerable<IPAddress> Resolve(string host)
            {
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    yield return IPAddress.Loopback;
                    if (EndPointManager.UseIpv6 && Socket.OSSupportsIPv6) yield return IPAddress.IPv6Loopback;
                }
                else if (host is "*" or "+" or "0.0.0.0")
                    yield return EndPointManager.UseIpv6 && Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
                else if (IPAddress.TryParse(host, out var address)) yield return address;
                else
                {
                    var addresses = Dns.GetHostAddresses(host);
                    if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                    yield return addresses[0];
                }
            }
            private async Task AcceptAsync(Http3ListenerBinding listener, ListenerPrefix[] prefixes)
            {
                try
                {
                    while (true)
                    {
                        Http3AcceptedConnection connection;
                        try { connection = await listener.AcceptAsync(_acceptStop.Token).ConfigureAwait(false); }
                        catch (Exception error) when (!_acceptStop.IsCancellationRequested && (error is AuthenticationException
                            || error is QuicException quic && quic.QuicError is QuicError.ConnectionAborted or QuicError.ConnectionTimeout or QuicError.ConnectionIdle or QuicError.TransportError))
                        {
                            // The BCL reports failed peer handshakes through Accept.
                            // They do not invalidate the listening socket or other peers.
                            continue;
                        }
                        var rejected = false;
                        var pending = connection.HandshakePending;
                        lock (_gate)
                        {
                            if ((pending ? _pendingHandshakes >= 512 : _activeConnections >= 256) || _acceptStop.IsCancellationRequested) rejected = true;
                            else
                            {
                                var id = ++_next;
                                if (pending) _pendingHandshakes++; else _activeConnections++;
                                _connections.Add(id, Task.Run(() => RunConnectionAsync(id, connection, prefixes, pending)));
                            }
                        }
                        if (rejected)
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            try { await connection.RejectAsync(deadline.Token).ConfigureAwait(false); }
                            catch (Exception error) when (error is QuicException or OperationCanceledException) { }
                            finally { await connection.DisposeAsync().ConfigureAwait(false); }
                        }
                    }
                }
                catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_acceptStop.IsCancellationRequested) { }
                catch (ChannelClosedException) when (_acceptStop.IsCancellationRequested) { }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    error.Log(NameForLog, "HTTP/3 accept loop failed.");
                    Contexts.Writer.TryComplete(error);
                    _stop.Cancel();
                    _acceptStop.Cancel();
                }
            }
            private const string NameForLog = "HTTP/3 listener";
            private async Task RunConnectionAsync(long id, Http3AcceptedConnection connection, ListenerPrefix[] prefixes, bool pending)
            {
                var active = !pending;
                ServerTelemetry.LifetimeTelemetry? counted = null;
                try
                {
                    await connection.WaitReadyAsync(_stop.Token).ConfigureAwait(false);
                    if (pending)
                    {
                        lock (_gate)
                        {
                            _pendingHandshakes--;
                            pending = false;
                            if (_activeConnections < 256 && !_stop.IsCancellationRequested && !_drain.IsCancellationRequested)
                            { _activeConnections++; active = true; }
                        }
                        if (!active)
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await connection.RejectAsync(deadline.Token).ConfigureAwait(false);
                            return;
                        }
                    }
                    // Only handshaken connections the session admitted are counted.
                    if (prefixes.Length != 0) counted = _telemetry?.ConnectionStarted("quic", prefixes[0].Port);
                    var clientCertificate = connection.ClientCertificate;
                    await connection.RunAsync((exchange, local, remote) => DispatchAsync(exchange, local, remote, prefixes, clientCertificate),
                        _stop.Token, _drain.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                { if (!_stop.IsCancellationRequested) error.Log(NameForLog, "HTTP/3 connection ended with an error."); }
                finally
                {
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        counted?.Stop("3");
                        lock (_gate)
                        {
                            if (pending) _pendingHandshakes--;
                            if (active) _activeConnections--;
                            _connections.Remove(id);
                        }
                    }
                }
            }
            private static bool Matches(Uri uri, IPEndPoint local, ListenerPrefix[] prefixes)
            {
                if (uri.Scheme != Uri.UriSchemeHttps) return false;
                var path = WebUtility.UrlDecode(uri.AbsolutePath);
                var slash = path.EndsWith('/') ? path : path + "/";
                return prefixes.Any(prefix => prefix.Port == local.Port && prefix.Port == uri.Port
                    && (prefix.Host is "*" or "+" || string.Equals(prefix.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                    && (path.StartsWith(prefix.Path, StringComparison.Ordinal) || slash.StartsWith(prefix.Path, StringComparison.Ordinal)));
            }
            private async Task DispatchAsync(Http3QuicExchange exchange, IPEndPoint local, IPEndPoint remote, ListenerPrefix[] prefixes,
                X509Certificate2? clientCertificate)
            {
                var context = new MultiplexedContext(exchange, local, remote, true, clientCertificate);
                try
                {
                    if (!Matches(context.Request.Url, local, prefixes)) { context.Response.StatusCode = 404; return; }
                    // Generic extended CONNECT is authorized by the application.
                    // Preserve the dedicated WebSocket version negotiation here.
                    if (exchange.Request.Protocol == "websocket")
                    {
                        if (context.Request.Headers[HttpHeaderNames.SecWebSocketVersion] != "13")
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Headers[HttpHeaderNames.SecWebSocketVersion] = "13";
                            return;
                        }
                    }
                    _bodyPolicy?.Apply(context);
                    await context.SendContinueAsync(_deferContinue || RequestBodyPolicy.KnownRefusal(context.Request) != null).ConfigureAwait(false);
                    // With the server's dispatcher installed the handler starts here, on
                    // the request stream's own task; its task belongs to the server,
                    // which observes a fault it leaves uncaught, and is null on the pull
                    // path, where the context queues for GetContextAsync instead. The
                    // exchange waits for the response, not for the handler.
                    var handling = Volatile.Read(ref _dispatcher)?.Invoke(context);
                    if (handling == null) await Contexts.Writer.WriteAsync(context, exchange.CancellationToken).ConfigureAwait(false);
                    await context.Completion.WaitAsync(exchange.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try { await context.CloseAsync().ConfigureAwait(false); }
                    catch (Exception error) when (exchange.CancellationToken.IsCancellationRequested && ExceptionPolicy.IsRecoverable(error)) { }
                    if (context.Completion.IsFaulted) _ = context.Completion.Exception;
                }
            }
            internal async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                using var registration = cancellationToken.Register(Abort);
                await Shutdown(true, timeout).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            private void Abort()
            {
                try { _stop.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            private Task Shutdown(bool graceful, TimeSpan timeout)
            {
                lock (_gate)
                {
                    if (_shutdown != null) return _shutdown;
                    IsDraining = graceful;
                    _shutdown = Task.Run(() => ShutdownAsync(graceful, timeout));
                    return _shutdown;
                }
            }
            private async Task ShutdownAsync(bool graceful, TimeSpan timeout)
            {
                if (graceful) { _stop.CancelAfter(timeout); _drain.Cancel(); }
                else _stop.Cancel();
                _acceptStop.Cancel();
                try
                {
                    foreach (var listener in _listeners)
                    {
                        await listener.Key.DisposeAsync().ConfigureAwait(false);
                        QuicEndpointReleases.Shared.Record(listener.Value.EndPoint);
                    }
                    await Task.WhenAll(_accepts).ConfigureAwait(false);
                    Task[] connections;
                    lock (_gate) connections = _connections.Values.ToArray();
                    await Task.WhenAll(connections).ConfigureAwait(false);
                }
                finally
                {
                    _stop.Cancel();
                    Contexts.Writer.TryComplete();
                    while (Contexts.Reader.TryRead(out _)) { }
                }
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Abort();
                try { Shutdown(false, TimeSpan.Zero).GetAwaiter().GetResult(); }
                finally { _acceptStop.Dispose(); _drain.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
#endif
