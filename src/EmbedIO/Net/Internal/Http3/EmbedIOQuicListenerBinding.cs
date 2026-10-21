#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    // Socket lifetime is independent of acceptance: active peers retain UDP I/O
    // while HTTP/3 sends GOAWAY and drains. Dispose terminates all remaining work.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class EmbedIOQuicListenerBinding : Http3ListenerBinding
    {
        private readonly Socket _socket;
        private readonly QuicConnectionDispatcher _connections;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<Peer> _accepted = Channel.CreateBounded<Peer>(new BoundedChannelOptions(512)
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly IPEndPoint _local;
        private readonly Task _loop;
        private readonly object _lifetime = new();
        private Task? _shutdown;
        private Task? _acceptanceStopped;
        private int _accepting = 1;

        internal EmbedIOQuicListenerBinding(IPEndPoint endpoint, Func<byte[], TlsSession> createTls)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(createTls);
            _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                _socket.ExclusiveAddressUse = true;

                if (endpoint.Address.Equals(IPAddress.IPv6Any)) _socket.DualMode = true;

                _socket.Bind(endpoint);
                _local = QuicConnectionEndpoints.Copy(
                    (IPEndPoint)(_socket.LocalEndPoint
                        ?? throw new InvalidOperationException("UDP socket has no local endpoint."))
                );
                var clock = Stopwatch.StartNew();
                _connections = new QuicConnectionDispatcher(
                    (packet, now, owner, endpoints) => CreateConnection(packet, now, owner, endpoints, createTls),
                    () => clock.Elapsed.TotalMilliseconds,
                    capacity: 768,
                    workers: Math.Clamp(Environment.ProcessorCount, 1, 8),
                    handshakeTimeout: 10000,
                    outputPacketCapacity: 4096,
                    outputByteCapacity: 4194304,
                    admissionAttemptsPerSecond: 0
                );
                _loop = Task.Run(RunAsync);
            }

            catch
            {
                _socket.Dispose();
                _stop.Dispose();
                throw;
            }
        }

        internal string ClosedDiagnostics
        {
            get
            {
                if (_shutdown?.IsCompleted != true)
                    throw new InvalidOperationException("Await listener disposal before reading its diagnostics.");

                var failure = _connections.LastRejectedFailure?.ToString();

                if (failure?.Length > 4096) failure = failure[..4096];

                return FormattableString.Invariant(
                    $"active={_connections.Active};accepted={_connections.Accepted};retired={_connections.Retired};peak={_connections.Peak};rejected={_connections.Rejected};expired={_connections.ExpiredHandshakes};failed={_connections.FailedOwners};dropped={_connections.DroppedOutput};last-rejection={failure}"
                );
            }
        }

        internal bool IsAccepting => Volatile.Read(ref _accepting) != 0;
        internal IPEndPoint LocalEndPoint => QuicConnectionEndpoints.Copy(_local);

        private EmbedIO.Quic.QuicConnection CreateConnection(
            ReadOnlySpan<byte> packet,
            double now,
            QuicConnectionOwner owner,
            QuicConnectionEndpoints endpoints,
            Func<byte[], TlsSession> createTls
        )
        {
            var peer = new Peer(owner, endpoints);
            TlsSession? tls = null;
            var connection = new EmbedIO.Quic.QuicConnection(
                packet,
                parameters => tls = createTls(parameters),
                (operations, features) => peer.CreateApplication(operations, features, tls),
                now,
                limits: new QuicConnectionLimits
                {
                    InboundBidirectionalStreams = 128,
                    InboundUnidirectionalStreams = 16,
                    IdleTimeoutMilliseconds = 60000
                }
            );

            if (Volatile.Read(ref _accepting) == 0 || !_accepted.Writer.TryWrite(peer)) owner.RequestClose();

            return connection;
        }

        internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token) =>
            await _accepted.Reader
                .ReadAsync(token)
                .ConfigureAwait(false);

        internal override ValueTask StopAcceptingAsync()
        {
            lock (_lifetime)
            {
                _acceptanceStopped ??= StopAcceptanceAsync();
                return new ValueTask(_acceptanceStopped);
            }
        }

        private async Task StopAcceptanceAsync()
        {
            Interlocked.Exchange(ref _accepting, 0);
            _accepted.Writer.TryComplete();
            var pending = new List<Task>();

            while (_accepted.Reader.TryRead(out var peer))
                pending.Add(
                    peer
                        .DisposeAsync()
                        .AsTask()
                );

            await Task
                .WhenAll(pending)
                .ConfigureAwait(false);
        }

        public override ValueTask DisposeAsync()
        {
            lock (_lifetime)
            {
                _shutdown ??= ShutdownAsync();
                return new ValueTask(_shutdown);
            }
        }

        private async Task ShutdownAsync()
        {
            await Task.Yield();
            var failures = new List<Exception>();

            try
            {
                _stop.Cancel();
                var running = Task.WhenAll(StopAcceptingAsync().AsTask(), _loop);
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (running.Exception != null) failures.Add(running.Exception);

                // The loop also releases these resources on I/O failure. This
                // idempotent join covers disposal before its first scheduled turn.
                var owners = _connections
                    .DisposeAsync()
                    .AsTask();
                await owners.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (owners.Exception != null) failures.Add(owners.Exception);
            }

            finally
            {
                _socket.Dispose();
                _stop.Dispose();
            }

            if (failures.Count != 0) throw new AggregateException("QUIC listener shutdown failed.", failures);
        }

        private async Task RunAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            var bytes = new byte[65535];
            EndPoint any = new IPEndPoint(
                _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0
            );
            Task<SocketReceiveMessageFromResult>? receive = null;
            Task<bool>? tick = null;
            Task<bool>? output = null;
            var failures = new List<Exception>();

            try
            {
                receive = Receive();
                tick = timer
                    .WaitForNextTickAsync(_stop.Token)
                    .AsTask();
                output = _connections
                    .WaitForOutputAsync(_stop.Token)
                    .AsTask();

                while (true)
                {
                    await Task
                        .WhenAny(receive, tick, output)
                        .ConfigureAwait(false);
                    _connections.Accepting = Volatile.Read(ref _accepting) != 0;

                    if (receive.IsCompleted)
                    {
                        try
                        {
                            var packet = await receive.ConfigureAwait(false);

                            if ((packet.SocketFlags & SocketFlags.Truncated) == 0)
                                _connections.Receive(
                                    bytes.AsSpan(0, packet.ReceivedBytes),
                                    (IPEndPoint)packet.RemoteEndPoint,
                                    new IPEndPoint(packet.PacketInformation.Address, _local.Port)
                                );
                        }

                        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }

                        receive = Receive();
                    }

                    if (tick.IsCompleted)
                    {
                        if (!await tick.ConfigureAwait(false)) break;

                        _connections.Tick();
                        tick = timer
                            .WaitForNextTickAsync(_stop.Token)
                            .AsTask();
                    }

                    if (output.IsCompleted)
                    {
                        if (!await output.ConfigureAwait(false)) break;

                        await _connections
                            .SendOneAsync(SendAsync)
                            .ConfigureAwait(false);
                        output = _connections
                            .WaitForOutputAsync(_stop.Token)
                            .AsTask();
                    }
                }
            }

            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                failures.Add(error);
            }

            finally
            {
                _stop.Cancel();
                _accepted.Writer.TryComplete(failures.Count == 0 ? null : failures[0]);
                await ObserveAsync(receive).ConfigureAwait(false);
                await ObserveAsync(tick).ConfigureAwait(false);
                await ObserveAsync(output).ConfigureAwait(false);
                var cleanup = _connections
                    .DisposeAsync()
                    .AsTask();
                await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (cleanup.Exception != null) failures.Add(cleanup.Exception);

                var pending = new List<Task>();

                while (_accepted.Reader.TryRead(out var peer))
                    pending.Add(
                        peer
                            .DisposeAsync()
                            .AsTask()
                    );

                var abandoned = Task.WhenAll(pending);
                await abandoned.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (abandoned.Exception != null) failures.Add(abandoned.Exception);

                _socket.Dispose();
            }

            if (failures.Count != 0) throw new AggregateException("QUIC listener failed.", failures);

            Task<SocketReceiveMessageFromResult> Receive() =>
                _socket
                    .ReceiveMessageFromAsync(bytes, SocketFlags.None, any, _stop.Token)
                    .AsTask();

            async ValueTask SendAsync(OutboundDatagram datagram)
            {
                try
                {
                    var count = await _socket
                        .SendToAsync(datagram.Bytes, SocketFlags.None, datagram.Peer, _stop.Token)
                        .ConfigureAwait(false);

                    if (count != datagram.Bytes.Length)
                        throw new InvalidOperationException("UDP send did not consume its datagram.");
                }

                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
            }

            async Task ObserveAsync(Task? pending)
            {
                if (pending == null) return;

                try
                {
                    await pending.ConfigureAwait(false);
                }

                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }

                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    failures.Add(error);
                }
            }
        }

        private sealed class Peer : Http3AcceptedConnection
        {
            private readonly QuicConnectionOwner _owner;
            private readonly IPEndPoint _local;
            private readonly IPEndPoint _remote;
            private readonly TaskCompletionSource<AsyncStreamApplication> _ready = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private readonly Task _closed;
            private X509Certificate2? _clientCertificate;

            internal Peer(QuicConnectionOwner owner, QuicConnectionEndpoints endpoints)
            {
                _owner = owner;
                _local = QuicConnectionEndpoints.Copy(
                    endpoints.Local ?? throw new InvalidOperationException("Missing destination endpoint.")
                );
                _remote = QuicConnectionEndpoints.Copy(endpoints.Peer);
                _closed = ObserveClosureAsync();
            }

            internal AsyncStreamApplication CreateApplication(
                StreamOperations operations,
                QuicPeerFeatures _,
                TlsSession? tls
            )
            {
                _clientCertificate = tls?.CopyClientCertificate();
                var application = new AsyncStreamApplication(operations, _owner.Notify);

                if (!_ready.TrySetResult(application))
                {
                    application.Dispose();
                    throw new InvalidOperationException("QUIC application was initialized twice.");
                }

                return application;
            }

            internal override X509Certificate2? ClientCertificate => Volatile.Read(ref _clientCertificate);
            internal override bool HandshakePending => true;

            internal override ValueTask WaitReadyAsync(CancellationToken token) => new(_ready.Task.WaitAsync(token));

            internal override async Task RunAsync(
                Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
                CancellationToken abort,
                CancellationToken drain
            )
            {
                var application = await _ready.Task
                    .WaitAsync(abort)
                    .ConfigureAwait(false);
                await Http3QuicConnection
                    .RunTransportForListenerAsync(
                        new EmbedIOQuicTransportConnection(application, _owner),
                        exchange => dispatch(
                            exchange,
                            QuicConnectionEndpoints.Copy(_local),
                            QuicConnectionEndpoints.Copy(_remote)
                        ),
                        abort,
                        drain,
                        TimeSpan.FromMilliseconds(uint.MaxValue - 1)
                    )
                    .ConfigureAwait(false);
            }

            internal override async ValueTask RejectAsync(CancellationToken token)
            {
                if (_ready.Task.IsCompletedSuccessfully)
                {
                    var application = await _ready.Task.ConfigureAwait(false);
                    await new EmbedIOQuicTransportConnection(application, _owner)
                        .CloseAsync(0x107, token)
                        .ConfigureAwait(false);
                }

                else _owner.RequestClose();
            }

            public override async ValueTask DisposeAsync()
            {
                try
                {
                    await _owner
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }

                finally
                {
                    try
                    {
                        await _closed.ConfigureAwait(false);
                    }

                    finally
                    {
                        // Request certificate copies retain their non-disposing public contract.
                        // The TLS owner releases its separate native certificate/context lease.
                        Interlocked.Exchange(ref _clientCertificate, null);
                    }
                }
            }

            private async Task ObserveClosureAsync()
            {
                await _owner.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                var failure = _owner.Completion.Exception?.InnerException ?? new QuicConnectionFailure(0, false, false);
                _ready.TrySetException(failure);

                // The factory may fail or acceptance may stop before a caller owns this peer.
                if (_ready.Task.IsFaulted) _ = _ready.Task.Exception;
            }
        }
    }
}
#endif
