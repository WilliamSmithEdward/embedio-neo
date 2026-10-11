using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;

// Executable regression for the profiler itself, without a listener or network.
internal static class ExceptionAccountingCheck
{
    internal static async Task<int> RunAsync()
    {
        var counts = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
            counts.AddOrUpdate(args.Exception.GetType().FullName ?? "?", 1, static (_, count) => count + 1);
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                using var measurement = new Measurement(counts);
                // Both immediate and outstanding-tick disposal must be exception-free.
                if (iteration % 2 == 0) await Task.Delay(1).ConfigureAwait(false);
                var window = JsonSerializer.SerializeToElement(await measurement.FinishAsync().ConfigureAwait(false));
                if (window.GetProperty("firstChanceExceptions").EnumerateObject().Any())
                    throw new InvalidOperationException("The sampler injected an exception: " + window);
            }

            // Genuine caught errors and caller cancellation remain observable.
            ThrowControl();
            using var control = new Measurement(counts);
            ThrowControl();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { cancellation.Token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { }
            var result = JsonSerializer.SerializeToElement(await control.FinishAsync().ConfigureAwait(false));
            var exceptions = result.GetProperty("firstChanceExceptions");
            if (exceptions.GetProperty(typeof(InvalidOperationException).FullName ?? "?").GetInt64() != 1
                || exceptions.GetProperty(typeof(OperationCanceledException).FullName ?? "?").GetInt64() != 1)
                throw new InvalidOperationException("The census lost a real error or included a pre-window control.");
            Console.WriteLine("Exception accounting: 10 exception-free sampler stops; caught error, exact window and cancellation controls passed.");
            return 0;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }
    }

    private static void ThrowControl()
    {
        try { throw new InvalidOperationException("Deliberate census control."); }
        catch (InvalidOperationException) { }
    }
}
