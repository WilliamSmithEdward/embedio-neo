using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;

namespace EmbedIO.Cli
{
    internal sealed class CliHost : IAsyncDisposable
    {
        private readonly PluginLoader _plugins = new();
        private CliFileProvider? _provider;
        private WebServer? _watchServer;
        private ReloadSocket? _socket;
        private string? _root;

        private CliHost(int port)
        {
            Url = $"http://localhost:{port}/";
            Server = new WebServer(options => options.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO));
            Server.WithLocalSessionManager();
        }

        internal WebServer Server { get; }
        internal string Url { get; }

        internal static CliHost Create(Options options, string currentDirectory)
        {
            var host = new CliHost(options.Port);
            try
            {
                host._plugins.Register(host.Server,
                    options.ApiPath == null ? currentDirectory : System.IO.Path.GetFullPath(options.ApiPath, currentDirectory),
                    options.ApiPath != null);
                if (options.RootPath != null || options.ApiPath == null)
                {
                    host._root = options.ResolveRoot(currentDirectory);
                    host._provider = new CliFileProvider(host._root);
                    if (!options.NoWatch)
                        host.Server.WithModule(new ReloadHtmlModule(host._provider, options.Port + 1));
                    host.Server.WithModule(new FileModule("/", host._provider)
                    {
                        DirectoryLister = DirectoryLister.Html,
                        ContentCaching = false,
                    });
                }
                if (!options.NoWatch)
                {
                    host._root ??= options.ResolveRoot(currentDirectory);
                    host._socket = new ReloadSocket();
                    host._watchServer = new WebServer(o => o.WithUrlPrefix($"http://localhost:{options.Port + 1}/").WithMode(HttpListenerMode.EmbedIO));
                    host._watchServer.WithModule(host._socket);
                }
                return host;
            }
            catch
            {
                host.DisposeAsync().GetAwaiter().GetResult();
                throw;
            }
        }

        internal async Task RunAsync(CancellationToken token, Action? onListening = null)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var main = Server.RunAsync(stop.Token);
            var watch = _watchServer?.RunAsync(stop.Token);
            LiveReload? reload = null;
            try
            {
                while (Server.State != WebServerState.Listening || (_watchServer != null && _watchServer.State != WebServerState.Listening))
                {
                    if (main.IsCompleted) { await main; return; }
                    if (watch?.IsCompleted == true) { await watch; return; }
                    await Task.Delay(10, stop.Token);
                }
                if (_socket != null) reload = new LiveReload(_root ?? throw new InvalidOperationException("The live reload root has not been configured."), _socket);
                onListening?.Invoke();
                if (watch == null) await main;
                else await await Task.WhenAny(main, watch);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                await stop.CancelAsync();
                if (reload != null) await reload.DisposeAsync();
                await Task.WhenAll(watch == null ? new[] { main } : new[] { main, watch });
            }
        }

        public ValueTask DisposeAsync()
        {
            _watchServer?.Dispose();
            Server.Dispose();
            _provider?.Dispose();
            _plugins.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
