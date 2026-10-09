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
        private readonly Dictionary<long, Queue<long[]>> _sections = new();
        private readonly Dictionary<long, int> _references = new();
        private readonly byte[] _pending = new byte[10];
        private int _count;
        private int _sectionCount;
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
        internal bool TryRegisterSection(long streamId, long[] references)
        {
            if (streamId < 0 || streamId > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(streamId));
            if (references == null) throw new ArgumentNullException(nameof(references));
            lock (_sync)
            {
                EnsureUsable();
                if (references.Length == 0) return true;
                foreach (var index in references)
                    if (index < 0 || index >= _insertCount) throw new ArgumentOutOfRangeException(nameof(references));
                // No ownership mutation on admission failure. The caller can emit
                // a stateless section instead without waiting for the peer.
                if (_sectionCount == _maximumSections || references.Length > _maximumReferences - _referenceCount) return false;
                var owned = references.Distinct().ToArray();
                if (!_sections.TryGetValue(streamId, out var queue)) _sections.Add(streamId, queue = new Queue<long[]>());
                queue.Enqueue(owned);
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
                    _failed = true;
                    _sections.Clear(); _references.Clear();
                    _sectionCount = _referenceCount = _count = 0;
                    throw;
                }
            }
        }
        private void Apply(byte instruction, long value)
        {
            if ((instruction & 128) != 0)
            {
                if (!_sections.TryGetValue(value, out var queue) || queue.Count == 0) throw Invalid();
                var section = queue.Dequeue();
                _known = Math.Max(_known, section.Max() + 1);
                Release(section);
                if (queue.Count == 0) _sections.Remove(value);
            }
            else if ((instruction & 64) != 0)
            {
                // Cancellation is permitted even for streams with no references.
                if (_sections.TryGetValue(value, out var queue))
                {
                    foreach (var section in queue) Release(section);
                    _sections.Remove(value);
                }
            }
            else
            {
                if (value == 0 || value > _insertCount - _known) throw Invalid();
                _known += value;
            }
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
