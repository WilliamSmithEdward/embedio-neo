using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.WebSockets;

namespace EmbedIO.Cli
{
    internal sealed class ReloadSocket : WebSocketModule
    {
        internal ReloadSocket() : base("/watcher", true) { }
        internal Task NotifyAsync() => BroadcastAsync("{\"Update\":true}");
        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            => Task.CompletedTask;
    }

    internal sealed class LiveReload : IAsyncDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;

        internal LiveReload(string root, ReloadSocket socket)
        {
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true };
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += Changed;
            _watcher.Error += (_, e) => {
                Console.Error.WriteLine($"File watcher: {e.GetException().Message}");
                _changes.Writer.TryWrite(true);
            };
            try
            {
                _watcher.EnableRaisingEvents = true;
                _worker = RunAsync(socket);
            }
            catch
            {
                _watcher.Dispose();
                _stop.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watcher.Dispose();
            await _stop.CancelAsync();
            await _worker;
            _stop.Dispose();
        }

        private void Changed(object sender, FileSystemEventArgs args) => _changes.Writer.TryWrite(true);

        private async Task RunAsync(ReloadSocket socket)
        {
            try
            {
                while (await _changes.Reader.WaitToReadAsync(_stop.Token))
                {
                    await Task.Delay(100, _stop.Token);
                    while (_changes.Reader.TryRead(out _)) { }
                    try { await socket.NotifyAsync(); }
                    catch (Exception error) { Console.Error.WriteLine($"Live reload: {error.Message}"); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
    }
}
