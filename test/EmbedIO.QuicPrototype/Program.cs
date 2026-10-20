using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EmbedIO.QuicPrototype
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

                if (arguments is ["certificate", var directory])
                {
                    Certificate(directory);
                    return 0;
                }

                if (arguments is ["serve", var library, var certificate, var key])
                    return await ServeAsync(library, certificate, key).ConfigureAwait(false);

                Console.Error.WriteLine(
                    "Usage: self-test | certificate <output-directory> | serve <absolute-openssl-directory> <certificate.pem> <key.pem>"
                );
                return 2;
            }

            catch (Exception error) when (
                error is InvalidOperationException
                    or IOException
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

        private static async Task<int> ServeAsync(string library, string certificate, string key)
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = new
            {
                ready = true,
                port = ((IPEndPoint)(socket.Client.LocalEndPoint
                    ?? throw new InvalidOperationException("UDP endpoint unavailable."))).Port
            };
            Console.WriteLine(JsonSerializer.Serialize(ready));
            ProbeConnection? connection = null;
            IPEndPoint? peer = null;
            var ignored = 0;

            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    var datagram = await socket
                        .ReceiveAsync(deadline.Token)
                        .ConfigureAwait(false);

                    if (peer != null && !peer.Equals(datagram.RemoteEndPoint)) continue;

                    if (connection == null)
                    {
                        try
                        {
                            connection = new ProbeConnection(datagram.Buffer, library, certificate, key);
                        }

                        catch (Exception error) when (error is InvalidDataException or CryptographicException)
                        {
                            if (++ignored > 32)
                                throw new InvalidDataException("Unauthenticated packet budget exceeded.", error);

                            continue;
                        }

                        peer = datagram.RemoteEndPoint;
                    }

                    foreach (var packet in connection.Receive(datagram.Buffer))
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
                var result = new
                {
                    established = connection?.Established ?? false,
                    complete = connection?.Complete ?? false,
                    echoBytes = connection?.EchoBytes ?? 0,
                    receivedBytes = connection?.ReceivedBytes ?? 0,
                    sentBytes = connection?.SentBytes ?? 0,
                    rejectedPackets = ignored + (connection?.RejectedPackets ?? 0)
                };
                Console.WriteLine(JsonSerializer.Serialize(result));
                connection?.Dispose();
            }

            return connection?.Complete == true ? 0 : 1;
        }
    }
}
