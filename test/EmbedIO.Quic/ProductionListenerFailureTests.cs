using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class ProductionListenerFailureTests
    {
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
                () => StopJoinsAsync(source),
                () => BindingFailuresAsync(source, false),
                () => BindingFailuresAsync(source, true),
                () => DrainRestartAsync(source),
                () => CallbackFailureAsync(source, false),
                () => CallbackFailureAsync(source, true),
                () => CriticalCallbackAsync(source)
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
                throw new AggregateException("HTTP/3 failure ownership regressions failed.", failures);
        }

        private static Http3Listener Listener(
            ServerCertificateSource source,
            Func<IPEndPoint, Http3ListenerBinding> create
        )
        {
            var listener = new Http3Listener(source, false, null, (endpoint, _, _, _) => create(endpoint));
            listener.AddPrefix("https://127.0.0.1:59213/");
            return listener;
        }

        private static async Task StopJoinsAsync(ServerCertificateSource source)
        {
            var failure = new IOException("stop acceptance");
            var peer = new Peer(false);
            var binding = new Binding(peer, failure, null, false);
            using var listener = Listener(source, _ => binding);
            listener.Start();
            await peer.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            var stopping = Task.Run(listener.Stop);
            bool endedBeforeRelease;

            try
            {
                await binding.StopAttempt.Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                await Task
                    .WhenAny(stopping, Task.Delay(250))
                    .ConfigureAwait(false);
                endedBeforeRelease = stopping.IsCompleted;
            }

            finally
            {
                peer.Release.TrySetResult();
                await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await peer.Ended.Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }

            Check(
                !endedBeforeRelease && peer.Finished && peer.Disposed && Has(stopping, failure),
                "failed stop joins accepted connection before reporting failure"
            );
        }

        private static async Task BindingFailuresAsync(ServerCertificateSource source, bool asynchronous)
        {
            var firstStop = new IOException("first stop");
            var firstDispose = new IOException("first dispose");
            var secondStop = new IOException("second stop");
            var secondDispose = new IOException("second dispose");
            var first = new Binding(null, firstStop, firstDispose, asynchronous);
            var second = new Binding(null, secondStop, secondDispose, asynchronous);
            using var listener = Listener(
                source,
                endpoint => endpoint.Address.Equals(IPAddress.Loopback) ? first : second
            );
            listener.AddPrefix("https://127.0.0.2:59213/");
            listener.Start();
            Task pending = listener.GetContextAsync(CancellationToken.None);
            var stopping = Task.Run(listener.Stop);
            await stopping
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var completed = await Task
                .WhenAny(pending, Task.Delay(250))
                .ConfigureAwait(false)
                == pending;

            if (completed) await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            Check(
                first.StopAttempt.Task.IsCompleted
                    && second.StopAttempt.Task.IsCompleted
                    && first.DisposeAttempt.Task.IsCompleted
                    && second.DisposeAttempt.Task.IsCompleted,
                "all sibling bindings receive stop and disposal despite errors"
            );
            Check(completed && pending.IsFaulted, "queued context reader ends after failed teardown");
            Check(
                Has(stopping, firstStop)
                    && Has(stopping, firstDispose)
                    && Has(stopping, secondStop)
                    && Has(stopping, secondDispose),
                "all teardown failures remain observable"
            );
        }

        private static async Task DrainRestartAsync(ServerCertificateSource source)
        {
            var calls = 0;
            var failure = new IOException("drain stop");
            using var listener = Listener(source, _ => new Binding(null, calls++ == 0 ? failure : null, null, false));
            listener.Start();
            var draining = listener.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            await draining
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Check(Has(draining, failure) && !listener.IsListening, "failed drain reports error and ends acceptance");
            listener.Start();
            Check(calls == 2 && listener.IsListening, "failed drain clears the previous session before restart");
            listener.Stop();
        }

        private static async Task CallbackFailureAsync(ServerCertificateSource source, bool drain)
        {
            var peer = new Peer(true);
            var binding = new Binding(peer, null, null, false);
            using var listener = Listener(source, _ => binding);
            listener.Start();
            await peer.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var stopping = drain
                ? listener.DrainAsync(TimeSpan.FromSeconds(2), cancellation.Token)
                : Task.Run(listener.Stop);
            await stopping
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Check(
                peer.Finished
                    && peer.Disposed
                    && binding.StopAttempt.Task.IsCompleted
                    && binding.DisposeAttempt.Task.IsCompleted
                    && Has(stopping, peer.CallbackFailure),
                "throwing cancellation callback cannot skip session cleanup"
            );
        }

        private static async Task CriticalCallbackAsync(ServerCertificateSource source)
        {
            var failure = new OutOfMemoryException("fatal callback fixture");
            var peer = new Peer(true, failure);
            var binding = new Binding(peer, null, null, false);
            using var listener = Listener(source, _ => binding);
            listener.Start();
            await peer.Entered.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            var stopping = Task.Run(listener.Stop);
            await stopping
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Check(Has(stopping, failure), "critical callback failure propagates unchanged");
            await peer.Ended.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || (data[0] & 63) != 0) return;

            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1)
            );
            BindingFailuresAsync(new ServerCertificateSource(certificate), (data[1] & 1) != 0)
                .GetAwaiter()
                .GetResult();
        }

        private static bool Has(Task task, Exception expected) =>
            task.Exception
                ?.Flatten()
                .InnerExceptions.Any(error => ReferenceEquals(error, expected))
                == true;

        private static void Check(bool condition, string message) => QuicFuzzChecks.Require(condition, message);

        private sealed class Binding(
            Peer? peer,
            Exception? stopError,
            Exception? disposeError,
            bool asynchronous
        ) : Http3ListenerBinding
        {
            private int _accepted;

            internal TaskCompletionSource StopAttempt { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            internal TaskCompletionSource DisposeAttempt { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            internal override ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token) =>
                peer != null && Interlocked.Increment(ref _accepted) == 1
                    ? ValueTask.FromResult<Http3AcceptedConnection>(peer)
                    : WaitAsync(token);

            private static async ValueTask<Http3AcceptedConnection> WaitAsync(CancellationToken token)
            {
                await Task
                    .Delay(Timeout.Infinite, token)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Canceled accept unexpectedly resumed.");
            }

            internal override ValueTask StopAcceptingAsync()
            {
                StopAttempt.TrySetResult();
                return Complete(stopError);
            }

            public override ValueTask DisposeAsync()
            {
                DisposeAttempt.TrySetResult();
                return Complete(disposeError);
            }

            private ValueTask Complete(Exception? failure)
            {
                if (asynchronous) return new ValueTask(CompleteAsync(failure));

                if (failure != null) throw failure;

                return ValueTask.CompletedTask;
            }

            private static async Task CompleteAsync(Exception? failure)
            {
                await Task.Yield();

                if (failure != null) throw failure;
            }
        }

        private sealed class Peer(bool callbackThrows, Exception? callbackFailure = null) : Http3AcceptedConnection
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Exception CallbackFailure { get; } = callbackFailure ?? new IOException("cancellation callback");

            internal bool Finished;
            internal bool Disposed;

            internal override bool HandshakePending => false;

            internal override ValueTask WaitReadyAsync(CancellationToken token) => ValueTask.CompletedTask;

            internal override async Task RunAsync(
                Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
                CancellationToken abort,
                CancellationToken drain
            )
            {
                using var registration = abort.Register(() =>
                {
                    if (callbackThrows) throw CallbackFailure;
                });
                Entered.TrySetResult();

                if (callbackThrows)
                {
                    try
                    {
                        await Task
                            .Delay(Timeout.Infinite, abort)
                            .ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
                }

                else await Release.Task.ConfigureAwait(false);

                Finished = true;
            }

            internal override ValueTask RejectAsync(CancellationToken token) => ValueTask.CompletedTask;

            public override ValueTask DisposeAsync()
            {
                Disposed = true;
                Ended.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
}
