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
        private static readonly System.Resources.ResourceManager HelpResources = new("EmbedIO.Cli.Help", typeof(Program).Assembly);
        private static string Help => HelpResources.GetString("Help", System.Globalization.CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("The CLI help resource is missing.");

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
                await host.RunAsync(stop.Token, () =>
                {
                    Console.WriteLine($"Serving {host.Url} — press Ctrl+C or any key to stop.");
                    if (!options.NoBrowser)
                    {
                        try { Process.Start(new ProcessStartInfo(host.Url) { UseShellExecute = true })?.Dispose(); }
                        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { Console.Error.WriteLine($"Cannot open browser: {error.Message}"); }
                    }
                });
                return 0;
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { Console.Error.WriteLine($"embedio-cli: {error.Message}"); return 1; }
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
