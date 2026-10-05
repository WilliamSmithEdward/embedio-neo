using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Cli
{
    internal static class Program
    {
        private const string Help = """
            EmbedIO-Neo CLI — local development web server
            Usage: embedio-cli [options]
              -p, --path PATH    Serve PATH (default: ./wwwroot if present, otherwise ./)
              -o, --port PORT    HTTP port (default: 9696; watch mode also uses PORT+1)
              -a, --api PATH     Load controllers/WebSocket modules from a DLL or directory
                  --no-watch     Disable file watching and live-reload script injection
                  --no-browser   Do not open the browser
              -h, --help         Show this help
                  --version      Show the version
            With --api alone, static serving is disabled. Otherwise, DLLs in the current
            directory are scanned. Load only trusted plugins. Press Ctrl+C to stop.
            """;

        private static async Task<int> Main(string[] args)
        {
            Options options;
            try { options = Options.Parse(args); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
            if (options.Help) { Console.WriteLine(Help); return 0; }
            if (options.Version)
            {
                Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                return 0;
            }

            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            Task? keyboard = null;
            try
            {
                await using var host = CliHost.Create(options, Directory.GetCurrentDirectory());
                if (!Console.IsInputRedirected) keyboard = StopOnKeyAsync(stop);
                await host.RunAsync(stop.Token, () => {
                    Console.WriteLine($"Serving {host.Url} — press Ctrl+C or any key to stop.");
                    if (!options.NoBrowser)
                    {
                        try { Process.Start(new ProcessStartInfo(host.Url) { UseShellExecute = true })?.Dispose(); }
                        catch (Exception error) { Console.Error.WriteLine($"Cannot open browser: {error.Message}"); }
                    }
                });
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine($"embedio-cli: {error.Message}"); return 1; }
            finally
            {
                Console.CancelKeyPress -= cancel;
                await stop.CancelAsync();
                if (keyboard != null) await keyboard;
            }
        }

        private static async Task StopOnKeyAsync(CancellationTokenSource stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (Console.KeyAvailable) { Console.ReadKey(true); await stop.CancelAsync(); return; }
                    await Task.Delay(100, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
