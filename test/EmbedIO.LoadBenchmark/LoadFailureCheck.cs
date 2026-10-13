using System.Net;

// Socket-free controls exercise the same multiplexed worker used by measurements.
internal static class LoadFailureCheck
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly LoadSettings Settings = new(Protocol.Http2, false, 1,
        new BenchmarkRoute(RouteKind.Bytes, 4, 0), 0, 1, 2, 1, 0, string.Empty);

    internal static async Task<int> RunAsync()
    {
        await FailureAsync(false, false).ConfigureAwait(false);
        await FailureAsync(true, false).ConfigureAwait(false);
        await ExternalCancellationAsync().ConfigureAwait(false);
        using (var state = new RunState(TimeSpan.FromSeconds(30), CancellationToken.None))
        {
            state.Fail(new IOException("primary-control"));
            var primary = state.Error;
            state.Fail(new OperationCanceledException(state.Token));
            Require(primary is not null && state.Error == primary, "Cancellation replaced the primary error.");
        }
        await FailureAsync(false, true).ConfigureAwait(false);
        await HealthyAsync().ConfigureAwait(false);
        Console.WriteLine("Load failure accounting: six socket-free controls passed.");
        return 0;
    }

    private static async Task FailureAsync(bool mismatch, bool validFirst)
    {
        using var abort = new CancellationTokenSource();
        using var state = new RunState(TimeSpan.FromSeconds(30), abort.Token);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var disposed = false;
        var worker = MultiplexedWorker.RunAsync(Settings, state, () => new Handler(async token =>
        {
            var call = Interlocked.Increment(ref calls);
            if (validFirst && call == 1) return Response(false);
            if (call == (validFirst ? 2 : 1))
            {
                await blocked.Task.WaitAsync(token).ConfigureAwait(false);
                if (mismatch) return Response(true);
                throw new HttpRequestException("header-control");
            }
            blocked.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            throw new InvalidOperationException("The blocked request unexpectedly resumed.");
        }, () => disposed = true));
        try
        {
            await worker.WaitAsync(Bound).ConfigureAwait(false);
            Require(canceled.Task.IsCompletedSuccessfully, "The failing stream did not cancel its blocked sibling.");
            Require(state.Error?.Contains(mismatch ? "Response body mismatch." : "header-control", StringComparison.Ordinal) == true,
                "The primary stream failure was lost.");
            var expected = validFirst ? 1 : 0;
            Require(state.Requests == expected && state.ResponseBytes == expected * 4 && state.RequestBodyBytes == 0
                && state.Histograms.Sum(item => item.Count) == expected, "Unvalidated work entered the request or latency totals.");
            Require(disposed, "The worker returned before disposing its handler.");
        }
        finally
        {
            abort.Cancel();
            await worker.WaitAsync(Bound).ConfigureAwait(false);
        }
    }

    private static async Task ExternalCancellationAsync()
    {
        using var abort = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = LoadClient.ExerciseAsync(Settings, TimeSpan.FromSeconds(30), abort.Token,
            () => new Handler(async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("Canceled request resumed.");
            }));
        try
        {
            await entered.Task.WaitAsync(Bound).ConfigureAwait(false);
            abort.Cancel();
            var result = await run.WaitAsync(Bound).ConfigureAwait(false);
            Require(result.Error is not null && result.Requests == 0 && result.Latency.Count == 0, "An active caller abort reported success.");
            var skipped = await LoadClient.ExerciseAsync(Settings, TimeSpan.FromSeconds(30), abort.Token,
                () => throw new InvalidOperationException("Pre-canceled work created a handler.")).ConfigureAwait(false);
            Require(skipped.Error is not null && skipped.Requests == 0, "An abort before worker entry reported success.");
        }
        finally
        {
            abort.Cancel();
            await run.WaitAsync(Bound).ConfigureAwait(false);
        }
    }

    private static async Task HealthyAsync()
    {
        using var abort = new CancellationTokenSource();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = LoadClient.ExerciseAsync(Settings with { RequestRate = 2 }, TimeSpan.FromSeconds(1), abort.Token,
            () => new Handler(async token =>
            {
                if (Interlocked.Increment(ref calls) == 2) both.TrySetResult();
                await both.Task.WaitAsync(token).ConfigureAwait(false);
                return Response(false);
            }));
        try
        {
            var result = await run.WaitAsync(Bound).ConfigureAwait(false);
            Require(result.Error is null && result.Requests == 2 && result.Latency.Count == 2 && result.ResponseBytes == 8,
                "Healthy streams lost validated responses or latency samples.");
        }
        finally
        {
            abort.Cancel();
            await run.WaitAsync(Bound).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage Response(bool mismatch)
    {
        var bytes = Payloads.Get(4).ToArray();
        if (mismatch) bytes[1] ^= 1;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new StreamContent(new BytewiseStream(bytes)),
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send, Action? disposed = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed?.Invoke();
        }
    }

    private sealed class BytewiseStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
