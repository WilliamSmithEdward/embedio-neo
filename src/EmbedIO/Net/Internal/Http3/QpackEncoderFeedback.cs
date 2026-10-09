using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbedIO.Net.Internal.Http3
{
    // Encoder-side decoder feedback. The table writer registers inserts and
    // dynamic sections before publishing bytes; acknowledged entries still need
    // a zero reference count before they can be evicted.
    internal sealed class QpackEncoderFeedback
    {
        private readonly object _sync = new();
        private readonly int _maximumSections;
        private readonly int _maximumReferences;
        private sealed class StreamSections
        {
            internal readonly Queue<(long[] References, long Required)> Pending = new();
            internal long Required;
        }
        private readonly Dictionary<long, StreamSections> _sections = new();
        private readonly Dictionary<long, int> _references = new();
        private readonly byte[] _pending = new byte[10];
        private int _count;
        private int _sectionCount;
        private int _blockedStreamCount;
        private int _referenceCount;
        private long _insertCount;
        private long _known;
        private bool _failed;

        internal QpackEncoderFeedback(int maximumSections, int maximumReferences)
        {
            if (maximumSections < 0) throw new ArgumentOutOfRangeException(nameof(maximumSections));
            if (maximumReferences < 0) throw new ArgumentOutOfRangeException(nameof(maximumReferences));
            _maximumSections = maximumSections;
            _maximumReferences = maximumReferences;
        }
        internal long KnownReceivedCount { get { lock (_sync) return _known; } }
        internal int PotentiallyBlockedStreams { get { lock (_sync) return _blockedStreamCount; } }
        internal int PendingSections { get { lock (_sync) return _sectionCount; } }
        internal bool IsReferenced(long index) { lock (_sync) return _references.ContainsKey(index); }
        internal long RegisterInsert()
        {
            lock (_sync)
            {
                EnsureUsable();
                if (_insertCount == QuicInteger.Maximum) throw new InvalidOperationException("QPACK insert count exhausted.");
                return _insertCount++;
            }
        }
        internal bool TryRegisterSection(long streamId, long[] references, long maximumBlockedStreams) =>
            TryRegisterSectionCore(streamId, references, maximumBlockedStreams, false);
        // Only a private encoder result may transfer ownership. Other callers
        // retain the defensive copy made by TryRegisterSection.
        internal bool TryRegisterOwnedSection(long streamId, long[] references, long maximumBlockedStreams) =>
            TryRegisterSectionCore(streamId, references, maximumBlockedStreams, true);
        private bool TryRegisterSectionCore(long streamId, long[] references, long maximumBlockedStreams, bool ownedReferences)
        {
            if (streamId < 0 || streamId > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(streamId));
            if (references == null) throw new ArgumentNullException(nameof(references));
            if (maximumBlockedStreams < 0 || maximumBlockedStreams > QuicInteger.Maximum)
                throw new ArgumentOutOfRangeException(nameof(maximumBlockedStreams));
            lock (_sync)
            {
                EnsureUsable();
                if (references.Length == 0) return true;
                long required = 0;
                foreach (var index in references)
                {
                    if (index < 0 || index >= _insertCount) throw new ArgumentOutOfRangeException(nameof(references));
                    required = Math.Max(required, index + 1);
                }
                _sections.TryGetValue(streamId, out var stream);
                var newlyBlocked = required > _known && (stream == null || stream.Required <= _known);
                if (newlyBlocked && _blockedStreamCount >= maximumBlockedStreams) return false;
                // No ownership mutation on admission failure. The caller can emit
                // a stateless section instead without waiting for the peer.
                if (_sectionCount == _maximumSections || references.Length > _maximumReferences - _referenceCount) return false;
                var owned = ownedReferences ? references : references.Distinct().ToArray();
                if (stream == null) _sections.Add(streamId, stream = new StreamSections());
                stream.Pending.Enqueue((owned, required));
                stream.Required = Math.Max(stream.Required, required);
                if (newlyBlocked) ++_blockedStreamCount;
                ++_sectionCount;
                _referenceCount += owned.Length;
                foreach (var index in owned)
                {
                    _references.TryGetValue(index, out var count);
                    _references[index] = count + 1;
                }
                return true;
            }
        }
        internal void Feed(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                EnsureUsable();
                try
                {
                    for (var end = offset + count; offset < end; ++offset)
                    {
                        if (_count == _pending.Length) throw Invalid();
                        _pending[_count++] = bytes[offset];
                        long value;
                        var cursor = 0;
                        try { value = QpackInteger.Read(_pending, ref cursor, _count, (_pending[0] & 128) != 0 ? 7 : 6); }
                        catch (EndOfStreamException) { continue; }
                        catch (InvalidDataException) { throw Invalid(); }
                        Apply(_pending[0], value);
                        _count = 0;
                    }
                }
                catch (Http3ProtocolException)
                {
                    Abort();
                    throw;
                }
            }
        }
        internal void Abort()
        {
            lock (_sync)
            {
                _failed = true;
                _sections.Clear(); _references.Clear();
                _sectionCount = _referenceCount = _count = _blockedStreamCount = 0;
            }
        }
        private void Apply(byte instruction, long value)
        {
            if ((instruction & 128) != 0)
            {
                if (!_sections.TryGetValue(value, out var stream)) throw Invalid();
                var section = stream.Pending.Dequeue();
                if (stream.Required > _known) --_blockedStreamCount;
                stream.Required = 0;
                foreach (var pending in stream.Pending) stream.Required = Math.Max(stream.Required, pending.Required);
                if (stream.Required > _known) ++_blockedStreamCount;
                Release(section.References);
                if (stream.Pending.Count == 0) _sections.Remove(value);
                AdvanceKnown(section.Required);
            }
            else if ((instruction & 64) != 0)
            {
                // Cancellation is permitted even for streams with no references.
                if (_sections.TryGetValue(value, out var stream))
                {
                    if (stream.Required > _known) --_blockedStreamCount;
                    foreach (var section in stream.Pending) Release(section.References);
                    _sections.Remove(value);
                }
            }
            else
            {
                if (value == 0 || value > _insertCount - _known) throw Invalid();
                AdvanceKnown(_known + value);
            }
        }
        private void AdvanceKnown(long value)
        {
            if (value <= _known) return;
            // Only feedback advances the frontier. Admission uses a constant-time
            // counter; this bounded scan allocates no temporary collections.
            foreach (var stream in _sections.Values)
                if (stream.Required > _known && stream.Required <= value) --_blockedStreamCount;
            _known = value;
        }
        private void Release(long[] section)
        {
            --_sectionCount;
            _referenceCount -= section.Length;
            foreach (var index in section)
            {
                var count = _references[index] - 1;
                if (count == 0) _references.Remove(index);
                else _references[index] = count;
            }
        }
        private void EnsureUsable() { if (_failed) throw Invalid(); }
        private static Http3ProtocolException Invalid() => new(0x202, "Invalid QPACK decoder feedback.");
    }
}
