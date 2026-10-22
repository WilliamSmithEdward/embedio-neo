using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

// Real listeners, with application-entry barriers rather than timing sleeps.
internal static class LifecycleFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) =>
        RunCoreAsync(trace, false, token);

    internal static async Task RunCoreAsync(StateMachineTrace trace, bool injectFault, CancellationToken token)
    {
        var hosts = new Host?[3];

        try
        {
            // Mandatory overlap and all shutdown causes survive trace shrinking.
            for (
                var actor = 0;
                actor < 3;
                actor++
            )
                hosts[actor] = await Host
                    .Start(actor, 0, actor % 2 + 1, token, injectFault)
                    .ConfigureAwait(false);

            await Task
                .WhenAll(hosts.Select(host => (host ?? throw new InvalidOperationException()).Enter()))
                .ConfigureAwait(false);
            trace.Cover("lifecycle.three-active-listeners");

            for (var actor = 0; actor < 3; actor++)
            {
                await Retire(actor, actor).ConfigureAwait(false);
                hosts[actor] = await Host
                    .Start(actor, 1, 1 + actor % 2, token)
                    .ConfigureAwait(false);
                await (hosts[actor] ?? throw new InvalidOperationException())
                    .Healthy()
                    .ConfigureAwait(false);
            }

            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = step.Actor;

                if (step.Command % 6 == 0)
                    hosts[actor] ??= await Host
                        .Start(actor, step.Value, 1 + step.Value % 2, token)
                        .ConfigureAwait(false);

                if (hosts[actor] is not Host host)
                {
                    trace.Cover("lifecycle.skip-retired-owner");
                    continue;
                }

                switch (step.Command % 6)
                {
                    case 0:
                        await host
                            .Enter()
                            .ConfigureAwait(false);
                        trace.Cover("lifecycle.enter-held-request");
                        break;

                    case 1:
                        await host
                            .Complete()
                            .ConfigureAwait(false);
                        trace.Cover("lifecycle.complete-request");
                        break;

                    case 2:
                        await Retire(actor, 0).ConfigureAwait(false);
                        break;

                    case 3:
                        await Retire(actor, 1).ConfigureAwait(false);
                        break;

                    case 4:
                        await Retire(actor, 2).ConfigureAwait(false);
                        break;

                    default:
                        await host
                            .Complete()
                            .ConfigureAwait(false);
                        await host
                            .Healthy()
                            .ConfigureAwait(false);
                        trace.Cover("lifecycle.healthy-followup");
                        break;
                }
            }
        }

        finally
        {
            await Task
                .WhenAll(
                    hosts
                        .OfType<Host>()
                        .Select(host => host.Shutdown(0))
                )
                .ConfigureAwait(false);
        }

        async Task Retire(int actor, int cause)
        {
            var host = hosts[actor] ?? throw new InvalidOperationException();
            await host
                .Shutdown(cause)
                .ConfigureAwait(false);
            hosts[actor] = null;
            trace.Cover("lifecycle.shutdown-" + cause);
        }
    }

    // Only used after intentionally stopping the listener and observing handler cleanup.
    // Healthy responses continue through Complete and retain their full assertions.
    internal static async Task ObserveShutdownRequestAsync(
        Task<HttpResponseMessage>? pending,
        CancellationToken deadline
    )
    {
        if (pending == null) return;

        try
        {
            using var response = await pending
                .WaitAsync(deadline)
                .ConfigureAwait(false);
        }

        catch (Exception error) when (
            error is HttpRequestException
                or OperationCanceledException
                or SocketException { SocketErrorCode: SocketError.NotConnected }
            && !deadline.IsCancellationRequested
        )
        { }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebServer _server;
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private readonly Task _running;
        private readonly ConcurrentDictionary<string, Call> _calls = new(StringComparer.Ordinal);
        private readonly string _prefix;
        private readonly int _protocol;
        private Task<HttpResponseMessage>? _pending;
        private Call? _held;
        private int _sequence;
        private bool _disposed;

        private Host(int actor, int generation, int protocol, bool injectFault, CancellationToken token)
        {
            _prefix = actor + ":" + generation + ":";
            _protocol = protocol;
            _token = token;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var url = ConnectionIsolationFuzz.Prefix(false);
            _server = new WebServer(HttpListenerMode.EmbedIO, url);
            _server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    var id = context.Request.Headers["X-Lifecycle"] ?? throw new InvalidOperationException("Missing identity.");
                    var call = _calls[id];
                    context.OnClose(_ =>
                    {
                        Interlocked.Increment(ref call.Closes);
                        call.Closed.TrySetResult();
                    });
                    call.Entered.TrySetResult();

                    try
                    {
                        await call.Release.Task
                            .WaitAsync(context.CancellationToken)
                            .ConfigureAwait(false);
                        await context
                            .SendStringAsync(injectFault ? "corrupted" : _prefix + id, "text/plain", WebServer.Utf8NoBomEncoding)
                            .ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                    {
                        call.Canceled = true;
                    }

                    finally
                    {
                        call.Exited.TrySetResult();
                    }
                }));
            _running = _server.RunAsync(_stop.Token);
            _client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = 1
            })
            {
                BaseAddress = new Uri(url),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        internal static async Task<Host> Start(
            int actor,
            int generation,
            int protocol,
            CancellationToken token,
            bool injectFault = false
        )
        {
            var host = new Host(actor, generation, protocol, injectFault, token);

            try
            {
                await host
                    .Healthy()
                    .ConfigureAwait(false);
                return host;
            }

            catch
            {
                await host
                    .Shutdown(0)
                    .ConfigureAwait(false);
                throw;
            }
        }

        internal async Task Enter()
        {
            if (_pending != null) return;

            _held = new Call();
            var id = (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _calls.TryAdd(id, _held);
            var request = new HttpRequestMessage(HttpMethod.Get, "")
            {
                Version = _protocol == 1 ? HttpVersion.Version11 : HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Add("X-Lifecycle", id);
            _held.Message = request;
            _pending = _client.SendAsync(request, _token);
            await _held.Entered.Task
                .WaitAsync(_token)
                .ConfigureAwait(false);
        }

        internal async Task Complete()
        {
            if (_pending == null) return;

            var call = _held ?? throw new InvalidOperationException();
            call.Release.TrySetResult();
            using var response = await _pending.ConfigureAwait(false);
            var id = call.Message?.Headers
                .GetValues("X-Lifecycle")
                .Single()
                ?? "";
            Require(
                response.StatusCode == HttpStatusCode.OK
                    && await response.Content
                        .ReadAsStringAsync(_token)
                        .ConfigureAwait(false)
                        == _prefix + id,
                "lifecycle.response-isolation",
                id
            );
            await call.Exited.Task
                .WaitAsync(_token)
                .ConfigureAwait(false);
            await call.Closed.Task
                .WaitAsync(_token)
                .ConfigureAwait(false);
            Require(call.Closes == 1 && !call.Canceled, "lifecycle.healthy-close", id);
            _pending = null;
            _held = null;
        }

        internal async Task Healthy()
        {
            await Enter().ConfigureAwait(false);
            await Complete().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => new(Shutdown(0));

        internal async Task Shutdown(int cause)
        {
            if (_disposed) return;

            _disposed = true;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));

            try
            {
                // Dispose/caller cancellation must release a handler waiting on its context token.
                if (cause == 0) _stop.Cancel();

                else if (cause == 1) _server.Dispose();

                else
                {
                    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var contenders = new[]
                    {
                        Task.Run(async () =>
                        {
                            await gate.Task.ConfigureAwait(false);
                            _stop.Cancel();
                        }),
                        Task.Run(async () =>
                        {
                            await gate.Task.ConfigureAwait(false);
                            _server.Dispose();
                        })
                    };
                    gate.TrySetResult();
                    await Task
                        .WhenAll(contenders)
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                }

                if (_held?.Entered.Task.IsCompletedSuccessfully == true)
                {
                    await _held.Exited.Task
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                    await _held.Closed.Task
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                    Require(
                        (_held.Release.Task.IsCompleted || _held.Canceled) && _held.Closes == 1,
                        "lifecycle.shutdown-cancels-context",
                        _prefix
                    );
                }

                // Server-side cancellation and closure were observed above; stop HttpClient
                // retrying an idempotent GET against a listener that has intentionally ended.
                _client.CancelPendingRequests();
                await ObserveShutdownRequestAsync(_pending, cleanup.Token).ConfigureAwait(false);
                await _running
                    .WaitAsync(cleanup.Token)
                    .ConfigureAwait(false);
                Require(_server.State == WebServerState.Stopped, "lifecycle.stopped-state", _prefix);
            }

            finally
            {
                foreach (var call in _calls.Values) call.Release.TrySetResult();

                _stop.Cancel();
                _client.Dispose();
                _server.Dispose();

                try
                {
                    var observing = ObserveShutdownRequestAsync(_pending, cleanup.Token);
                    await Task
                        .WhenAll(_running, observing)
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                }

                finally
                {
                    foreach (var call in _calls.Values) call.Message?.Dispose();

                    _stop.Dispose();
                }
            }
        }
    }

    private sealed class Call
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HttpRequestMessage? Message;
        internal int Closes;
        internal bool Canceled;
    }

    private static void Require(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }
}
