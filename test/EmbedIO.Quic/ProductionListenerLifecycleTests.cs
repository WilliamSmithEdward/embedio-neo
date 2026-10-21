using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading.Channels;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    internal static class ProductionListenerLifecycleTests
    {
        internal static Task RunAsync()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return RunSupportedAsync();

            return Task.CompletedTask;
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task RunSupportedAsync()
        {
            for (var iteration = 0; iteration < 8; iteration++)
            {
                await using var listener = new EmbedIOQuicListenerBinding(
                    new IPEndPoint(IPAddress.Loopback, 0),
                    static _ => throw new InvalidOperationException("No TLS expected without a packet.")
                );
                var original = listener.LocalEndPoint;
                var mutated = listener.LocalEndPoint;
                mutated.Port = 1;
                QuicFuzzChecks.Require(listener.LocalEndPoint.Equals(original), "listener-endpoint-copy");
                using var canceled = new CancellationTokenSource();
                Task pending = listener
                    .AcceptAsync(canceled.Token)
                    .AsTask();
                canceled.Cancel();
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                QuicFuzzChecks.Require(pending.IsCanceled, "accept-cancellation");
                Task abandoned = listener
                    .AcceptAsync(CancellationToken.None)
                    .AsTask();
                await Task
                    .WhenAll(
                        listener
                            .StopAcceptingAsync()
                            .AsTask(),
                        listener
                            .StopAcceptingAsync()
                            .AsTask()
                    )
                    .ConfigureAwait(false);
                await abandoned.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                QuicFuzzChecks.Require(
                    abandoned.Exception?.InnerException is ChannelClosedException && !listener.IsAccepting,
                    "stopped-acceptance-completes-waiters"
                );

                using (var contender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    contender.ExclusiveAddressUse = true;

                    try
                    {
                        contender.Bind(original);
                        throw new InvalidOperationException("Stopping acceptance released the socket prematurely.");
                    }

                    catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse) { }
                }

                await Task
                    .WhenAll(
                        listener
                            .DisposeAsync()
                            .AsTask(),
                        listener
                            .DisposeAsync()
                            .AsTask()
                    )
                    .ConfigureAwait(false);
                using var rebound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                rebound.ExclusiveAddressUse = true;
                rebound.Bind(original);
            }
        }
    }
}
