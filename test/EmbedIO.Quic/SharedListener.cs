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
            int port,
            bool asyncStreams = false,
            bool productionHttp3 = false
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
            var clock = Stopwatch.StartNew();
            var observed = new System.Collections.Concurrent.ConcurrentBag<ProbeConnection>();
            var applications = new System.Collections.Concurrent.ConcurrentBag<(AsyncStreamEcho Echo, Task Task)>();
            var productionApplications =
                new System.Collections.Concurrent.ConcurrentBag<(ProductionHttp3Application Http3, Task Task)>();
            var connections = new QuicConnectionDispatcher(
                (packet, now, owner, _) =>
                {
                    StreamApplication CreateApplication(StreamOperations operations, QuicPeerFeatures features)
                    {
                        var application = new AsyncStreamApplication(operations, owner.Notify);

                        if (productionHttp3)
                        {
                            var http3 = new ProductionHttp3Application();
                            productionApplications.Add(
                                (http3, http3.RunAsync(application, owner, features, stop.Token))
                            );
                        }

                        else
                        {
                            var echo = new AsyncStreamEcho(application, closeAfterEcho: true);
                            applications.Add((echo, echo.RunAsync(stop.Token)));
                        }

                        return application;
                    }

                    var connection = new ProbeConnection(
                        packet,
                        library,
                        certificate,
                        key,
                        webTransport || productionHttp3,
                        authorization,
                        now,
                        asyncStreams: asyncStreams,
                        tlsFactory: provider.Create,
                        applicationFactory: asyncStreams || productionHttp3 ? CreateApplication : null
                    );
                    observed.Add(connection);
                    return connection;
                },
                () => clock.Elapsed.TotalMilliseconds
            );
            Task<UdpReceiveResult>? receive = null;
            Task<bool>? tick = null;
            Task<bool>? output = null;
            var completed = 0;
            var deadline = false;
            var failures = new List<Exception>();

            try
            {
                var ready = new
                {
                    ready = true,
                    authorization,
                    port = ((IPEndPoint)(socket.Client.LocalEndPoint
                        ?? throw new InvalidOperationException("Missing endpoint."))).Port
                };
                Console.WriteLine(JsonSerializer.Serialize(ready));

                receive = socket
                    .ReceiveAsync(stop.Token)
                    .AsTask();
                tick = timer
                    .WaitForNextTickAsync(stop.Token)
                    .AsTask();

                output = connections
                    .WaitForOutputAsync(stop.Token)
                    .AsTask();

                while (true)
                {
                    await Task
                        .WhenAny(receive, tick, output)
                        .ConfigureAwait(false);

                    if (receive.IsCompleted)
                    {
                        try
                        {
                            var datagram = await receive.ConfigureAwait(false);
                            connections.Receive(datagram.Buffer, datagram.RemoteEndPoint);
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

                        connections.Tick();
                        tick = timer
                            .WaitForNextTickAsync(stop.Token)
                            .AsTask();
                    }

                    if (output.IsCompleted)
                    {
                        if (!await output.ConfigureAwait(false)) break;

                        await connections
                            .SendOneAsync(SendAsync)
                            .ConfigureAwait(false);
                        output = connections
                            .WaitForOutputAsync(stop.Token)
                            .AsTask();
                    }

                    if (connections.Retired >= expected && connections.Active == 0) break;
                }
            }

            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                deadline = true;
            }

            catch (Exception error) when (
                error is not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException
            )
            {
                failures.Add(error);
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

                if (output != null)
                {
                    try
                    {
                        await output.ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) { }
                }

                var owners = connections
                    .DisposeAsync()
                    .AsTask();
                await owners.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // Factories have finished, so this includes every application owner,
                // even when native shutdown reported an error.
                var handlers = Task
                    .WhenAll(
                        applications
                            .Select(application => application.Task)
                            .Concat(productionApplications.Select(application => application.Task))
                    )
                    .WaitAsync(TimeSpan.FromSeconds(5));
                var shutdown = Task.WhenAll(owners, handlers);
                await shutdown.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (shutdown.Exception != null) failures.Add(shutdown.Exception);

                else if (shutdown.IsCanceled) failures.Add(new TaskCanceledException(shutdown));

                completed = productionHttp3
                    ? productionApplications.Count(application => application.Http3.Completed > 0)
                    : asyncStreams
                        ? applications.Count(application => application.Echo.Completed > 0)
                        : observed.Count(connection => connection.Complete);
                var result = new
                {
                    accepted = connections.Accepted,
                    completed,
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

            if (failures.Count != 0) throw new AggregateException("Shared QUIC listener failed.", failures);

            return !deadline && completed >= expected ? 0 : 1;

            async ValueTask SendAsync(OutboundDatagram packet)
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
