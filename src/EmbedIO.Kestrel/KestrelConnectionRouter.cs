using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmbedIO.Kestrel
{
    // Route once per TCP connection. Kestrel receives the original transport and every
    // preface byte; no application request or response is copied through a proxy.
    internal sealed class KestrelConnectionRouter(
        WebServerOptions options,
        ServerTelemetry? telemetry
    ) : IAsyncDisposable
    {
        private readonly SocketTransportFactory _sockets = new(
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance
        );
        private readonly SemaphoreSlim _binding = new(1, 1);
        private readonly Dictionary<string, Hub> _hubs = new(StringComparer.Ordinal);
        private int _connections;

        internal IConnectionListenerFactory Factory(bool http2) => new FactoryImpl(this, http2);

        private ServerTelemetry.LifetimeTelemetry? ConnectionStarted(EndPoint? endpoint) =>
            telemetry?.ConnectionStarted("tcp", (endpoint as IPEndPoint)?.Port ?? 0);

        private async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, bool http2, CancellationToken token)
        {
            await _binding
                .WaitAsync(token)
                .ConfigureAwait(false);

            try
            {
                var key = endpoint.ToString() ?? throw new InvalidOperationException("Missing endpoint identity.");

                if (!_hubs.TryGetValue(key, out var hub))
                {
                    if (http2) throw new InvalidOperationException("The shared TCP endpoint must be bound first.");

                    var listener = await _sockets
                        .BindAsync(endpoint, token)
                        .ConfigureAwait(false);
                    var port = ((IPEndPoint)listener.EndPoint).Port;
                    var secure = false;

                    foreach (var prefix in options.UrlPrefixes)
                    {
                        var binding = new ListenerPrefix(prefix);

                        if (binding.Port == port)
                        {
                            secure = binding.Secure;
                            break;
                        }
                    }

                    hub = new Hub(this, listener, secure, options.RequestHeadersTimeout ?? TimeSpan.FromSeconds(30));
                    _hubs.Add(key, hub);
                }

                return new LogicalListener(hub, http2);
            }

            finally
            {
                _binding.Release();
            }
        }

        private bool TryAcquire()
        {
            var maximum = options.MaximumConnections;
            var current = Volatile.Read(ref _connections);

            while (maximum == null || current < maximum)
            {
                var observed = Interlocked.CompareExchange(ref _connections, current + 1, current);

                if (observed == current) return true;

                current = observed;
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var hub in _hubs.Values)
                await hub
                    .DisposeAsync()
                    .ConfigureAwait(false);

            _binding.Dispose();
        }

        private sealed class FactoryImpl(KestrelConnectionRouter owner, bool http2) : IConnectionListenerFactory
        {
            public ValueTask<IConnectionListener> BindAsync(
                EndPoint endpoint,
                CancellationToken cancellationToken = default
            ) => owner.BindAsync(endpoint, http2, cancellationToken);
        }

        private sealed class LogicalListener(Hub hub, bool http2) : IConnectionListener
        {
            public EndPoint EndPoint => hub.EndPoint;

            public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    return await (http2 ? hub.Http2 : hub.Http1).Reader
                        .ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                catch (ChannelClosedException)
                {
                    return null;
                }
            }

            public ValueTask UnbindAsync(CancellationToken cancellationToken = default) =>
                hub.UnbindAsync(cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Hub : IAsyncDisposable
        {
            private readonly KestrelConnectionRouter _owner;
            private readonly IConnectionListener _listener;
            private readonly bool _secure;
            private readonly TimeSpan _headTimeout;
            private readonly CancellationTokenSource _stopping = new();
            private readonly ConcurrentDictionary<string, ForwardedConnection> _classifying = new(
                StringComparer.Ordinal
            );
            private readonly Task _accepting;
            private int _unbound;
            private int _disposed;
            internal readonly Channel<ConnectionContext> Http1 = Channel.CreateUnbounded<ConnectionContext>();
            internal readonly Channel<ConnectionContext> Http2 = Channel.CreateUnbounded<ConnectionContext>();

            internal EndPoint EndPoint => _listener.EndPoint;

            internal Hub(KestrelConnectionRouter owner, IConnectionListener listener, bool secure, TimeSpan headTimeout)
            {
                _owner = owner;
                _listener = listener;
                _secure = secure;
                _headTimeout = headTimeout;
                _accepting = AcceptAsync();
            }

            private async Task AcceptAsync()
            {
                try
                {
                    while (!_stopping.IsCancellationRequested)
                    {
                        var connection = await _listener
                            .AcceptAsync(_stopping.Token)
                            .ConfigureAwait(false);

                        if (connection == null) break;

                        if (!_owner.TryAcquire())
                        {
                            connection.Abort(
                                new ConnectionAbortedException("The server connection limit was reached.")
                            );
                            await connection
                                .DisposeAsync()
                                .ConfigureAwait(false);
                            continue;
                        }

                        var owned = new ForwardedConnection(connection, _owner, _headTimeout);
                        _classifying.TryAdd(owned.ConnectionId, owned);
                        _ = ClassifyAsync(owned);
                    }
                }

                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    Http1.Writer.TryComplete(error);
                    Http2.Writer.TryComplete(error);
                }

                finally
                {
                    Http1.Writer.TryComplete();
                    Http2.Writer.TryComplete();
                }
            }

            private async Task ClassifyAsync(ForwardedConnection connection)
            {
                var transferred = false;

                try
                {
                    var http2 = false;

                    if (!_secure)
                    {
                        var reader = connection.Transport.Input;

                        while (true)
                        {
                            var read = await reader
                                .ReadAsync(_stopping.Token)
                                .ConfigureAwait(false);
                            var selected = Select(read.Buffer);

                            if (selected != null || read.IsCompleted || read.IsCanceled)
                            {
                                http2 = selected == true;
                                reader.AdvanceTo(read.Buffer.Start, read.Buffer.Start);
                                break;
                            }

                            reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                        }
                    }

                    if (connection.ConnectionClosed.IsCancellationRequested) return;

                    transferred = (http2 ? Http2 : Http1).Writer.TryWrite(connection);
                }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    connection.Abort(new ConnectionAbortedException("Protocol selection failed.", error));
                }

                finally
                {
                    try
                    {
                        if (!transferred)
                            await connection
                                .DisposeAsync()
                                .ConfigureAwait(false);
                    }

                    finally
                    {
                        connection.SelectionEnded();
                        _classifying.TryRemove(connection.ConnectionId, out _);
                    }
                }
            }

            private static bool? Select(ReadOnlySequence<byte> input)
            {
                var preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;
                var reader = new SequenceReader<byte>(input);

                for (var index = 0; index < preface.Length; index++)
                {
                    if (!reader.TryRead(out var value)) return null;

                    if (value != preface[index]) return false;
                }

                return true;
            }

            internal async ValueTask UnbindAsync(CancellationToken token)
            {
                if (Interlocked.Exchange(ref _unbound, 1) != 0) return;

                _stopping.Cancel();
                await _listener
                    .UnbindAsync(token)
                    .ConfigureAwait(false);

                foreach (var connection in _classifying.Values)
                    connection.Abort(new ConnectionAbortedException("The listener is stopping."));

                await _accepting.ConfigureAwait(false);
                await Task
                    .WhenAll(_classifying.Values.Select(connection => connection.SelectionCompleted))
                    .ConfigureAwait(false);
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                await UnbindAsync(CancellationToken.None).ConfigureAwait(false);

                foreach (var queue in new[] { Http1, Http2 })
                    while (queue.Reader.TryRead(out var connection))
                    {
                        connection.Abort(new ConnectionAbortedException("The listener stopped before dispatch."));
                        await connection
                            .DisposeAsync()
                            .ConfigureAwait(false);
                    }

                await _listener
                    .DisposeAsync()
                    .ConfigureAwait(false);
                _stopping.Dispose();
            }
        }

        internal sealed class ForwardedConnection : ConnectionContext
        {
            private readonly ConnectionContext _inner;
            private readonly KestrelConnectionRouter _owner;
            private readonly Timer _headDeadline;
            private readonly ServerTelemetry.LifetimeTelemetry? _lifetime;
            private readonly TaskCompletionSource<bool> _selection = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private int _headState;
            private int _disposed;

            internal Task SelectionCompleted => _selection.Task;

            internal void SelectionEnded() => _selection.TrySetResult(true);

            internal ForwardedConnection(ConnectionContext inner, KestrelConnectionRouter owner, TimeSpan timeout)
            {
                _inner = inner;
                _owner = owner;
                _lifetime = owner.ConnectionStarted(inner.LocalEndPoint);
                Features.Set(this);
                _headDeadline = new Timer(
                    static state => (state as ForwardedConnection)?.Expire(),
                    this,
                    timeout,
                    Timeout.InfiniteTimeSpan
                );
            }

            internal bool AdmitHead()
            {
                if (Volatile.Read(ref _headState) == 1) return true;

                var state = Interlocked.CompareExchange(ref _headState, 1, 0);

                if (state == 2) return false;

                _headDeadline.Dispose();
                return true;
            }

            private void Expire()
            {
                if (Interlocked.CompareExchange(ref _headState, 2, 0) == 0)
                    Abort(new ConnectionAbortedException("The initial request header deadline expired."));
            }

            public override string ConnectionId
            {
                get => _inner.ConnectionId;
                set => _inner.ConnectionId = value;
            }

            public override IFeatureCollection Features => _inner.Features;

            public override IDictionary<object, object?> Items
            {
                get => _inner.Items;
                set => _inner.Items = value;
            }

            public override IDuplexPipe Transport
            {
                get => _inner.Transport;
                set => _inner.Transport = value;
            }

            public override CancellationToken ConnectionClosed
            {
                get => _inner.ConnectionClosed;
                set => _inner.ConnectionClosed = value;
            }

            public override EndPoint? LocalEndPoint
            {
                get => _inner.LocalEndPoint;
                set => _inner.LocalEndPoint = value;
            }

            public override EndPoint? RemoteEndPoint
            {
                get => _inner.RemoteEndPoint;
                set => _inner.RemoteEndPoint = value;
            }

            public override void Abort(ConnectionAbortedException abortReason) => _inner.Abort(abortReason);

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                _headDeadline.Dispose();

                try
                {
                    await _inner
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }

                finally
                {
                    _lifetime?.Stop();
                    Interlocked.Decrement(ref _owner._connections);
                }
            }
        }
    }
}
