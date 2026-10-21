using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbedIO.Quic
{
    internal static class SharedListener
    {
        internal static async Task<int> RunAsync(
            string library,
            string certificate,
            string key,
            bool webTransport,
            int expected,
            int port
        )
        {
            if (expected is < 1 or > 64 || port is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(expected));

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.ExclusiveAddressUse = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            var authorization = webTransport ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : string.Empty;
            using var provider = new DevelopmentTlsProvider(library, certificate, key);
            using var connections = new ConnectionSet(
                library,
                certificate,
                key,
                webTransport,
                authorization,
                provider.Create
            );
            var clock = Stopwatch.StartNew();
            var ready = new
            {
                ready = true,
                authorization,
                port = ((IPEndPoint)(socket.Client.LocalEndPoint
                    ?? throw new InvalidOperationException("Missing endpoint."))).Port
            };
            Console.WriteLine(JsonSerializer.Serialize(ready));
            Task<UdpReceiveResult>? receive = null;
            Task<bool>? tick = null;
            var deadline = false;

            try
            {
                receive = socket
                    .ReceiveAsync(stop.Token)
                    .AsTask();
                tick = timer
                    .WaitForNextTickAsync(stop.Token)
                    .AsTask();

                while (true)
                {
                    await Task
                        .WhenAny(receive, tick)
                        .ConfigureAwait(false);

                    if (receive.IsCompleted)
                    {
                        try
                        {
                            var datagram = await receive.ConfigureAwait(false);
                            await SendAsync(
                                connections.Receive(
                                    datagram.Buffer,
                                    datagram.RemoteEndPoint,
                                    clock.Elapsed.TotalMilliseconds
                                )
                            ).ConfigureAwait(false);
                        }

                        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            // A shared unconnected UDP socket cannot attribute Windows ICMP to an owner.
                            // The owner's existing QUIC timeout decides its lifetime.
                        }

                        receive = socket
                            .ReceiveAsync(stop.Token)
                            .AsTask();
                    }

                    if (tick.IsCompleted)
                    {
                        if (!await tick.ConfigureAwait(false)) break;

                        await SendAsync(connections.Tick(clock.Elapsed.TotalMilliseconds)).ConfigureAwait(false);
                        tick = timer
                            .WaitForNextTickAsync(stop.Token)
                            .AsTask();
                    }

                    if (connections.Retired >= expected && connections.Active == 0) break;
                }
            }

            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                deadline = true;
            }

            finally
            {
                await stop
                    .CancelAsync()
                    .ConfigureAwait(false);

                if (receive != null)
                {
                    try
                    {
                        await receive.ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) { }

                    catch (SocketException) { }
                }

                if (tick != null)
                {
                    try
                    {
                        await tick.ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) { }
                }

                connections.Dispose();
                var result = new
                {
                    accepted = connections.Accepted,
                    completed = connections.Completed,
                    retired = connections.Retired,
                    active = connections.Active,
                    peak = connections.Peak,
                    rejected = connections.Rejected,
                    expiredHandshakes = connections.ExpiredHandshakes,
                    failedOwners = connections.FailedOwners,
                    deadline
                };
                Console.WriteLine(JsonSerializer.Serialize(result));
            }

            return !deadline && connections.Completed >= expected ? 0 : 1;

            async Task SendAsync(List<OutboundDatagram> packets)
            {
                foreach (var packet in packets)
                {
                    try
                    {
                        await socket
                            .SendAsync(packet.Bytes, packet.Peer, stop.Token)
                            .ConfigureAwait(false);
                    }

                    catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
                }
            }
        }
    }
}
