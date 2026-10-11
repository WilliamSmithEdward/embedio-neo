#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal abstract class Http3ListenerBinding : IAsyncDisposable
    {
        internal abstract ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token);
        public abstract ValueTask DisposeAsync();
        internal static Http3ListenerBinding Bind(IPEndPoint endpoint, X509Certificate2 certificate, bool native)
            => native ? NativeBinding.Create(endpoint, certificate) : SystemBinding.Create(endpoint, certificate);

        private sealed class SystemBinding : Http3ListenerBinding
        {
            private readonly QuicListener _listener;
            private SystemBinding(QuicListener listener) { _listener = listener; }
            internal static SystemBinding Create(IPEndPoint endpoint, X509Certificate2 certificate)
            {
                var options = new QuicListenerOptions
                {
                    ListenEndPoint = endpoint,
                    ListenBacklog = 512,
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
                        { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = certificate },
                    }),
                };
                return new SystemBinding(QuicListener.ListenAsync(options).AsTask().GetAwaiter().GetResult());
            }
            internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token)
                => new SystemPeer(await _listener.AcceptConnectionAsync(token).ConfigureAwait(false));
            public override ValueTask DisposeAsync() => _listener.DisposeAsync();
        }
        private sealed class NativeBinding : Http3ListenerBinding
        {
            private readonly MsQuicApi _api;
            private readonly MsQuicRegistration _registration;
            private readonly MsQuicConfiguration _configuration;
            private readonly MsQuicNativeListener _listener;
            private NativeBinding(MsQuicApi api, MsQuicRegistration registration, MsQuicConfiguration configuration, MsQuicNativeListener listener)
            { _api = api; _registration = registration; _configuration = configuration; _listener = listener; }
            internal static NativeBinding Create(IPEndPoint endpoint, X509Certificate2 certificate)
            {
                MsQuicApi? api = null;
                MsQuicRegistration? registration = null;
                MsQuicConfiguration? configuration = null;
                MsQuicNativeListener? listener = null;
                try
                {
                    api = MsQuicApi.Open();
                    registration = api.CreateRegistration();
                    var alpn = new byte[] { (byte)'h', (byte)'3' };
                    configuration = registration.CreateHttp3ListenerConfiguration(alpn);
                    configuration.LoadServerCertificate(certificate);
                    listener = registration.CreateListener();
                    listener.EnableHttp3Acceptance();
                    listener.StartHttp3(endpoint, alpn);
                    return new NativeBinding(api, registration, configuration, listener);
                }
                catch
                {
                    try { listener?.Dispose(); }
                    finally
                    {
                        try { configuration?.Dispose(); }
                        finally { try { registration?.Dispose(); } finally { api?.Dispose(); } }
                    }
                    throw;
                }
            }
            internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token)
            {
                while (true)
                {
                    var connection = await _listener.AcceptForHttp3Async(token).ConfigureAwait(false);
                    try
                    {
                        connection.EnableStreamAcceptance();
                        connection.Configure(_configuration);
                        return new NativePeer(connection, _listener);
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException)
                    {
                        // A peer can reset between the native acceptance callback
                        // and configuration. Dispose that peer, keep listening.
                        try { connection.Dispose(); }
                        finally { _listener.ReleaseHttp3Admission(); }
                        token.ThrowIfCancellationRequested();
                    }
                    catch
                    {
                        try { connection.Dispose(); }
                        finally { _listener.ReleaseHttp3Admission(); }
                        throw;
                    }
                }
            }
            public override async ValueTask DisposeAsync()
            {
                try { await _listener.StopAsync().ConfigureAwait(false); }
                finally
                {
                    try { _listener.Dispose(); }
                    finally
                    {
                        try { _configuration.Dispose(); }
                        finally { try { _registration.Dispose(); } finally { _api.Dispose(); } }
                    }
                }
            }
        }
        private sealed class SystemPeer : Http3AcceptedConnection
        {
            private readonly QuicConnection _connection;
            private bool _transferred;
            internal SystemPeer(QuicConnection connection) { _connection = connection; }
            internal override bool HandshakePending => false;
            internal override ValueTask WaitReadyAsync(CancellationToken token) => ValueTask.CompletedTask;
            internal override Task RunAsync(Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch, CancellationToken abort, CancellationToken drain)
            {
                var local = _connection.LocalEndPoint;
                var remote = _connection.RemoteEndPoint;
                _transferred = true;
                return Http3QuicConnection.RunForListenerAsync(_connection, exchange => dispatch(exchange, local, remote),
                    abort, drain, TimeSpan.FromMilliseconds(uint.MaxValue - 1));
            }
            internal override ValueTask RejectAsync(CancellationToken token) => _connection.CloseAsync(0x107, token);
            public override ValueTask DisposeAsync() => _transferred ? ValueTask.CompletedTask : _connection.DisposeAsync();
        }
        private sealed class NativePeer : Http3AcceptedConnection
        {
            private readonly MsQuicNativeConnection _connection;
            private readonly MsQuicNativeListener _admission;
            private int _admissionReleased;
            private bool _transferred;
            internal NativePeer(MsQuicNativeConnection connection, MsQuicNativeListener admission)
            { _connection = connection; _admission = admission; }
            private void ReleaseAdmission()
            {
                if (Interlocked.Exchange(ref _admissionReleased, 1) == 0) _admission.ReleaseHttp3Admission();
            }
            internal override bool HandshakePending => !_connection.Connected.IsCompletedSuccessfully;
            internal override async ValueTask WaitReadyAsync(CancellationToken token)
            {
                try { await _connection.Connected.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
                finally { ReleaseAdmission(); }
            }
            internal override async Task RunAsync(Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch, CancellationToken abort, CancellationToken drain)
            {
                // Configure handshakes concurrently on connection workers; one
                // failed peer must not stall acceptance/configuration of others.
                var local = _connection.LocalEndPoint;
                var remote = _connection.RemoteEndPoint;
                _transferred = true;
                await Http3QuicConnection.RunNativeForListenerAsync(_connection, exchange => dispatch(exchange, local, remote),
                    abort, drain, TimeSpan.FromMilliseconds(uint.MaxValue - 1)).ConfigureAwait(false);
            }
            internal override async ValueTask RejectAsync(CancellationToken token)
                => await _connection.ShutdownAsync(0x107).WaitAsync(token).ConfigureAwait(false);
            public override ValueTask DisposeAsync()
            {
                try { if (!_transferred) _connection.Dispose(); }
                finally { ReleaseAdmission(); }
                return ValueTask.CompletedTask;
            }
        }
    }
    internal abstract class Http3AcceptedConnection : IAsyncDisposable
    {
        internal abstract bool HandshakePending { get; }
        internal abstract ValueTask WaitReadyAsync(CancellationToken token);
        internal abstract Task RunAsync(Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch, CancellationToken abort, CancellationToken drain);
        internal abstract ValueTask RejectAsync(CancellationToken token);
        public abstract ValueTask DisposeAsync();
    }
}
#endif
