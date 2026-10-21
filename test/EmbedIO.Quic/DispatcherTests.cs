using System.Net;

namespace EmbedIO.Quic
{
    internal static class DispatcherTests
    {
        private sealed class ControlledTls(Action pump, Action dispose) : TlsSession
        {
            internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes) { }

            internal override void Pump() => pump();

            public override void Dispose()
            {
                dispose();
                ReleaseKeys();
            }
        }

        private static byte[] Packet(byte owner, uint number = 0)
        {
            byte[] original = [owner, 2, 3, 4, 5, 6, 7, 8];
            using var keys = PacketKeys.Initial(original, false);
            return NativeFuzz.Initial(keys, original, [9, 10, 11, 12, 13, 14, 15, 16], number, [1]);
        }

        private static QuicConnection Connection(ReadOnlySpan<byte> packet, double now, TlsSession tls) =>
            new(packet, _ => tls, static (port, _) => new StreamEchoApplication(port), now);

        internal static async Task RunAsync()
        {
            await SiblingProgressAsync().ConfigureAwait(false);
            await AdmissionCleanupAsync().ConfigureAwait(false);
            await QueuedExpiryAsync().ConfigureAwait(false);
            await OutputOwnershipAsync().ConfigureAwait(false);
            await InvalidInitialAsync().ConfigureAwait(false);
            await CleanupFailureAsync().ConfigureAwait(false);
            await CallbackTimeoutAsync().ConfigureAwait(false);
            await ScheduledApplicationAsync([0, 42]).ConfigureAwait(false);
            await ScheduledApplicationAsync([1]).ConfigureAwait(false);
            await RoutingAndSendFailureAsync().ConfigureAwait(false);
            await EndpointOwnershipAsync().ConfigureAwait(false);
            await AdmissionWindowAsync(0).ConfigureAwait(false);
            await AdmissionWindowAsync(1).ConfigureAwait(false);
            await ProductionListenerLifecycleTests
                .RunAsync()
                .ConfigureAwait(false);

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                await ProductionListenerFailureTests
                    .RunAsync()
                    .ConfigureAwait(false);

            Mutate([0, 1, 2, 3, 4, 5, 6, 7]);
            Mutate([255, 17, 2, 1]);
        }

