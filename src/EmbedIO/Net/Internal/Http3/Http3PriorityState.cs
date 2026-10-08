using System;
using System.Collections.Generic;
using System.Threading;

namespace EmbedIO.Net.Internal.Http3
{
    // Bounded advisory state. QUIC can deliver updates before request streams.
    // Active state is never evicted; pending and recently closed IDs are bounded.
    internal sealed class Http3PriorityState
    {
        internal sealed class Entry
        {
            private int _value = 3;
            internal Entry(long id) { Id = id; }
            internal long Id { get; }
            internal bool HasUpdate { get; set; }
            public HttpPriority Value
            {
                get { var value = Volatile.Read(ref _value); return new HttpPriority(value & 7, (value & 8) != 0); }
            }
            internal void Set(HttpPriority value) => Volatile.Write(ref _value, value.Urgency | (value.Incremental ? 8 : 0));
        }
        private readonly object _sync = new();
        private readonly int _capacity;
        private readonly Dictionary<long, Entry> _active = new();
        private readonly Dictionary<long, LinkedListNode<Entry>> _pending = new();
        private readonly LinkedList<Entry> _order = new();
        private readonly HashSet<long> _closed = new();
        private readonly Queue<long> _closedOrder = new();
        internal Http3PriorityState(int capacity)
        { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity)); _capacity = capacity; }
        public int ActiveCount { get { lock (_sync) return _active.Count; } }
        public int PendingCount { get { lock (_sync) return _pending.Count; } }
        public int ClosedCount { get { lock (_sync) return _closed.Count; } }
        internal Entry Open(long id)
        {
            lock (_sync)
            {
                if (_active.ContainsKey(id) || _closed.Contains(id)) throw new InvalidOperationException("Request priority state is already open or closed.");
                if (_active.Count == _capacity) throw new Http3ProtocolException(0x107, "Too many active request priorities.");
                Entry entry;
                if (_pending.TryGetValue(id, out var pending))
                { entry = pending.Value; _pending.Remove(id); _order.Remove(pending); }
                else entry = new Entry(id);
                _active.Add(id, entry);
                return entry;
            }
        }
        internal Entry Get(long id) { lock (_sync) return _active[id]; }
        internal void Headers(long id, string? field)
        {
            HttpPriority.TryParse(field ?? "", out var priority);
            lock (_sync)
            {
                if (_active.TryGetValue(id, out var entry) && !entry.HasUpdate) entry.Set(priority);
            }
        }
        internal void Update(long id, HttpPriority priority)
        {
            lock (_sync)
            {
                if (_closed.Contains(id)) return;
                if (_active.TryGetValue(id, out var active)) { active.HasUpdate = true; active.Set(priority); return; }
                if (_pending.TryGetValue(id, out var existing))
                { existing.Value.Set(priority); _order.Remove(existing); _order.AddLast(existing); return; }
                if (_pending.Count == _capacity)
                {
                    var oldest = _order.First ?? throw new InvalidOperationException("Missing pending priority owner.");
                    _pending.Remove(oldest.Value.Id); _order.RemoveFirst();
                }
                var entry = new Entry(id) { HasUpdate = true }; entry.Set(priority);
                _pending.Add(id, _order.AddLast(entry));
            }
        }
        internal void Close(long id)
        {
            lock (_sync)
            {
                if (!_active.Remove(id)) return;
                if (_closed.Count == _capacity) _closed.Remove(_closedOrder.Dequeue());
                _closed.Add(id); _closedOrder.Enqueue(id);
            }
        }
        internal void Clear()
        {
            lock (_sync) { _active.Clear(); _pending.Clear(); _order.Clear(); _closed.Clear(); _closedOrder.Clear(); }
        }
    }
}
