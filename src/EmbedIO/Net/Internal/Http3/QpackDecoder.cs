using System;
using System.Collections.Generic;
using System.IO;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // One coordinator per incoming connection direction. The connection owns the
    // single decoder-stream writer and drains feedback in order. A stream reader
    // stops after Submit returns null; it must not release blocked payload credit
    // or submit its next field section until resumed or canceled.
    internal sealed class QpackDecoder : IDisposable
    {
        private readonly object _sync = new();
        private readonly QpackDecoderTable _table;
        private readonly Dictionary<long, Pending> _pending = new();
        private readonly int _maximumCapacity;
        private readonly int _maximumBlockedStreams;
        private readonly int _maximumEncodedBytes;
        private readonly int _maximumDecodedBytes;
        private readonly long _maximumBlockedBytes;
        private readonly int _maximumFeedbackBytes;
        private readonly MemoryStream _feedback;
        private long _blockedBytes;
        private long _knownReceivedCount;
        private long _failure;
        private bool _disposed;

        internal QpackDecoder(int maximumCapacity, int maximumBlockedStreams, int maximumEncodedBytes,
            int maximumDecodedBytes, long maximumBlockedBytes, int maximumFeedbackBytes)
        {
            if (maximumBlockedStreams < 0) throw new ArgumentOutOfRangeException(nameof(maximumBlockedStreams));
            if (maximumEncodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
            if (maximumDecodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
            if (maximumBlockedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBlockedBytes));
            if (maximumFeedbackBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumFeedbackBytes));
            _table = new QpackDecoderTable(maximumCapacity);
            _maximumCapacity = maximumCapacity;
            _maximumBlockedStreams = maximumBlockedStreams;
            _maximumEncodedBytes = maximumEncodedBytes;
            _maximumDecodedBytes = maximumDecodedBytes;
            _maximumBlockedBytes = maximumBlockedBytes;
            _maximumFeedbackBytes = maximumFeedbackBytes;
            _feedback = new MemoryStream(Math.Min(maximumFeedbackBytes, 128));
        }
        public int BlockedStreams { get { lock (_sync) return _pending.Count; } }
        public long BlockedBytes { get { lock (_sync) return _blockedBytes; } }
        public long KnownReceivedCount { get { lock (_sync) return _knownReceivedCount; } }

        internal HpackField[]? Submit(long streamId, byte[] wire)
        {
            ValidateStream(streamId);
            if (wire == null) throw new ArgumentNullException(nameof(wire));
            lock (_sync)
            {
                CheckUsable();
                if (_pending.ContainsKey(streamId)) throw new InvalidOperationException("Resume the previous field section before reading another on this stream.");
                try
                {
                    var section = _table.ReadSection(wire, _maximumEncodedBytes, _maximumDecodedBytes);
                    var fields = section.TryDecode();
                    if (fields != null)
                    {
                        Acknowledge(streamId, section.RequiredInsertCount);
                        return fields;
                    }
                    if (_pending.Count >= _maximumBlockedStreams) throw new Http3ProtocolException(0x200, "Peer exceeded advertised QPACK blocked-stream count.");
                    if (wire.Length > _maximumBlockedBytes - _blockedBytes) throw Excessive();
                    _pending.Add(streamId, new Pending(section, wire.Length));
                    _blockedBytes += wire.Length;
                    return null;
                }
                catch (Http3ProtocolException error) { Fail(error.ErrorCode); throw; }
            }
        }

        internal QpackDecodedSection[] FeedEncoder(byte[] bytes, int offset, int count)
        {
            lock (_sync)
            {
                CheckUsable();
                try
                {
                    _table.Feed(bytes, offset, count);
                    var completed = new List<QpackDecodedSection>();
                    foreach (var item in _pending)
                    {
                        var fields = item.Value.Section.TryDecode();
                        if (fields == null) continue;
                        Acknowledge(item.Key, item.Value.Section.RequiredInsertCount);
                        completed.Add(new QpackDecodedSection(item.Key, fields));
                    }
                    foreach (var item in completed)
                    {
                        _blockedBytes -= _pending[item.StreamId].Length;
                        _pending.Remove(item.StreamId);
                    }
                    var inserts = _table.InsertCount;
                    if (inserts > _knownReceivedCount)
                    {
                        Instruction(inserts - _knownReceivedCount, 6, 0);
                        _knownReceivedCount = inserts;
                    }
                    return completed.ToArray();
                }
                catch (Http3ProtocolException error) { Fail(error.ErrorCode); throw; }
            }
        }

        // The stream owner prevents future submissions after reset/abandonment.
        // Closed stream IDs are not retained here, keeping bookkeeping bounded.
        internal void Cancel(long streamId)
        {
            ValidateStream(streamId);
            lock (_sync)
            {
                CheckUsable();
                try
                {
                    if (_pending.TryGetValue(streamId, out var pending))
                    {
                        _blockedBytes -= pending.Length;
                        _pending.Remove(streamId);
                    }
                    if (_maximumCapacity != 0) Instruction(streamId, 6, 64);
                }
                catch (Http3ProtocolException error) { Fail(error.ErrorCode); throw; }
            }
        }

        internal byte[] DrainFeedback()
        {
            lock (_sync)
            {
                CheckUsable();
                if (_feedback.Length == 0) return Array.Empty<byte>();
                var result = _feedback.ToArray();
                _feedback.SetLength(0);
                _feedback.Position = 0;
                return result;
            }
        }
        internal void EncoderClosed()
        {
            lock (_sync)
            {
                CheckUsable();
                Fail(0x104);
                throw new Http3ProtocolException(0x104, "QPACK encoder critical stream closed.");
            }
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                Fail(0x200);
                _feedback.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
        internal void Abort()
        {
            lock (_sync) Fail(0x200);
        }
        private void Acknowledge(long streamId, long required)
        {
            if (required == 0) return;
            Instruction(streamId, 7, 128);
            _knownReceivedCount = Math.Max(_knownReceivedCount, required);
        }
        private void Instruction(long value, int bits, byte flags)
        {
            var mask = (1 << bits) - 1;
            var length = 1;
            if (value >= mask)
            {
                length++;
                for (var remainder = value - mask; remainder >= 128; remainder >>= 7) length++;
            }
            if (length > _maximumFeedbackBytes - _feedback.Length) throw Excessive();
            var needed = (int)_feedback.Length + length;
            if (needed > _feedback.Capacity)
                _feedback.Capacity = (int)Math.Min(_maximumFeedbackBytes, Math.Max(needed, _feedback.Capacity * 2L));
            QpackInteger.Write(_feedback, value, bits, flags);
        }
        private static void ValidateStream(long streamId)
        { if (streamId < 0 || streamId > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(streamId)); }
        private static Http3ProtocolException Excessive() => new(0x107, "QPACK connection buffer budget exceeded.");
        private void CheckUsable()
        { if (_failure != 0) throw new Http3ProtocolException(_failure, "QPACK connection state is no longer usable."); }
        private void Fail(long code)
        {
            if (_disposed) return;
            if (_failure == 0) _failure = code;
            _pending.Clear();
            _blockedBytes = 0;
            _feedback.SetLength(0);
            _feedback.Position = 0;
            _table.Abort();
        }
        private sealed class Pending
        {
            internal Pending(QpackFieldSection section, int length) { Section = section; Length = length; }
            internal QpackFieldSection Section { get; }
            internal int Length { get; }
        }
    }

    internal readonly struct QpackDecodedSection
    {
        internal QpackDecodedSection(long streamId, HpackField[] fields) { StreamId = streamId; Fields = fields; }
        public long StreamId { get; }
        public HpackField[] Fields { get; }
    }
}
