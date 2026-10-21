using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        private sealed record Sent(
            int Level,
            ulong Number,
            double Time,
            int Size,
            Delivery Delivery,
            bool InFlight,
            bool ApplicationLimited
        );

        private readonly Dictionary<(int Level, ulong Number), Sent> _sent = [];
        private readonly Dictionary<(int Level, ulong Number), Sent> _retired = [];
        private readonly Queue<(int Level, ulong Number)> _retiredOrder = new();
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
        private double _nextPacedSend;
        private int _ptoCount;
        private double _lastTimeout = -1;
        private readonly List<Sent> _lostHistory = [];
        private readonly List<double> _acknowledgedTimes = [];

        internal long BytesInFlight { get; private set; }
        internal double CongestionWindow { get; private set; } = 10 * MaximumPacket;
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

        internal int ActivePacketCount => _sent.Count;
        internal int RetiredPacketCount => _retired.Count;
        internal int AcknowledgmentCount => _acknowledgedTimes.Count;
        internal int LossHistoryCount => _lostHistory.Count;

        internal bool IsDiscarded(int level) => _discarded[level];

        internal bool CanSend(int size, double now, bool probe) =>
            probe || (BytesInFlight + size <= CongestionWindow && now >= _nextPacedSend);

        internal void SentPacket(
            int level,
            ulong number,
            int size,
            double now,
            Delivery delivery,
            bool padded,
            bool applicationLimited = false
        )
        {
            if (delivery.Transmissions++ != 0 && delivery.Reliable) RetransmittedPackets++;

            var inFlight = delivery.Eliciting || padded;

            if (_sent.Count >= MaximumActivePackets) throw new InvalidDataException("Recovery packet budget exceeded.");

            _sent.Add((level, number), new Sent(level, number, now, size, delivery, inFlight, applicationLimited));

            if (inFlight) BytesInFlight += size;

            if (delivery.Eliciting) _lastEliciting[level] = now;

            if (inFlight) _nextPacedSend = now + size * Math.Max(_smoothedRtt, 1) / (1.25 * CongestionWindow);
        }

        internal void Acknowledge(int level, IReadOnlyList<(ulong Low, ulong High)> ranges, double delay, double now)
        {
            var largest = ranges[0].High;
            var late = _retired.Values
                .Where(p => p.Level == level && ranges.Any(r => p.Number >= r.Low && p.Number <= r.High))
                .ToArray();

            foreach (var packet in late)
            {
                packet.Delivery.Acknowledged = true;
                _acknowledgedTimes.Add(packet.Time);
                _lostHistory.RemoveAll(p => p.Level == packet.Level && p.Number == packet.Number);
                _retired.Remove((packet.Level, packet.Number));
            }

            var acknowledged = _sent.Values
                .Where(p => p.Level == level && ranges.Any(r => p.Number >= r.Low && p.Number <= r.High))
                .ToArray();
            _largestAcknowledged[level] = Math.Max(_largestAcknowledged[level] ?? 0, largest);
            var sample = acknowledged.FirstOrDefault(p => p.Number == largest);

            if (sample != null && acknowledged.Any(p => p.Delivery.Eliciting))
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

            foreach (var packet in acknowledged)
            {
                _sent.Remove((packet.Level, packet.Number));

                if (packet.InFlight) BytesInFlight -= packet.Size;

                packet.Delivery.Acknowledged = true;
                _acknowledgedTimes.Add(packet.Time);
                _lostHistory.RemoveAll(p => p.Level == packet.Level && p.Number == packet.Number);

                if (packet.Time > _recoveryStart && packet.InFlight && !packet.ApplicationLimited)
                    CongestionWindow += CongestionWindow < _slowStartThreshold
                        ? packet.Size
                        : MaximumPacket * (double)packet.Size / CongestionWindow;
            }

            if (acknowledged.Any(p => p.Delivery.Eliciting) || late.Any(p => p.Delivery.Eliciting))
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
                _lostHistory.RemoveAll(packet => packet.Time <= _historyFloor);
            }

            DetectLost(now);
        }

        private double LossDelay => Math.Max(1, 1.125 * Math.Max(_latestRtt, _smoothedRtt));

        private double Pto(int level) =>
            (_smoothedRtt + Math.Max(4 * _variation, 1) + (level == 2 && HandshakeConfirmed ? MaximumAckDelay : 0))
                * Math.Pow(2, Math.Min(_ptoCount, 16));

        internal void Tick(double now)
        {
            if (DetectLost(now)) return;

            var candidates = _sent.Values
                .Where(p => p.Delivery.Eliciting && !_discarded[p.Level] && (p.Level != 2 || HandshakeConfirmed))
                .ToArray();

            if (candidates.Length == 0) return;

            var earliest = candidates
                .OrderBy(p => _lastEliciting[p.Level] + Pto(p.Level))
                .First();
            var deadline = Math.Max(_lastEliciting[earliest.Level], _lastTimeout) + Pto(earliest.Level);

            if (now < deadline) return;

            _lastTimeout = now;
            _ptoCount++;
            ProbeTimeouts++;
            var deliveries = candidates
                .Where(p => p.Level == earliest.Level && p.Delivery.Reliable && !p.Delivery.Acknowledged)
                .OrderBy(p => p.Time)
                .Select(p => p.Delivery)
                .Distinct()
                .Take(2)
                .ToArray();

            foreach (var delivery in deliveries) _repeat(delivery, true);

            if (deliveries.Length == 0) _repeat(new Delivery(earliest.Level, Wire.Frame(1)), true);
        }

        private bool DetectLost(double now)
        {
            var lost = _sent.Values
                .Where(
                    p => _largestAcknowledged[p.Level].HasValue
                        && p.Number <= _largestAcknowledged[p.Level]
                        && (p.Number + 3 <= _largestAcknowledged[p.Level] || p.Time + LossDelay <= now)
                )
                .ToArray();

            if (lost.Length == 0) return false;

            foreach (var packet in lost)
            {
                _sent.Remove((packet.Level, packet.Number));

                if (packet.InFlight) BytesInFlight -= packet.Size;

                LostPackets++;

                _retired.Add((packet.Level, packet.Number), packet);
                _retiredOrder.Enqueue((packet.Level, packet.Number));

                while (_retiredOrder.Count > _retiredLimit) _retired.Remove(_retiredOrder.Dequeue());

                if (packet.Delivery.Eliciting && packet.Time > _historyFloor) _lostHistory.Add(packet);

                if (packet.Delivery.Reliable && !packet.Delivery.Acknowledged) _repeat(packet.Delivery, false);
            }

            if (lost.Any(p => p.InFlight && p.Time > _recoveryStart))
            {
                _recoveryStart = now;
                CongestionWindow = Math.Max(2 * MaximumPacket, CongestionWindow / 2);
                _slowStartThreshold = CongestionWindow;
                CongestionEvents++;
            }

            // Acknowledged packets break a persistent-congestion interval. Retain
            // bounded history across detection passes and packet number spaces.
            _lostHistory.RemoveAll(p => p.Time < _firstSample);

            if (_lostHistory.Count > 4096) _lostHistory.RemoveRange(0, _lostHistory.Count - 4096);

            var acknowledgments = _acknowledgedTimes
                .Order()
                .ToArray();
            var cursor = 0;
            double? firstLost = null;
            var previous = double.NegativeInfinity;

            foreach (var packet in _lostHistory.OrderBy(p => p.Time))
            {
                while (cursor < acknowledgments.Length && acknowledgments[cursor] <= packet.Time)
                {
                    if (acknowledgments[cursor] >= previous) firstLost = null;

                    cursor++;
                }

                firstLost ??= packet.Time;
                previous = packet.Time;
                var first = firstLost.Value;

                if (
                    packet.Time - first >= 3 * (_smoothedRtt + Math.Max(4 * _variation, 1) + MaximumAckDelay)
                    && !_sent.Values.Any(p => p.Time >= first && p.Time <= packet.Time)
                )
                    CongestionWindow = 2 * MaximumPacket;
            }

            return true;
        }

        internal void Abandon()
        {
            _sent.Clear();
            _retired.Clear();
            _retiredOrder.Clear();
            _lostHistory.Clear();
            _acknowledgedTimes.Clear();
            BytesInFlight = 0;
        }

        internal void Discard(int level)
        {
            _discarded[level] = true;

            foreach (var packet in _sent.Values
                .Where(p => p.Level == level)
                .ToArray())
            {
                if (packet.InFlight) BytesInFlight -= packet.Size;

                _sent.Remove((level, packet.Number));
            }

            foreach (var packet in _retired.Values
                .Where(p => p.Level == level)
                .ToArray())
                _retired.Remove((packet.Level, packet.Number));

            _lostHistory.RemoveAll(p => p.Level == level);
            _ptoCount = 0;
            _lastTimeout = -1;
        }
    }
}
