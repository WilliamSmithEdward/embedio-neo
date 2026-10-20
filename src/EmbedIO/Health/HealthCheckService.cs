using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Health
{
    /// <summary>Opt-in health and readiness for one server. Does not start the server or issue HTTP requests.</summary>
    /// <remarks>Dispose this service when the host ends. Checks run only on explicit probes or readiness waits.
    /// Concurrent probes share an evaluation; canceling one waiter does not cancel other waiters.
    /// Each registration has at most one executing callback, even if it ignores its deadline.</remarks>
    public sealed class HealthCheckService : IDisposable
    {
        private readonly IWebServer _server;
        private readonly Slot[] _slots;
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _listening = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _drainRegistration;
        private Task<HealthReport>? _evaluation;
        private int _disposed;

        /// <summary>Creates health checks for a server, with at most 32 uniquely named readiness checks.</summary>
        /// <param name="server">The server whose lifecycle is monitored.</param>
        /// <param name="checks">Optional dependency checks, copied at construction.</param>
        public HealthCheckService(IWebServer server, IEnumerable<HealthCheckRegistration>? checks = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            var registrations = (checks ?? [])
                .Take(33)
                .ToArray();

            if (registrations.Length > 32) throw new ArgumentOutOfRangeException(nameof(checks));

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var check in registrations)
                if (check == null || !names.Add(check.Name))
                    throw new ArgumentException("Checks must be non-null and have unique names.", nameof(checks));

            _slots = [.. registrations.Select(check => new Slot(check))];
            _server.StateChanged += OnStateChanged;

            if (_server is WebServer web)
                _drainRegistration = web.DrainStarted.Register(() => _stopped.TrySetResult(true));

            if (_server.State == WebServerState.Stopped) _stopped.TrySetResult(true);

            else if (_server.State == WebServerState.Listening) _listening.TrySetResult(true);
        }

        /// <summary>Reports listening state only; never runs dependency checks. Drain alone does not fail liveness.</summary>
        /// <returns>Healthy while listening and undisposed, otherwise Unhealthy.</returns>
        public HealthCheckResult CheckLiveness() =>
            new(
                Volatile.Read(ref _disposed) == 0 && _server.State == WebServerState.Listening
                    ? HealthStatus.Healthy
                    : HealthStatus.Unhealthy
            );

        /// <summary>Evaluates readiness without a result cache. Only Healthy means ready.</summary>
        /// <param name="cancellationToken">Cancels this wait only.</param>
        /// <returns>The dependency results and current lifecycle outcome.</returns>
        public async Task<HealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanBeReady) return Unavailable();

            Task<HealthReport> evaluation;

            lock (_gate)
            {
                if (!CanBeReady) return Unavailable();

                if (_evaluation == null || _evaluation.IsCompleted)
                    _evaluation = EvaluateAsync();

                evaluation = _evaluation;
            }

            var report = await WaitAsync(evaluation, cancellationToken).ConfigureAwait(false);
            return CanBeReady ? report : Unavailable();
        }

        /// <summary>Waits for a healthy evaluation, without making HTTP requests. Does not warm controller routes.</summary>
        /// <param name="timeout">A finite wait from one millisecond through one day.</param>
        /// <param name="cancellationToken">Cancels this wait.</param>
        /// <param name="pollInterval">Probe interval, 100 milliseconds by default; from one millisecond through one minute.</param>
        /// <returns>A task completing on readiness. Throws TimeoutException on deadline, InvalidOperationException on drain, stop or disposal.</returns>
        public async Task WaitForReadyAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            TimeSpan? pollInterval = null
        )
        {
            if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(timeout));

            var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

            if (interval < TimeSpan.FromMilliseconds(1) || interval > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(pollInterval));

            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);

            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();

                    if (_stopped.Task.IsCompleted || _server.State == WebServerState.Stopped)
                        throw new InvalidOperationException(
                            "The server has drained, stopped, or its health service was disposed."
                        );

                    if (_server.State < WebServerState.Listening)
                    {
                        await WaitAsync(Task.WhenAny(_listening.Task, _stopped.Task), linked.Token).ConfigureAwait(
                            false
                        );
                        continue;
                    }

                    if ((await CheckReadinessAsync(linked.Token).ConfigureAwait(false)).Status == HealthStatus.Healthy)
                        return;

                    using var pause = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);

                    try
                    {
                        await WaitAsync(
                            Task.WhenAny(Task.Delay(interval, pause.Token), _stopped.Task),
                            linked.Token
                        ).ConfigureAwait(false);
                    }

                    finally
                    {
                        pause.Cancel();
                    }
                }
            }

            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
            )
            {
                throw new TimeoutException("The application did not become ready within the deadline.");
            }
        }

        /// <summary>Unsubscribes lifecycle notifications, rejects readiness and requests cancellation of active checks.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _stopped.TrySetResult(true);
            _server.StateChanged -= OnStateChanged;
            _drainRegistration.Dispose();
        }

        private bool CanBeReady =>
            Volatile.Read(ref _disposed) == 0
                && !_stopped.Task.IsCompleted
                && _server.State == WebServerState.Listening;

        private static HealthReport Unavailable() =>
            new(HealthStatus.Unhealthy, new Dictionary<string, HealthCheckResult>());

        private void OnStateChanged(object sender, WebServerStateChangedEventArgs args)
        {
            if (args.NewState == WebServerState.Stopped) _stopped.TrySetResult(true);

            else if (args.NewState == WebServerState.Listening) _listening.TrySetResult(true);
        }

        private async Task<HealthReport> EvaluateAsync()
        {
            var results = await Task
                .WhenAll(_slots.Select(slot => slot.RunAsync(_stopped.Task)))
                .ConfigureAwait(false);
            var entries = new Dictionary<string, HealthCheckResult>(StringComparer.Ordinal);
            var status = HealthStatus.Healthy;

            for (var index = 0; index < results.Length; index++)
            {
                entries.Add(_slots[index].Name, results[index]);

                if (results[index].Status < status) status = results[index].Status;
            }

            return new HealthReport(CanBeReady ? status : HealthStatus.Unhealthy, entries);
        }

        private static async Task WaitAsync(Task task, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (
                    await Task
                        .WhenAny(task, canceled.Task)
                        .ConfigureAwait(false)
                    != task
                ) token.ThrowIfCancellationRequested();

                token.ThrowIfCancellationRequested();
                await task.ConfigureAwait(false);
            }
        }

        private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken token)
        {
            await WaitAsync((Task)task, token).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        private sealed class Slot
        {
            private readonly HealthCheckRegistration _registration;
            private Task<HealthCheckResult>? _running;

            internal Slot(HealthCheckRegistration registration) => _registration = registration;

            internal string Name => _registration.Name;

            // Evaluations are serialized by the service; late callbacks remain owned by this slot.
            internal async Task<HealthCheckResult> RunAsync(Task stopped)
            {
                if (_running != null && !_running.IsCompleted)
                    return new HealthCheckResult(HealthStatus.Unhealthy, "A previous check has not finished.");

                var cancellation = new CancellationTokenSource();
                var elapsed = Stopwatch.StartNew();
                var token = cancellation.Token;
                var running = Task.Run(async () =>
                {
                    try
                    {
                        if (stopped.IsCompleted) return new HealthCheckResult(HealthStatus.Unhealthy);

                        return await _registration
                            .Check(token)
                            .ConfigureAwait(false);
                    }

                    catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                    {
                        return new HealthCheckResult(HealthStatus.Unhealthy, "The check failed.");
                    }

                    finally
                    {
                        cancellation.Dispose();
                    }
                });
                _running = running;
                using var wait = new CancellationTokenSource();
                var delay = Task.Delay(_registration.Timeout, wait.Token);
                var completed = await Task
                    .WhenAny(running, delay, stopped)
                    .ConfigureAwait(false);
                wait.Cancel();

                if (completed == running && elapsed.Elapsed < _registration.Timeout && !stopped.IsCompleted)
                    return await running.ConfigureAwait(false);

                // Application cancellation callbacks may block or throw. Never execute them on a server lifecycle
                // thread.
                _ = Task.Run(() =>
                {
                    try
                    {
                        cancellation.Cancel();
                    }

                    catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { }
                });
                return new HealthCheckResult(HealthStatus.Unhealthy, "The check timed out or the host stopped.");
            }
        }
    }
}
