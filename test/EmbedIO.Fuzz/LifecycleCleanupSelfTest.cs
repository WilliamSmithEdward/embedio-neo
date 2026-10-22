using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal static class LifecycleCleanupSelfTest
{
    internal static async Task RunAsync()
    {
        await LifecycleFuzz
            .ObserveShutdownRequestAsync(null, CancellationToken.None)
            .ConfigureAwait(false);
        var content = new ByteArrayContent([1, 2, 3]);
        using var response = new HttpResponseMessage { Content = content };
        await LifecycleFuzz
            .ObserveShutdownRequestAsync(Task.FromResult(response), CancellationToken.None)
            .ConfigureAwait(false);

        try
        {
            await content
                .ReadAsByteArrayAsync()
                .ConfigureAwait(false);
            throw new InvalidOperationException("Shutdown response content was not disposed.");
        }

        catch (ObjectDisposedException) { }

        Exception[] expected =
        [
            new HttpRequestException("Stopped listener."),
            new OperationCanceledException(),
            new SocketException((int)SocketError.NotConnected)
        ];

        foreach (var error in expected)
        {
            await LifecycleFuzz
                .ObserveShutdownRequestAsync(Task.FromException<HttpResponseMessage>(error), CancellationToken.None)
                .ConfigureAwait(false);
            using var expired = new CancellationTokenSource();
            expired.Cancel();
            await Refuses(error, expired.Token).ConfigureAwait(false);
        }

        Exception[] unexpected =
        [
            new SocketException((int)SocketError.ConnectionReset),
            new SocketException((int)SocketError.ConnectionRefused),
            new SocketException((int)SocketError.TimedOut),
            new SocketException((int)SocketError.InvalidArgument),
            new InvalidOperationException("Unexpected cleanup failure."),
            new StateMachineFailure("lifecycle.response-isolation", "controlled negative oracle")
        ];

        foreach (var error in unexpected) await Refuses(error, CancellationToken.None).ConfigureAwait(false);

        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource();
        var observing = LifecycleFuzz.ObserveShutdownRequestAsync(pending.Task, deadline.Token);

        if (observing.IsCompleted) throw new InvalidOperationException("Cleanup failed to observe a pending request.");

        deadline.Cancel();

        try
        {
            await observing.ConfigureAwait(false);
            throw new InvalidOperationException("Cleanup deadline was ignored.");
        }

        catch (OperationCanceledException error) when (error.CancellationToken == deadline.Token) { }

        finally
        {
            pending.TrySetCanceled();
        }
    }

    private static async Task Refuses(Exception failure, CancellationToken deadline)
    {
        try
        {
            await LifecycleFuzz
                .ObserveShutdownRequestAsync(Task.FromException<HttpResponseMessage>(failure), deadline)
                .ConfigureAwait(false);
        }

        catch (Exception actual) when (ReferenceEquals(actual, failure))
        {
            return;
        }

        throw new InvalidOperationException("Unexpected or expired cleanup failure was suppressed.", failure);
    }
}
