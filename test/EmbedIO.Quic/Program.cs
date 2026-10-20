using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EmbedIO.Quic
{
    internal static class Program
    {
        private static async Task<int> Main(string[] arguments)
        {
            try
            {
                if (arguments is ["self-test"])
                {
                    SelfTests.Run();
                    return 0;
                }

                if (
                    arguments is [
                        "fuzz-native",
                        var nativeLibrary,
                        var nativeCertificate,
                        var nativeKey,
                        var seed,
                        var cases,
                        var evidence
                    ]
                )
                {
                    NativeFuzz.Run(nativeLibrary, nativeCertificate, nativeKey, seed, cases, evidence);
                    return 0;
                }

                if (arguments is ["certificate", var directory])
                {
                    Certificate(directory);
                    return 0;
                }

                if (
                    arguments is [
                        "serve-many" or "serve-many-h3",
                        var sharedLibrary,
                        var sharedCertificate,
                        var sharedKey,
                        var expected,
                        var port
                    ]
                )
                    return await SharedListener
                        .RunAsync(
                            sharedLibrary,
                            sharedCertificate,
                            sharedKey,
                            arguments[0] == "serve-many-h3",
                            int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)
                        )
                        .ConfigureAwait(false);

                if (arguments is ["serve" or "serve-h3", var library, var certificate, var key])
                    return await ServeAsync(library, certificate, key, arguments[0] == "serve-h3").ConfigureAwait(
                        false
                    );

                Console.Error.WriteLine(
                    "Usage: self-test | certificate <output-directory> | serve[-h3] <absolute-openssl-directory> <certificate.pem> <key.pem>"
                );
                return 2;
            }

            catch (Exception error) when (
                error is InvalidOperationException
                    or IOException
                    or SocketException
                    or InvalidDataException
                    or CryptographicException
                    or ArgumentException
                    or NotSupportedException
                    or DllNotFoundException
                    or EntryPointNotFoundException
            )
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static void Certificate(string directory)
        {
            Directory.CreateDirectory(directory);

            if (
                File.Exists(Path.Combine(directory, "certificate.pem"))
                || File.Exists(Path.Combine(directory, "key.pem"))
            )
                throw new IOException("Use a fresh certificate output directory.");

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1)
            );
            File.WriteAllText(
                Path.Combine(directory, "certificate.pem"),
                certificate.ExportCertificatePem(),
                Encoding.ASCII
            );
            File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
            Console.WriteLine(JsonSerializer.Serialize(new { certificateCreated = true }));
        }

        private static async Task<int> ServeAsync(string library, string certificate, string key, bool webTransport)
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var authorization = webTransport ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : string.Empty;
            var ready = new
            {
                ready = true,
                authorization,
                port = ((IPEndPoint)(socket.Client.LocalEndPoint
                    ?? throw new InvalidOperationException("UDP endpoint unavailable."))).Port
            };
            Console.WriteLine(JsonSerializer.Serialize(ready));
            ProbeConnection? connection = null;
            IPEndPoint? peer = null;
            var ignored = 0;

            Task<UdpReceiveResult>? receive = null;

            try
            {
                var clock = Stopwatch.StartNew();
                receive = socket
                    .ReceiveAsync(deadline.Token)
                    .AsTask();

                while (!deadline.IsCancellationRequested)
                {
                    if (connection != null && peer != null)
                    {
                        await Task
                            .WhenAny(receive, Task.Delay(2, deadline.Token))
                            .ConfigureAwait(false);

                        if (!receive.IsCompleted)
                        {
                            foreach (var packet in connection.Tick(clock.Elapsed.TotalMilliseconds))
                                await socket
                                    .SendAsync(packet, peer, deadline.Token)
                                    .ConfigureAwait(false);

                            if (connection.Closed) break;

                            continue;
                        }
                    }

                    UdpReceiveResult datagram;

                    try
                    {
                        datagram = await receive.ConfigureAwait(false);
                    }

                    catch (SocketException error) when (
                        error.SocketErrorCode == SocketError.ConnectionReset
                            && connection?.Phase is ConnectionPhase.Closing
                            or ConnectionPhase.Draining
                    )
                    {
                        // Windows can report ICMP from a peer that has already closed.
                        // Preserve the bounded closing/draining timer without sending application data.
                        connection.Tick(clock.Elapsed.TotalMilliseconds);

                        if (connection.Closed) break;

                        receive = socket
                            .ReceiveAsync(deadline.Token)
                            .AsTask();
                        continue;
                    }

                    receive = socket
                        .ReceiveAsync(deadline.Token)
                        .AsTask();

                    if (peer != null && !peer.Equals(datagram.RemoteEndPoint)) continue;

                    if (connection == null)
                    {
                        try
                        {
                            connection = new ProbeConnection(
                                datagram.Buffer,
                                library,
                                certificate,
                                key,
                                webTransport,
                                authorization
                            );
                        }

                        catch (Exception error) when (error is InvalidDataException or CryptographicException)
                        {
                            if (++ignored > 32)
                                throw new InvalidDataException("Unauthenticated packet budget exceeded.", error);

                            continue;
                        }

                        peer = datagram.RemoteEndPoint;
                    }

                    foreach (var packet in connection.Receive(datagram.Buffer, clock.Elapsed.TotalMilliseconds))
                        await socket
                            .SendAsync(packet, datagram.RemoteEndPoint, deadline.Token)
                            .ConfigureAwait(false);

                    if (connection.Closed) break;
                }
            }

            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Console.Error.WriteLine("Probe deadline expired.");
            }

            finally
            {
                await deadline
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

                var result = new
                {
                    established = connection?.Established ?? false,
                    phase = connection?.Phase.ToString(),
                    idleExpired = connection?.IdleExpired ?? false,
                    closePackets = connection?.ClosePackets ?? 0,
                    complete = connection?.Complete ?? false,
                    echoBytes = connection?.EchoBytes ?? 0,
                    receivedBytes = connection?.ReceivedBytes ?? 0,
                    sentBytes = connection?.SentBytes ?? 0,
                    rejectedPackets = ignored + (connection?.RejectedPackets ?? 0),
                    lostPackets = connection?.LostPackets ?? 0,
                    retransmittedPackets = connection?.RetransmittedPackets ?? 0,
                    keyUpdates = connection?.KeyUpdates ?? 0,
                    outboundResets = connection?.OutboundResets ?? 0,
                    receiveCreditUpdates = connection?.ReceiveCreditUpdates ?? 0,
                    streamBytesReceived = connection?.StreamBytesReceived ?? 0,
                    probeTimeouts = connection?.ProbeTimeouts ?? 0,
                    congestionEvents = connection?.CongestionEvents ?? 0,
                    bytesInFlight = connection?.BytesInFlight ?? 0,
                    acceptedSessions = connection?.WebTransport?.AcceptedSessions ?? 0,
                    rejectedSessions = connection?.WebTransport?.RejectedSessions ?? 0,
                    streamsDelivered = connection?.WebTransport?.StreamsDelivered ?? 0,
                    datagramsDelivered = connection?.WebTransport?.DatagramsDelivered ?? 0,
                    resetsDelivered = connection?.WebTransport?.ResetsDelivered ?? 0
                };
                Console.WriteLine(JsonSerializer.Serialize(result));
                connection?.Dispose();
            }

            return connection?.Complete == true ? 0 : 1;
        }
    }
}
