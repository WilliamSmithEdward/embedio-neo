using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Callers retain borrowed payloads until their write completes. Only queued
    // operations can be canceled; committed writes use connection cancellation.
    internal sealed class Http2OutputWriter : IDisposable
    {
        private static readonly Action<Task> ObserveDrainFailure = failed => _ = failed.Exception;
        private const int MaximumBatchBytes = 65536;
        private const int MaximumBatchOperations = 16;
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _stream;
        private readonly object _sync = new();
        private readonly Action<object?> _cancelQueued;
        private readonly WaitCallback _drainQueued;
        private readonly LinkedList<Operation> _pending = new();
        private bool _draining;
        private bool _disposed;
        private bool _failed;

        internal Http2OutputWriter(EmbedIO.Internal.BorrowedResource<Stream> stream)
        {
            _stream = stream;
            _cancelQueued = state => { if (state is Operation queued) CancelQueued(queued); };
            _drainQueued = _ => ObserveDrain(DrainAsync());
        }
        internal bool IsFailed => Volatile.Read(ref _failed);

        internal Task<bool> WriteAsync(Http2Frame[] frames, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl? flow, Action? apply)
        {
            requestToken.ThrowIfCancellationRequested();
            var leading = false;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http2OutputWriter));
                if (!_draining) { _draining = true; leading = true; }
            }
            return leading ? WriteLeadingAsync(frames, requestToken, connectionToken, flow, apply)
                : QueueAsync(frames, requestToken, connectionToken, flow, apply);
        }

        private async Task<bool> QueueAsync(Http2Frame[] frames, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl? flow, Action? apply)
        {
            var operation = new Operation(frames, requestToken, connectionToken, flow, apply);
            using var cancellation = requestToken.Register(_cancelQueued, operation);
            var start = false;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http2OutputWriter));
                requestToken.ThrowIfCancellationRequested();
                operation.Node = _pending.AddLast(operation);
                if (!_draining) { _draining = true; start = true; }
            }
            if (start) ObserveDrain(DrainAsync());
            return await operation.Completion.Task.ConfigureAwait(false);
        }

        private static void ObserveDrain(Task drain)
        {
            if (drain.IsFaulted) _ = drain.Exception;
            else if (!drain.IsCompleted)
                _ = drain.ContinueWith(ObserveDrainFailure, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task<bool> WriteLeadingAsync(Http2Frame[] frames, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl? flow, Action? apply)
        {
            byte[]? buffer = null;
            var started = false;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                if (IsFailed) throw new IOException("HTTP/2 output is no longer usable.");
                apply?.Invoke();
                if (flow != null && !flow.CanSendReserved(frames)) return false;
                long bytes = 0; var capacity = 9;
                foreach (var frame in frames)
                {
                    bytes += 9L + frame.PayloadLength;
                    capacity = Math.Max(capacity, frame.PayloadLength + 9);
                }
                buffer = ArrayPool<byte>.Shared.Rent(bytes <= MaximumBatchBytes ? Math.Max(9, (int)bytes) : capacity);
                if (bytes <= MaximumBatchBytes)
                {
                    var offset = 0;
                    foreach (var frame in frames) offset = Encode(frame, buffer, offset);
                    if (offset != 0)
                    {
                        started = true;
                        await _stream.Value.WriteAsync(buffer, 0, offset, connectionToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    foreach (var frame in frames)
                    {
                        var length = Encode(frame, buffer, 0);
                        started = true;
                        await _stream.Value.WriteAsync(buffer, 0, length, connectionToken).ConfigureAwait(false);
                    }
                }
                return true;
            }
            catch { if (started) Volatile.Write(ref _failed, true); throw; }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, true);
                FinishLeading();
            }
        }

        private void FinishLeading()
        {
            lock (_sync)
            {
                if (_pending.Count == 0) { _draining = false; return; }
            }
            // The completed leader must not wait for unrelated queued writers.
            if (!ThreadPool.QueueUserWorkItem(_drainQueued))
            {
                Volatile.Write(ref _failed, true);
                lock (_sync)
                {
                    while (_pending.First is { } node)
                    {
                        _pending.RemoveFirst(); node.Value.Node = null;
                        node.Value.Completion.TrySetException(new InvalidOperationException("HTTP/2 writer scheduling failed."));
                    }
                    _draining = false;
                }
            }
        }
        private void CancelQueued(Operation operation)
        {
            var canceled = false;
            lock (_sync)
            {
                if (operation.Node != null)
                {
                    _pending.Remove(operation.Node);
                    operation.Node = null;
                    canceled = true;
                }
            }
            if (canceled) operation.Completion.TrySetCanceled(operation.RequestToken);
        }

        private Operation? Take(Operation? first = null, long bytes = 0)
        {
            lock (_sync)
            {
                if (_pending.First is not { } node) return null;
                var next = node.Value;
                if (first != null && (next.ConnectionToken != first.ConnectionToken
                    || next.WireBytes > MaximumBatchBytes - bytes)) return null;
                _pending.RemoveFirst();
                next.Node = null;
                return next;
            }
        }

        private bool Prepare(Operation operation)
        {
            try
            {
                operation.RequestToken.ThrowIfCancellationRequested();
                if (IsFailed) throw new IOException("HTTP/2 output is no longer usable.");
                operation.Apply?.Invoke();
                if (operation.Flow != null && !operation.Flow.CanSendReserved(operation.Frames))
                {
                    operation.Completion.TrySetResult(false);
                    return false;
                }
                return true;
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                operation.Completion.TrySetException(error);
                return false;
            }
        }

        private async Task DrainAsync()
        {
            Operation?[]? operations = null;
            try
            {
                operations = ArrayPool<Operation?>.Shared.Rent(MaximumBatchOperations);
                while (true)
                {
                    var first = Take();
                    if (first == null)
                    {
                        lock (_sync)
                        {
                            if (_pending.Count != 0) continue;
                            _draining = false;
                            return;
                        }
                    }
                    operations[0] = first;
                    if (!Prepare(first)) { operations[0] = null; continue; }
                    var count = 1;
                    var bytes = first.WireBytes;
                    if (bytes <= MaximumBatchBytes)
                    {
                        while (count < MaximumBatchOperations)
                        {
                            var next = Take(first, bytes);
                            if (next == null) break;
                            operations[count] = next;
                            if (!Prepare(next)) { operations[count] = null; continue; }
                            count++;
                            bytes += next.WireBytes;
                        }
                    }
                    await SendBatchAsync(operations, count, bytes, first.ConnectionToken).ConfigureAwait(false);
                    Array.Clear(operations, 0, count);
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _failed, true);
                if (operations != null)
                    foreach (var operation in operations) operation?.Completion.TrySetException(error);
                lock (_sync)
                {
                    while (_pending.First is { } node)
                    {
                        _pending.RemoveFirst(); node.Value.Node = null;
                        node.Value.Completion.TrySetException(error);
                    }
                    _draining = false;
                }
                throw;
            }
            finally { if (operations != null) ArrayPool<Operation?>.Shared.Return(operations, true); }
        }

        private async Task SendBatchAsync(Operation?[] operations, int count, long bytes, CancellationToken token)
        {
            byte[]? buffer = null;
            var started = false;
            try
            {
                if (bytes <= MaximumBatchBytes)
                {
                    buffer = ArrayPool<byte>.Shared.Rent(Math.Max(9, (int)bytes));
                    var offset = 0;
                    for (var i = 0; i < count; i++)
                        foreach (var frame in (operations[i] ?? throw new InvalidOperationException("Missing committed write.")).Frames) offset = Encode(frame, buffer, offset);
                    if (offset != 0)
                    {
                        started = true;
                        await _stream.Value.WriteAsync(buffer, 0, offset, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    // An existing large header block remains indivisible. Its
                    // individual frames retain their negotiated-size write path.
                    var capacity = 9;
                    foreach (var frame in (operations[0] ?? throw new InvalidOperationException("Missing committed write.")).Frames) capacity = Math.Max(capacity, frame.PayloadLength + 9);
                    buffer = ArrayPool<byte>.Shared.Rent(capacity);
                    foreach (var frame in (operations[0] ?? throw new InvalidOperationException("Missing committed write.")).Frames)
                    {
                        var length = Encode(frame, buffer, 0);
                        started = true;
                        await _stream.Value.WriteAsync(buffer, 0, length, token).ConfigureAwait(false);
                    }
                }
                for (var i = 0; i < count; i++) operations[i]?.Completion.TrySetResult(true);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                if (started) Volatile.Write(ref _failed, true);
                for (var i = 0; i < count; i++) operations[i]?.Completion.TrySetException(error);
            }
            finally { if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, true); }
        }

        private static int Encode(Http2Frame frame, byte[] buffer, int offset)
        {
            var length = frame.PayloadLength;
            buffer[offset] = (byte)(length >> 16); buffer[offset + 1] = (byte)(length >> 8); buffer[offset + 2] = (byte)length;
            buffer[offset + 3] = frame.Type; buffer[offset + 4] = frame.Flags;
            buffer[offset + 5] = (byte)(frame.StreamId >> 24); buffer[offset + 6] = (byte)(frame.StreamId >> 16);
            buffer[offset + 7] = (byte)(frame.StreamId >> 8); buffer[offset + 8] = (byte)frame.StreamId;
            Buffer.BlockCopy(frame.Payload, frame.PayloadOffset, buffer, offset + 9, length);
            return offset + 9 + length;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                while (_pending.First is { } node)
                {
                    _pending.RemoveFirst();
                    node.Value.Node = null;
                    node.Value.Completion.TrySetException(new ObjectDisposedException(nameof(Http2OutputWriter)));
                }
            }
        }

        private sealed class Operation
        {
            internal readonly Http2Frame[] Frames;
            internal readonly CancellationToken RequestToken;
            internal readonly CancellationToken ConnectionToken;
            internal readonly Http2SendFlowControl? Flow;
            internal readonly Action? Apply;
            internal readonly long WireBytes;
            internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LinkedListNode<Operation>? Node;

            internal Operation(Http2Frame[] frames, CancellationToken requestToken, CancellationToken connectionToken,
                Http2SendFlowControl? flow, Action? apply)
            {
                Frames = frames; RequestToken = requestToken; ConnectionToken = connectionToken; Flow = flow; Apply = apply;
                foreach (var frame in frames) WireBytes += 9L + frame.PayloadLength;
            }
        }
    }
}
