using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // A borrowed capability does not transfer the listener secret's lifetime.
    internal interface IQuicConnectionIdSource
    {
        byte[] Create();

        byte[] Token(ReadOnlySpan<byte> id);
    }

    // Fixed-length random IDs give unrelated connections independent 128-bit names.
    // One listener secret permits resets after a connection's state is retired.
    internal sealed class QuicConnectionIdIssuer : IQuicConnectionIdSource, IDisposable
    {
        internal const int Length = 16;
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private bool _disposed;

        public byte[] Create()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RandomNumberGenerator.GetBytes(Length);
        }

        public byte[] Token(ReadOnlySpan<byte> id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (id.Length != Length) throw new ArgumentException("Unexpected local connection-ID length.", nameof(id));

            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(_key, id, digest);
            var token = digest[..16].ToArray();
            CryptographicOperations.ZeroMemory(digest);
            return token;
        }

        internal static bool CanReset(ReadOnlySpan<byte> packet) =>
            packet.Length >= 1 + Length + 20 && packet.Length <= 65535 && (packet[0] & 0xc0) == 0x40;

        internal byte[]? Reset(ReadOnlySpan<byte> packet)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A protected short packet needs the CID and a 20-byte sample suffix.
            // Smaller responses terminate reset loops without retaining peer history.
            if (!CanReset(packet)) return null;

            var reset = RandomNumberGenerator.GetBytes(Math.Min(43, packet.Length - 1));
            reset[0] = (byte)((reset[0] & 0x3f) | 0x40);
            var token = Token(packet.Slice(1, Length));
            token.CopyTo(reset.AsSpan(reset.Length - token.Length));
            CryptographicOperations.ZeroMemory(token);
            return reset;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    // RFC 9000 5.1.1/19.16: keep active IDs, not a history of retired sequences.
    // Delivery transmission counts distinguish announced IDs from queued proposals.
    internal sealed class LocalConnectionIds : IDisposable
    {
        internal const int ActiveLimit = 2;

        private sealed record Issued(ulong Sequence, byte[] Id, byte[] Token, Delivery? Announcement);

        private readonly IQuicConnectionIdSource _issuer;
        private readonly Action<Delivery> _announce;
        private readonly List<Issued> _active;
        private IReadOnlyList<ReadOnlyMemory<byte>>? _snapshot;
        private ulong _nextSequence = 1;
        private ulong _highestSent;
        private bool _activated;
        private bool _disposed;

        internal LocalConnectionIds(IQuicConnectionIdSource issuer, byte[] initial, Action<Delivery> announce)
        {
            _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            _announce = announce ?? throw new ArgumentNullException(nameof(announce));
            _active = [First(issuer, initial)];
        }

        internal int Count => _active.Count;
        internal ulong NextSequence => _nextSequence;
        internal ReadOnlySpan<byte> Current => _active[0].Id;
        internal ReadOnlySpan<byte> InitialToken => _active[0].Token;

        internal IReadOnlyList<ReadOnlyMemory<byte>> Snapshot
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _snapshot ??= [.. _active.Select(static entry => (ReadOnlyMemory<byte>)entry.Id)];
            }
        }

        private static Issued First(IQuicConnectionIdSource issuer, byte[] initial)
        {
            ArgumentNullException.ThrowIfNull(issuer);
            ArgumentNullException.ThrowIfNull(initial);

            if (initial.Length != QuicConnectionIdIssuer.Length)
                throw new ArgumentException("Unexpected initial connection-ID length.", nameof(initial));

            byte[] copy = [.. initial];
            return new Issued(0, copy, issuer.Token(copy), null);
        }

        internal bool Contains(ReadOnlySpan<byte> id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var entry in _active)
                if (id.SequenceEqual(entry.Id)) return true;

            return false;
        }

        internal void Activate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activated = true;
            Refill();
        }

        internal void Retire(ulong sequence, ReadOnlySpan<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var entry in _active)
                if (entry.Announcement?.Transmissions > 0) _highestSent = Math.Max(_highestSent, entry.Sequence);

            if (sequence > _highestSent)
                throw new TransportException(0x0a, "Retirement refers to an unsent connection ID.");

            var index = _active.FindIndex(entry => entry.Sequence == sequence);

            if (index < 0) return;

            var retired = _active[index];

            if (destination.SequenceEqual(retired.Id))
                throw new TransportException(0x0a, "Retirement names the packet destination ID.");

            if (retired.Announcement is { } announcement) announcement.Acknowledged = true;

            CryptographicOperations.ZeroMemory(retired.Token);
            _active.RemoveAt(index);
            _snapshot = null;

            if (_activated) Refill();
        }

        private void Refill()
        {
            while (_active.Count < ActiveLimit)
            {
                if (_nextSequence > PacketNumberWindow.Maximum)
                    throw new TransportException(9, "Local connection-ID sequence exhausted.");

                var id = _issuer.Create();

                if (Contains(id)) throw new InvalidOperationException("Local connection-ID collision.");

                var token = _issuer.Token(id);
                using var frame = new MemoryStream();
                frame.Write(Wire.Frame(0x18, _nextSequence, 0));
                frame.WriteByte((byte)id.Length);
                frame.Write(id);
                frame.Write(token);
                var delivery = new Delivery(2, frame.ToArray());
                _active.Add(new Issued(_nextSequence++, id, token, delivery));
                _snapshot = null;
                _announce(delivery);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            foreach (var entry in _active)
            {
                CryptographicOperations.ZeroMemory(entry.Token);

                if (entry.Announcement is { } announcement) announcement.Acknowledged = true;
            }

            _active.Clear();
            _snapshot = null;
        }
    }
}
