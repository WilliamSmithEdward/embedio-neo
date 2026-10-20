using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // One non-consuming receive per opted-in connection, including on legacy runtimes
    // where an individual socket receive cannot be canceled without closing the socket.
    internal sealed class Http1RequestAbortObserver
    {
        private readonly object _sync = new();
        private readonly Socket _socket;
        private readonly Action _abortTransport;

        // The observation worker owns disposal after its last uncancelable socket peek.
        private readonly BorrowedResource<ObservationResources> _resources = new(new ObservationResources());

        private CancellationTokenSource Stop => _resources.Value.Stop;
        private SemaphoreSlim Activity => _resources.Value.Activity;

        private RequestAbortScope? _active;
        private bool _inputEnded;
        private bool _retired;

        internal Http1RequestAbortObserver(Socket socket, Action abortTransport)
        {
            _socket = socket;
            _abortTransport = abortTransport;

            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(ObserveAsync);

            else
            {
                using (ExecutionContext.SuppressFlow()) _ = Task.Run(ObserveAsync);
            }
        }

        internal void Activate(RequestAbortScope scope)
        {
            lock (_sync)
            {
                if (_retired) throw new InvalidOperationException("The connection has retired.");

                _active = scope;

                if (_inputEnded) scope.InputEnded();

                else if (Activity.CurrentCount == 0) Activity.Release();
            }
        }

        internal void Complete(object context)
        {
            var scope = RequestAbortRegistry.Find(context);

            if (scope == null) return;

            lock (_sync)
            {
                if (ReferenceEquals(_active, scope)) _active = null;
            }

            scope.Complete();
        }

        internal void Retire()
        {
            lock (_sync)
            {
                if (_retired) return;

                _retired = true;
                _active?.Abort();
                _active?.Complete();
                _active = null;
                Stop.Cancel();
            }
        }

        private async Task ObserveAsync()
        {
            var buffer = new byte[1];

            try
            {
                while (true)
                {
                    await Activity
                        .WaitAsync(Stop.Token)
                        .ConfigureAwait(false);

                    while (true)
                    {
                        // Completed fast requests need no socket operation; unread upload/pipeline
                        // or TLS bytes must neither be consumed nor trigger a busy loop.
                        await Task
                            .Delay(50, Stop.Token)
                            .ConfigureAwait(false);

                        lock (_sync)
                        {
                            if (_retired) return;

                            if (_active == null) break;
                        }

                        var count = await _socket
                            .ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.Peek)
                            .ConfigureAwait(false);

                        if (count != 0) continue;

                        lock (_sync)
                        {
                            _inputEnded = true;
                            _active?.InputEnded();
                        }

                        // FIN is a valid half-close. Future opted-in requests can still consume
                        // buffered input; the default token remains healthy until a real abort.
                        await Task
                            .Delay(Timeout.Infinite, Stop.Token)
                            .ConfigureAwait(false);
                    }
                }
            }

            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }

            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                // Peeking may consume the pending socket error. Retire the transport so the
                // consuming parser cannot continue after losing that error indication.
                try
                {
                    _abortTransport();
                }

                finally
                {
                    Retire();
                }
            }

            finally
            {
                // Legacy delay cancellation can resume inline. Leave the canceling stack
                // and let Retire finish before disposing its borrowed synchronization state.
                await Task.Yield();

                lock (_sync) _resources.Value.Dispose();
            }
        }

        private sealed class ObservationResources : IDisposable
        {
            internal readonly CancellationTokenSource Stop = new();
            internal readonly SemaphoreSlim Activity = new(0, 1);

            public void Dispose()
            {
                Activity.Dispose();
                Stop.Dispose();
            }
        }
    }
}
