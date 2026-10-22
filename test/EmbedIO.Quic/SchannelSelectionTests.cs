using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EmbedIO.Quic
{
    [SupportedOSPlatform("windows")]
    internal static class SchannelSelectionTests
    {
        internal static void Run(SchannelTestCertificate first, SchannelTestCertificate replacement)
        {
            foreach (var name in new string?[] { null, "Selected.EXAMPLE" })
            {
                var current = first.BorrowCertificate();
                var calls = 0;
                var observed = "not-called";
                using var tls = new SchannelTls(
                    serverName =>
                    {
                        calls++;
                        observed = serverName;
                        return current;
                    },
                    [],
                    "h3"
                );
                var hello = name == null
                    ? TlsHelloTests.NativeHello([], "h3")
                    : TlsHelloTests.NativeHello([], "h3", (0, TlsHelloTests.ServerName(Encoding.ASCII.GetBytes(name))));
                var split = hello.Length / 2;
                tls.Add(0, 0, hello.AsSpan(0, split));
                tls.Pump();
                Require(calls == 0 && SchannelTls.ActiveCredentials == 0, "incomplete-hello-does-not-select");
                current = replacement.BorrowCertificate();
                tls.Add(0, (ulong)split, hello.AsSpan(split));

                tls.Pump();
                Require(tls.WriteKeys[1] != null && tls.AlertCode == null, "selected-credential-first-flight");

                Require(calls == 1 && observed == name, "full-hello-selects-once");
                Require(
                    tls.SelectedCertificateSha256 == current.GetCertHashString(HashAlgorithmName.SHA256),
                    "current-certificate-snapshot"
                );
                current = first.BorrowCertificate();
                tls.Pump();
                Require(
                    calls == 1
                        && tls.SelectedCertificateSha256
                            == replacement
                                .BorrowCertificate()
                                .GetCertHashString(HashAlgorithmName.SHA256),
                    "snapshot-survives-publication"
                );
                Require(
                    first.BorrowCertificate().HasPrivateKey && current.HasPrivateKey,
                    "source-certificate-remains-borrowed"
                );
            }

            var failedCalls = 0;

            using (var tls = new SchannelTls(
                _ =>
                {
                    failedCalls++;
                    throw new InvalidOperationException("Controlled selector failure.");
                },
                [],
                "h3"
            ))
            {
                tls.Add(0, 0, TlsHelloTests.Create([]));
                Refused(tls);
                Refused(tls);
                Require(failedCalls == 1, "failed-selector-not-retried");
            }

            using (var tls = new SchannelTls(_ => null, [], "h3"))
            {
                tls.Add(0, 0, TlsHelloTests.Create([]));
                Refused(tls);
            }

            using (var certificate = X509CertificateLoader.LoadCertificate(
                first
                    .BorrowCertificate()
                    .Export(X509ContentType.Cert)
            ))
            using (var tls = new SchannelTls(_ => certificate, [], "h3"))
            {
                tls.Add(0, 0, TlsHelloTests.Create([]));
                Refused(tls);
            }

            SchannelTls? reentrant = null;

            try
            {
                reentrant = new SchannelTls(
                    _ =>
                    {
                        reentrant?.Dispose();
                        return first.BorrowCertificate();
                    },
                    [],
                    "h3"
                );
                reentrant.Add(0, 0, TlsHelloTests.Create([]));
                Refused(reentrant);
                Require(
                    SchannelTls.ActiveCredentials == 0
                        && SchannelTls.ActiveContexts == 0
                        && SchannelTls.ActiveRegions == 0,
                    "selector-disposal-does-not-resurrect-resources"
                );
            }

            finally
            {
                reentrant?.Dispose();
            }

            var malformedCalls = 0;

            using (var tls = new SchannelTls(
                _ =>
                {
                    malformedCalls++;
                    return first.BorrowCertificate();
                },
                [],
                "h3"
            ))
            {
                tls.Add(0, 0, TlsHelloTests.Create([], (0, TlsHelloTests.ServerName([0]))));

                try
                {
                    tls.Pump();
                    throw new InvalidOperationException("Malformed SNI was accepted.");
                }

                catch (TransportException error)
                {
                    Require(error.ErrorCode == 0x12f && malformedCalls == 0, "malformed-sni-rejected-before-selector");
                }
            }
        }

        private static void Refused(TlsSession tls)
        {
            try
            {
                tls.Pump();
            }

            catch (InvalidDataException)
            {
                Require(tls.AlertCode == 80 && !tls.Established, "selector-failure-refuses-handshake");
                return;
            }

            throw new InvalidOperationException("Invalid TLS selection was accepted.");
        }

        private static void Require(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "native-selection-" + name);
    }
}
