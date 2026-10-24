#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace EmbedIO.Net.Internal.Http3
{
    // A native TLS credential that handshakes share. Each holder releases its reference
    // once; the last release frees the native resources.
    internal abstract class SharedCredential
    {
        private int _references = 1;

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
    // Entries go when the certificate source publishes a replacement, when more than
    // Capacity other certificates were selected since, and when the listener stops. A
    // handshake keeps the credential it holds until it ends.
    internal sealed class ServerCredentialCache<TCredential> : IDisposable where TCredential : SharedCredential
    {
        internal const int Capacity = 32;
        private readonly object _gate = new();
        private readonly ServerCertificateSource _source;
        private readonly Func<X509Certificate2, TCredential> _prepare;

        // Most recently used first. A listener selects few certificates, so a scan is enough.
        private readonly List<(X509Certificate2 Certificate, TCredential Credential)> _entries = [];
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
        // replaced certificate does not return to the cache.
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

                    if (version != _version) return prepared;

                    var index = Find(certificate);

                    if (index >= 0)
                    {
                        (released ??= []).Add(prepared);
                        return Use(index);
                    }

                    prepared.Retain();
                    _entries.Insert(0, (certificate, prepared));

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

        // Drops every entry once the source has published since they were prepared.
        private List<TCredential>? Refresh()
        {
            var current = _source.Version;

            if (current == _version) return null;

            _version = current;
            return Drain();
        }

        private List<TCredential>? Drain()
        {
            if (_entries.Count == 0) return null;

            var drained = new List<TCredential>(_entries.Count);

            foreach (var (_, credential) in _entries) drained.Add(credential);

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
    }
}
#endif
