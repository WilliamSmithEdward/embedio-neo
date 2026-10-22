using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Quic
{
    internal sealed class QuicConnectionFailure : IOException
    {
        internal QuicConnectionFailure(ulong errorCode, bool application, bool remote)
            : base("QUIC connection has closed.")
        {
            ErrorCode = errorCode;
            Application = application;
            Remote = remote;
        }

        internal ulong ErrorCode { get; }
        internal bool Application { get; }
        internal bool Remote { get; }
    }

    internal sealed class AsyncStreamApplication : StreamApplication
    {
        private readonly StreamOperations _operations;
        private readonly Action? _wake;

        internal AsyncStreamApplication(StreamOperations operations, Action? wake = null)
        {
            ArgumentNullException.ThrowIfNull(operations);
            _operations = operations;
            _wake = wake;
            _accepted = Channel.CreateBounded<AsyncQuicStream>(new BoundedChannelOptions(
                operations.InboundStreamCapacity
            )
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        private sealed class OpenRequest
        {
            private readonly bool _uni;
            private readonly Action _notify;

            internal OpenRequest(bool uni, Action notify, CancellationToken token)
            {
                _uni = uni;
                _notify = notify;
                Token = token;
            }

            private readonly object _gate = new();
            private bool _done;

            internal TaskCompletionSource<AsyncQuicStream> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            internal CancellationToken Token { get; }

            internal void Cancel()
            {
                lock (_gate)
                {
                    if (_done) return;

                    _done = true;
                    Completion.TrySetCanceled(Token);
                }

                _notify();
            }

            internal bool TryOpen(StreamOperations port, Func<ulong, AsyncQuicStream> stream)
            {
                lock (_gate)
                {
                    if (_done) return true;

                    if (Token.IsCancellationRequested)
                    {
                        _done = true;
                        Completion.TrySetCanceled(Token);
                        return true;
                    }

                    var id = port.Open(_uni);

                    if (!id.HasValue) return false;

                    var opened = stream(id.Value);
                    _done = true;
                    Completion.TrySetResult(opened);
                    return true;
                }
            }

            internal void Fail(Exception error)
            {
                lock (_gate)
                {
                    if (_done) return;

                    _done = true;
                    Completion.TrySetException(error);
                }
            }
        }

        private readonly object _gate = new();
        private readonly Dictionary<ulong, AsyncQuicStream> _streams = [];
        private readonly List<OpenRequest> _opening = [];
        private readonly Queue<byte[]> _datagrams = new();
        private readonly Channel<byte> _ready = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
        private readonly Channel<AsyncQuicStream> _accepted;
        private readonly Channel<byte[]> _receivedDatagrams = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(
            32
        )
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private ulong? _closeCode;
        private int _accepting;
        private int _readingDatagram;

        internal int RetiredStreams { get; private set; }

        internal int Retained
        {
            get
            {
                lock (_gate) return _streams.Count;
            }
        }

        internal int PendingOpens
        {
            get
            {
                lock (_gate) return _opening.Count;
            }
        }

        internal int? MaximumDatagramPayload => _operations.DatagramLimit;
        internal Task Closed => _closed.Task;

        internal bool IsClosed
        {
            get
            {
                lock (_gate) return _failure != null;
            }
        }

        internal Task<bool> WaitForCommandsAsync(CancellationToken token) =>
            _ready.Reader
                .WaitToReadAsync(token)
                .AsTask();

        private void Notify()
        {
            _ready.Writer.TryWrite(0);
            _wake?.Invoke();
        }

        private void CheckOpen()
        {
            if (_failure != null) throw _failure;
        }

        private AsyncQuicStream GetStream(ulong id)
        {
            lock (_gate)
            {
                CheckOpen();
                return GetStreamCore(id);
            }
        }

        private AsyncQuicStream? GetInboundStream(ulong id)
        {
            lock (_gate)
            {
                // A packet accepted before connection close may finish parsing
                // after the application has released all of its stream owners.
                if (_failure != null) return null;

                return GetStreamCore(id);
            }
        }

        private AsyncQuicStream GetStreamCore(ulong id)
        {
            if (_streams.TryGetValue(id, out var stream)) return stream;

            if (_streams.Count >= _operations.StreamCapacity)
                throw new InvalidDataException("Async stream owner limit exceeded.");

            stream = new AsyncQuicStream(id, Notify);
            _streams.Add(id, stream);

            if ((id & 1) == 0 && !_accepted.Writer.TryWrite(stream))
                throw new InvalidDataException("Unaccepted stream limit exceeded.");

            return stream;
        }

        internal override void Open(ulong id) => _ = GetInboundStream(id);

        internal override void Stream(ulong id, byte[] bytes, bool fin) => GetInboundStream(id)?.Receive(bytes, fin);

        internal override void Reset(ulong id, ulong error, ulong reliableSize) =>
            GetInboundStream(id)?.PeerReset(error, reliableSize);

        internal override void Stop(ulong id, ulong error) => GetInboundStream(id)?.PeerStopped(error);

        internal override void WritesClosed(ulong id)
        {
            AsyncQuicStream? stream;

            lock (_gate) _streams.TryGetValue(id, out stream);

            stream?.AcknowledgeWrites();
        }

        internal override bool CanRetire(ulong id)
        {
            AsyncQuicStream? stream;

            lock (_gate) _streams.TryGetValue(id, out stream);

            return stream?.CanRetire == true;
        }

        internal override void Retire(ulong id)
        {
            AsyncQuicStream? stream;

            lock (_gate) _streams.Remove(id, out stream);

            stream?.Retire();
            RetiredStreams++;
            Notify();
        }

        internal async ValueTask<AsyncQuicStream> AcceptInboundStreamAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _accepting, 1, 0) != 0)
                throw new InvalidOperationException("A stream accept is already pending.");

            try
            {
                lock (_gate) CheckOpen();

                var stream = await _accepted.Reader
                    .ReadAsync(token)
                    .ConfigureAwait(false);

                lock (_gate) CheckOpen();

                stream.Accept();
                return stream;
            }

            catch (ChannelClosedException)
            {
                lock (_gate) throw _failure ?? new IOException("Connection accept queue has closed.");
            }

            finally
            {
                Interlocked.Exchange(ref _accepting, 0);
            }
        }

        internal ValueTask<AsyncQuicStream> OpenOutboundStreamAsync(
            bool unidirectional,
            CancellationToken token = default
        )
        {
            token.ThrowIfCancellationRequested();
            OpenRequest request;

            lock (_gate)
            {
                CheckOpen();

                if (_opening.Count >= 32) throw new InvalidOperationException("Pending stream-open limit exceeded.");

                request = new OpenRequest(unidirectional, Notify, token);
                _opening.Add(request);
                Notify();
            }

            return AwaitOpenAsync(request);
        }

        private static async ValueTask<AsyncQuicStream> AwaitOpenAsync(OpenRequest request)
        {
            using var registration = request.Token.UnsafeRegister(
                static state => (state as OpenRequest)?.Cancel(),
                request
            );
            return await request.Completion.Task.ConfigureAwait(false);
        }

        internal override void ReceiveDatagram(byte[] bytes) => _receivedDatagrams.Writer.TryWrite(bytes);

        internal async ValueTask<byte[]> ReceiveDatagramAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _readingDatagram, 1, 0) != 0)
                throw new InvalidOperationException("A datagram read is already pending.");

            try
            {
                lock (_gate) CheckOpen();

                var bytes = await _receivedDatagrams.Reader
                    .ReadAsync(token)
                    .ConfigureAwait(false);

                lock (_gate) CheckOpen();

                return bytes;
            }

            catch (ChannelClosedException)
            {
                lock (_gate) throw _failure ?? new IOException("Connection datagram queue has closed.");
            }

            finally
            {
                Interlocked.Exchange(ref _readingDatagram, 0);
            }
        }

        internal bool TrySendDatagram(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                CheckOpen();
                var limit = MaximumDatagramPayload
                    ?? throw new NotSupportedException("Peer has not enabled datagrams.");

                if (bytes.Length > limit) throw new ArgumentOutOfRangeException(nameof(bytes));

                if (_datagrams.Count >= 32) return false;

                _datagrams.Enqueue(bytes.ToArray());
                Notify();
                return true;
            }
        }

        internal ValueTask CloseAsync(ulong code, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (code > StreamReassembly.MaximumOffset) throw new ArgumentOutOfRangeException(nameof(code));

            lock (_gate)
            {
                if (_failure != null) return ValueTask.CompletedTask;

                if (_operations.Close == null)
                    throw new NotSupportedException("Connection close callback is unavailable.");

                _closeCode ??= code;
                Notify();
            }

            // Once queued, closing is irreversible; cancellation only stops waiting.
            return new ValueTask(_closed.Task.WaitAsync(token));
        }

        internal override void Poll()
        {
            // Capacity is one. Consume at most one signal so concurrent producers
            // cannot postpone processing the bounded work represented by it.
            _ready.Reader.TryRead(out _);

            OpenRequest[] opening;
            AsyncQuicStream[] streams;
            ulong? close;

            lock (_gate)
            {
                if (_failure != null) return;

                opening = [.. _opening];
                streams = [.. _streams.Values];
                close = _closeCode;
            }

            if (close.HasValue)
            {
                (_operations.Close ?? throw new InvalidOperationException("Missing close callback."))(close.Value);
                FailAll(new QuicConnectionFailure(close.Value, true, false));
                return;
            }

            foreach (var request in opening)
                if (request.TryOpen(_operations, GetStream))
                    lock (_gate) _opening.Remove(request);

            foreach (var stream in streams) stream.Process(_operations);

            for (var index = 0; index < 8; index++)
            {
                lock (_gate)
                {
                    if (_datagrams.Count == 0 || !_operations.TryDatagram(_datagrams.Peek())) break;

                    _datagrams.Dequeue();

                    if (_datagrams.Count != 0) Notify();
                }
            }
        }

        internal override void ConnectionClosed(ulong error, bool application) =>
            FailAll(new QuicConnectionFailure(error, application, true));

        private void FailAll(Exception error)
        {
            AsyncQuicStream[] streams;
            OpenRequest[] opening;

            lock (_gate)
            {
                if (_failure != null) return;

                _failure = error;
                streams = [.. _streams.Values];
                opening = [.. _opening];
                _streams.Clear();
                _opening.Clear();
                _datagrams.Clear();
            }

            foreach (var stream in streams) stream.CloseConnection(error);

            foreach (var request in opening) request.Fail(error);

            while (_accepted.Reader.TryRead(out _)) { }

            while (_receivedDatagrams.Reader.TryRead(out _)) { }

            _accepted.Writer.TryComplete();
            _receivedDatagrams.Writer.TryComplete();
            _ready.Writer.TryComplete();
            _closed.TrySetResult();
        }

        public override void Dispose() => FailAll(new QuicConnectionFailure(0, false, false));
    }
}
