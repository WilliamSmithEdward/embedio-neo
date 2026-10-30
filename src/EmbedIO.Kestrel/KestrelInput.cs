using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using Microsoft.AspNetCore.Http;

namespace EmbedIO.Kestrel
{
    // A retained input never follows Kestrel's reusable request feature into its successor.
    internal sealed class KestrelInput(PipeReader source) : Stream
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _retirement = new();
        private bool _retired;
        private int _disposed;
        private int _reads;
        private TaskCompletionSource<bool>? _settled;
        private Task? _retiring;

        internal bool Complete { get; private set; }
        public override bool CanRead => !_retired;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void Enter()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_retired, this);

                if (_reads != 0) throw new InvalidOperationException("A request body read is already in progress.");

                _reads++;
            }
        }

        private void Leave()
        {
            lock (_sync)
            {
                _reads--;

                if (_reads == 0) _settled?.TrySetResult(true);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Enter();

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _retirement.Token
                );
                linked.Token.ThrowIfCancellationRequested();

                if (buffer.Length == 0) return 0;

                using var cancellation = linked.Token.UnsafeRegister(
                    static state => (state as PipeReader)?.CancelPendingRead(),
                    source
                );

                while (true)
                {
                    // Kestrel's token-canceled body read can throw before returning
                    // its ReadResult. CancelPendingRead returns a result that we
                    // can always release through AdvanceTo, including cancellation.
                    var read = await source
                        .ReadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    var consumed = read.Buffer.Start;

                    try
                    {
                        if (read.IsCanceled) throw new OperationCanceledException(linked.Token);

                        var count = (int)Math.Min(read.Buffer.Length, buffer.Length);
                        var slice = read.Buffer.Slice(0, count);
                        slice.CopyTo(buffer.Span);
                        consumed = slice.End;

                        if (read.IsCompleted && count == read.Buffer.Length) Complete = true;

                        if (count != 0 || read.IsCompleted) return count;
                    }

                    finally
                    {
                        source.AdvanceTo(consumed);
                    }
                }
            }

            catch (BadHttpRequestException error)
            {
                throw new HttpException(error.StatusCode, "Invalid or incomplete request body.");
            }

            finally
            {
                Leave();
            }
        }

        internal Task RetireAsync()
        {
            Task pending;
            TaskCompletionSource<bool> completion;

            lock (_sync)
            {
                if (_retiring != null) return _retiring;

                _retired = true;
                pending = _reads == 0
                    ? Task.CompletedTask
                    : (_settled ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _retiring = completion.Task;
            }

            _ = FinishRetireAsync(pending, completion);
            return completion.Task;
        }

        private async Task FinishRetireAsync(Task pending, TaskCompletionSource<bool> completion)
        {
            // Cancel only this request's borrowed read. Aborting the TCP connection
            // here can discard a completed 408 response before the client receives it.
            try
            {
                _retirement.Cancel();
                await pending.ConfigureAwait(false);
                completion.TrySetResult(true);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                completion.TrySetException(error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                RetireAsync()
                    .GetAwaiter()
                    .GetResult();
                _retirement.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
