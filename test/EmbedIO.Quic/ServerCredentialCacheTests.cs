using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    // The listener's credential cache with counted stand-in credentials: sharing by certificate
    // object, publication, handshakes that started before one, capacity, disposal and
    // concurrent leases. SchannelTests and the portable TLS checks cover the native credentials.
    internal static class ServerCredentialCacheTests
    {
        internal static void Run()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=credential cache fixture", key, HashAlgorithmName.SHA256);
            using var first = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            using var second = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            Sharing(first, second);
            Publication(first, second);
            Capacity(first);
            Disposal(first);
            Concurrency(first, second);
        }

        private static void Sharing(X509Certificate2 first, X509Certificate2 second)
        {
            var source = new ServerCertificateSource(first);
            var ledger = new Ledger();

            using (var cache = new ServerCredentialCache<Counted>(source, ledger.Prepare))
            {
                var version = source.Version;
                var one = cache.Lease(first, version);
                var two = cache.Lease(first, version);
                using var sameBytes = X509CertificateLoader.LoadCertificate(first.RawData);
                var other = cache.Lease(sameBytes, version);
                var third = cache.Lease(second, version);
                Check(
                    ReferenceEquals(one, two) && !ReferenceEquals(one, other) && ledger.Prepared == 3 && cache.Count == 3,
                    "one credential per selected certificate object"
                );

                foreach (var credential in new[] { one, two, other, third }) credential.Release();

                Check(ledger.Live == 3 && ledger.Freed == 0, "the cache keeps credentials between handshakes");
                Check(ReferenceEquals(cache.Lease(first, version), one) && ledger.Prepared == 3, "a later handshake reuses it");
                one.Release();
            }

            Check(ledger.Live == 0 && ledger.Freed == 3 && ledger.DoubleFrees == 0, "disposal frees each credential once");
        }

        private static void Publication(X509Certificate2 first, X509Certificate2 second)
        {
            var source = new ServerCertificateSource(first);
            var ledger = new Ledger();
            using var cache = new ServerCredentialCache<Counted>(source, ledger.Prepare);
            var before = source.Version;
            var held = cache.Lease(first, before);
            source.Publish(second);
            var after = source.Version;
            var current = cache.Lease(second, after);
            Check(
                cache.Count == 1 && ledger.Live == 2 && held.Frees == 0,
                "a publication drops the cache's entries but not a handshake's reference"
            );
            held.Release();
            Check(held.Frees == 1 && ledger.Live == 1, "the last holder of a dropped credential frees it");

            // A handshake that captured the replaced certificate before the publication gets a
            // credential of its own, freed when it ends; the cache does not take it back.
            var stale = cache.Lease(first, before);
            Check(cache.Count == 1 && ledger.Live == 2, "an older handshake's credential stays out of the cache");
            stale.Release();
            Check(stale.Frees == 1 && ledger.Live == 1, "an older handshake's credential ends with it");
            Check(ReferenceEquals(cache.Lease(second, after), current), "current entries survive older handshakes");
            current.Release();
            current.Release();
            Check(current.Frees == 0 && cache.Count == 1, "the cache still holds the current credential");
        }

        private static void Capacity(X509Certificate2 certificate)
        {
            var source = new ServerCertificateSource(certificate);
            var ledger = new Ledger();
            var version = source.Version;
            var keys = new List<X509Certificate2>();

            try
            {
                using var cache = new ServerCredentialCache<Counted>(source, ledger.Prepare);

                for (var index = 0; index <= ServerCredentialCache<Counted>.Capacity; index++)
                    keys.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));

                var oldest = cache.Lease(keys[0], version);
                oldest.Release();
                var recent = cache.Lease(keys[1], version);
                recent.Release();
                // Using the oldest again makes the second the least recently used.
                cache.Lease(keys[0], version).Release();

                foreach (var key in keys.Skip(2)) cache.Lease(key, version).Release();

                Check(
                    cache.Count == ServerCredentialCache<Counted>.Capacity && recent.Frees == 1 && oldest.Frees == 0,
                    "the least recently used credential leaves a full cache"
                );
                Check(
                    ledger.Live == ServerCredentialCache<Counted>.Capacity,
                    "eviction frees the evicted credential"
                );
            }

            finally
            {
                foreach (var key in keys) key.Dispose();
            }

            Check(ledger.Live == 0 && ledger.DoubleFrees == 0, "eviction and disposal free each credential once");
        }

        private static void Disposal(X509Certificate2 certificate)
        {
            var source = new ServerCertificateSource(certificate);
            var ledger = new Ledger();
            var cache = new ServerCredentialCache<Counted>(source, ledger.Prepare);
            var held = cache.Lease(certificate, source.Version);
            cache.Dispose();
            cache.Dispose();
            Check(held.Frees == 0 && ledger.Live == 1, "a stopped listener's handshake keeps its credential");
            held.Release();
            Check(held.Frees == 1 && ledger.Live == 0, "the handshake frees it when it ends");

            try
            {
                cache.Lease(certificate, source.Version);
                throw new InvalidOperationException("A disposed cache leased a credential.");
            }

            catch (ObjectDisposedException)
            {
                Check(ledger.Prepared == 1, "a disposed cache prepares nothing");
            }

            try
            {
                held.Release();
                throw new InvalidOperationException("A credential was released twice.");
            }

            catch (InvalidOperationException error) when (error.Message == "The TLS credential was released twice.")
            {
                Check(held.Frees == 1, "a second release does not free again");
            }

            // Disposal while a lease prepares: the prepared credential is freed, not cached.
            var prepared = 0;
            ServerCredentialCache<Counted>? disposing = null;
            disposing = new ServerCredentialCache<Counted>(
                source,
                selected =>
                {
                    prepared++;
                    disposing?.Dispose();
                    return ledger.Prepare(selected);
                }
            );

            try
            {
                disposing.Lease(certificate, source.Version);
                throw new InvalidOperationException("A cache disposed during preparation leased a credential.");
            }

            catch (ObjectDisposedException)
            {
                Check(prepared == 1 && ledger.Live == 0, "a credential prepared during disposal is freed");
            }

            var failing = new ServerCredentialCache<Counted>(
                source,
                _ => throw new CryptographicException("preparation failure")
            );

            try
            {
                failing.Lease(certificate, source.Version);
                throw new InvalidOperationException("A failed preparation was cached.");
            }

            catch (CryptographicException)
            {
                Check(failing.Count == 0, "a failed preparation leaves nothing cached");
            }

            failing.Dispose();
        }

        private static void Concurrency(X509Certificate2 first, X509Certificate2 second)
        {
            var source = new ServerCertificateSource(first);
            var ledger = new Ledger();
            var certificates = new[] { first, second };

            using (var cache = new ServerCredentialCache<Counted>(source, ledger.Prepare))
            {
                Parallel.For(
                    0,
                    4000,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    index =>
                    {
                        if (index % 997 == 0) source.Publish(certificates[index % 2]);

                        var version = source.Version;
                        var selected = certificates[index % 2];
                        var credential = cache.Lease(selected, version);
                        Check(
                            ReferenceEquals(credential.Certificate, selected) && credential.Frees == 0,
                            "a lease returns a live credential"
                        );
                        credential.Release();
                    }
                );
                Check(cache.Count <= 2 && ledger.Live == cache.Count, "concurrent handshakes leave only cached credentials");
            }

            Check(ledger.Live == 0 && ledger.DoubleFrees == 0, "concurrent leases free each credential once");
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "credential-cache-" + name);

        private sealed class Ledger
        {
            internal int Prepared;
            internal int Live;
            internal int Freed;
            internal int DoubleFrees;

            internal Counted Prepare(X509Certificate2? certificate)
            {
                Interlocked.Increment(ref Prepared);
                Interlocked.Increment(ref Live);
                return new Counted(this, certificate);
            }
        }

        private sealed class Counted(Ledger ledger, X509Certificate2? certificate) : SharedCredential
        {
            internal int Frees;
            internal X509Certificate2? Certificate { get; } = certificate;

            protected override int Free()
            {
                if (Interlocked.Increment(ref Frees) != 1) Interlocked.Increment(ref ledger.DoubleFrees);

                Interlocked.Decrement(ref ledger.Live);
                Interlocked.Increment(ref ledger.Freed);
                return 0;
            }
        }
    }
}
