using EmbedIO.Actions;

namespace EmbedIO.AndroidSmoke;

// Test-only application-owned host. It is not attached to an Activity/Window lifetime.
public sealed class SmokeHost
{
    public static readonly SmokeHost Instance = new();
    public static int Created, Resumed, Stopped, Configured;
    private WebServer? _server;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private int _generation, _observed;
    private string? _error;
    private string _https = "pending";
    private readonly SemaphoreSlim _restart = new(1, 1);
    private readonly List<Task> _workers = new();

    public void Start()
    {
        try
        {
            _stop = new CancellationTokenSource();
            _server = new WebServer(options => options.WithUrlPrefix("http://127.0.0.1:59697/")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/state", HttpVerbs.Get, context => context.SendDataAsync(new
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
                    runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.ToString(),
                })))
                .WithModule(new ActionModule("/work", HttpVerbs.Post, context =>
                {
                    // The worker owns its exception handling and never retains this HTTP context.
                    lock (_workers) _workers.Add(ObserveWorkerAsync(_stop.Token));
                    context.Response.StatusCode = 200;
                    return context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            _running = _server.RunAsync(_stop.Token);
            if (_server.State != WebServerState.Listening)
                throw new InvalidOperationException("The listener did not start.");
            Interlocked.Increment(ref _generation);
            _ = ObserveServerAsync(_running);
            if (_generation == 1) _ = ObserveHttpsAsync();
        }
        catch (Exception error)
        {
            _error = error.ToString();
            Console.Error.WriteLine(_error);
        }
    }

    private async Task ObserveHttpsAsync()
    {
        try
        {
            await PlatformTests.HttpsSmoke.RunAsync();
            _https = "passed";
        }
        catch (Exception error) { _error = error.ToString(); Console.Error.WriteLine(_error); }
    }

    private async Task ObserveServerAsync(Task running)
    {
        try { await running; }
        catch (Exception error) { _error = error.ToString(); Console.Error.WriteLine(_error); }
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
        await _restart.WaitAsync();
        try
        {
            _stop!.Cancel();
            await _running!.WaitAsync(TimeSpan.FromSeconds(10));
            Task[] workers;
            lock (_workers) { workers = _workers.ToArray(); _workers.Clear(); }
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            _server!.Dispose();
            _stop.Dispose();
            Start();
        }
        catch (Exception error) { _error = error.ToString(); Console.Error.WriteLine(_error); }
        finally { _restart.Release(); }
    }
}
