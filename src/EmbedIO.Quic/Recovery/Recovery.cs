using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Quic
{
    // Original RFC 9002 recovery logic. Time is supplied by the owner, in monotonic
    // milliseconds, so loss, PTO and congestion behavior can be tested without sleeps.
    internal sealed class Delivery
    {
        internal Delivery(int level, byte[] bytes, bool reliable = true, bool eliciting = true)
        {
            Level = level;
            Bytes = bytes;
            Reliable = reliable;
            Eliciting = eliciting;
        }

        internal int Level { get; }
        internal byte[] Bytes { get; }
        internal bool Reliable { get; }
        internal bool Eliciting { get; }
        internal bool PadToMinimum { get; init; }
        internal QuicPathRoute? Path { get; init; }
        internal bool PreferValidatedPath { get; init; }
        internal bool Acknowledged { get; set; }
        internal bool Queued { get; set; }
        internal bool ProbePending { get; set; }
        internal int Transmissions { get; set; }
    }

    internal sealed class Recovery
    {
        internal const int MaximumPacket = 1200;
        internal const int MaximumActivePackets = 4096;
        internal const int MaximumRetiredPackets = 4096;
        internal const int MaximumAcknowledgments = 12288;
        private const int MaximumBurst = 10 * MaximumPacket;
        private readonly int _acknowledgmentLimit;
        private readonly int _retainedAcknowledgments;
        private readonly int _retiredLimit;

        internal Recovery(
            Action<Delivery, bool> repeat,
            int acknowledgmentLimit = MaximumAcknowledgments,
            int retiredLimit = MaximumRetiredPackets
        )
        {
            _acknowledgmentLimit = Bound(acknowledgmentLimit, 8, MaximumAcknowledgments, nameof(acknowledgmentLimit));
            _retainedAcknowledgments = acknowledgmentLimit * 2 / 3;
            _retiredLimit = Bound(retiredLimit, 2, MaximumRetiredPackets, nameof(retiredLimit));
            _repeat = repeat ?? throw new ArgumentNullException(nameof(repeat));
        }

        // Where a sent packet's record is: outstanding (neither acknowledged nor declared lost),
        // retired after its loss so that a late acknowledgment still finds it, or released.
        private enum PacketState
        {
            Outstanding,
            Retired,
            Released
        }

        // Joined holds the frames that shared Delivery's packet; Eliciting covers them all.
        private sealed record Sent(
            int Level,
            ulong Number,
            double Time,
            int Size,
            Delivery Delivery,
            bool InFlight,
            bool ApplicationLimited,
            ulong Epoch,
            Delivery[]? Joined,
            bool Eliciting
        )
        {
            // Set by the list that holds the packet.
            internal PacketState State;

            internal void Acknowledge()
            {
                Delivery.Acknowledged = true;

                if (Joined == null) return;

                foreach (var delivery in Joined) delivery.Acknowledged = true;
            }
        }

        // One packet number space's packets in one state, in packet-number order, so the packets
        // an ACK range names are found by a binary search instead of a pass over every packet.
        // A packet that leaves the state keeps its place, skipped, until the front of the list
        // passes it or such places are half of the list.
        private sealed class PacketList(PacketState state)
        {
            private readonly List<Sent> _packets = [];
            private int _head;
            private int _stale;

            // The packets in the state, and how many of them are ack-eliciting.
            internal int Count { get; private set; }
            internal int Eliciting { get; private set; }

            // Indexes, from Start up to End, that may hold a packet in the state.
            internal int Start => _head;
            internal int End => _packets.Count;

            internal Sent this[int index] => _packets[index];

            internal bool Holds(Sent packet) => packet.State == state;

            internal void Add(Sent packet)
            {
                var index = _packets.Count;

                // Packet numbers rise within a space, so a packet nearly always goes last.
                if (index > _head && _packets[index - 1].Number >= packet.Number)
                {
                    index = Find(packet.Number);

                    if (index < _packets.Count && _packets[index].Number == packet.Number)
                    {
                        if (Holds(_packets[index]))
                            throw new ArgumentException("The packet number is already recorded.", nameof(packet));

                        _packets[index] = packet;
                        _stale--;
                        Hold(packet);
                        return;
                    }
                }

                _packets.Insert(index, packet);
                Hold(packet);
            }

            private void Hold(Sent packet)
            {
                packet.State = state;
                Count++;

                if (packet.Eliciting) Eliciting++;
            }

            // Moves a packet the list holds to the next state. No pass over the list may be
            // under way: the list can drop or move the places it skips.
            internal void Remove(Sent packet, PacketState next)
            {
                packet.State = next;
                Count--;

                if (packet.Eliciting) Eliciting--;

                _stale++;

                while (_head < _packets.Count && !Holds(_packets[_head]))
                {
                    _head++;
                    _stale--;
                }

                if (_head == _packets.Count)
                {
                    _packets.Clear();
                    _head = 0;
                }

                else if (_stale >= 32 && _stale * 2 > _packets.Count - _head) Compact();

                else if (_head >= 32 && _head * 2 >= _packets.Count)
                {
                    _packets.RemoveRange(0, _head);
                    _head = 0;
                }
            }

            private void Compact()
            {
                var kept = 0;

                for (var index = _head; index < _packets.Count; index++)
                    if (Holds(_packets[index])) _packets[kept++] = _packets[index];

                _packets.RemoveRange(kept, _packets.Count - kept);
                _head = 0;
                _stale = 0;
            }

            internal void Clear(PacketState next)
            {
                for (var index = _head; index < _packets.Count; index++)
                    if (Holds(_packets[index])) _packets[index].State = next;

                _packets.Clear();
                _head = 0;
                _stale = 0;
                Count = 0;
                Eliciting = 0;
            }

            // The first index from Start whose packet number is at least number, or End.
            internal int Find(ulong number)
            {
                var low = _head;
                var high = _packets.Count;

                while (low < high)
                {
                    var middle = low + (high - low) / 2;

                    if (_packets[middle].Number < number) low = middle + 1;

                    else high = middle;
                }

                return low;
            }
        }

        private readonly PacketList[] _sent =
        [
            new(PacketState.Outstanding),
            new(PacketState.Outstanding),
            new(PacketState.Outstanding)
        ];
        private readonly PacketList[] _retired =
        [
            new(PacketState.Retired),
            new(PacketState.Retired),
            new(PacketState.Retired)
        ];
        private readonly Queue<Sent> _retiredOrder = new();
        private double _historyFloor = double.NegativeInfinity;
        private readonly ulong?[] _largestAcknowledged = new ulong?[3];
        private readonly bool[] _discarded = new bool[3];
        private readonly double[] _lastEliciting = new double[3];
        private readonly Action<Delivery, bool> _repeat;
        private double _minimumRtt = double.PositiveInfinity;
        private double _latestRtt = 333;
        private double _smoothedRtt = 333;
        private double _variation = 166.5;
        private double _recoveryStart = -1;
        private double _firstSample = double.PositiveInfinity;
        private double _slowStartThreshold = double.PositiveInfinity;
        private double _pacingCredit = MaximumBurst;
        private double _pacingTime;
        private int _ptoCount;
        private ulong _epoch;
        private double _lastTimeout = -1;
        private readonly List<Sent> _lostHistory = [];
        private readonly List<double> _acknowledgedTimes = [];

        internal long BytesInFlight { get; private set; }
        internal double CongestionWindow { get; private set; } = MaximumBurst;
        internal int LostPackets { get; private set; }
        internal int RetransmittedPackets { get; private set; }
        internal int ProbeTimeouts { get; private set; }
        internal int CongestionEvents { get; private set; }
        internal double SmoothedRtt => _smoothedRtt;
        internal double KeyRetention => 3 * (_smoothedRtt + Math.Max(4 * _variation, 1) + MaximumAckDelay);
        internal double MaximumAckDelay { get; set; } = 25;
        internal bool HandshakeConfirmed { get; set; }

        private static int Bound(int value, int minimum, int maximum, string parameter)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, minimum, parameter);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum, parameter);
            return value;
        }

        internal int ActivePacketCount => _sent[0].Count + _sent[1].Count + _sent[2].Count;
        internal int RetiredPacketCount => _retired[0].Count + _retired[1].Count + _retired[2].Count;
        internal int AcknowledgmentCount => _acknowledgedTimes.Count;
        internal int LossHistoryCount => _lostHistory.Count;

        internal bool IsDiscarded(int level) => _discarded[level];

        internal bool CanSend(int size, double now, bool probe)
        {
            if (probe) return true;

            RefillPacing(now);
            return BytesInFlight + size <= CongestionWindow && size <= _pacingCredit;
        }

        private void RefillPacing(double now)
        {
            // RFC 9002 7.7 permits bounded bursts to absorb scheduler delay.
            // Idle time earns at most the initial window; congestion still gates
            // every ordinary packet independently of this pacing budget.
            var elapsed = Math.Max(0, now - _pacingTime);
            _pacingTime = Math.Max(_pacingTime, now);
            _pacingCredit = Math.Min(
                Math.Min(CongestionWindow, MaximumBurst),
                _pacingCredit + elapsed * 1.25 * CongestionWindow / Math.Max(_smoothedRtt, 1)
            );
        }

        // joined lists any further frames sent in the same packet as delivery.
        internal void SentPacket(
            int level,
            ulong number,
            int size,
            double now,
            Delivery delivery,
            bool padded,
            bool applicationLimited = false,
            Delivery[]? joined = null
        )
        {
            var retransmission = delivery.Transmissions++ != 0 && delivery.Reliable;
            var eliciting = delivery.Eliciting;

            if (joined != null)
                foreach (var other in joined)
                {
                    retransmission |= other.Transmissions++ != 0 && other.Reliable;
                    eliciting |= other.Eliciting;
                }

            if (retransmission) RetransmittedPackets++;

            var inFlight = eliciting || padded;

            if (ActivePacketCount >= MaximumActivePackets)
                throw new InvalidDataException("Recovery packet budget exceeded.");

            _sent[level].Add(
                new Sent(level, number, now, size, delivery, inFlight, applicationLimited, _epoch, joined, eliciting)
            );

            if (inFlight) BytesInFlight += size;

            if (eliciting) _lastEliciting[level] = now;

            if (inFlight)
            {
                RefillPacing(now);
                _pacingCredit = Math.Max(0, _pacingCredit - size);
            }
        }

        internal void Acknowledge(int level, IReadOnlyList<(ulong Low, ulong High)> ranges, double delay, double now)
        {
            RefillPacing(now);
            var largest = ranges[0].High;
            var matched = _matched;
            var elicitingLate = false;
            var retired = _retired[level];

            if (retired.Count != 0)
            {
                Match(retired, ranges, matched);

                foreach (var packet in matched)
                {
                    retired.Remove(packet, PacketState.Released);
                    packet.Acknowledge();

                    if (packet.Epoch == _epoch)
                    {
                        _acknowledgedTimes.Add(packet.Time);
                        elicitingLate |= packet.Eliciting;
                    }

                    ForgetLoss(packet);
                }

                matched.Clear();
            }

            Match(_sent[level], ranges, matched);

            try
            {
                Acknowledge(matched, level, largest, delay, now, elicitingLate);
            }

            finally
            {
                matched.Clear();
            }
        }

        // Packets an ACK names, from Acknowledge.
        private readonly List<Sent> _matched = [];

        // Collects the packets of list that ranges name, in packet-number order.
        private static void Match(PacketList list, IReadOnlyList<(ulong Low, ulong High)> ranges, List<Sent> matched)
        {
            var ordered = true;

            // An ACK frame lists its ranges largest first, so taking them from the last keeps the
            // packets in order. Ranges given in any other order are sorted afterwards.
            for (
                var range = ranges.Count - 1;
                range >= 0;
                range--
            )
            {
                var (low, high) = ranges[range];

                for (var index = list.Find(low); index < list.End; index++)
                {
                    var packet = list[index];

                    if (packet.Number > high) break;

                    if (!list.Holds(packet)) continue;

                    if (matched.Count != 0 && matched[^1].Number >= packet.Number) ordered = false;

                    matched.Add(packet);
                }
            }

            if (ordered) return;

            matched.Sort(static (left, right) => left.Number.CompareTo(right.Number));
            var kept = 0;

            // Overlapping ranges name some packets twice.
            for (
                var index = 0;
                index < matched.Count;
                index++
            )
                if (kept == 0 || matched[kept - 1].Number != matched[index].Number) matched[kept++] = matched[index];

            matched.RemoveRange(kept, matched.Count - kept);
        }

        private void ForgetLoss(Sent packet)
        {
            for (var index = 0; index < _lostHistory.Count; index++)
                if (_lostHistory[index].Level == packet.Level && _lostHistory[index].Number == packet.Number)
                    _lostHistory.RemoveAt(index--);
        }

        // Drops the loss history of packets sent before time, or also at it when inclusive.
        private void ForgetLosses(double time, bool inclusive)
        {
            var kept = 0;

            for (var index = 0; index < _lostHistory.Count; index++)
            {
                var packet = _lostHistory[index];

                if (inclusive ? packet.Time > time : packet.Time >= time) _lostHistory[kept++] = packet;
            }

            _lostHistory.RemoveRange(kept, _lostHistory.Count - kept);
        }

        private void Acknowledge(
            List<Sent> acknowledged,
            int level,
            ulong largest,
            double delay,
            double now,
            bool elicitingLate
        )
        {
            _largestAcknowledged[level] = Math.Max(_largestAcknowledged[level] ?? 0, largest);
            Sent? sample = null;
            var eliciting = false;

            foreach (var packet in acknowledged)
            {
                if (packet.Epoch != _epoch) continue;

                if (sample == null && packet.Number == largest) sample = packet;

                eliciting |= packet.Eliciting;
            }

            if (sample != null && eliciting)
            {
                var elapsed = Math.Max(now - sample.Time, 0.001);
                _latestRtt = elapsed;
                _minimumRtt = Math.Min(_minimumRtt, elapsed);

                if (double.IsPositiveInfinity(_firstSample))
                {
                    _smoothedRtt = elapsed;
                    _variation = elapsed / 2;
                    _firstSample = now;
                }

                else
                {
                    if (level == 2 && HandshakeConfirmed)
                        elapsed = Math.Max(_minimumRtt, elapsed - Math.Min(delay, MaximumAckDelay));

                    _variation = 0.75 * _variation + 0.25 * Math.Abs(_smoothedRtt - elapsed);
                    _smoothedRtt = 0.875 * _smoothedRtt + 0.125 * elapsed;
                }
            }

            var sent = _sent[level];

            foreach (var packet in acknowledged)
            {
                sent.Remove(packet, PacketState.Released);

                if (packet.InFlight && packet.Epoch == _epoch) BytesInFlight -= packet.Size;

                packet.Acknowledge();

                // An outstanding packet was never declared lost, so no loss history names it.
                if (packet.Epoch == _epoch) _acknowledgedTimes.Add(packet.Time);

                if (
                    packet.Epoch == _epoch
                    && packet.Time > _recoveryStart
                    && packet.InFlight
                    && !packet.ApplicationLimited
                )
                    CongestionWindow += CongestionWindow < _slowStartThreshold
                        ? packet.Size
                        : MaximumPacket * (double)packet.Size / CongestionWindow;
            }

            if (eliciting || elicitingLate)
            {
                _ptoCount = 0;
                _lastTimeout = -1;
            }

            if (_acknowledgedTimes.Count > _acknowledgmentLimit)
            {
                _acknowledgedTimes.Sort();
                var remove = _acknowledgedTimes.Count - _retainedAcknowledgments;
                // An acknowledged packet breaks every persistent-loss interval
                // crossing this time. Retire both sides of that known barrier.
                _historyFloor = Math.Max(_historyFloor, _acknowledgedTimes[remove - 1]);
                _acknowledgedTimes.RemoveRange(0, remove);
                ForgetLosses(_historyFloor, true);
            }

            DetectLost(now);
        }

        private double LossDelay => Math.Max(1, 1.125 * Math.Max(_latestRtt, _smoothedRtt));

        private double Pto(int level) =>
            (_smoothedRtt + Math.Max(4 * _variation, 1) + (level == 2 && HandshakeConfirmed ? MaximumAckDelay : 0))
                * Math.Pow(2, Math.Min(_ptoCount, 16));

        internal void Tick(double now)
        {
            RefillPacing(now);

            if (DetectLost(now)) return;

            // The packet number space whose probe timeout comes first, among those with packets
            // that can be probed; ties go to the earlier space, as in RFC 9002's
            // GetPtoTimeAndSpace.
            var level = -1;
            var earliest = double.PositiveInfinity;

            for (var space = 0; space < 3; space++)
            {
                if (_sent[space].Eliciting == 0 || _discarded[space] || (space == 2 && !HandshakeConfirmed)) continue;

                var timeout = _lastEliciting[space] + Pto(space);

                if (level < 0 || timeout < earliest)
                {
                    level = space;
                    earliest = timeout;
                }
            }

            if (level < 0) return;

            var deadline = Math.Max(_lastEliciting[level], _lastTimeout) + Pto(level);

            if (now < deadline) return;

            _lastTimeout = now;
            _ptoCount++;
            ProbeTimeouts++;
            // The probe repeats the oldest two unacknowledged reliable frames of the space.
            Delivery? first = null;
            Delivery? second = null;
            var sent = _sent[level];

            for (var index = sent.Start; index < sent.End && second == null; index++)
            {
                var packet = sent[index];

                if (!sent.Holds(packet) || !packet.Eliciting) continue;

                Probe(packet.Delivery, ref first, ref second);

                if (packet.Joined != null)
                    foreach (var delivery in packet.Joined) Probe(delivery, ref first, ref second);
            }

            if (first == null)
            {
                _repeat(new Delivery(level, Wire.Frame(1)), true);
                return;
            }

            _repeat(first, true);

            if (second != null) _repeat(second, true);
        }

        private static void Probe(Delivery delivery, ref Delivery? first, ref Delivery? second)
        {
            if (!delivery.Reliable || delivery.Acknowledged || second != null || ReferenceEquals(delivery, first))
                return;

            if (first == null) first = delivery;

            else second = delivery;
        }

        private void Repeat(Delivery delivery)
        {
            if (delivery.Reliable && !delivery.Acknowledged) _repeat(delivery, false);
        }

        // Packets declared lost, from DetectLost.
        private readonly List<Sent> _lost = [];

        private bool DetectLost(double now)
        {
            var lost = _lost;
            var lossDelay = LossDelay;

            // Only outstanding packets below the largest acknowledged one can be lost, and the
            // acknowledged ones among them have left the list.
            for (
                var level = 0;
                level < 3;
                level++
            )
            {
                if (_largestAcknowledged[level] is not { } largest) continue;

                var sent = _sent[level];

                for (var index = sent.Start; index < sent.End; index++)
                {
                    var packet = sent[index];

                    if (packet.Number > largest) break;

                    if (sent.Holds(packet) && (packet.Number + 3 <= largest || packet.Time + lossDelay <= now))
                        lost.Add(packet);
                }
            }

            if (lost.Count == 0) return false;

            try
            {
                Lose(lost, now);
            }

            finally
            {
                lost.Clear();
            }

            return true;
        }

        private void Lose(List<Sent> lost, double now)
        {
            var reduce = false;

            foreach (var packet in lost)
            {
                _sent[packet.Level].Remove(packet, PacketState.Retired);

                if (packet.InFlight && packet.Epoch == _epoch) BytesInFlight -= packet.Size;

                LostPackets++;
                _retired[packet.Level].Add(packet);
                _retiredOrder.Enqueue(packet);

                while (_retiredOrder.Count > _retiredLimit)
                {
                    var oldest = _retiredOrder.Dequeue();

                    if (oldest.State == PacketState.Retired)
                        _retired[oldest.Level].Remove(oldest, PacketState.Released);
                }

                if (packet.Epoch == _epoch && packet.Eliciting && packet.Time > _historyFloor)
                    _lostHistory.Add(packet);

                Repeat(packet.Delivery);

                if (packet.Joined != null)
                    foreach (var delivery in packet.Joined) Repeat(delivery);

                reduce |= packet.Epoch == _epoch && packet.InFlight && packet.Time > _recoveryStart;
            }

            if (reduce)
            {
                _recoveryStart = now;
                CongestionWindow = Math.Max(2 * MaximumPacket, CongestionWindow / 2);
                _slowStartThreshold = CongestionWindow;
                CongestionEvents++;
            }

            // Acknowledged packets break a persistent-congestion interval. Retain
            // bounded history across detection passes and packet number spaces.
            ForgetLosses(_firstSample, false);

            if (_lostHistory.Count > 4096) _lostHistory.RemoveRange(0, _lostHistory.Count - 4096);

            DetectPersistentCongestion();
        }

        // Send times of the lost packets, from DetectPersistentCongestion.
        private readonly List<double> _lossTimes = [];

        private void DetectPersistentCongestion()
        {
            var duration = 3 * (_smoothedRtt + Math.Max(4 * _variation, 1) + MaximumAckDelay);
            var earliest = double.PositiveInfinity;
            var latest = double.NegativeInfinity;

            foreach (var packet in _lostHistory)
            {
                earliest = Math.Min(earliest, packet.Time);
                latest = Math.Max(latest, packet.Time);
            }

            // Without losses spanning the duration, no interval can qualify.
            if (latest - earliest < duration) return;

            // Sorting in place is safe: trimming sorts these times too, and nothing else depends
            // on their order.
            _acknowledgedTimes.Sort();
            var times = _lossTimes;

            foreach (var packet in _lostHistory) times.Add(packet.Time);

            times.Sort();
            var cursor = 0;
            double? firstLost = null;
            var previous = double.NegativeInfinity;

            try
            {
                foreach (var time in times)
                {
                    while (cursor < _acknowledgedTimes.Count && _acknowledgedTimes[cursor] <= time)
                    {
                        if (_acknowledgedTimes[cursor] >= previous) firstLost = null;

                        cursor++;
                    }

                    firstLost ??= time;
                    previous = time;
                    var first = firstLost.Value;

                    if (time - first >= duration && !Outstanding(first, time)) CongestionWindow = 2 * MaximumPacket;
                }
            }

            finally
            {
                times.Clear();
            }
        }

        // Whether a packet sent on the current path between from and to is still outstanding.
        private bool Outstanding(double from, double to)
        {
            foreach (var sent in _sent)
                for (var index = sent.Start; index < sent.End; index++)
                {
                    var packet = sent[index];

                    if (sent.Holds(packet) && packet.Epoch == _epoch && packet.Time >= from && packet.Time <= to)
                        return true;
                }

            return false;
        }

        internal void ChangePath(double now)
        {
            // Packet numbers and reliable deliveries belong to the connection.
            // Old-path ACKs still retire them, but cannot train the new controller.
            _epoch++;
            BytesInFlight = 0;
            CongestionWindow = MaximumBurst;
            _minimumRtt = double.PositiveInfinity;
            _latestRtt = _smoothedRtt = 333;
            _variation = 166.5;
            _firstSample = _slowStartThreshold = double.PositiveInfinity;
            _recoveryStart = -1;
            _pacingCredit = MaximumBurst;
            _pacingTime = now;
            _ptoCount = 0;
            _lastTimeout = -1;
            _lostHistory.Clear();
            _acknowledgedTimes.Clear();
            _historyFloor = double.NegativeInfinity;
            Array.Fill(_lastEliciting, now);
        }

        internal void Abandon()
        {
            foreach (var sent in _sent) sent.Clear(PacketState.Released);

            foreach (var retired in _retired) retired.Clear(PacketState.Released);

            _retiredOrder.Clear();
            _lostHistory.Clear();
            _acknowledgedTimes.Clear();
            BytesInFlight = 0;
        }

        internal void Discard(int level)
        {
            _discarded[level] = true;
            var sent = _sent[level];

            for (var index = sent.Start; index < sent.End; index++)
            {
                var packet = sent[index];

                if (sent.Holds(packet) && packet.InFlight && packet.Epoch == _epoch) BytesInFlight -= packet.Size;
            }

            sent.Clear(PacketState.Released);
            _retired[level].Clear(PacketState.Released);
            _lostHistory.RemoveAll(packet => packet.Level == level);
            _ptoCount = 0;
            _lastTimeout = -1;
        }
    }
}
