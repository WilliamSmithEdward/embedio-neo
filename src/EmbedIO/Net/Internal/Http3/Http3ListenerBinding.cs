#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal abstract class Http3ListenerBinding : IAsyncDisposable
    {
        internal abstract ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token);

        public abstract ValueTask DisposeAsync();

        // The native binding supports none of the policy's settings but EnabledProtocols, which QUIC fixes at TLS 1.3;
        // ServerTlsPolicy.EnsureSupported refuses the others before a session starts.
        internal static Http3ListenerBinding Bind(
            IPEndPoint endpoint,
            ServerCertificateSource certificates,
            ServerTlsPolicy? policy,
            bool native
        ) =>
            native
                ? NativeBinding.Create(endpoint, certificates)
                : SystemBinding.Create(endpoint, certificates, policy);

        private sealed class SystemBinding : Http3ListenerBinding
        {
            private readonly QuicListener _listener;

            private SystemBinding(QuicListener listener) => _listener = listener;

            internal static SystemBinding Create(
                IPEndPoint endpoint,
                ServerCertificateSource certificates,
                ServerTlsPolicy? policy
            )
            {
                var options = new QuicListenerOptions
                {
                    ListenEndPoint = endpoint,
                    ListenBacklog = 512,
                    ApplicationProtocols = [new SslApplicationProtocol("h3")],
                    ConnectionOptionsCallback = (_, hello, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                    {
                        DefaultCloseErrorCode = 0x100,
                        DefaultStreamErrorCode = 0x10c,
                        MaxInboundBidirectionalStreams = 128,
                        MaxInboundUnidirectionalStreams = 16,
                        IdleTimeout = TimeSpan.FromSeconds(60),
                        HandshakeTimeout = TimeSpan.FromSeconds(10),
                        ServerAuthenticationOptions = CreateAuthentication(certificates, policy, hello.ServerName)
                    })
                };
                return new SystemBinding(
                    QuicListener
                        .ListenAsync(options)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult()
                );
            }

            // Read per connection, so a published certificate applies to the next handshake.
            private static SslServerAuthenticationOptions CreateAuthentication(
                ServerCertificateSource certificates,
                ServerTlsPolicy? policy,
                string? serverName
            )
            {
                var current = certificates.Current;
                var options = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("h3")],
                    ServerCertificate = policy == null ? current : policy.SelectServerCertificate(serverName, current)
                };

                if (policy != null && policy.RequestsClientCertificate)
                {
                    options.ClientCertificateRequired = true;
                    options.RemoteCertificateValidationCallback = policy.ValidateClientCertificate;
                    options.CertificateRevocationCheckMode = policy.CheckCertificateRevocation
                        ? X509RevocationMode.Online
                        : X509RevocationMode.NoCheck;
                }

                return options;
            }

            internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token) =>
                new SystemPeer(
                    await _listener
                        .AcceptConnectionAsync(token)
                        .ConfigureAwait(false)
                );

            public override ValueTask DisposeAsync() => _listener.DisposeAsync();
        }

        private sealed class NativeBinding : Http3ListenerBinding
        {
            private readonly MsQuicApi _api;
            private readonly MsQuicRegistration _registration;
            private static readonly byte[] Alpn = [
                (byte)'h',
                (byte)'3'
            ];
            private readonly MsQuicNativeListener _listener;
            private readonly ServerCertificateSource _certificates;
            private readonly object _configurationSync = new();

            // Configurations of replaced certificates. Connections configured with them may still be open, so
            // they are released with the binding, like the current one.
            private readonly List<MsQuicConfiguration> _retiredConfigurations = [];
            private MsQuicConfiguration _configuration;
            private long _configurationVersion;
            private byte[] _configurationThumbprint;
            private long _failedVersion;

            private NativeBinding(
                MsQuicApi api,
                MsQuicRegistration registration,
                MsQuicConfiguration configuration,
                MsQuicNativeListener listener,
                ServerCertificateSource certificates,
                long version,
                byte[] thumbprint
            )
            {
                _api = api;
                _registration = registration;
                _configuration = configuration;
                _listener = listener;
                _certificates = certificates;
                _configurationVersion = version;
                _configurationThumbprint = thumbprint;
            }

            internal static NativeBinding Create(IPEndPoint endpoint, ServerCertificateSource certificates)
            {
                MsQuicApi? api = null;
                MsQuicRegistration? registration = null;
                MsQuicConfiguration? configuration = null;
                MsQuicNativeListener? listener = null;

                try
                {
                    api = MsQuicApi.Open();
                    registration = api.CreateRegistration();
                    var version = certificates.Version;
                    var certificate = certificates.Current;
                    configuration = registration.CreateHttp3ListenerConfiguration(Alpn);
                    configuration.LoadServerCertificate(certificate);
                    listener = registration.CreateListener();
                    listener.EnableHttp3Acceptance();
                    listener.StartHttp3(endpoint, Alpn);
                    return new NativeBinding(
                        api,
                        registration,
                        configuration,
                        listener,
                        certificates,
                        version,
                        Thumbprint(certificate)
                    );
                }

                catch
                {
                    try
                    {
                        listener?.Dispose();
                    }

                    finally
                    {
                        try
                        {
                            configuration?.Dispose();
                        }

                        finally
                        {
                            try
                            {
                                registration?.Dispose();
                            }

                            finally
                            {
                                api?.Dispose();
                            }
                        }
                    }

                    throw;
                }
            }

            internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token)
            {
                while (true)
                {
                    var connection = await _listener
                        .AcceptForHttp3Async(token)
                        .ConfigureAwait(false);

                    try
                    {
                        connection.EnableStreamAcceptance();
                        var datagrams = connection.EnableDatagrams();
                        connection.Configure(CurrentConfiguration());
                        return new NativePeer(connection, _listener, datagrams);
                    }

                    catch (Exception error) when (error is IOException or ObjectDisposedException)
                    {
                        // A peer can reset between the native acceptance callback
                        // and configuration. Dispose that peer, keep listening.
                        try
                        {
                            connection.Dispose();
                        }

                        finally
                        {
                            _listener.ReleaseHttp3Admission();
                        }

                        token.ThrowIfCancellationRequested();
                    }

                    catch
                    {
                        try
                        {
                            connection.Dispose();
                        }

                        finally
                        {
                            _listener.ReleaseHttp3Admission();
                        }

                        throw;
                    }
                }
            }

            // MsQuic loads a configuration's credentials once, so a published certificate gets a new
            // configuration. Republishing the same certificate, such as reloading an unchanged file, reuses
            // the current one, so retained configurations grow only with actual replacements. If a new
            // certificate cannot be loaded, handshakes keep the previous one and the failure is logged once
            // per publication.
            private MsQuicConfiguration CurrentConfiguration()
            {
                var version = _certificates.Version;

                lock (_configurationSync)
                {
                    if (version == _configurationVersion || version == _failedVersion)
                        return _configuration;

                    var certificate = _certificates.Current;
                    var thumbprint = Thumbprint(certificate);

                    if (thumbprint
                        .AsSpan()
                        .SequenceEqual(_configurationThumbprint))
                    {
                        _configurationVersion = version;
                        return _configuration;
                    }

                    MsQuicConfiguration? next = null;

                    try
                    {
                        next = _registration.CreateHttp3ListenerConfiguration(Alpn);
                        next.LoadServerCertificate(certificate);
                    }

                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        next?.Dispose();
                        _failedVersion = version;
                        $"HTTP/3 could not load the published server certificate; new connections keep the previous certificate: {error.Message}".Error(
                        );
                        return _configuration;
                    }

                    _retiredConfigurations.Add(_configuration);
                    _configuration = next;
                    _configurationVersion = version;
                    _configurationThumbprint = thumbprint;
                    return next;
                }
            }

            private static byte[] Thumbprint(X509Certificate2 certificate) =>
                certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

            public override async ValueTask DisposeAsync()
            {
                try
                {
                    await _listener
                        .StopAsync()
                        .ConfigureAwait(false);
                }

                finally
                {
                    try
                    {
                        _listener.Dispose();
                    }

                    finally
                    {
                        try
                        {
                            lock (_configurationSync)
                            {
                                try
                                {
                                    _configuration.Dispose();
                                }

                                finally
                                {
                                    foreach (var retired in _retiredConfigurations)
                                        retired.Dispose();
                                }
                            }
                        }

                        finally
                        {
                            try
                            {
                                _registration.Dispose();
                            }

                            finally
                            {
                                _api.Dispose();
                            }
                        }
                    }
                }
            }
        }

        private sealed class SystemPeer : Http3AcceptedConnection
        {
            private readonly QuicConnection _connection;
            private bool _transferred;

            internal SystemPeer(QuicConnection connection)
            {
                _connection = connection;
                ClientCertificate = ServerTls.CopyRemoteCertificate(connection.RemoteCertificate);
            }

            internal override X509Certificate2? ClientCertificate { get; }
            internal override bool HandshakePending => false;

            internal override ValueTask WaitReadyAsync(CancellationToken token) => ValueTask.CompletedTask;

            internal override Task RunAsync(
                Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
                CancellationToken abort,
                CancellationToken drain
            )
            {
                var local = _connection.LocalEndPoint;
                var remote = _connection.RemoteEndPoint;
                _transferred = true;
                return Http3QuicConnection.RunForListenerAsync(
                    _connection,
                    exchange => dispatch(exchange, local, remote),
                    abort,
                    drain,
                    TimeSpan.FromMilliseconds(uint.MaxValue - 1)
                );
            }

            internal override ValueTask RejectAsync(CancellationToken token) => _connection.CloseAsync(0x107, token);

            public override ValueTask DisposeAsync() =>
                _transferred ? ValueTask.CompletedTask : _connection.DisposeAsync();
        }

        private sealed class NativePeer : Http3AcceptedConnection
        {
            private readonly MsQuicNativeConnection _connection;
            private readonly MsQuicNativeListener _admission;
            private readonly MsQuicNativeDatagrams _datagrams;
            private int _admissionReleased;
            private bool _transferred;

            internal NativePeer(
                MsQuicNativeConnection connection,
                MsQuicNativeListener admission,
                MsQuicNativeDatagrams datagrams
            )
            {
                _connection = connection;
                _admission = admission;
                _datagrams = datagrams;
            }

            private void ReleaseAdmission()
            {
                if (Interlocked.Exchange(ref _admissionReleased, 1) == 0) _admission.ReleaseHttp3Admission();
            }

            internal override bool HandshakePending => !_connection.Connected.IsCompletedSuccessfully;

            internal override async ValueTask WaitReadyAsync(CancellationToken token)
            {
                try
                {
                    await _connection.Connected
                        .WaitAsync(TimeSpan.FromSeconds(10), token)
                        .ConfigureAwait(false);
                }

                finally
                {
                    ReleaseAdmission();
                }
            }

            internal override async Task RunAsync(
                Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
                CancellationToken abort,
                CancellationToken drain
            )
            {
                // Configure handshakes concurrently on connection workers; one
                // failed peer must not stall acceptance/configuration of others.
                var local = _connection.LocalEndPoint;
                var remote = _connection.RemoteEndPoint;
                _transferred = true;
                await Http3QuicConnection
                    .RunNativeDatagramsForListenerAsync(
                        _connection,
                        exchange => dispatch(exchange, local, remote),
                        abort,
                        drain,
                        TimeSpan.FromMilliseconds(uint.MaxValue - 1),
                        _datagrams
                    )
                    .ConfigureAwait(false);
            }

            internal override async ValueTask RejectAsync(CancellationToken token) =>
                await _connection
                    .ShutdownAsync(0x107)
                    .WaitAsync(token)
                    .ConfigureAwait(false);

            public override ValueTask DisposeAsync()
            {
                try
                {
                    if (!_transferred) _connection.Dispose();
                }

                finally
                {
                    ReleaseAdmission();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    internal abstract class Http3AcceptedConnection : IAsyncDisposable
    {
        internal abstract bool HandshakePending { get; }

        // The client's certificate from the handshake, when the listener's policy asked for one.
        internal virtual X509Certificate2? ClientCertificate => null;

        internal abstract ValueTask WaitReadyAsync(CancellationToken token);

        internal abstract Task RunAsync(
            Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
            CancellationToken abort,
            CancellationToken drain
        );

        internal abstract ValueTask RejectAsync(CancellationToken token);

        public abstract ValueTask DisposeAsync();
    }
}
#endif
