#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EmbedIO.Net.Internal.Http3
{
    // The keys that protect the TLS 1.3 session tickets of one credential (RFC 8446 section
    // 4.6.1). The server draws them at random and keeps them in memory only. The newest key
    // encrypts new tickets until it is older than the rotation period. Every key decrypts until
    // the last ticket it encrypted has expired, so it goes once it is older than the period plus
    // the ticket lifetime. Each credential has its own keys, so a ticket resumes only under the
    // certificate object it was issued with.
    // OpenSSL refuses a ticket older than the lifetime. Schannel announces a lifetime of its own
    // and does not check ours, so on Windows a ticket resumes until its key goes: at least the
    // lifetime and at most the lifetime plus the period after it was issued.
    internal sealed class SessionTicketKeys : IDisposable
    {
        // A key is a 16-byte name and 64 bytes of secret. OpenSSL uses the secret's two halves
        // as the AES-256 and HMAC-SHA256 keys; Schannel takes all 64 bytes as keying material.
        internal const int NameLength = 16;
        internal const int KeyLength = NameLength + 64;

        // At most this many keys decrypt at once, which bounds the lifetime against the period.
        internal const int Capacity = 4;
        internal static readonly TimeSpan DefaultPeriod = TimeSpan.FromHours(1);
        internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(2);
        private readonly object _gate = new();
        private readonly Func<long> _clock;
        private readonly long _period;
        private readonly long _lifetime;

        // Newest first.
        private readonly List<Key> _keys = [];
        private long _version;
        private bool _disposed;

        internal SessionTicketKeys() : this(DefaultPeriod, DefaultLifetime) { }

        // clock returns milliseconds from a monotonic source; tests supply their own.
        internal SessionTicketKeys(TimeSpan period, TimeSpan lifetime, Func<long>? clock = null)
        {
            // A new key starts every period and each lives for the period plus the lifetime.
            if (
                period < TimeSpan.FromSeconds(1)
                || lifetime < TimeSpan.FromSeconds(1)
                || lifetime > TimeSpan.FromDays(7)
                || lifetime > (Capacity - 1) * period
            )
                throw new ArgumentOutOfRangeException(nameof(lifetime));

            _period = (long)period.TotalMilliseconds;
            _lifetime = (long)lifetime.TotalMilliseconds;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        internal TimeSpan Lifetime => TimeSpan.FromMilliseconds(_lifetime);

        // The keys a handshake uses now, newest first and KeyLength bytes each: the first
        // encrypts new tickets and all of them decrypt. The caller clears the copy. version
        // changes whenever the keys do.
        internal byte[] Copy(out long version)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _clock();

                while (_keys.Count != 0 && now - _keys[^1].Created >= _period + _lifetime) Retire(_keys.Count - 1);

                if (_keys.Count == 0 || now - _keys[0].Created >= _period)
                {
                    _keys.Insert(0, new Key(RandomNumberGenerator.GetBytes(KeyLength), now));
                    _version++;
                }

                var copy = new byte[_keys.Count * KeyLength];
                var offset = 0;

                foreach (var key in _keys)
                {
                    key.Bytes.CopyTo(copy, offset);
                    offset += KeyLength;
                }

                version = _version;
                return copy;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;

                while (_keys.Count != 0) Retire(_keys.Count - 1);
            }
        }

        private void Retire(int index)
        {
            CryptographicOperations.ZeroMemory(_keys[index].Bytes);
            _keys.RemoveAt(index);
            _version++;
        }

        private sealed class Key(byte[] bytes, long created)
        {
            internal byte[] Bytes { get; } = bytes;
            internal long Created { get; } = created;
        }
    }
}
#endif
