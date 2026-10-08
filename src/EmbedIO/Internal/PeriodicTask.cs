using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Internal
{
    internal sealed class PeriodicTask : IDisposable
    {
        private readonly Action _cancel;
        private readonly object _sync = new object();
        private bool _disposed;

        internal PeriodicTask(TimeSpan interval, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (action == null) throw new ArgumentNullException(nameof(action));
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancel = stop.Cancel;
            Completion = Task.Run(() => RunAsync(interval, action, stop));
        }

        internal Task Completion { get; }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _cancel();
            }
        }

        private async Task RunAsync(TimeSpan interval, Func<CancellationToken, Task> action, CancellationTokenSource stop)
        {
            var token = stop.Token;
            try
            {
                while (true)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    try { await action(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception)) { exception.Log(nameof(PeriodicTask)); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                lock (_sync)
                {
                    _disposed = true;
                    stop.Dispose();
                }
            }
        }
    }
}
