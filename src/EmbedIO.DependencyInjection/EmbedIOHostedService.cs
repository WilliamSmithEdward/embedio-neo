using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmbedIO.DependencyInjection
{
    internal sealed class EmbedIOHostedService : IHostedService, IDisposable, IAsyncDisposable
    {
        private readonly WebServer _server;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<EmbedIOHostedService> _logger;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _run;
        private Task? _shutdown;

        internal EmbedIOHostedService(WebServer server, IHostApplicationLifetime lifetime, ILogger<EmbedIOHostedService> logger)
        {
            _server = server;
            _lifetime = lifetime;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_run != null) throw new InvalidOperationException("The EmbedIO hosted server cannot be restarted.");
            cancellationToken.ThrowIfCancellationRequested();
            _server.StateChanged += OnStateChanged;
            _run = Task.Run(RunServerAsync);
            try
            {
                var completed = await WaitWithCancellationAsync(Task.WhenAny(_listening.Task, _run), cancellationToken).ConfigureAwait(false);
                if (completed == _run)
                {
                    await _run.ConfigureAwait(false);
                    throw new InvalidOperationException("EmbedIO stopped before the listener was ready.");
                }

                await _listening.Task.ConfigureAwait(false);
            }
            catch
            {
                _stop.Cancel();
                throw;
            }
            finally
            {
                _server.StateChanged -= OnStateChanged;
            }
        }

        private void OnStateChanged(object sender, WebServerStateChangedEventArgs args)
        {
            if (_server.State == WebServerState.Listening) _listening.TrySetResult(true);
        }

        private async Task RunServerAsync()
        {
            try
            {
                await _server.RunAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "EmbedIO server failed.");
                throw;
            }
            finally
            {
                if (!_stop.IsCancellationRequested) _lifetime.StopApplication();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _shutdown ??= ShutdownAsync();
            await WaitWithCancellationAsync(_shutdown, cancellationToken).ConfigureAwait(false);
        }

        private async Task ShutdownAsync()
        {
            _stop.Cancel();
            try
            {
                if (_run != null) await _run.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    foreach (var module in _server.Modules)
                    {
                        if (module is RequestServicesModule requests)
                            await requests.DrainAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _server.Dispose();
                }
            }
        }

        public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            try
            {
                _shutdown ??= ShutdownAsync();
                await _shutdown.ConfigureAwait(false);
            }
            finally
            {
                _stop.Dispose();
            }
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled(token)))
            {
                var completed = await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }

        private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken token)
        {
            await WaitWithCancellationAsync((Task)task, token).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
