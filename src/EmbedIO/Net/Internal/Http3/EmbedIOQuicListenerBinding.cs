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
using EmbedIO.Diagnostics;
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

        // Taken by the receive, send and timer loops around every dispatcher call.
        private readonly object _owner = new();

        // Holds an item while the dispatcher has output the send loop has not taken.
        private readonly Channel<bool> _outputReady = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        // Local endpoints by reported destination address; null when the socket's own
        // address is every datagram's destination.
        private readonly Dictionary<IPAddress, IPEndPoint>? _destinations;
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
        private readonly EmbedIOQuicTlsFactory? _tls;
        private readonly object _lifetime = new();
        private Task? _shutdown;
        private Task? _acceptanceStopped;
        private int _accepting = 1;
        private long _datagramPathErrors;
        private int _reportedRefusals;
        private long _refusalReport;

        // New connections are refused while queued QUIC work, TLS included, waits longer
        // than this, as MsQuic's workers bound their queue delay by default.
        internal const double QueueDelayBoundMilliseconds = 250;

        // A burst the loops have not read yet waits in the socket rather than being dropped,
        // as MsQuic also sizes its sockets past the operating system's defaults.
        private const int SocketBufferBytes = 4 << 20;

        internal EmbedIOQuicListenerBinding(IPEndPoint endpoint, Func<byte[], TlsSession> createTls)
            : this(endpoint, createTls, false)
        { }

        // Once constructed, the binding owns the factory and disposes it, with the credentials
        // it cached, after the binding's connections.
        internal EmbedIOQuicListenerBinding(IPEndPoint endpoint, EmbedIOQuicTlsFactory tls) : this(endpoint, tls, false)
        { }

        internal EmbedIOQuicListenerBinding(IPEndPoint endpoint, EmbedIOQuicTlsFactory tls, bool validateAddresses)
            : this(endpoint, (tls ?? throw new ArgumentNullException(nameof(tls))).Create, validateAddresses, tls)
        { }

        // With validateAddresses every new connection must first return a Retry's token.
        internal EmbedIOQuicListenerBinding(
            IPEndPoint endpoint,
            Func<byte[], TlsSession> createTls,
            bool validateAddresses
        ) : this(endpoint, createTls, validateAddresses, null)
        { }

        private EmbedIOQuicListenerBinding(
            IPEndPoint endpoint,
            Func<byte[], TlsSession> createTls,
            bool validateAddresses,
            EmbedIOQuicTlsFactory? tls
        )
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(createTls);
            _tls = tls;
            _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                _socket.ExclusiveAddressUse = true;

                if (endpoint.Address.Equals(IPAddress.IPv6Any)) _socket.DualMode = true;

                // Only a wildcard socket needs each datagram's destination. macOS reports none
                // on a dual-mode socket, and .NET's ReceiveMessageFrom refuses such a socket
                // there, so its datagrams are attributed to the bound address.
                if (
                    (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
                    && (!_socket.DualMode || !OperatingSystem.IsMacOS())
                )
                {
                    ReceiveDestinations(_socket);
                    _destinations = [];
                }

                SizeBuffers(_socket);
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
                    admissionAttemptsPerSecond: 0,
                    refuseConnections: true,
                    queueDelayBound: QueueDelayBoundMilliseconds,
                    validateAddresses: validateAddresses,
                    outputReady: () => _outputReady.Writer.TryWrite(true)
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
                    $"active={_connections.Active};accepted={_connections.Accepted};retired={_connections.Retired};peak={_connections.Peak};rejected={_connections.Rejected};refused={_connections.Refused};refused-at-capacity={_connections.RefusedAtCapacity};refused-for-delay={_connections.RefusedForQueueDelay};refused-for-http3={_connections.RefusedForApplication};unsent-refusals={_connections.UnsentRefusals};retries={_connections.RetriesSent};invalid-tokens={_connections.InvalidTokens};expired={_connections.ExpiredHandshakes};failed={_connections.FailedOwners};dropped={_connections.DroppedOutput};reset-routes={_connections.ResetRoutes};peer-groups={_connections.EndpointGroups};issued-routes={_connections.IssuedRoutes};datagram-path-errors={DatagramPathErrors};last-rejection={failure}"
                );
            }
        }

        internal long DatagramPathErrors => Interlocked.Read(ref _datagramPathErrors);

        // The session counts a connection once its accept loop takes it from this binding, so
        // the ones admitted but not yet created, and the ones waiting to be accepted, are
        // passed along.
        internal override void LimitAdmission(Func<int, bool> saturated) =>
            _connections.Saturated = () => saturated(_connections.Admitting + _accepted.Reader.Count);

        // Refusals are logged at most every ten seconds, by reason, with the queue delay and
        // the open connections, so an operator can see which bound engaged.
        private void ReportRefusals()
        {
            var capacity = _connections.RefusedAtCapacity;
            var delay = _connections.RefusedForQueueDelay;
            var http3 = _connections.RefusedForApplication;
            var refused = capacity + delay + http3;

            if (refused == _reportedRefusals) return;

            var now = Environment.TickCount64;

            if (_refusalReport != 0 && now - _refusalReport < 10000) return;

            FormattableString
                .Invariant(
                    $"HTTP/3 on {_local} refused new connections: {refused - _reportedRefusals} since the last report. Since start: {capacity} with all 768 connection slots in use, {delay} while queued QUIC work waited longer than {QueueDelayBoundMilliseconds:F0} ms, {http3} while the HTTP/3 listener had no room, {_connections.UnsentRefusals} of them unanswered because the refusal budget or send queue was full. Queue delay now {_connections.QueueDelay.TotalMilliseconds:F0} ms; open connections: {_connections.Active}."
                )
                .Warn(nameof(EmbedIOQuicListenerBinding));
            _reportedRefusals = refused;
            _refusalReport = now;
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
                    IdleTimeoutMilliseconds = 60000,
                    SharePackets = true
                },
                connectionIds: owner.ConnectionIds,
                retry: owner.Retry
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
                // The connections released their TLS sessions above; this frees the
                // credentials those sessions shared.
                _tls?.Dispose();
            }

            if (failures.Count != 0) throw new AggregateException("QUIC listener shutdown failed.", failures);
        }

        // Receiving, sending and the timer run as three loops, so a burst of datagrams in
        // one direction does not wait for the other. Each takes _owner around its
        // dispatcher calls, which keeps the dispatcher on one owner at a time, and none
        // allocates per datagram or per wait.
        private async Task RunAsync()
        {
            var failures = new List<Exception>();
            using var stopping = _stop.Token.UnsafeRegister(
                static state => (state as Channel<bool>)?.Writer.TryComplete(),
                _outputReady
            );

            try
            {
                await Task
                    .WhenAll(
                        RunLoopAsync(ReceiveLoopAsync, failures),
                        RunLoopAsync(SendLoopAsync, failures),
                        RunLoopAsync(TickLoopAsync, failures)
                    )
                    .ConfigureAwait(false);
            }

            finally
            {
                _stop.Cancel();
                _accepted.Writer.TryComplete(failures.Count == 0 ? null : failures[0]);
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
        }

        // A loop ends only when the listener stops or the loop fails; either way the
        // other two stop with it.
        private async Task RunLoopAsync(Func<Task> loop, List<Exception> failures)
        {
            await Task.Yield();

            try
            {
                await loop().ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }

            catch (SocketException error) when (IsStopAbort(error)) { }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                lock (failures) failures.Add(error);
            }

            finally
            {
                _stop.Cancel();
            }
        }

        // Every datagram already queued is read before the loop waits: a receive that
        // finds one completes at once.
        private async Task ReceiveLoopAsync()
        {
            var bytes = GC.AllocateUninitializedArray<byte>(65535, pinned: true);
            var addressFamily = _socket.AddressFamily;

            if (_destinations != null)
            {
                EndPoint any = new IPEndPoint(
                    addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                    0
                );

                while (true)
                {
                    SocketReceiveMessageFromResult packet;

                    try
                    {
                        packet = await _socket
                            .ReceiveMessageFromAsync(bytes, SocketFlags.None, any, _stop.Token)
                            .ConfigureAwait(false);
                    }

                    catch (SocketException error) when (IsDatagramPathError(error.SocketErrorCode))
                    {
                        Interlocked.Increment(ref _datagramPathErrors);
                        continue;
                    }

                    catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }

                    if ((packet.SocketFlags & SocketFlags.Truncated) != 0) continue;

                    var destination = Destination(packet.PacketInformation.Address);

                    lock (_owner)
                    {
                        _connections.Accepting = Volatile.Read(ref _accepting) != 0;
                        _connections.Receive(
                            bytes.AsSpan(0, packet.ReceivedBytes),
                            (IPEndPoint)packet.RemoteEndPoint,
                            destination
                        );
                    }
                }
            }

            var address = new SocketAddress(addressFamily);
            var peers = new Dictionary<SocketAddress, IPEndPoint>();
            var template = new IPEndPoint(
                addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0
            );

            while (true)
            {
                int count;

                try
                {
                    count = await _socket
                        .ReceiveFromAsync(bytes, SocketFlags.None, address, _stop.Token)
                        .ConfigureAwait(false);
                }

                catch (SocketException error) when (IsDatagramPathError(error.SocketErrorCode))
                {
                    Interlocked.Increment(ref _datagramPathErrors);
                    continue;
                }

                // A datagram longer than the buffer cannot be a QUIC packet this endpoint sent for.
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
                {
                    continue;
                }

                if (!peers.TryGetValue(address, out var peer))
                {
                    // Bounded: a flood from spoofed sources only empties the cache.
                    if (peers.Count >= PeerCacheLimit) peers.Clear();

                    var key = new SocketAddress(address.Family, address.Size);
                    address.Buffer.Span[..address.Size].CopyTo(key.Buffer.Span);
                    peer = (IPEndPoint)template.Create(key);
                    peers.Add(key, peer);
                }

                lock (_owner)
                {
                    _connections.Accepting = Volatile.Read(ref _accepting) != 0;
                    _connections.Receive(bytes.AsSpan(0, count), peer, _local);
                }
            }
        }

        private const int PeerCacheLimit = 1024;

        // Sends every queued datagram, in the dispatcher's order, then waits for the next.
        private async Task SendLoopAsync()
        {
            var addresses = new Dictionary<IPEndPoint, SocketAddress>();
            var dualMode = _socket.AddressFamily == AddressFamily.InterNetworkV6 && _socket.DualMode;

            while (true)
            {
                while (true)
                {
                    IPEndPoint? peer;
                    byte[]? bytes;

                    lock (_owner)
                        if (!_connections.TryTakeOutput(out peer, out bytes)) break;

                    try
                    {
                        if (!addresses.TryGetValue(peer, out var address))
                        {
                            if (addresses.Count >= PeerCacheLimit) addresses.Clear();

                            // A dual-mode socket sends to IPv4 peers at their mapped address.
                            address = (dualMode && peer.AddressFamily == AddressFamily.InterNetwork
                                ? new IPEndPoint(peer.Address.MapToIPv6(), peer.Port)
                                : peer).Serialize();
                            addresses.Add(peer, address);
                        }

                        var count = await _socket
                            .SendToAsync(bytes, SocketFlags.None, address, _stop.Token)
                            .ConfigureAwait(false);

                        if (count != bytes.Length)
                            throw new InvalidOperationException("UDP send did not consume its datagram.");
                    }

                    catch (SocketException error) when (IsDatagramPathError(error.SocketErrorCode))
                    {
                        Interlocked.Increment(ref _datagramPathErrors);
                    }

                    finally
                    {
                        _connections.FinishOutput();
                    }
                }

                if (
                    !await _outputReady.Reader
                        .WaitToReadAsync()
                        .ConfigureAwait(false)
                ) return;

                _outputReady.Reader.TryRead(out _);
            }
        }

        private async Task TickLoopAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));

            while (
                await timer
                    .WaitForNextTickAsync(_stop.Token)
                    .ConfigureAwait(false)
            )
                lock (_owner)
                {
                    _connections.Tick();
                    ReportRefusals();
                }
        }

        // A wildcard socket reports few distinct destinations; each keeps one endpoint.
        private IPEndPoint Destination(IPAddress? address)
        {
            var destinations = _destinations ?? throw new InvalidOperationException("Destinations are not reported.");

            if (address == null) return _local;

            if (!destinations.TryGetValue(address, out var destination))
            {
                if (destinations.Count >= PeerCacheLimit) destinations.Clear();

                destination = new IPEndPoint(address, _local.Port);
                destinations.Add(address, destination);
            }

            return destination;
        }

        // Stopping cancels pending socket operations. One that .NET is still starting
        // completes at once with OperationAborted instead of as canceled.
        private bool IsStopAbort(SocketException error) =>
            error.SocketErrorCode == SocketError.OperationAborted && _stop.IsCancellationRequested;

        // A default stands where the operating system refuses the size; Linux caps both at
        // net.core.rmem_max and wmem_max without failing.
        private static void SizeBuffers(Socket socket)
        {
            try
            {
                socket.ReceiveBufferSize = SocketBufferBytes;
            }

            catch (SocketException) { }

            try
            {
                socket.SendBufferSize = SocketBufferBytes;
            }

            catch (SocketException) { }
        }

        // .NET turns on destination reporting at the first ReceiveMessageFrom, and macOS
        // reports nothing for datagrams that arrived before then. Turn it on before
        // binding instead, for IPv4 datagrams on a dual-mode socket as well.
        private static void ReceiveDestinations(Socket socket)
        {
            if (socket.AddressFamily == AddressFamily.InterNetwork || socket.DualMode)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);

            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
        }

        // A datagram without a reported destination arrived on the bound address,
        // which is the wildcard for a dual-mode socket on macOS.
        internal static IPEndPoint DestinationOf(IPPacketInformation information, IPEndPoint bound) =>
            new(information.Address ?? bound.Address, bound.Port);

        // A shared unconnected socket can report a previous destination's ICMP
        // failure on either receive or send. Loss recovery remains per connection.
        private static bool IsDatagramPathError(SocketError error) =>
            error is SocketError.ConnectionReset
                or SocketError.ConnectionRefused
                or SocketError.HostUnreachable
                or SocketError.HostDown
                or SocketError.NetworkUnreachable;

        private sealed class Peer : Http3AcceptedConnection
        {
            private readonly QuicConnectionOwner _owner;
            private readonly IPEndPoint _local;
            private readonly IPEndPoint _remote;
            private readonly TaskCompletionSource<(
                AsyncStreamApplication Application,
                QuicPeerFeatures Features
            )> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                QuicPeerFeatures features,
                TlsSession? tls
            )
            {
                _clientCertificate = tls?.CopyClientCertificate();
                var application = new AsyncStreamApplication(operations, _owner.Notify);

                if (!_ready.TrySetResult((application, features)))
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
                CancellationToken drain,
                WebTransportOptions? webTransport = null,
                TimeSpan? requestHeadersTimeout = null,
                Action? released = null
            )
            {
                var (application, features) = await _ready.Task
                    .WaitAsync(abort)
                    .ConfigureAwait(false);
                await Http3QuicConnection
                    .RunTransportForListenerAsync(
                        new EmbedIOQuicTransportConnection(application, _owner, features),
                        exchange => dispatch(
                            exchange,
                            QuicConnectionEndpoints.Copy(_local),
                            QuicConnectionEndpoints.Copy(_remote)
                        ),
                        abort,
                        drain,
                        TimeSpan.FromMilliseconds(uint.MaxValue - 1),
                        webTransport,
                        requestHeadersTimeout,
                        released
                    )
                    .ConfigureAwait(false);
            }

            internal override async ValueTask RejectAsync(CancellationToken token)
            {
                if (_ready.Task.IsCompletedSuccessfully)
                {
                    var (application, features) = await _ready.Task.ConfigureAwait(false);
                    var transport = new EmbedIOQuicTransportConnection(application, _owner, features);
                    await transport
                        .CloseAsync(0x107, token)
                        .ConfigureAwait(false);
                    await transport
                        .WaitClosedAsync(token)
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
