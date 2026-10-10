using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Owns an accepted tunnel stream and coordinates completion of its send direction.</summary>
    public sealed class HttpTunnel : IDisposable
#if NET10_0_OR_GREATER
        , IAsyncDisposable
#endif
    {
        private readonly object _sync = new();
        private readonly Func<CancellationToken, Task> _completeOutput;
        private readonly Func<Exception, Task>? _abort;
        private Task? _completion;
        private Task? _disposal;
        /// <summary>Creates a tunnel over an already negotiated stream, taking ownership of that stream.</summary>
        /// <param name="stream">The duplex stream whose disposal ends the tunnel.</param>
        /// <param name="completeOutput">Backend action finishing only the send direction.</param>
        /// <param name="protocol">Selected protocol metadata; null for ordinary CONNECT.</param>
        /// <param name="useCapsules">Whether this accepted extension uses reliable capsule framing.</param>
        public HttpTunnel(Stream stream, Func<CancellationToken, Task> completeOutput, string? protocol = null, bool useCapsules = false)
            : this(stream, completeOutput, protocol, useCapsules, null) { }
        internal HttpTunnel(Stream stream, Func<CancellationToken, Task> completeOutput, string? protocol, bool useCapsules, Func<Exception, Task>? abort)
        {
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _completeOutput = completeOutput ?? throw new ArgumentNullException(nameof(completeOutput));
            _abort = abort;
            Protocol = protocol;
            Capsules = useCapsules ? new HttpCapsuleChannel(stream, abort) : null;
        }
        /// <summary>Gets the duplex stream. Capsule users should use Capsules for framing.</summary>
        public Stream Stream { get; }
        /// <summary>Gets the selected named protocol, or null for ordinary CONNECT.</summary>
        public string? Protocol { get; }
        /// <summary>Gets the capsule channel when explicitly negotiated, otherwise null.</summary>
        public HttpCapsuleChannel? Capsules { get; }
        /// <summary>Finishes the send direction after pending writes, while preserving readable peer input.</summary>
        /// <param name="cancellationToken">The first caller's token controls shared completion.</param>
        /// <returns>The same completion task for every caller, including failures.</returns>
        public Task CompleteOutputAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> start;
            Task completion;
            lock (_sync)
            {
                if (_completion != null) return _completion;
                // Core waits until its task is published, even when the backend
                // completes synchronously or asks for the same operation again.
                start = new TaskCompletionSource<bool>();
                completion = CompleteCoreAsync(start.Task, cancellationToken);
                _completion = completion;
            }
            start.TrySetResult(true);
            return completion;
        }
        private async Task CompleteCoreAsync(Task start, CancellationToken token)
        {
            await start.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            try { Capsules?.CompleteOutput(); }
            catch (InvalidDataException error) when (_abort != null)
            {
                try { await _abort(error).ConfigureAwait(false); }
                catch (Exception abortError) when (Internal.ExceptionPolicy.IsRecoverable(abortError))
                { throw new AggregateException(error, abortError); }
                throw;
            }
            await _completeOutput(token).ConfigureAwait(false);
        }
        /// <summary>Starts output completion and disposes the stream after that operation settles.</summary>
        /// <remarks>Use asynchronous disposal to observe completion and stream-disposal errors. This method does not block on network output.</remarks>
        public void Dispose()
        {
            var disposal = DisposeOwnedStreamAsync();
            if (!disposal.IsCompleted)
                _ = disposal.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else if (disposal.IsFaulted) _ = disposal.Exception;
        }
        /// <summary>Completes output and closes the owned stream once on every supported target.</summary>
        /// <returns>The shared close task, including completion and disposal failures.</returns>
        public Task CloseAsync() => DisposeOwnedStreamAsync();
#if NET10_0_OR_GREATER
        /// <summary>Completes output and disposes the owned stream once, observing any failure.</summary>
        /// <returns>The shared asynchronous disposal operation.</returns>
        public ValueTask DisposeAsync() => new(DisposeOwnedStreamAsync());
#endif
        internal Task DisposeOwnedStreamAsync(CancellationToken token = default)
        {
            TaskCompletionSource<bool> start;
            Task disposal;
            lock (_sync)
            {
                if (_disposal != null) return _disposal;
                start = new TaskCompletionSource<bool>();
                disposal = DisposeCoreAsync(start.Task, token);
                _disposal = disposal;
            }
            start.TrySetResult(true);
            return disposal;
        }
        private async Task DisposeCoreAsync(Task start, CancellationToken token)
        {
            await start.ConfigureAwait(false);
            Exception? failure = null;
            try { await CompleteOutputAsync(token).ConfigureAwait(false); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { Stream.Dispose(); }
                catch (Exception error) when (failure != null && Internal.ExceptionPolicy.IsRecoverable(failure) && Internal.ExceptionPolicy.IsRecoverable(error))
                { throw new AggregateException(failure, error); }
            }
        }
    }
}