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

                using (var replacement = new SchannelTestCertificate(
                    Path.Combine(output, elliptic ? "replacement-rsa.pem" : "replacement-ecdsa.pem"),
                    !elliptic
                ))
                {
                    SchannelSelectionTests.Run(certificate, replacement);
                    ProductionTlsTests.Run(certificate, replacement);
                }

                FirstFlight(certificate);

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

        private static void FirstFlight(SchannelTestCertificate certificate)
        {
            foreach (var size in new[] { 0, 1, 2, 4096 })
            {
                var parameters = Enumerable
                    .Repeat((byte)0x5a, size)
                    .ToArray();
                using var tls = certificate.Create(parameters, "h3");
                tls.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));
                tls.Pump();
                QuicFuzzChecks.Require(
                    tls.ReadKeys[1] != null
                        && tls.WriteKeys[1] != null
                        && tls.WriteKeys[2] != null
                        && !tls.Established
                        && tls.AlertCode == null,
                    "native-tls-first-flight"
                );
                var extensions = tls.Output
                    .Single(message => message.Bytes[0] == 8)
                    .Bytes.AsSpan(6);
                var found = false;

                while (!extensions.IsEmpty)
                {
                    var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(extensions);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(extensions[2..]);

                    if (type == 57)
                    {
                        QuicFuzzChecks.Require(
                            !found
                                && extensions
                                    .Slice(4, length)
                                    .SequenceEqual(parameters),
                            "native-tls-extension-bytes"
                        );
                        found = true;
                    }

                    extensions = extensions[(4 + length)..];
                }

                QuicFuzzChecks.Require(found, "native-tls-extension-present");
            }

            Released();
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
                    QuicFuzzChecks.Require(
                        tls.AlertCode is > 0
                            && !tls.Established
                            && tls.Output.Count == 0
                            && tls.ReadKeys.All(key => key == null)
                            && tls.WriteKeys.All(key => key == null),
                        "native-tls-error-alert"
                    );
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
