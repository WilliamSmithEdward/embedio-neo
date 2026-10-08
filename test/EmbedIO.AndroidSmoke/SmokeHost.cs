using EmbedIO.Actions;

namespace EmbedIO.AndroidSmoke;

// Test-only application-owned host. It is not attached to an Activity/Window lifetime.
public sealed class SmokeHost : IAsyncDisposable
{
    public static readonly SmokeHost Instance = new();
    public static int Created, Resumed, Stopped, Configured;
    private WebServer? _server, _frontend;
    private CancellationTokenSource? _stop;
    private Task? _running, _frontRunning;
    private int _requests;
    private bool _stoppingBackend;
    private int _generation, _observed;
    private string? _error;
    private string _https = "pending";
    private readonly EmbedIO.Internal.AsyncWriteGate _restart = new();
    private int _disposed;
    private readonly List<Task> _workers = new();

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            _stop = new CancellationTokenSource();
            _server = new WebServer(options => options.WithUrlPrefix("http://127.0.0.1:59697/")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/state", HttpVerbs.Get, context => context.SendDataAsync(GetState())))
                .WithModule(new ActionModule("/api", HttpVerbs.Get, context =>
                    context.SendDataAsync(new { request = Interlocked.Increment(ref _requests) })))
                .WithModule(new ActionModule("/work", HttpVerbs.Post, context =>
                {
                    // The worker owns its exception handling and never retains this HTTP context.
                    lock (_workers) _workers.Add(ObserveWorkerAsync(_stop.Token));
                    context.Response.StatusCode = 200;
                    return context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var directory = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "embedio-smoke");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "index.html"), "frontend");
            _frontend = new WebServer(options => options.WithUrlPrefix("http://127.0.0.1:59698/")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/host-state", HttpVerbs.Get, context => context.SendDataAsync(GetState())))
                .WithStaticFolder("/", directory, false);
            _frontRunning = _frontend.RunAsync(_stop.Token);
            _ = ObserveServerAsync(_frontRunning);
            _running = _server.RunAsync(_stop.Token);
            if (_server.State != WebServerState.Listening)
                throw new InvalidOperationException("The listener did not start.");
            Interlocked.Increment(ref _generation);
            _ = ObserveServerAsync(_running);
            if (_generation == 1) _ = ObserveHttpsAsync();
        }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
        {
            _error = error.ToString();
            Console.Error.WriteLine(_error);
        }
    }

    private object GetState() => new
    {
        pid = Environment.ProcessId,
        generation = _generation,
        created = Created,
        resumed = Resumed,
        stopped = Stopped,
        configured = Configured,
        observed = _observed,
        error = _error,
        https = _https,
        listener = _server?.Listener.Name,
        backend_state = _server?.State.ToString(),
        frontend_state = _frontend?.State.ToString(),
        backend_completed = _running?.IsCompleted,
        requests = _requests,
        runtime = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(),
    };

    private async Task ObserveHttpsAsync()
    {
        try
        {
            await PlatformTests.HttpsSmoke.RunAsync();
            _https = "passed";
        }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { _error = error.ToString(); Console.Error.WriteLine(_error); }
    }

    private async Task ObserveServerAsync(Task running)
    {
        try { await running; }
        catch (System.Net.HttpListenerException) when (_stoppingBackend) { }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { _error = error.ToString(); Console.Error.WriteLine(_error); }
    }

    private async Task ObserveWorkerAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            throw new InvalidOperationException("Expected supervised background failure.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException) { Interlocked.Increment(ref _observed); }
    }

    public async Task RestartObservedAsync()
    {
        using var scope = await _restart.EnterAsync(CancellationToken.None);
        try
        {
            var stop = _stop ?? throw new InvalidOperationException("The host has not started.");
            stop.Cancel();
            await Task.WhenAll(_running ?? Task.CompletedTask, _frontRunning ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            Task[] workers;
            lock (_workers) { workers = _workers.ToArray(); _workers.Clear(); }
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            _server?.Dispose();
            _frontend?.Dispose();
            stop.Dispose();
            Start();
        }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { _error = error.ToString(); Console.Error.WriteLine(_error); }
    }
    public async Task StopBackendObservedAsync()
    {
        _stoppingBackend = true;
        try
        {
            (_server ?? throw new InvalidOperationException("The host has not started.")).Dispose();
            try { await (_running ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (System.Net.HttpListenerException error) when (error.NativeErrorCode == 995) { }
        }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { _error = error.ToString(); Console.Error.WriteLine(_error); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            using var scope = await _restart.EnterAsync(CancellationToken.None);
            _stop?.Cancel();
            try { await Task.WhenAll(_running ?? Task.CompletedTask, _frontRunning ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                _server?.Dispose();
                _frontend?.Dispose();
                _stop?.Dispose();
            }
        }
        finally { _restart.Dispose(); }
    }
}
