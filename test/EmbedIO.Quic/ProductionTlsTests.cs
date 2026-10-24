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

            using (var factory = EmbedIOQuicTls.CreateFactory(source, null))
            {
                using (var started = (SchannelTls)factory.Create([]))
                {
                    source.Publish(replacement.BorrowCertificate());
                    using var later = (SchannelTls)factory.Create([]);
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
                    Check(
                        SchannelTls.ActiveCredentials == 2 && factory.CachedCredentials == 1,
                        "a handshake that started before a publication does not cache the replaced certificate"
                    );
                }

                Check(
                    SchannelTls.ActiveCredentials == 1 && factory.CachedCredentials == 1,
                    "the replaced certificate's credential ends with its handshake"
                );
            }

            Check(SchannelTls.ActiveCredentials == 0, "a disposed factory frees its credentials");
            source.Publish(first.BorrowCertificate());
            SharedCredentials(first, replacement);

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
                using var select = EmbedIOQuicTls.CreateFactory(source, policy);
                using var tls = (SchannelTls)select.Create([]);
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
                using var factory = EmbedIOQuicTls.CreateFactory(
                    source,
                    new ServerTlsPolicy { ClientCertificateMode = mode }
                );
                using var tls = factory.Create([]);
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
                EmbedIOQuicTls
                    .CreateFactory(source, new ServerTlsPolicy { EnabledProtocols = SslProtocols.Tls12 })
                    .Dispose();
                throw new InvalidOperationException("TLS 1.2-only policy was ignored.");
            }

            catch (NotSupportedException) { }

            var failure = new InvalidOperationException("selector failure");
            var failingPolicy = new ServerTlsPolicy { ServerCertificateSelector = _ => throw failure };

            using (var factory = EmbedIOQuicTls.CreateFactory(source, failingPolicy))
            using (var tls = factory.Create([]))
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

            using (var factory = EmbedIOQuicTls.CreateFactory(
                source,
                new ServerTlsPolicy { ServerCertificateSelector = _ => throw critical }
            ))
            using (var tls = factory.Create([]))
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

        // Handshakes that select one certificate object share its Schannel credential. A
        // publication or the factory's disposal drops it from the cache, and a handshake that
        // still holds it keeps it until it ends.
        private static void SharedCredentials(SchannelTestCertificate first, SchannelTestCertificate replacement)
        {
            var source = new ServerCertificateSource(first.BorrowCertificate());
            var selector = new ServerTlsPolicy
            {
                ServerCertificateSelector = name =>
                    StringComparer.OrdinalIgnoreCase.Equals(name, "selected.example")
                        ? replacement.BorrowCertificate()
                        : null
            };
            var selected = TlsHelloTests.NativeHello(
                [],
                "h3",
                (0, TlsHelloTests.ServerName(Encoding.ASCII.GetBytes("selected.example")))
            );
            var factory = EmbedIOQuicTls.CreateFactory(source, selector);
            SchannelTls? survivor = null;
            SchannelTls? pending = null;

            try
            {
                using (var one = (SchannelTls)factory.Create([]))
                using (var two = (SchannelTls)factory.Create([]))
                using (var named = (SchannelTls)factory.Create([]))
                using (var renamed = (SchannelTls)factory.Create([]))
                {
                    FirstFlight(one);
                    FirstFlight(two);
                    FirstFlight(named, selected);
                    FirstFlight(renamed, selected);
                    Check(
                        SchannelTls.ActiveCredentials == 2
                            && factory.CachedCredentials == 2
                            && named.SelectedCertificateSha256
                                == replacement
                                    .BorrowCertificate()
                                    .GetCertHashString(HashAlgorithmName.SHA256),
                        "handshakes that select one certificate share its credential"
                    );
                }

                Check(SchannelTls.ActiveCredentials == 2, "the listener keeps credentials between handshakes");
                survivor = (SchannelTls)factory.Create([]);
                FirstFlight(survivor);
                source.Publish(replacement.BorrowCertificate());

                using (var next = (SchannelTls)factory.Create([]))
                {
                    FirstFlight(next);
                    Check(
                        SchannelTls.ActiveCredentials == 2 && factory.CachedCredentials == 1,
                        "publication drops the cache's credentials while a handshake keeps its own"
                    );
                }

                survivor.Dispose();
                survivor = null;
                Check(SchannelTls.ActiveCredentials == 1, "the last handshake holding a dropped credential frees it");
                pending = (SchannelTls)factory.Create([]);
                FirstFlight(pending);
                factory.Dispose();
                Check(
                    SchannelTls.ActiveCredentials == 1 && factory.CachedCredentials == 0,
                    "a handshake keeps its credential after the listener stops"
                );
                pending.Dispose();
                pending = null;
                Check(SchannelTls.ActiveCredentials == 0, "the last holder frees the credential once");

                using var late = factory.Create([]);
                late.Add(0, 0, TlsHelloTests.NativeHello([], "h3"));

                try
                {
                    late.Pump();
                    throw new InvalidOperationException("A stopped listener prepared a credential.");
                }

                catch (InvalidDataException)
                {
                    Check(
                        late.AlertCode == 80 && SchannelTls.ActiveCredentials == 0,
                        "a stopped listener refuses new handshakes"
                    );
                }
            }

            finally
            {
                survivor?.Dispose();
                pending?.Dispose();
                factory.Dispose();
            }
        }

        private static void FirstFlight(SchannelTls tls, byte[]? hello = null)
        {
            tls.Add(0, 0, hello ?? TlsHelloTests.NativeHello([], "h3"));
            tls.Pump();
            Check(tls.WriteKeys[1] != null && tls.AlertCode == null, "native TLS first flight succeeds");
        }

        private static void Check(bool condition, string message) => QuicFuzzChecks.Require(condition, message);
    }
}
