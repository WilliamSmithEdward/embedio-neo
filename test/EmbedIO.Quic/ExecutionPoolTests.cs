namespace EmbedIO.Quic
{
    internal static class ExecutionPoolTests
    {
        internal static async Task RunAsync()
        {
            await CoalescingAndCleanupAsync().ConfigureAwait(false);
            await FairnessAsync().ConfigureAwait(false);
            await LimitsAndFailuresAsync().ConfigureAwait(false);
            await ShutdownRaceAsync().ConfigureAwait(false);
            await ShutdownFailuresAsync().ConfigureAwait(false);
            await QueueDelayAsync().ConfigureAwait(false);
            await ConnectionOwnerTests
                .RunAsync()
                .ConfigureAwait(false);
            Mutate([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
            Mutate([2, 255, 127, 64, 32, 16, 8, 4, 2, 1]);
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;

            MutateAsync(data[..Math.Min(data.Length, 32)].ToArray())
                .GetAwaiter()
                .GetResult();
        }

        private static async Task MutateAsync(byte[] commands)
        {
            var workers = 1 + commands[0] % 3;
            await using var pool = new QuicExecutionPool(workers, 3);
            var executing = new int[3];
            var cleaned = new int[3];
            var active = 0;
            var owners = Enumerable
                .Range(0, 3)
                .Select(
                    index => pool.Register(
                        () =>
                        {
                            QuicFuzzChecks.Require(
                                Interlocked.Increment(ref executing[index]) == 1
                                    && Volatile.Read(ref cleaned[index]) == 0,
                                "execution-single-live-owner"
                            );
                            QuicFuzzChecks.Require(
                                Interlocked.Increment(ref active) <= workers,
                                "execution-worker-bound"
                            );
                            Thread.SpinWait(commands[index % commands.Length] & 31);
                            Interlocked.Decrement(ref active);
                            Interlocked.Decrement(ref executing[index]);
                        },
                        () =>
                        {
                            QuicFuzzChecks.Require(
                                Volatile.Read(ref executing[index]) == 0
                                    && Interlocked.Increment(ref cleaned[index]) == 1,
                                "execution-cleanup-ownership"
                            );
                        }
                    )
                )
                .ToArray();

            foreach (var command in commands)
            {
                var owner = owners[command % owners.Length];

                switch ((command / 3) % 4)
                {
                    case 0:
                        owner.Wake();
                        break;

                    case 1:
                        Parallel.For(0, 3, index => owners[index].Wake());
                        break;

                    case 2:
                        owner.RequestClose();
                        QuicFuzzChecks.Require(!owner.Wake(), "execution-closed-wake");
                        break;

                    default:
                        Parallel.Invoke(() => owner.Wake(), () =>
                        {
                            owner.RequestClose();
                        });
                        break;
                }
            }

            await pool
                .DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            await Task
                .WhenAll(owners.Select(owner => owner.Completion))
                .ConfigureAwait(false);
            QuicFuzzChecks.Require(
                pool.Active == 0
                    && active == 0
                    && cleaned.All(count => count == 1)
                    && owners.All(owner => !owner.Wake()),
                "execution-final-ownership"
            );
        }

        private static async Task CoalescingAndCleanupAsync()
        {
            await using var pool = new QuicExecutionPool(2, 2);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var executing = 0;
            var turns = 0;
            var disposed = 0;
            var owner = pool.Register(
                () =>
                {
                    Check(Interlocked.Increment(ref executing) == 1, "one executing turn per owner");
                    Interlocked.Increment(ref turns);
                    entered.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(10)), "blocked native turn released");
                    Interlocked.Decrement(ref executing);
                },
                () =>
                {
                    Check(Volatile.Read(ref executing) == 0, "cleanup waits for active native work");
                    Interlocked.Increment(ref disposed);
                }
            );

            try
            {
                Check(owner.Wake() && entered.Wait(TimeSpan.FromSeconds(10)), "owner scheduled");
                Parallel.For(0, 10000, _ => Check(owner.Wake(), "live wake accepted"));
                var close = owner
                    .DisposeAsync()
                    .AsTask();
                Check(
                    !close.IsCompleted && Volatile.Read(ref disposed) == 0 && !owner.Wake(),
                    "close owns deferred cleanup"
                );
                release.Set();
                await close
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await owner
                    .DisposeAsync()
                    .ConfigureAwait(false);
                Check(turns == 1 && disposed == 1 && pool.Active == 0, "coalesced turns discarded on close");
            }

            finally
            {
                release.Set();
            }
        }

        private static async Task FairnessAsync()
        {
            await using var pool = new QuicExecutionPool(1, 2);
            var order = new List<int>();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            QuicExecutionPool.Registration? busy = null;
            busy = pool.Register(
                () =>
                {
                    order.Add(1);

                    if (order.Count == 1)
                    {
                        entered.Set();
                        Check(release.Wait(TimeSpan.FromSeconds(10)), "fairness turn released");
                        Check(busy?.Wake() == true, "executing owner wakes itself");
                    }

                    else complete.TrySetResult();
                },
                static () => { }
            );
            var sibling = pool.Register(() => order.Add(2), static () => { });

            try
            {
                busy.Wake();
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "first turn entered");
                sibling.Wake();
                release.Set();
                await complete.Task
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await busy
                    .DisposeAsync()
                    .ConfigureAwait(false);
                await sibling
                    .DisposeAsync()
                    .ConfigureAwait(false);
                Check(order is [1, 2, 1], "busy owner yields to sibling");
            }

            finally
            {
                release.Set();
            }
        }

        private static async Task LimitsAndFailuresAsync()
        {
            await using var pool = new QuicExecutionPool(2, 2);
            var failed = pool.Register(
                () => throw new IOException("turn failure"),
                () => throw new InvalidOperationException("cleanup failure")
            );
            var healthy = pool.Register(
                static () => { },
                static () => { }
            );

            try
            {
                pool.Register(
                    static () => { },
                    static () => { }
                );
                throw new IOException("Exceeded registration limit.");
            }

            catch (InvalidOperationException) { }

            failed.Wake();

            try
            {
                await failed.Completion
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                throw new IOException("Execution failures were hidden.");
            }

            catch (AggregateException error)
            {
                Check(error.InnerExceptions.Count == 2, "execution and cleanup failures preserved");
            }

            var replacement = pool.Register(
                static () => { },
                static () => { }
            );
            Check(healthy.Wake() && replacement.Wake(), "failed owner releases admission for healthy work");
            await healthy
                .DisposeAsync()
                .ConfigureAwait(false);
            await replacement
                .DisposeAsync()
                .ConfigureAwait(false);
            Check(pool.Active == 0 && !failed.Wake(), "all owners released");
        }

        private static async Task ShutdownFailuresAsync()
        {
            var pool = new QuicExecutionPool(2, 2);
            var cleaned = 0;
            var failed = pool.Register(
                static () => { },
                () =>
                {
                    Interlocked.Increment(ref cleaned);
                    throw new IOException("shutdown cleanup failure");
                }
            );
            var healthy = pool.Register(
                static () => { },
                () => Interlocked.Increment(ref cleaned)
            );
            var closing = pool
                .DisposeAsync()
                .AsTask();
            await closing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Check(
                closing.IsFaulted && closing.Exception?.Flatten().InnerExceptions is [IOException],
                "shutdown reports cleanup failure"
            );
            Check(
                cleaned == 2
                    && pool.Active == 0
                    && failed.Completion.IsFaulted
                    && healthy.Completion.IsCompletedSuccessfully,
                "shutdown failure still releases sibling ownership"
            );
            var repeated = pool
                .DisposeAsync()
                .AsTask();
            Check(ReferenceEquals(closing, repeated), "repeated shutdown preserves its completion and failure");
        }

        private static async Task ShutdownRaceAsync()
        {
            var pool = new QuicExecutionPool(2, 2);
            using var entered = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            var executing = 0;
            var cleaned = 0;
            var owners = Enumerable
                .Range(0, 2)
                .Select(
                    _ => pool.Register(
                        () =>
                        {
                            Check(Interlocked.Increment(ref executing) <= 2, "worker concurrency bounded");
                            entered.Signal();
                            Check(release.Wait(TimeSpan.FromSeconds(10)), "shutdown work released");
                            Interlocked.Decrement(ref executing);
                        },
                        () => Interlocked.Increment(ref cleaned)
                    )
                )
                .ToArray();

            try
            {
                foreach (var owner in owners) owner.Wake();

                Check(entered.Wait(TimeSpan.FromSeconds(10)), "configured workers execute independently");
                var closing = pool
                    .DisposeAsync()
                    .AsTask();
                Check(!closing.IsCompleted && cleaned == 0, "pool shutdown waits for both active owners");

                try
                {
                    pool.Register(
                        static () => { },
                        static () => { }
                    );
                    throw new IOException("Shutdown admitted an owner.");
                }

                catch (ObjectDisposedException) { }

                release.Set();
                await closing
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await pool
                    .DisposeAsync()
                    .ConfigureAwait(false);
                Check(cleaned == 2 && pool.Active == 0, "bounded workers clean once");
            }

            finally
            {
                release.Set();
                await pool
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        // Only a turn waiting for a worker counts: an idle pool and a running turn report
        // no delay, a queued turn's wait grows while the worker is busy, and draining resets it.
        private static async Task QueueDelayAsync()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var pool = new QuicExecutionPool(1, 4);

            try
            {
                var busy = pool.Register(
                    () =>
                    {
                        entered.Set();
                        Check(release.Wait(TimeSpan.FromSeconds(10)), "delay turn released");
                    },
                    static () => { }
                );
                var waiting = pool.Register(static () => { }, static () => { });
                Check(pool.QueueDelay == TimeSpan.Zero, "idle pool has no queue delay");
                Check(busy.Wake() && entered.Wait(TimeSpan.FromSeconds(10)), "only worker busy");
                Check(pool.QueueDelay == TimeSpan.Zero, "a running turn alone is no wait");
                Check(waiting.Wake(), "second turn queued");
                await Task
                    .Delay(40)
                    .ConfigureAwait(false);
                Check(pool.QueueDelay >= TimeSpan.FromMilliseconds(30), "queued turn's wait measured while the worker is busy");
                release.Set();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                while (pool.QueueDelay != TimeSpan.Zero)
                    await Task
                        .Delay(1, stop.Token)
                        .ConfigureAwait(false);
            }

            finally
            {
                release.Set();
                await pool
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("QUIC execution regression: " + name);
        }
    }
}
