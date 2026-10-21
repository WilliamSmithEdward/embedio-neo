using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbedIO.Quic
{
    // Bounded native first-flight diagnostic, separate from full TLS/peer acceptance.
    // Every combination is reported; a failed case still produces a failing result.
    [SupportedOSPlatform("windows")]
    internal static class SchannelContractProbe
    {
        internal static int Run()
        {
            var output = Path.Combine(
                "TestResults",
                "quic-schannel-contracts",
                Guid
                    .NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(output);
            var failures = 0;
            var count = 0;

            foreach (var elliptic in new[] { false, true })
            {
                using var certificate = new SchannelTestCertificate(
                    Path.Combine(output, elliptic ? "ecdsa.pem" : "rsa.pem"),
                    elliptic
                );

                foreach (var protocol in new[] { "h3", OpenSslTls.ApplicationProtocol })
                    foreach (var parameterSize in new[] { 0, 2, 32 })
                        foreach (var name in new string?[] { null, "localhost" })
                            foreach (var keyShare in new[] { 0, 1, 2 })
                                foreach (var fragmented in new[] { false, true })
                                {
                                    count++;

                                    if (
                                        !Check(
                                            certificate,
                                            elliptic,
                                            protocol,
                                            parameterSize,
                                            name,
                                            keyShare,
                                            fragmented
                                        )
                                    ) failures++;
                                }
            }

            var summary = new
            {
                kind = "summary",
                count,
                failures,
                passed = failures == 0
            };
            Console.WriteLine(JsonSerializer.Serialize(summary));
            return failures == 0 ? 0 : 1;
        }

        private static bool Check(
            SchannelTestCertificate certificate,
            bool elliptic,
            string protocol,
            int parameterSize,
            string? name,
            int keyShare,
            bool fragmented
        )
        {
            var extensions = name == null
                ? Array.Empty<(ushort Type, byte[] Value)>()
                : [(Type: (ushort)0, Value: TlsHelloTests.ServerName(Encoding.ASCII.GetBytes(name)))];
            var hello = TlsHelloTests.NativeHello([], protocol, keyShare, extensions);
            string? error = null;
            var firstFlight = false;
            byte? alert = null;
            string[] diagnostics = [];

            try
            {
                using var tls = new SchannelTls(
                    _ => certificate.BorrowCertificate(),
                    new byte[parameterSize],
                    protocol
                );

                try
                {
                    if (fragmented)
                    {
                        var split = hello.Length / 2;
                        tls.Add(0, 0, hello.AsSpan(0, split));
                        tls.Pump();

                        if (tls.NativeContextCreated)
                            throw new InvalidOperationException("Partial hello started native TLS.");

                        tls.Add(0, (ulong)split, hello.AsSpan(split));
                    }

                    else tls.Add(0, 0, hello);

                    tls.Pump();
                    firstFlight = tls.ReadKeys[1] != null
                        && tls.WriteKeys[1] != null
                        && tls.WriteKeys[2] != null
                        && !tls.Established
                        && tls.AlertCode == null;
                }

                finally
                {
                    alert = tls.AlertCode;
                    diagnostics = [.. tls.Diagnostics];
                }
            }

            catch (Exception failure) when (
                failure is InvalidDataException
                    or CryptographicException
                    or InvalidOperationException
            )
            {
                error = failure.ToString();
            }

            var released = SchannelTls.ActiveRegions == 0
                && SchannelTls.ActiveContexts == 0
                && SchannelTls.ActiveCredentials == 0;
            var passed = firstFlight && error == null && released;
            var result = new
            {
                kind = "first-flight-contract",
                credential = elliptic ? "ECDSA" : "RSA",
                protocol,
                parameterSize,
                serverName = name,
                group = keyShare == 2 ? "P-256" : "X25519",
                keyShare = keyShare switch
                {
                    0 => "basepoint",
                    1 => "RFC7748-Alice",
                    _ => "generated"
                },
                fragmented,
                passed,
                firstFlight,
                released,
                alert,
                diagnostics,
                error,
                hello = Convert.ToHexString(hello)
            };
            Console.WriteLine(JsonSerializer.Serialize(result));

            if (!released) throw new InvalidOperationException("Native diagnostic retained resources.");

            return passed;
        }
    }
}
