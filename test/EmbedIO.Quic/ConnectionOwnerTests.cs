using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal static class ConnectionOwnerTests
    {
        private static readonly byte[] Original = [1, 2, 3, 4, 5, 6, 7, 8];
        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        internal static async Task RunAsync()
        {
            await MailboxOwnershipAsync().ConfigureAwait(false);
            await DeferredCleanupAsync().ConfigureAwait(false);
            await RejectedInitialAsync().ConfigureAwait(false);
            Mutate([0]);
            Mutate([1, 255, 6]);
            Mutate([6, 0, 1, 255]);
        }

        internal static void Mutate(ReadOnlySpan<byte> data) =>
            MutateAsync(data[..Math.Min(data.Length, 256)].ToArray())
                .GetAwaiter()
                .GetResult();

        private sealed class ObservedConnection(byte[] first, TlsSession tls, Action processed) : QuicConnection(
            first,
            _ => tls,
            static (port, _) => new StreamEchoApplication(port)
        )
        {
            internal override List<byte[]> Receive(ReadOnlySpan<byte> bytes, double now)
            {
                var output = base.Receive(bytes, now);
                processed();
                return output;
            }
        }

        private static async Task MutateAsync(byte[] data)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim(true);
            var tls = new BlockingTls(entered, release);
            await using var pool = new QuicExecutionPool(1, 1);
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var second = NativeFuzz.Initial(keys, Original, Client, 1, data);

            if (data.Length != 0 && (data[0] & 1) != 0) second[^1] ^= 1;

            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processed = 0;
            await using var owner = new QuicConnectionOwner(
                pool,
                (packet, _) => new ObservedConnection(packet.ToArray(), tls, () =>
                {
                    if (Interlocked.Increment(ref processed) == 2) completed.TrySetResult();
                }),
                static () => 0,
                static output => QuicFuzzChecks.Require(output.ConnectionId.Length == 8, "execution-packet-identity"),
                packetCapacity: 2,
                byteCapacity: first.Length + second.Length
            );
            QuicFuzzChecks.Require(owner.Receive(first) && owner.Receive(second), "execution-packet-admission");
            var finished = await Task
                .WhenAny(completed.Task, owner.Completion)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            QuicFuzzChecks.Require(processed == 2, "execution-packet-turns");
            await owner
                .DisposeAsync()
                .ConfigureAwait(false);
            QuicFuzzChecks.Require(
                tls.DisposeCalls == 1 && owner.PendingBytes == 0 && owner.PendingPackets == 0 && pool.Active == 0,
                "execution-packet-cleanup"
            );
        }

        internal static async Task NativeAsync(string library, string certificate, string key)
        {
            using var provider = new DevelopmentTlsProvider(library, certificate, key);
            await using var pool = new QuicExecutionPool(2, 2);
            using var parameters = new MemoryStream();
            Wire.Parameter(parameters, 15, Client);
            var hello = TlsHelloTests.NativeHello(parameters.ToArray(), "h3");
            using var keys = PacketKeys.Initial(Original, false);
            var packet = NativeFuzz.Initial(
                keys,
                Original,
                Client,
                0,
                [
                    .. Wire.Frame(6, 0, (ulong)hello.Length),
                    .. hello
                ]
            );
            var owners = new List<QuicConnectionOwner>();
            var ready = new List<Task>();
            var sessions = new System.Collections.Concurrent.ConcurrentBag<TlsSession>();

            try
            {
                for (var index = 0; index < 2; index++)
                {
                    TlsSession? tls = null;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var owner = new QuicConnectionOwner(
                        pool,
                        (first, now) => new QuicConnection(
                            first,
                            local =>
                            {
                                tls = provider.Create(local, "h3");
                                sessions.Add(tls);
                                return tls;
                            },
                            static (port, _) => new StreamEchoApplication(port),
                            now
                        ),
                        static () => 0,
                        output =>
                        {
                            Check(
                                tls != null
                                    && tls.ReadKeys[1] != null
                                    && tls.WriteKeys[1] != null
                                    && tls.AlertCode == null
                                    && output.Datagrams.Count != 0,
                                "scheduled native first flight installs handshake keys"
                            );
                            completion.TrySetResult();
                        }
                    );
                    owners.Add(owner);
                    ready.Add(completion.Task);
                    Check(owner.Receive(packet), "native first flight admitted");
                }

                await Task
                    .WhenAll(ready)
                    .WaitAsync(TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
            }

            finally
            {
                await Task
                    .WhenAll(owners.Select(owner => owner
                            .DisposeAsync()
                            .AsTask()))
                    .ConfigureAwait(false);
            }

            Check(
                pool.Active == 0
                    && sessions.Count == 2
                    && sessions.All(
                        session => session.ReadKeys.All(key => key == null) && session.WriteKeys.All(key => key == null)
                    ),
                "scheduled native owners release packet keys"
            );
        }

        private sealed class BlockingTls(ManualResetEventSlim entered, ManualResetEventSlim release) : TlsSession
        {
            internal ManualResetEventSlim Entered => entered;
            internal ManualResetEventSlim Release => release;

            internal int PumpCalls;
            internal int DisposeCalls;
            private int _executing;

            internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes) { }

            internal override void Pump()
            {
                Check(Interlocked.Increment(ref _executing) == 1, "native calls serialize");
                Interlocked.Increment(ref PumpCalls);
                Entered.Set();
                Check(Release.Wait(TimeSpan.FromSeconds(10)), "native operation released");
                Interlocked.Decrement(ref _executing);
            }

            public override void Dispose()
            {
                Check(Volatile.Read(ref _executing) == 0, "native cleanup follows execution");
                Interlocked.Increment(ref DisposeCalls);
                ReleaseKeys();
            }
        }

        private static async Task MailboxOwnershipAsync()
        {
            await using var pool = new QuicExecutionPool(2, 2);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var tls = new BlockingTls(entered, release);
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var second = NativeFuzz.Initial(keys, Original, Client, 1, [1]);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publications = 0;
            var owner = new QuicConnectionOwner(
                pool,
                (packet, now) => new QuicConnection(
                    packet,
                    _ => tls,
                    static (port, _) => new StreamEchoApplication(port),
                    now
                ),
                static () => 0,
                output =>
                {
                    Check(
                        output.ConnectionId.Length == 8 && output.Datagrams.Count != 0,
                        "output belongs to connection"
                    );

                    if (Interlocked.Increment(ref publications) == 2) completed.TrySetResult();
                },
                packetCapacity: 2,
                byteCapacity: first.Length + second.Length
            );

            try
            {
                Check(owner.Receive(first) && tls.Entered.Wait(TimeSpan.FromSeconds(10)), "first packet executes");
                Check(
                    owner.PendingPackets == 1 && owner.PendingBytes == first.Length,
                    "active packet counts toward memory budget"
                );
                Check(owner.Receive(second), "second packet admitted");
                Check(!owner.Receive(second), "mailbox refuses excess packet and byte ownership");
                second[^1] ^= 1;
                tls.Release.Set();
                await completed.Task
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                await owner
                    .DisposeAsync()
                    .ConfigureAwait(false);
                Check(
                    tls.PumpCalls == 2 && tls.DisposeCalls == 1,
                    "queued input copied and native owner released once"
                );
                Check(
                    owner.PendingPackets == 0 && owner.PendingBytes == 0 && !owner.Receive(first),
                    "closed mailbox releases all input"
                );
            }

            finally
            {
                tls.Release.Set();
                await owner
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static async Task DeferredCleanupAsync()
        {
            await using var pool = new QuicExecutionPool(2, 2);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var tls = new BlockingTls(entered, release);
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var owner = new QuicConnectionOwner(
                pool,
                (packet, now) => new QuicConnection(
                    packet,
                    _ => tls,
                    static (port, _) => new StreamEchoApplication(port),
                    now
                ),
                static () => 0,
                static _ => throw new InvalidOperationException("Closing owner published output.")
            );

            try
            {
                owner.Receive(first);
                Check(tls.Entered.Wait(TimeSpan.FromSeconds(10)), "native call pending");
                var closing = owner
                    .DisposeAsync()
                    .AsTask();
                Check(!closing.IsCompleted && tls.DisposeCalls == 0, "active native resources remain valid");
                tls.Release.Set();
                await closing
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                Check(tls.DisposeCalls == 1 && owner.PendingBytes == 0, "deferred owner shutdown complete");
            }

            finally
            {
                tls.Release.Set();
                await owner
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static async Task RejectedInitialAsync()
        {
            await using var pool = new QuicExecutionPool(1, 1);
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            first[^1] ^= 1;
            var factories = 0;
            var owner = new QuicConnectionOwner(
                pool,
                (packet, now) => new QuicConnection(
                    packet,
                    _ =>
                    {
                        factories++;
                        throw new InvalidOperationException("TLS factory should not run.");
                    },
                    static (port, _) => new StreamEchoApplication(port),
                    now
                ),
                static () => 0,
                static _ => throw new InvalidOperationException("Invalid Initial published output.")
            );
            owner.Receive(first);

            try
            {
                await owner.Completion
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Invalid Initial was accepted.");
            }

            catch (CryptographicException) { }

            Check(
                factories == 0 && pool.Active == 0 && owner.PendingBytes == 0,
                "failed authentication releases admission without TLS"
            );
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("QUIC scheduled owner regression: " + name);
        }
    }
}