        private static async Task SiblingProgressAsync()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var disposed = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) =>
                {
                    var blocked = packet[6] == 1;
                    return Connection(
                        packet,
                        now,
                        new ControlledTls(
                            () =>
                            {
                                if (!blocked) return;

                                entered.Set();
                                Check(release.Wait(TimeSpan.FromSeconds(10)), "blocked TLS released");
                            },
                            () => Interlocked.Increment(ref disposed)
                        )
                    );
                },
                static () => 0,
                capacity: 2,
                workers: 2
            );

            try
            {
                Check(dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001)), "first owner admitted");
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "first native call blocked");
                Check(
                    dispatcher.Receive(Packet(2), new IPEndPoint(IPAddress.Loopback, 17002)),
                    "sibling admitted while blocked"
                );
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Check(
                    await dispatcher
                        .WaitForOutputAsync(stop.Token)
                        .ConfigureAwait(false),
                    "sibling output becomes ready"
                );
                var sent = 0;
                Check(
                    await dispatcher
                        .SendOneAsync(output =>
                        {
                            Check(output.Peer.Port == 17002, "sibling output routes to its peer");
                            sent++;
                            return ValueTask.CompletedTask;
                        })
                        .ConfigureAwait(false),
                    "sibling sends while native call remains blocked"
                );
                Check(sent == 1 && dispatcher.Active == 2 && disposed == 0, "blocked owner stays independently live");
            }

            finally
            {
                release.Set();
                await dispatcher
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            Check(
                disposed == 2 && dispatcher.Active == 0 && dispatcher.PendingOutputBytes == 0,
                "sibling cleanup complete"
            );
        }

        private static async Task AdmissionCleanupAsync()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var now = 0.0;
            var disposed = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, time, _, _) => Connection(
                    packet,
                    time,
                    new ControlledTls(
                        () =>
                        {
                            entered.Set();
                            Check(release.Wait(TimeSpan.FromSeconds(10)), "admission TLS released");
                        },
                        () => Interlocked.Increment(ref disposed)
                    )
                ),
                () => Volatile.Read(ref now),
                capacity: 1,
                workers: 1
            );

            try
            {
                dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001));
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "admission owner executing");
                Interlocked.Exchange(ref now, 4000);
                dispatcher.Tick();
                Check(
                    dispatcher.Active == 1 && dispatcher.ExpiredHandshakes == 1 && disposed == 0,
                    "expiry retains native admission"
                );
                Check(
                    !dispatcher.Receive(Packet(2), new IPEndPoint(IPAddress.Loopback, 17002)),
                    "closing owner cannot be replaced before cleanup"
                );
                release.Set();
                await UntilAsync(() =>
                {
                    dispatcher.Tick();
                    return dispatcher.Active == 0;
                }).ConfigureAwait(false);
                Check(disposed == 1, "expired owner cleaned before slot release");
                Check(
                    dispatcher.Receive(Packet(2), new IPEndPoint(IPAddress.Loopback, 17002)),
                    "released slot accepts replacement"
                );
            }

            finally
            {
                release.Set();
                await dispatcher
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            Check(dispatcher.Active == 0 && dispatcher.PendingOutputPackets == 0, "replacement shutdown bounded");
        }

        private static async Task QueuedExpiryAsync()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var now = 0.0;
            var expiredReads = 0;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factories = 0;
            var disposed = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, time, _, _) =>
                {
                    Interlocked.Increment(ref factories);
                    return Connection(
                        packet,
                        time,
                        new ControlledTls(
                            () =>
                            {
                                entered.Set();
                                Check(release.Wait(TimeSpan.FromSeconds(10)), "queued deadline TLS released");
                            },
                            () => Interlocked.Increment(ref disposed)
                        )
                    );
                },
                () =>
                {
                    var time = Volatile.Read(ref now);

                    if (time >= 4000 && Interlocked.Increment(ref expiredReads) == 2) reached.TrySetResult();

                    return time;
                },
                capacity: 2,
                workers: 1
            );

            try
            {
                dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001));
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "worker occupied before queued handshake");
                dispatcher.Receive(Packet(2), new IPEndPoint(IPAddress.Loopback, 17002));
                Interlocked.Exchange(ref now, 4000);
                release.Set();
                await reached.Task
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await dispatcher
                    .DisposeAsync()
                    .ConfigureAwait(false);
                Check(
                    factories == 1 && disposed == 1 && dispatcher.ExpiredHandshakes == 2,
                    "queued deadline prevents second native factory without a timer tick"
                );
                Check(
                    dispatcher.PendingOutputBytes == 0 && dispatcher.Active == 0,
                    "expired queued input/output released"
                );
            }

            finally
            {
                release.Set();
                await dispatcher
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static async Task OutputOwnershipAsync()
        {
            var disposed = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, time, _, _) => Connection(
                    packet,
                    time,
                    new ControlledTls(
                        static () => { },
                        () => Interlocked.Increment(ref disposed)
                    )
                ),
                static () => 0,
                capacity: 1,
                workers: 1,
                outputPacketCapacity: 1,
                outputByteCapacity: 1200
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var peer = new IPEndPoint(IPAddress.Loopback, 17001);
            dispatcher.Receive(Packet(1), peer);
            Check(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "first output queued"
            );
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var send = dispatcher
                .SendOneAsync(async output =>
                {
                    Check(output.Bytes.Length == 1200, "Initial output is padded");
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                })
                .AsTask();

            try
            {
                await entered.Task
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
                dispatcher.Receive(Packet(1, 1), peer);
                await UntilAsync(() => dispatcher.DroppedOutput != 0).ConfigureAwait(false);
                Check(
                    dispatcher.PendingOutputPackets == 1 && dispatcher.PendingOutputBytes == 1200,
                    "in-flight send counts against output limits"
                );
                var shutdown = dispatcher
                    .DisposeAsync()
                    .AsTask();
                Check(
                    !shutdown.IsCompleted && dispatcher.PendingOutputBytes == 1200,
                    "shutdown waits for active output borrow"
                );
                release.TrySetResult();
                Check(
                    await send
                        .WaitAsync(stop.Token)
                        .ConfigureAwait(false),
                    "owned send completed"
                );
                await shutdown
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
                Check(
                    disposed == 1 && dispatcher.PendingOutputPackets == 0 && dispatcher.PendingOutputBytes == 0,
                    "output and native ownership released after shutdown"
                );
            }

            finally
            {
                release.TrySetResult();
                await send.ConfigureAwait(false);
                await dispatcher
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static async Task InvalidInitialAsync()
        {
            var nativeFactories = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) => new QuicConnection(
                    packet,
                    _ =>
                    {
                        Interlocked.Increment(ref nativeFactories);
                        throw new InvalidOperationException("Unauthenticated Initial reached TLS.");
                    },
                    static (port, _) => new StreamEchoApplication(port),
                    now
                ),
                static () => 0
            );
            var packet = Packet(1);
            packet[^1] ^= 1;
            dispatcher.Receive(packet, new IPEndPoint(IPAddress.Loopback, 17001));
            await UntilAsync(() =>
            {
                dispatcher.Tick();
                return dispatcher.Active == 0;
            }).ConfigureAwait(false);
            Check(
                nativeFactories == 0
                    && dispatcher.Rejected > 0
                    && dispatcher.FailedOwners == 0
                    && dispatcher.LastRejectedFailure?.GetBaseException() is System.Security.Cryptography.CryptographicException,
                "invalid authentication retires without TLS or listener failure"
            );
        }

        private static async Task CleanupFailureAsync()
        {
            var cleaned = 0;
            var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) =>
                {
                    var fail = packet[6] == 1;
                    return Connection(
                        packet,
                        now,
                        new ControlledTls(
                            static () => { },
                            () =>
                            {
                                Interlocked.Increment(ref cleaned);

                                if (fail)
                                    throw new System.Security.Cryptography.CryptographicException(
                                        "native cleanup failure"
                                    );
                            }
                        )
                    );
                },
                static () => 0,
                capacity: 2,
                workers: 2
            );

            try
            {
                dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001));
                dispatcher.Receive(Packet(2), new IPEndPoint(IPAddress.Loopback, 17002));
                await UntilAsync(() => dispatcher.PendingOutputPackets == 2).ConfigureAwait(false);
                var closing = dispatcher
                    .DisposeAsync()
                    .AsTask();
                await closing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Check(
                    closing.IsFaulted && closing.Exception
                        ?.Flatten()
                        .InnerExceptions.Any(error => error is System.Security.Cryptography.CryptographicException)
                        == true,
                    "native cleanup failure remains observable"
                );
                Check(
                    cleaned == 2
                        && dispatcher.Active == 0
                        && dispatcher.PendingOutputBytes == 0
                        && dispatcher.FailedOwners == 1,
                    "cleanup failure does not abandon sibling resources"
                );
            }

            finally
            {
                await dispatcher
                    .DisposeAsync()
                    .AsTask()
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        private static async Task CallbackTimeoutAsync()
        {
            var disposed = 0;
            var failure = new TimeoutException("callback timeout");
            var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) => Connection(
                    packet,
                    now,
                    new ControlledTls(() => throw failure, () => Interlocked.Increment(ref disposed))
                ),
                static () => 0
            );

            try
            {
                dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001));
                await UntilAsync(() =>
                {
                    dispatcher.Tick();
                    return dispatcher.Active == 0;
                }).ConfigureAwait(false);
                var closing = dispatcher
                    .DisposeAsync()
                    .AsTask();
                await closing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Check(
                    closing.IsFaulted && closing.Exception
                        ?.Flatten()
                        .InnerExceptions.Contains(failure)
                        == true,
                    "callback timeout remains observable"
                );
                Check(
                    disposed == 1
                        && dispatcher.ExpiredHandshakes == 0
                        && dispatcher.FailedOwners == 1
                        && dispatcher.Active == 0
                        && dispatcher.PendingOutputBytes == 0,
                    "callback timeout is not handshake expiry and releases resources"
                );
            }

            finally
            {
                await dispatcher
                    .DisposeAsync()
                    .AsTask()
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        private static async Task ScheduledApplicationAsync(byte[] data)
        {
            var ready = new TaskCompletionSource<AsyncStreamApplication>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var tls = new ConnectionCoreTests.ModelTls(true);
            Action? lateNotify = null;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, owner, _) =>
                {
                    lateNotify = owner.Notify;
                    return new QuicConnection(
                        packet,
                        _ => tls,
                        (operations, _) =>
                        {
                            var application = new AsyncStreamApplication(operations, owner.Notify);
                            ready.TrySetResult(application);
                            return application;
                        },
                        now
                    );
                },
                static () => 0
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001));
            var application = await ready.Task
                .WaitAsync(stop.Token)
                .ConfigureAwait(false);
            var unidirectional = data.Length == 0 || (data[0] & 1) == 0;
            var stream = await application
                .OpenOutboundStreamAsync(unidirectional, stop.Token)
                .ConfigureAwait(false);
            Check(stream.Id == (unidirectional ? 3UL : 1UL), "scheduled application opens without packet or timer");
            await stream
                .WriteAsync(data.AsMemory(), stop.Token)
                .ConfigureAwait(false);
            stream.CompleteWrites();
            Check(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "application output wakes socket owner"
            );
            var sent = 0;

            while (
                await dispatcher
                    .SendOneAsync(output =>
                    {
                        Check(output.Peer.Port == 17001, "async application output keeps its peer");
                        sent++;
                        return ValueTask.CompletedTask;
                    })
                    .ConfigureAwait(false)
            ) { }

            Check(sent > 0, "scheduled output sent without a timer");
            await application
                .CloseAsync(0x1234, stop.Token)
                .ConfigureAwait(false);
            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            await stream
                .DisposeAsync()
                .ConfigureAwait(false);

            for (var index = 0; index < 32; index++) lateNotify?.Invoke();

            Check(
                application.IsClosed
                    && tls.DisposeCalls == 1
                    && dispatcher.Active == 0
                    && dispatcher.PendingOutputBytes == 0
                    && dispatcher.FailedOwners == 0,
                "late application notifications cannot resurrect a retired owner"
            );
        }

        private static async Task RoutingAndSendFailureAsync()
        {
            var packets = 0;
            var disposed = 0;
            QuicConnection? connection = null;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) =>
                {
                    connection = Connection(
                        packet,
                        now,
                        new ControlledTls(
                            () => Interlocked.Increment(ref packets),
                            () => Interlocked.Increment(ref disposed)
                        )
                    );
                    return connection;
                },
                static () => 0,
                capacity: 1,
                workers: 1
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var peer = new IPEndPoint(IPAddress.Loopback, 17001);
            dispatcher.Receive(Packet(1), peer);
            peer.Port = 17002;
            Check(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "routing output ready"
            );

            try
            {
                await dispatcher
                    .SendOneAsync(output =>
                    {
                        Check(output.Peer.Port == 17001, "admitted endpoint is copied");
                        throw new IOException("send failure");
                    })
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Send failure was hidden.");
            }

            catch (IOException) { }

            Check(
                dispatcher.PendingOutputPackets == 0 && dispatcher.PendingOutputBytes == 0,
                "failed send releases in-flight ownership"
            );
            var owned = connection ?? throw new InvalidOperationException("Missing routed connection.");
            byte[] forged = [0x40, .. owned.ServerId, 1];
            Check(!dispatcher.Receive(forged, peer), "issued connection ID cannot move to another peer");
            Check(packets == 1 && dispatcher.Active == 1, "foreign peer cannot execute native work");
            Check(
                dispatcher.Receive(Packet(1, 1), new IPEndPoint(IPAddress.Loopback, 17001)),
                "original alias still routes after send failure"
            );
            Check(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "healthy output follows send failure"
            );
            Check(
                await dispatcher
                    .SendOneAsync(output =>
                    {
                        Check(output.Peer.Port == 17001, "healthy output retains original peer");
                        return ValueTask.CompletedTask;
                    })
                    .ConfigureAwait(false),
                "following send succeeds"
            );
            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            Check(
                disposed == 1 && dispatcher.Active == 0 && dispatcher.Accepted == dispatcher.Retired,
                "routing aliases retire after cleanup"
            );
        }

        private static async Task EndpointOwnershipAsync()
        {
            var ready = new TaskCompletionSource<QuicConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, endpoints) =>
                {
                    Check(
                        endpoints.Peer.Address.ScopeId == 3 && endpoints.Local?.Address.ScopeId == 4,
                        "factory receives original scopes"
                    );
                    endpoints.Peer.Address.ScopeId = 9;

                    var factoryLocal = endpoints.Local
                        ?? throw new InvalidOperationException("Missing local endpoint.");
                    factoryLocal.Address.ScopeId = 10;

                    endpoints.Peer.Port = 1;
                    var connection = Connection(
                        packet,
                        now,
                        new ControlledTls(
                            static () => { },
                            static () => { }
                        )
                    );
                    ready.TrySetResult(connection);
                    return connection;
                },
                static () => 0,
                capacity: 1,
                workers: 1
            );
            var peer = new IPEndPoint(IPAddress.Parse("fe80::1%3"), 17001);
            var local = new IPEndPoint(IPAddress.Parse("fe80::2%4"), 18001);
            Check(dispatcher.Receive(Packet(1), peer, local), "scoped endpoints admitted");
            peer.Address.ScopeId = 11;
            local.Address.ScopeId = 12;
            peer.Port = 17002;
            local.Port = 18002;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connection = await ready.Task
                .WaitAsync(stop.Token)
                .ConfigureAwait(false);
            Check(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "scoped output ready"
            );
            await dispatcher
                .SendOneAsync(output =>
                {
                    Check(output.Peer.Address.ScopeId == 3 && output.Peer.Port == 17001, "factory and caller mutations do not alter output route");
                    return ValueTask.CompletedTask;
                })
                .ConfigureAwait(false);
            var originalPeer = new IPEndPoint(IPAddress.Parse("fe80::1%3"), 17001);
            var originalLocal = new IPEndPoint(IPAddress.Parse("fe80::2%4"), 18001);
            byte[] forged = [0x40, .. connection.ServerId, 1];
            Check(!dispatcher.Receive(forged, originalPeer, local), "issued ID cannot move local destination");
            dispatcher.Accepting = false;
            Check(!dispatcher.Receive(Packet(2), originalPeer, originalLocal), "stopped admission rejects new owner");
            Check(
                dispatcher.Receive(Packet(1, 1), originalPeer, originalLocal),
                "stopped admission preserves existing aliases and endpoint copies"
            );
            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            Check(dispatcher.Active == 0 && dispatcher.FailedOwners == 0, "scoped routes release ownership");
        }

        private static async Task AdmissionWindowAsync(int limit)
        {
            var now = 0.0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, time, _, _) => Connection(
                    packet,
                    time,
                    new ControlledTls(
                        static () => { },
                        static () => { }
                    )
                ),
                () => now,
                capacity: 1,
                workers: 1,
                admissionAttemptsPerSecond: limit
            );
            var invalid = Packet(1);
            invalid[^1] ^= 1;
            var peer = new IPEndPoint(IPAddress.Loopback, 17001);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                Check(
                    dispatcher.Receive(invalid, peer) == (limit == 0 || attempt == 0),
                    "admission budget applies only when configured"
                );
                await UntilAsync(() =>
                {
                    dispatcher.Tick();
                    return dispatcher.Active == 0;
                }).ConfigureAwait(false);
            }

            now = 1000;
            Check(dispatcher.Receive(invalid, peer), "configured admission budget renews after its window");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                ProductionListenerFailureTests.Mutate(data);

            MutateAsync(data[..Math.Min(12, data.Length)].ToArray())
                .GetAwaiter()
                .GetResult();

            if (!data.IsEmpty && (data[0] & 15) == 0)
                ScheduledApplicationAsync(data[1..Math.Min(13, data.Length)].ToArray())
                    .GetAwaiter()
                    .GetResult();
        }

        private static async Task MutateAsync(byte[] data)
        {
            var created = 0;
            var disposed = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, _, _) =>
                {
                    var connection = new QuicConnection(
                        packet,
                        _ =>
                        {
                            Interlocked.Increment(ref created);
                            return new ControlledTls(
                                static () => { },
                                () => Interlocked.Increment(ref disposed)
                            );
                        },
                        static (port, _) => new StreamEchoApplication(port),
                        now
                    );
                    return connection;
                },
                static () => 0,
                capacity: 2,
                workers: 2,
                outputPacketCapacity: 2,
                outputByteCapacity: 2400
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var local = new IPEndPoint(IPAddress.Loopback, 18001);
            dispatcher.Receive(Packet(1), new IPEndPoint(IPAddress.Loopback, 17001), local);
            QuicFuzzChecks.Require(
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false),
                "dispatcher-initial-progress"
            );

            for (var index = 0; index < data.Length; index++)
            {
                var owner = (byte)(1 + data[index] % 3);
                var packet = Packet(owner, (uint)index + 1);

                if ((data[index] & 4) != 0) packet[^1] ^= 1;

                dispatcher.Accepting = (data[index] & 32) == 0;
                dispatcher.Receive(
                    packet,
                    new IPEndPoint(IPAddress.Loopback, 17000 + owner),
                    (data[index] & 64) == 0 ? local : new IPEndPoint(IPAddress.Loopback, 18002)
                );

                if ((data[index] & 8) != 0) dispatcher.Tick();

                if ((data[index] & 16) != 0)
                    await dispatcher
                        .SendOneAsync(output =>
                        {
                            QuicFuzzChecks.Require(output.Peer.Port is >= 17001 and <= 17003, "dispatcher-peer-bound");
                            return ValueTask.CompletedTask;
                        })
                        .ConfigureAwait(false);

                QuicFuzzChecks.Require(
                    dispatcher.Active <= 2
                        && dispatcher.PendingOutputPackets <= 2
                        && dispatcher.PendingOutputBytes <= 2400,
                    "dispatcher-aggregate-bounds"
                );
            }

            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            QuicFuzzChecks.Require(
                created == disposed
                    && dispatcher.Active == 0
                    && dispatcher.PendingOutputBytes == 0
                    && dispatcher.PendingOutputPackets == 0
                    && dispatcher.FailedOwners == 0,
                "dispatcher-final-ownership"
            );
        }

        private static async Task UntilAsync(Func<bool> complete)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (!complete())
                await Task
                    .Delay(1, stop.Token)
                    .ConfigureAwait(false);
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("QUIC dispatcher regression: " + name);
        }
    }
}
