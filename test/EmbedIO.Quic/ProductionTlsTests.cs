using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    [SupportedOSPlatform("windows")]
    internal static class ProductionTlsTests
    {
        internal static void Run(SchannelTestCertificate first, SchannelTestCertificate replacement)
        {
            Check(typeof(SchannelTls).Assembly == typeof(WebServer).Assembly, "native TLS belongs to the library");
            var source = new ServerCertificateSource(first.BorrowCertificate());
            var factory = EmbedIOQuicTls.CreateFactory(source, null);

            using (var started = (SchannelTls)factory([]))
            {
                source.Publish(replacement.BorrowCertificate());
                using var later = (SchannelTls)factory([]);
                FirstFlight(started);
                FirstFlight(later);
                Check(
                    started.SelectedCertificateSha256
                        == first
                            .BorrowCertificate()
                            .GetCertHashString(HashAlgorithmName.SHA256)
                        && later.SelectedCertificateSha256
                            == replacement
                                .BorrowCertificate()
                                .GetCertHashString(HashAlgorithmName.SHA256),
                    "certificate publication affects only later handshakes"
                );
                Check(
                    started.Diagnostics.Count == 0 && later.Diagnostics.Count == 0,
                    "ordinary TLS does not collect development diagnostics"
                );
            }

            source.Publish(first.BorrowCertificate());

            foreach (var selected in new[] { false, true })
            {
                var calls = 0;
                string? observed = null;
                var policy = new ServerTlsPolicy
                {
                    EnabledProtocols = SslProtocols.Tls13,
                    ServerCertificateSelector = name =>
                    {
                        calls++;
                        observed = name;
                        return selected ? replacement.BorrowCertificate() : null;
                    }
                };
                var select = EmbedIOQuicTls.CreateFactory(source, policy);
                using var tls = (SchannelTls)select([]);
                var hello = TlsHelloTests.NativeHello(
                    [],
                    "h3",
                    (0, TlsHelloTests.ServerName(Encoding.ASCII.GetBytes("Selected.EXAMPLE")))
                );
                var split = hello.Length / 2;
                tls.Add(0, 0, hello.AsSpan(0, split));
                tls.Pump();
                Check(calls == 0, "policy selection waits for complete ClientHello");
                source.Publish(replacement.BorrowCertificate());
                tls.Add(0, (ulong)split, hello.AsSpan(split));
                tls.Pump();
                Check(
                    calls == 1 && observed == "Selected.EXAMPLE" && tls.WriteKeys[1] != null,
                    "the actual policy receives original SNI once"
                );
                var expected = selected ? replacement : first;
                Check(
                    tls.SelectedCertificateSha256
                        == expected
                            .BorrowCertificate()
                            .GetCertHashString(HashAlgorithmName.SHA256),
                    "policy selection preserves the per-handshake fallback"
                );

                var locked = false;

                try
                {
                    policy.EnabledProtocols = SslProtocols.None;
                }

                catch (InvalidOperationException)
                {
                    locked = true;
                }

                Check(locked, "the listener locks its TLS policy");

                source.Publish(first.BorrowCertificate());
            }

            foreach (var mode in new[] { ClientCertificateMode.Allow, ClientCertificateMode.Require })
            {
                using var tls = EmbedIOQuicTls.CreateFactory(
                    source,
                    new ServerTlsPolicy { ClientCertificateMode = mode }
                )([]);
                tls.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));
                tls.Pump();
                Check(
                    tls.WriteKeys[1] != null
                        && !tls.Established
                        && tls.Output.Any(output => output.Level == 1 && output.Bytes[0] == 13),
                    "client certificate request is sent during the initial handshake"
                );
                using var earlyCertificate = tls.CopyClientCertificate();
                Check(earlyCertificate == null, "unvalidated identity is not published");
            }

            ClientCertificateValidationTests.Run();

            try
            {
                EmbedIOQuicTls.CreateFactory(source, new ServerTlsPolicy { EnabledProtocols = SslProtocols.Tls12 });
                throw new InvalidOperationException("TLS 1.2-only policy was ignored.");
            }

            catch (NotSupportedException) { }

            var failure = new InvalidOperationException("selector failure");
            var failingPolicy = new ServerTlsPolicy { ServerCertificateSelector = _ => throw failure };

            using (var tls = EmbedIOQuicTls.CreateFactory(source, failingPolicy)([]))
            {
                tls.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));

                try
                {
                    tls.Pump();
                    throw new InvalidOperationException("Failed selector admitted a handshake.");
                }

                catch (InvalidDataException)
                {
                    Check(tls.AlertCode == 80, "selector failure rejects the handshake");
                }
            }

            var critical = new OutOfMemoryException("critical selector fixture");

            using (var tls = EmbedIOQuicTls.CreateFactory(
                source,
                new ServerTlsPolicy { ServerCertificateSelector = _ => throw critical }
            )([]))
            {
                tls.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));

                try
                {
                    tls.Pump();
                    throw new InvalidOperationException("Critical selector error was swallowed.");
                }

                catch (OutOfMemoryException error) when (ReferenceEquals(error, critical)) { }
            }
        }

        private static void FirstFlight(SchannelTls tls)
        {
            tls.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));
            tls.Pump();
            Check(tls.WriteKeys[1] != null && tls.AlertCode == null, "native TLS first flight succeeds");
        }

        private static void Check(bool condition, string message) => QuicFuzzChecks.Require(condition, message);
    }
}
