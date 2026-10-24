#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace EmbedIO.Net.Internal.Http3
{
    // A native TLS credential that handshakes share. Each holder releases its reference
    // once; the last release frees the native resources. A credential that is not cacheable
    // serves only the handshake that prepared it.
    internal abstract class SharedCredential(bool cacheable)
    {
        private int _references = 1;

        internal bool Cacheable { get; } = cacheable;

        // Whether the certificate stores complete the certificate's chain, without downloading
        // issuers. A credential prepared while they cannot may hold a partial chain, so it is not
        // cached: the next handshake prepares again, as every handshake did before.
        internal static bool HasCompleteChain(X509Certificate2 certificate)
        {
            // A self-issued certificate has no issuer to find. Skipping the build also spares
            // the process's first chain build, which costs more than ten milliseconds on Windows.
            if (
                certificate.SubjectName.RawData
                    .AsSpan()
                    .SequenceEqual(certificate.IssuerName.RawData)
            )
                return true;

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;

            try
            {
                _ = chain.Build(certificate);

                foreach (var status in chain.ChainStatus)
                    if ((status.Status & X509ChainStatusFlags.PartialChain) != 0) return false;

                return true;
            }

            finally
            {
                foreach (var element in chain.ChainElements)
                    if (!ReferenceEquals(element.Certificate, certificate)) element.Certificate.Dispose();
            }
        }

        // Adds a reference for another holder. The caller must already hold one.
        internal void Retain()
        {
            var count = Volatile.Read(ref _references);

            while (true)
            {
                if (count <= 0) throw new InvalidOperationException("The TLS credential was already freed.");

                var previous = Interlocked.CompareExchange(ref _references, count + 1, count);

                if (previous == count) return;

                count = previous;
            }
        }

        // The native status of the free, or zero while other holders remain.
        internal int Release()
        {
            var remaining = Interlocked.Decrement(ref _references);

            if (remaining < 0) throw new InvalidOperationException("The TLS credential was released twice.");

            return remaining == 0 ? Free() : 0;
        }

        protected abstract int Free();
    }

    // The credentials one HTTP/3 listener prepared for the certificates its handshakes
    // selected. A credential costs a Schannel credential handle on Windows and an exported,
    // parsed key and chain on Linux and macOS, so each certificate object pays for it once
    // and later handshakes that select the same object share it.
    // Entries go when the certificate source publishes a replacement, when the application
    // disposes the certificate, when more than Capacity other certificates were selected
    // since, and when the listener stops. A connection keeps the credential its handshake
    // took until the connection closes.
    internal sealed class ServerCredentialCache<TCredential> : IDisposable where TCredential : SharedCredential
    {
        internal const int Capacity = 32;
        private readonly object _gate = new();
        private readonly ServerCertificateSource _source;
        private readonly Func<X509Certificate2, TCredential> _prepare;

        // Most recently used first. A listener selects few certificates, so a scan is enough.
        // Entries are objects so the list uses the runtime's shared, precompiled code rather
        // than code compiled at the first handshake.
        private readonly List<Entry> _entries = [];
        private long _version;
        private bool _disposed;

        internal ServerCredentialCache(ServerCertificateSource source, Func<X509Certificate2, TCredential> prepare)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
            _version = source.Version;
        }

        internal int Count
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        // The credential for the certificate a handshake selected, with a reference for the
        // caller to release. version is the source's version when the handshake started; one
        // that started before the latest publication gets a credential of its own, so a
        // replaced certificate does not return to the cache. So does a handshake whose
        // credential is not cacheable.
        internal TCredential Lease(X509Certificate2 certificate, long version)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            List<TCredential>? released = null;

            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    released = Refresh();
                    var index = Find(certificate);

                    if (index >= 0) return Use(index);
                }
            }

            finally
            {
                Release(released);
            }

            // Preparation takes milliseconds; other handshakes keep using the cache meanwhile.
            // Two that select a new certificate at once both prepare it, and one result is kept.
            var prepared = _prepare(certificate);
            released = null;

            try
            {
                lock (_gate)
                {
                    released = Refresh();

                    if (_disposed) (released ??= []).Add(prepared);

                    ObjectDisposedException.ThrowIf(_disposed, this);

                    if (version != _version || !prepared.Cacheable) return prepared;

                    var index = Find(certificate);

                    if (index >= 0)
                    {
                        (released ??= []).Add(prepared);
                        return Use(index);
                    }

                    prepared.Retain();
                    _entries.Insert(0, new Entry(certificate, prepared));

                    while (_entries.Count > Capacity)
                    {
                        (released ??= []).Add(_entries[^1].Credential);
                        _entries.RemoveAt(_entries.Count - 1);
                    }

                    return prepared;
                }
            }

            finally
            {
                Release(released);
            }
        }

        public void Dispose()
        {
            List<TCredential>? released;

            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;
                released = Drain();
            }

            Release(released);
        }

        // Drops every entry once the source has published since they were prepared, and the
        // entry of a certificate the application has disposed since, which no handshake can
        // select again.
        private List<TCredential>? Refresh()
        {
            var current = _source.Version;

            if (current != _version)
            {
                _version = current;
                return Drain();
            }

            List<TCredential>? disposed = null;

            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];

                if (entry.Certificate.Handle != IntPtr.Zero) continue;

                (disposed ??= []).Add(entry.Credential);
                _entries.RemoveAt(index);
            }

            return disposed;
        }

        private List<TCredential>? Drain()
        {
            if (_entries.Count == 0) return null;

            var drained = new List<TCredential>(_entries.Count);

            foreach (var entry in _entries) drained.Add(entry.Credential);

            _entries.Clear();
            return drained;
        }

        private int Find(X509Certificate2 certificate)
        {
            for (var index = 0; index < _entries.Count; index++)
                if (ReferenceEquals(_entries[index].Certificate, certificate)) return index;

            return -1;
        }

        private TCredential Use(int index)
        {
            var entry = _entries[index];
            entry.Credential.Retain();

            if (index != 0)
            {
                _entries.RemoveAt(index);
                _entries.Insert(0, entry);
            }

            return entry.Credential;
        }

        // Native frees run outside the lock.
        private static void Release(List<TCredential>? credentials)
        {
            if (credentials == null) return;

            foreach (var credential in credentials) credential.Release();
        }

        private sealed class Entry(X509Certificate2 certificate, TCredential credential)
        {
            internal X509Certificate2 Certificate { get; } = certificate;
            internal TCredential Credential { get; } = credential;
        }
    }
}
#endif
