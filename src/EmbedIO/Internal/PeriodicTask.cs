using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Internal
{
    internal sealed class PeriodicTask : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly object _sync = new object();
        private bool _disposed;

        internal PeriodicTask(TimeSpan interval, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (action == null) throw new ArgumentNullException(nameof(action));
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Completion = Task.Run(() => RunAsync(interval, action, _stop.Token));
        }

        internal Task Completion { get; }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
            }
        }

        private async Task RunAsync(TimeSpan interval, Func<CancellationToken, Task> action, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    try { await action(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception exception) { exception.Log(nameof(PeriodicTask)); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                lock (_sync)
                {
                    _disposed = true;
                    _stop.Dispose();
                }
            }
        }
    }
}
