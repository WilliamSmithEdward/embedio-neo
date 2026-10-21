using System.Runtime.Versioning;
using System.Text.Json;

namespace EmbedIO.Quic
{
    [SupportedOSPlatform("windows")]
    internal static class SchannelTests
    {
        internal static void Run()
        {
            var output = Path.Combine(
                "TestResults",
                "quic-schannel-self-test",
                Guid
                    .NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(output);

            foreach (var elliptic in new[] { false, true })
            {
                using var certificate = new SchannelTestCertificate(
                    Path.Combine(output, elliptic ? "ecdsa.pem" : "rsa.pem"),
                    elliptic
                );

                foreach (var protocol in new[]
                {
                    "",
                    new string('a', 256),
                    "h3\0",
                    "h3\u00e9"
                })
                {
                    try
                    {
                        using var invalid = certificate.Create([], protocol);
                        throw new InvalidOperationException("Invalid ALPN was accepted.");
                    }

                    catch (ArgumentException) { }

                    Released();
                }

                try
                {
                    using var invalid = certificate.Create(new byte[4097], "h3");
                    throw new InvalidOperationException("Oversized transport parameters were accepted.");
                }

                catch (ArgumentException) { }

                Released();

                foreach (var fragmented in new[] { false, true }) FailedHandshake(certificate, fragmented);

                Parallel.For(0, 32, index => FailedHandshake(certificate, index % 2 == 0));
                Released();
            }

            var report = new
            {
                passed = true,
                credentialKinds = 2,
                concurrentContexts = 64,
                leakedRegions = SchannelTls.ActiveRegions,
                leakedContexts = SchannelTls.ActiveContexts,
                leakedCredentials = SchannelTls.ActiveCredentials
            };
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        private static void FailedHandshake(SchannelTestCertificate certificate, bool fragmented)
        {
            var tls = certificate.Create([], "h3");

            try
            {
                var hello = TlsHelloTests.Create([]);

                try
                {
                    if (fragmented)
                    {
                        for (var index = 0; index < hello.Length; index++)
                        {
                            tls.Add(0, (ulong)index, hello.AsSpan(index, 1));
                            tls.Pump();
                        }
                    }

                    else
                    {
                        tls.Add(0, 0, hello);
                        tls.Pump();
                    }

                    throw new InvalidOperationException("Incomplete TLS 1.3 ClientHello was accepted by SSPI.");
                }

                catch (InvalidDataException)
                {
                    QuicFuzzChecks.Require(tls.AlertCode.HasValue && !tls.Established, "native-tls-error-alert");
                }
            }

            finally
            {
                tls.Dispose();
                tls.Dispose();
            }

            QuicFuzzChecks.Require(
                tls.ReadKeys.All(key => key == null)
                    && tls.WriteKeys.All(key => key == null)
                    && tls.Output.Count == 0
                    && tls.PeerParameters == null
                    && !tls.Established,
                "native-tls-disposed-state"
            );

            try
            {
                tls.Add(0, 0, []);
                throw new InvalidOperationException("Disposed TLS accepted input.");
            }

            catch (ObjectDisposedException) { }

            try
            {
                tls.Pump();
                throw new InvalidOperationException("Disposed TLS continued handshake.");
            }

            catch (ObjectDisposedException) { }
        }

        private static void Released() =>
            QuicFuzzChecks.Require(
                SchannelTls.ActiveCredentials == 0 && SchannelTls.ActiveContexts == 0 && SchannelTls.ActiveRegions == 0,
                "native-tls-resource-cleanup"
            );
    }
}
