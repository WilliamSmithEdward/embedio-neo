#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
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
    internal sealed class Http3Listener : IHttpListener
    {
        private readonly object _sync = new();
        private readonly X509Certificate2 _certificate;
        private readonly List<string> _prefixes = new();
        private Session? _session;
        private bool _disposed;
        private bool _stopping;
        internal Http3Listener(X509Certificate2? certificate)
        {
            if (certificate == null || !certificate.HasPrivateKey)
                throw new ArgumentException("HTTP/3 requires a certificate with its private key.", nameof(certificate));
            _certificate = certificate;
        }
        public bool IgnoreWriteExceptions { get; set; } = true;
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
                _session = Session.Start(_prefixes.Select(value => new ListenerPrefix(value)).ToArray(), _certificate);
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
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
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
                throw new HttpListenerException(995, "The listener stopped accepting requests.");
            }
        }
        private sealed class Session : IDisposable
        {
            internal readonly Channel<MultiplexedContext> Contexts = Channel.CreateBounded<MultiplexedContext>(new BoundedChannelOptions(256)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            private readonly CancellationTokenSource _stop = new();
            internal bool IsRunning => !_stop.IsCancellationRequested;
            private readonly Dictionary<QuicListener, ListenerPrefix[]> _listeners = new();
            private readonly List<Task> _accepts = new();
            private readonly object _gate = new();
            private readonly Dictionary<long, Task> _connections = new();
            private long _next;
            private Session() { }
            internal static Session Start(ListenerPrefix[] prefixes, X509Certificate2 certificate)
            {
                var session = new Session();
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
                        var listener = QuicListener.ListenAsync(new QuicListenerOptions
                        {
                            ListenEndPoint = binding.Key,
                            ListenBacklog = 128,
                            ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                            {
                                DefaultCloseErrorCode = 0x100,
                                DefaultStreamErrorCode = 0x10c,
                                MaxInboundBidirectionalStreams = 128,
                                MaxInboundUnidirectionalStreams = 16,
                                IdleTimeout = TimeSpan.FromSeconds(60),
                                HandshakeTimeout = TimeSpan.FromSeconds(10),
                                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                                { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = certificate }
                            })
                        }).AsTask().GetAwaiter().GetResult();
                        session._listeners.Add(listener, binding.Value.ToArray());
                    }
                    // Publish accept loops only after all bindings succeeded.
                    foreach (var listener in session._listeners)
                        session._accepts.Add(Task.Run(() => session.AcceptAsync(listener.Key, listener.Value)));
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
            private async Task AcceptAsync(QuicListener listener, ListenerPrefix[] prefixes)
            {
                try
                {
                    while (true)
                    {
                        QuicConnection connection;
                        try { connection = await listener.AcceptConnectionAsync(_stop.Token).ConfigureAwait(false); }
                        catch (Exception error) when (!_stop.IsCancellationRequested && (error is AuthenticationException
                            || error is QuicException quic && quic.QuicError is QuicError.ConnectionAborted or QuicError.ConnectionTimeout or QuicError.ConnectionIdle or QuicError.TransportError))
                        {
                            // The BCL reports failed peer handshakes through Accept.
                            // They do not invalidate the listening socket or other peers.
                            continue;
                        }
                        var rejected = false;
                        lock (_gate)
                        {
                            if (_connections.Count >= 256 || _stop.IsCancellationRequested) rejected = true;
                            else
                            {
                                var id = ++_next;
                                _connections.Add(id, Task.Run(() => RunConnectionAsync(id, connection, prefixes)));
                            }
                        }
                        if (rejected)
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            try { await connection.CloseAsync(0x107, deadline.Token).ConfigureAwait(false); }
                            catch (Exception error) when (error is QuicException or OperationCanceledException) { }
                            finally { await connection.DisposeAsync().ConfigureAwait(false); }
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    error.Log(NameForLog, "HTTP/3 accept loop failed.");
                    Contexts.Writer.TryComplete(error);
                    _stop.Cancel();
                }
            }
            private const string NameForLog = "HTTP/3 listener";
            private async Task RunConnectionAsync(long id, QuicConnection connection, ListenerPrefix[] prefixes)
            {
                var local = connection.LocalEndPoint;
                var remote = connection.RemoteEndPoint;
                try { await Http3QuicConnection.RunAsync(connection, exchange => DispatchAsync(exchange, local, remote, prefixes), _stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                { if (!_stop.IsCancellationRequested) error.Log(NameForLog, "HTTP/3 connection ended with an error."); }
                finally { lock (_gate) _connections.Remove(id); }
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
            private async Task DispatchAsync(Http3QuicExchange exchange, IPEndPoint local, IPEndPoint remote, ListenerPrefix[] prefixes)
            {
                var context = new MultiplexedContext(exchange, local, remote, true);
                try
                {
                    if (!Matches(context.Request.Url, local, prefixes)) { context.Response.StatusCode = 404; return; }
                    await Contexts.Writer.WriteAsync(context, exchange.CancellationToken).ConfigureAwait(false);
                    await context.Completion.WaitAsync(exchange.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try { context.Close(); }
                    catch (Exception error) when (exchange.CancellationToken.IsCancellationRequested && ExceptionPolicy.IsRecoverable(error)) { }
                    if (context.Completion.IsFaulted) _ = context.Completion.Exception;
                }
            }
            public void Dispose()
            {
                _stop.Cancel();
                Contexts.Writer.TryComplete();
                foreach (var listener in _listeners.Keys) listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Task.WhenAll(_accepts).GetAwaiter().GetResult();
                Task[] connections;
                lock (_gate) connections = _connections.Values.ToArray();
                Task.WhenAll(connections).GetAwaiter().GetResult();
                while (Contexts.Reader.TryRead(out _)) { }
                _stop.Dispose();
            }
        }
    }
}
#endif
