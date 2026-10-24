using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using EmbedIO.Net;
using EmbedIO.Net.Internal;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    // The HTTP/3 listener's 256 slots belong to connections that can still serve requests. A
    // connection gives its slot back once it has closed, or has finished a GOAWAY drain, and
    // every stream worker has ended. Its transport then closes or drains without the slot.
    // These run the actual listener and HTTP/3 connection over an in-memory transport whose
    // closing or draining period the test ends.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class ProductionListenerSlotTests
    {
        private const int Port = 59214;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        internal static async Task RunAsync()
        {
            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1)
            );
            var source = new ServerCertificateSource(certificate);
            var failures = new List<Exception>();
            Func<Task>[] tests =
            [
                () => ClosedConnectionAsync(source),
                () => ClosedConnectionWorkerAsync(source),
                () => DrainedConnectionAsync(source)
            ];

            foreach (var test in tests)
            {
                try
                {
                    await test().ConfigureAwait(false);
                }

                catch (Exception error) when (error is QuicFuzzFailure or TimeoutException)
                {
                    failures.Add(error);
                }
            }

            if (failures.Count != 0)
                throw new AggregateException("HTTP/3 listener slot regressions failed.", failures);
        }

        // A connection the peer closes frees its slot while its transport still drains, and
        // gives the slot back only once.
        private static async Task ClosedConnectionAsync(ServerCertificateSource source)
        {
            var binding = new Binding();
            using var listener = Listener(source, binding, null);
            var transport = new Transport();
            var peer = new Peer(transport);

            try
            {
                binding.Admit(peer);
                await UntilAsync(() => binding.Count == 1, "an accepted connection takes a slot").ConfigureAwait(false);
                var stream = transport.Open(0);
                stream.Receive(Request("/"), true);
                await stream.Responded
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                Check(binding.Count == 1, "an open connection keeps its slot between requests");
                transport.Close(0x100, true);
                await UntilAsync(
                    () => transport.ClosingAwaited,
                    "a closed connection's transport drains"
                ).ConfigureAwait(false);
                Check(
                    binding.Count == 0 && !transport.Disposed.Task.IsCompleted && !peer.Disposed.Task.IsCompleted,
                    "a closed connection frees its slot while its transport drains"
                );
                transport.FinishClosing();
                await peer.Disposed.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                Check(binding.Count == 0, "a closed connection gives its slot back once");
            }

            finally
            {
                transport.FinishClosing();
                listener.Stop();
            }
        }

        // A connection the peer closes keeps its slot until its last stream worker ends.
        private static async Task ClosedConnectionWorkerAsync(ServerCertificateSource source)
        {
            var binding = new Binding();
            using var listener = Listener(source, binding, null);
            var transport = new Transport();
            var peer = new Peer(transport);
            var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                binding.Admit(peer);
                await UntilAsync(() => binding.Count == 1, "an accepted connection takes a slot").ConfigureAwait(false);
                var stream = transport.Open(0);
                stream.DisposalHold = disposal.Task;
                stream.Receive(Request("/"), true);
                await stream.Disposing.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                transport.Close(0x100, true);
                await transport.CloseRequested.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                await Task
                    .Delay(200)
                    .ConfigureAwait(false);
                Check(
                    binding.Count == 1 && !transport.ClosingAwaited,
                    "a closed connection keeps its slot while a stream worker runs"
                );
                disposal.TrySetResult();
                await UntilAsync(() => binding.Count == 0, "the last stream worker frees the slot").ConfigureAwait(
                    false
                );
                transport.FinishClosing();
                await peer.Disposed.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                Check(binding.Count == 0, "a closed connection gives its slot back once");
            }

            finally
            {
                disposal.TrySetResult();
                transport.FinishClosing();
                listener.Stop();
            }
        }

        // A connection that sends GOAWAY keeps its slot while a request it accepted still runs,
        // and frees it when that request ends, before the peer closes the connection.
        private static async Task DrainedConnectionAsync(ServerCertificateSource source)
        {
            var binding = new Binding();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = Listener(source, binding, new Hold(held, hold.Task));
            var transport = new Transport();
            var peer = new Peer(transport);

            try
            {
                binding.Admit(peer);
                await UntilAsync(() => binding.Count == 1, "an accepted connection takes a slot").ConfigureAwait(false);
                var running = transport.Open(0);
                running.Receive(Request("/hold"), true);
                await held.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                var closing = transport.Open(4);
                closing.Receive(Request("/close"), true);
                await UntilAsync(
                    () => transport.Goaway == 8,
                    "a response that closes the connection starts a drain"
                ).ConfigureAwait(false);
                await Task
                    .Delay(200)
                    .ConfigureAwait(false);
                Check(binding.Count == 1, "a draining connection keeps its slot while a request runs");
                hold.TrySetResult();
                await running.Responded
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                await UntilAsync(() => binding.Count == 0, "a drained connection frees its slot").ConfigureAwait(false);
                Check(
                    !transport.CloseRequested.Task.IsCompleted && !transport.ClosingAwaited,
                    "a drained connection frees its slot while it waits for the peer to close"
                );
                var refused = transport.Open(8);
                refused.Receive(Request("/"), true);
                await UntilAsync(
                    () => refused.AbortCode == 0x10b,
                    "a drained connection refuses new requests"
                ).ConfigureAwait(false);
                transport.Close(0x100, true);
                await UntilAsync(() => transport.ClosingAwaited, "the closed connection drains").ConfigureAwait(false);
                transport.FinishClosing();
                await peer.Disposed.Task
                    .WaitAsync(Timeout)
                    .ConfigureAwait(false);
                Check(binding.Count == 0, "a drained connection gives its slot back once");
            }

            finally
            {
                hold.TrySetResult();
                transport.FinishClosing();
                listener.Stop();
            }
        }

        private static Http3Listener Listener(ServerCertificateSource source, Binding binding, Hold? hold)
        {
            var listener = new Http3Listener(source, null, (_, _, _) => binding);
            listener.AddPrefix($"https://127.0.0.1:{Port}/");
            ((IDispatchingHttpListener)listener).SetDispatcher(context => RespondAsync(context, hold));
            listener.Start();
            return listener;
        }

        private static async Task RespondAsync(IHttpContextImpl context, Hold? hold)
        {
            var path = context.Request.Url.AbsolutePath;

            if (path == "/hold" && hold != null)
            {
                hold.Entered.TrySetResult();
                await hold.Release.ConfigureAwait(false);
            }

            // A response that does not keep the connection alive makes it send GOAWAY and drain.
            if (path == "/close") context.Response.KeepAlive = false;

            context.Response.StatusCode = 204;
            await ((MultiplexedContext)context)
                .CloseAsync()
                .ConfigureAwait(false);
        }

        // HEADERS for a GET whose field section uses only the QPACK static table (RFC 9204
        // appendix A): Required Insert Count and Delta Base 0, :method GET, :scheme https, then
        // :authority and :path with literal values.
        private static byte[] Request(string path)
        {
            var block = new List<byte> { 0, 0, 0xd1, 0xd7 };
            Literal(block, 0x50, $"127.0.0.1:{Port}");
            Literal(block, 0x51, path);
            var frame = new List<byte>
            {
                1,
                (byte)block.Count
            };
            frame.AddRange(block);
            return frame.ToArray();
        }

        private static void Literal(List<byte> block, byte name, string value)
        {
            block.Add(name);
            block.Add((byte)value.Length);
            block.AddRange(Encoding.ASCII.GetBytes(value));
        }

        private static async Task UntilAsync(Func<bool> condition, string invariant)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (!condition())
            {
                if (clock.Elapsed > Timeout) throw new TimeoutException("HTTP/3 listener slot: " + invariant);

                await Task
                    .Delay(5)
                    .ConfigureAwait(false);
            }
        }

        private static void Check(bool condition, string invariant) =>
            QuicFuzzChecks.Require(condition, "HTTP/3 listener slot: " + invariant);

        private sealed record Hold(TaskCompletionSource Entered, Task Release);

        // Hands the listener the connections a test admits, and reads the listener's slot count
        // through the admission check the listener gives it.
        private sealed class Binding : Http3ListenerBinding
        {
            private readonly Channel<Http3AcceptedConnection> _accepted = Channel.CreateUnbounded<Http3AcceptedConnection>();
            private Func<int, bool>? _saturated;

            // The connections holding slots: the check refuses a connection once those, with the
            // ones queued here, reach 256. Negative if a slot was given back twice.
            internal int Count
            {
                get
                {
                    var saturated = Volatile.Read(ref _saturated)
                        ?? throw new InvalidOperationException("The listener set no admission limit.");

                    if (!saturated(256)) return -1;

                    var count = 0;

                    while (count < 256 && saturated(255 - count)) count++;

                    return count;
                }
            }

            internal void Admit(Http3AcceptedConnection connection) => _accepted.Writer.TryWrite(connection);

            internal override void LimitAdmission(Func<int, bool> saturated) =>
                Volatile.Write(ref _saturated, saturated);

            internal override async ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token) =>
                await _accepted.Reader
                    .ReadAsync(token)
                    .ConfigureAwait(false);

            internal override ValueTask StopAcceptingAsync()
            {
                _accepted.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }

            public override ValueTask DisposeAsync() => StopAcceptingAsync();
        }

        private sealed class Peer(Transport transport) : Http3AcceptedConnection
        {
            private static readonly IPEndPoint Local = new(IPAddress.Loopback, Port);
            private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 50000);

            internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override bool HandshakePending => true;

            internal override ValueTask WaitReadyAsync(CancellationToken token) => ValueTask.CompletedTask;

            internal override Task RunAsync(
                Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
                CancellationToken abort,
                CancellationToken drain,
                WebTransportOptions? webTransport = null,
                TimeSpan? requestHeadersTimeout = null,
                Action? released = null
            ) =>
                Http3QuicConnection.RunTransportForListenerAsync(
                    transport,
                    exchange => dispatch(exchange, Local, Remote),
                    abort,
                    drain,
                    TimeSpan.FromMilliseconds(uint.MaxValue - 1),
                    webTransport,
                    requestHeadersTimeout,
                    released
                );

            internal override ValueTask RejectAsync(CancellationToken token) => ValueTask.CompletedTask;

            public override ValueTask DisposeAsync()
            {
                Disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }

        // A connection whose peer opens streams and closes when the test says so. Its closing
        // or draining period lasts until FinishClosing.
        private sealed class Transport : Http3TransportConnection
        {
            private readonly object _gate = new();
            private readonly Channel<Http3TransportStream> _inbound = Channel.CreateUnbounded<Http3TransportStream>();
            private readonly List<PeerStream> _streams = [];
            private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private PeerStream? _control;
            private Exception? _failure;
            private long _next = 3;
            private int _closingAwaited;

            internal TaskCompletionSource CloseRequested { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool ClosingAwaited => Volatile.Read(ref _closingAwaited) != 0;

            // The stream ID limit of the GOAWAY on the server's control stream, if one was sent.
            internal long? Goaway
            {
                get
                {
                    PeerStream? control;

                    lock (_gate) control = _control;

                    if (control == null) return null;

                    var bytes = control.Written();
                    var offset = 0;
                    QuicInteger.Read(bytes, ref offset, bytes.Length);

                    while (offset < bytes.Length)
                    {
                        var type = QuicInteger.Read(bytes, ref offset, bytes.Length);
                        var length = (int)QuicInteger.Read(bytes, ref offset, bytes.Length);
                        var end = offset + length;

                        if (end > bytes.Length) return null;

                        if (type == 7) return QuicInteger.Read(bytes, ref offset, end);

                        offset = end;
                    }

                    return null;
                }
            }

            internal PeerStream Open(long id)
            {
                var stream = new PeerStream(id, Http3StreamType.Bidirectional);

                lock (_gate)
                {
                    if (_failure != null) throw new InvalidOperationException("The connection has closed.");

                    _streams.Add(stream);
                }

                _inbound.Writer.TryWrite(stream);
                return stream;
            }

            internal void Close(ulong code, bool remote) => Fail(new QuicConnectionFailure(code, true, remote));

            internal void FinishClosing() => _closing.TrySetResult();

            internal override ValueTask<Http3TransportStream> OpenOutboundStreamAsync(
                Http3StreamType type,
                CancellationToken token
            )
            {
                var stream = new PeerStream(Interlocked.Add(ref _next, 4) - 4, type);

                lock (_gate)
                {
                    if (_failure != null) throw _failure;

                    _control ??= stream;
                    _streams.Add(stream);
                }

                return ValueTask.FromResult<Http3TransportStream>(stream);
            }

            internal override async ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token)
            {
                try
                {
                    return await _inbound.Reader
                        .ReadAsync(token)
                        .ConfigureAwait(false);
                }

                catch (ChannelClosedException)
                {
                    lock (_gate) throw _failure ?? new InvalidOperationException("Missing connection failure.");
                }
            }

            internal override ValueTask CloseAsync(long code, CancellationToken token)
            {
                CloseRequested.TrySetResult();
                Close((ulong)code, false);
                return ValueTask.CompletedTask;
            }

            internal override Task WaitClosedAsync(CancellationToken token)
            {
                Interlocked.Exchange(ref _closingAwaited, 1);
                return _closing.Task.WaitAsync(token);
            }

            public override ValueTask DisposeAsync()
            {
                Close(0, false);
                _closing.TrySetResult();
                Disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }

            private void Fail(Exception error)
            {
                PeerStream[] streams;

                lock (_gate)
                {
                    if (_failure != null) return;

                    _failure = error;
                    streams = [.. _streams];
                }

                _inbound.Writer.TryComplete();

                foreach (var stream in streams) stream.Fail(error);
            }
        }

        // A stream whose input the test supplies and whose output it reads back; the peer
        // acknowledges output at once.
        private sealed class PeerStream : Http3TransportStream
        {
            private readonly object _gate = new();
            private readonly Queue<byte[]> _input = new();
            private readonly MemoryStream _output = new();
            private readonly TaskCompletionSource _readsClosed = Observed();
            private readonly TaskCompletionSource _writesClosed = Observed();
            private TaskCompletionSource? _readable;
            private Exception? _readFailure;
            private Exception? _writeFailure;
            private int _offset;
            private bool _fin;
            private long _abortCode = -1;

            internal PeerStream(long id, Http3StreamType type)
            {
                Id = id;
                Type = type;
            }

            internal override long Id { get; }
            internal override Http3StreamType Type { get; }
            internal override Task ReadsClosed => _readsClosed.Task;
            internal override Task WritesClosed => _writesClosed.Task;

            // Completes when the server has written a complete response.
            internal Task Responded => _writesClosed.Task;

            // Set before the stream is used: disposal waits for it.
            internal Task? DisposalHold { get; set; }
            internal TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal long AbortCode => Interlocked.Read(ref _abortCode);
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            internal void Receive(byte[] bytes, bool fin)
            {
                TaskCompletionSource? readable;

                lock (_gate)
                {
                    _input.Enqueue(bytes);
                    _fin |= fin;
                    readable = _readable;
                    _readable = null;
                }

                readable?.TrySetResult();
            }

            internal byte[] Written()
            {
                lock (_gate) return _output.ToArray();
            }

            internal void Fail(Exception error)
            {
                FailReads(error);
                FailWrites(error);
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default
            )
            {
                while (true)
                {
                    Task readable;

                    lock (_gate)
                    {
                        if (_readFailure != null) throw _readFailure;

                        while (_input.Count != 0 && _input.Peek().Length == _offset)
                        {
                            _input.Dequeue();
                            _offset = 0;
                        }

                        if (_input.Count != 0)
                        {
                            var head = _input.Peek();
                            var count = Math.Min(buffer.Length, head.Length - _offset);
                            head
                                .AsMemory(_offset, count)
                                .CopyTo(buffer);
                            _offset += count;
                            return count;
                        }

                        if (_fin)
                        {
                            _readsClosed.TrySetResult();
                            return 0;
                        }

                        readable =
                            (_readable ??= new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )).Task;
                    }

                    await readable
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            internal override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                bool completeWrites,
                CancellationToken token
            )
            {
                token.ThrowIfCancellationRequested();
                Append(buffer.Span, completeWrites);
                return ValueTask.CompletedTask;
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default
            ) => WriteAsync(buffer, false, cancellationToken);

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            ) => WriteAsync(buffer.AsMemory(offset, count), false, cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count) =>
                Append(buffer.AsSpan(offset, count), false);

            internal override void CompleteWrites() => Append([], true);

            internal override void Abort(Http3AbortDirection direction, long code)
            {
                Interlocked.CompareExchange(ref _abortCode, code, -1);
                var failure = new QuicStreamFailure((ulong)code, false);

                if ((direction & Http3AbortDirection.Read) != 0) FailReads(failure);

                if ((direction & Http3AbortDirection.Write) != 0) FailWrites(failure);
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override async ValueTask DisposeAsync()
            {
                Disposing.TrySetResult();

                if (DisposalHold != null) await DisposalHold.ConfigureAwait(false);

                Fail(new ObjectDisposedException(nameof(PeerStream)));
            }

            private void Append(ReadOnlySpan<byte> bytes, bool complete)
            {
                lock (_gate)
                {
                    if (_writeFailure != null) throw _writeFailure;

                    _output.Write(bytes);
                }

                if (complete) _writesClosed.TrySetResult();
            }

            private void FailReads(Exception error)
            {
                TaskCompletionSource? readable;

                lock (_gate)
                {
                    _readFailure ??= error;
                    readable = _readable;
                    _readable = null;
                }

                _readsClosed.TrySetException(error);
                readable?.TrySetResult();
            }

            private void FailWrites(Exception error)
            {
                lock (_gate) _writeFailure ??= error;

                _writesClosed.TrySetException(error);
            }

            private static TaskCompletionSource Observed()
            {
                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = source.Task.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
                return source;
            }
        }
    }
}
