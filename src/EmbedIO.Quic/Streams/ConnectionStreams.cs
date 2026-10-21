using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbedIO.Quic
{
    internal sealed class ConnectionStreams : IDisposable
    {
        private const int StreamLimit = 65536;
        private const ulong ConnectionLimit = 262144;

        private sealed class Incoming
        {
            internal StreamReassembly Buffer { get; } = new(StreamLimit);

            internal ulong Maximum = StreamLimit;
            internal ulong Released;
            internal ulong Consumed;
            internal ulong Discarded;
            internal bool ReadStopped;
            internal ulong Extent;
            internal ulong? ResetError;
            internal ulong ReliableSize;
            internal bool Terminal;
        }

        private sealed class Outgoing
        {
            internal ulong Offset;
            internal ulong Credit;
            internal bool Ended;
            internal bool CompletionNotified;
            internal byte[]? Prefix;
            internal long? PendingReset;
            internal Delivery? FinalDelivery;

            internal List<(Delivery Delivery, ulong End)> Deliveries { get; } = [];
        }

        private sealed class Write(ulong id, byte[] bytes, bool fin)
        {
            internal ulong Id { get; } = id;
            internal byte[] Bytes { get; } = bytes;
            internal bool Fin { get; } = fin;

            internal int Position;
        }

        private readonly StreamCountWindow _incomingBidi;
        private readonly StreamCountWindow _incomingUni;
        private readonly StreamCountWindow _localUni = new(32);
        private readonly StreamCountWindow _localBidi = new(32);
        private readonly Func<int, bool> _canQueue;
        private readonly Dictionary<ulong, ulong> _closedSizes = [];
        private readonly Queue<ulong> _closedOrder = new();
        private readonly Dictionary<ulong, Incoming> _incoming = [];
        private readonly Dictionary<ulong, Outgoing> _outgoing = [];
        private readonly List<Write> _writes = [];
        private readonly Func<byte[], bool, Delivery> _transmit;
        private readonly ulong _localBidiCredit;
        private readonly ulong _remoteBidiCredit;
        private readonly ulong _uniCredit;
        private ulong _maxUni;
        private ulong _maxBidi;
        private ulong _peerData;
        private ulong _sentData;
        private ulong _receivedExtent;
        private ulong _released;
        private ulong _receiveMaximum = ConnectionLimit;

        internal int RetiredStreams { get; private set; }
        internal int IncomingCount => _incoming.Count;
        internal int RetainedReceiveCapacity => _incoming.Values.Sum(input => input.Buffer.RetainedCapacity);
        internal int OutgoingCount => _outgoing.Count;
        internal int ClosedHistoryCount => _closedSizes.Count;
        internal int ReceiveCreditUpdates { get; private set; }
        internal ulong ReceivedData => _receivedExtent;
        internal ulong ReleasedData => _released;

        private int _queuedBytes;
        private bool _disposed;
        private bool _disposing;
        private readonly ulong _peerDatagram;
        private readonly bool _resetNegotiated;

        internal StreamApplication Protocol { get; }
        internal int OutboundResets { get; private set; }

        internal ConnectionStreams(
            Func<byte[], bool, Delivery> transmit,
            ulong peerData,
            ulong localBidiCredit,
            ulong remoteBidiCredit,
            ulong uniCredit,
            ulong maxUni,
            ulong maxBidi,
            ulong peerDatagram,
            bool reset,
            Func<StreamOperations, StreamApplication> createApplication,
            Func<int, bool>? canQueue = null,
            Action<ulong>? close = null,
            int inboundBidirectionalStreams = 16,
            int inboundUnidirectionalStreams = 16
        )
        {
            _incomingBidi = new StreamCountWindow(inboundBidirectionalStreams);
            _incomingUni = new StreamCountWindow(inboundUnidirectionalStreams);
            _transmit = transmit;
            _canQueue = canQueue ?? (static _ => true);
            _peerData = peerData;
            _localBidiCredit = localBidiCredit;
            _remoteBidiCredit = remoteBidiCredit;
            _uniCredit = uniCredit;
            _maxUni = maxUni;
            _maxBidi = maxBidi;
            _peerDatagram = peerDatagram;
            _resetNegotiated = reset;
            Protocol = createApplication(
                new StreamOperations(
                    SendStream,
                    SetReliablePrefix,
                    SendDatagram,
                    Abort,
                    Release,
                    TryWrite,
                    OpenLocal,
                    AbortRead,
                    AbortWrite,
                    TryDatagram,
                    DatagramLimit(),
                    close,
                    _incomingBidi.Capacity + _incomingUni.Capacity,
                    _incomingBidi.Capacity + _incomingUni.Capacity + _localBidi.Capacity + _localUni.Capacity
                )
            );
        }

        private Incoming? GetIncoming(ulong id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if ((id & 3) == 3 || (id & 3) == 1 && id / 4 >= _localBidi.Opened)
                throw new TransportException(0x05, "Data on a send-only or unopened local stream.");

            var window = (id & 1) != 0 ? _localBidi : (id & 2) == 0 ? _incomingBidi : _incomingUni;

            if (!window.Open(id / 4)) return null;

            if (!_incoming.TryGetValue(id, out var input))
            {
                _incoming.Add(id, input = new Incoming());
                Protocol.Open(id);
            }

            return input;
        }

        private void Account(Incoming input, ulong extent)
        {
            if (extent > input.Maximum || extent - Math.Min(extent, input.Extent) > _receiveMaximum - _receivedExtent)
                throw new InvalidDataException("QUIC receive flow control exceeded.");

            _receivedExtent += extent - Math.Min(extent, input.Extent);
            input.Extent = Math.Max(extent, input.Extent);
        }

        internal void Receive(ulong id, ulong offset, byte[] bytes, bool fin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (
                offset > StreamReassembly.MaximumOffset || (ulong)bytes.Length > StreamReassembly.MaximumOffset - offset
            )
                throw new InvalidDataException("QUIC stream limit exceeded.");

            var input = GetIncoming(id);

            if (input == null)
            {
                ValidateClosed(id, offset + (ulong)bytes.Length, fin);
                return;
            }

            Account(input, offset + (ulong)bytes.Length);
            input.Buffer.Add(offset, bytes, fin);
            Deliver(id, input);
            Prune();
        }

        private void Deliver(ulong id, Incoming input)
        {
            if (input.Terminal) return;

            if (input.ReadStopped)
            {
                input.Buffer.DiscardFrom(input.Buffer.Delivered);
                input.Discarded = input.Extent - input.Buffer.Delivered;
                input.Terminal = input.ResetError.HasValue || input.Buffer.FinalSize.HasValue;
                ReleaseTo(id, input, input.Consumed + input.Discarded);
                return;
            }

            var maximum = input.ResetError.HasValue ? input.ReliableSize : StreamReassembly.MaximumOffset;
            var bytes = input.Buffer.Consume(maximum);
            var fin = !input.ResetError.HasValue && input.Buffer.Complete;

            if (bytes.Length != 0 || fin) Protocol.Stream(id, bytes, fin);

            if (input.ResetError.HasValue && input.Buffer.Delivered >= input.ReliableSize)
            {
                Protocol.Reset(id, input.ResetError.Value, input.ReliableSize);
                input.Terminal = true;
                input.Discarded = input.Extent - input.Buffer.Delivered;
                ReleaseTo(id, input, input.Consumed + input.Discarded);
            }

            else input.Terminal = fin;
        }

        internal void Reset(ulong id, ulong error, ulong final, ulong reliable, bool partial)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (partial && !_resetNegotiated) throw new InvalidDataException("Reliable reset was not negotiated.");

            if (reliable > final) throw new InvalidDataException("Reliable size exceeds final size.");

            var input = GetIncoming(id);

            if (input == null)
            {
                ValidateClosed(id, final, true);
                return;
            }

            Account(input, final);
            input.Buffer.Add(final, [], true);

            if (input.ResetError.HasValue && reliable > input.ReliableSize)
                throw new InvalidDataException("Reliable size increased after reset.");

            if (!input.ResetError.HasValue)
            {
                input.ResetError = error;
                input.ReliableSize = reliable;
            }

            else input.ReliableSize = Math.Min(input.ReliableSize, reliable);

            input.Buffer.DiscardFrom(input.ReliableSize);
            Deliver(id, input);
            Prune();
        }

        private void Release(ulong id, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var input = _incoming[id];

            if ((ulong)count > input.Buffer.Delivered - input.Consumed)
                throw new InvalidOperationException("Application released unreceived stream bytes.");

            input.Consumed += (ulong)count;
            ReleaseTo(id, input, input.Consumed + input.Discarded);
        }

        private void ReleaseTo(ulong id, Incoming input, ulong target)
        {
            if (target < input.Released) return;

            _released += target - input.Released;
            input.Released = target;
            var connectionTarget = Math.Min(StreamReassembly.MaximumOffset, _released + ConnectionLimit);

            if (connectionTarget - _receiveMaximum >= ConnectionLimit / 2)
            {
                _receiveMaximum = connectionTarget;
                _transmit(Wire.Frame(0x10, _receiveMaximum), true);
                ReceiveCreditUpdates++;
            }

            var streamTarget = Math.Min(StreamReassembly.MaximumOffset, input.Released + StreamLimit);

            if (
                !input.ReadStopped
                && !input.Buffer.FinalSize.HasValue
                && streamTarget - input.Maximum >= StreamLimit / 2
            )
            {
                input.Maximum = streamTarget;
                _transmit(Wire.Frame(0x11, id, input.Maximum), true);
                ReceiveCreditUpdates++;
            }
        }

        internal void UpdateData(ulong value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _peerData = Math.Max(_peerData, value);
            Flush();
        }

        internal void UpdateStream(ulong id, ulong value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidatePeerSendReference(id);

            if (IsRetired(id)) return;

            var output = GetOutgoing(id);
            output.Credit = Math.Max(output.Credit, value);
            Flush();
        }

        internal void UpdateCount(bool uni, ulong value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (value > 1UL << 60) throw new InvalidDataException("Invalid stream count.");

            if (uni) _maxUni = Math.Max(_maxUni, value);

            else _maxBidi = Math.Max(_maxBidi, value);

            Flush();
        }

        private Outgoing GetOutgoing(ulong id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if ((id & 3) == 2 || (id & 3) == 1 && id / 4 >= _localBidi.Opened)
                throw new TransportException(0x05, "Invalid outbound stream ID.");

            if ((id & 1) == 0) GetIncoming(id);

            else ((id & 2) != 0 ? _localUni : _localBidi).Open(id / 4);

            if (!_outgoing.TryGetValue(id, out var output))
                _outgoing.Add(
                    id,
                    output = new Outgoing
                    {
                        Credit = (id & 2) != 0 ? _uniCredit : (id & 1) == 0 ? _localBidiCredit : _remoteBidiCredit
                    }
                );

            return output;
        }

        private void SendStream(ulong id, byte[] bytes, bool fin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRetired(id)) return;

            var output = GetOutgoing(id);

            if (output.Ended || output.PendingReset.HasValue) return;

            if (_writes.Count >= 128 || bytes.Length > 262144 - _queuedBytes)
                throw new InvalidDataException("Pending output budget exceeded.");

            _writes.Add(new Write(id, bytes, fin));
            _queuedBytes += bytes.Length;
            Flush();
        }

        private bool TryWrite(ulong id, ReadOnlyMemory<byte> bytes, bool fin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRetired(id)) throw new IOException("Stream has retired.");

            var output = GetOutgoing(id);

            if (output.Ended || output.PendingReset.HasValue) throw new IOException("Stream write side has closed.");

            if (bytes.Length > 8192) throw new ArgumentOutOfRangeException(nameof(bytes));

            if (_writes.Count >= 128 || bytes.Length > 262144 - _queuedBytes || !_canQueue(bytes.Length)) return false;

            SendStream(id, bytes.ToArray(), fin);
            return true;
        }

        private ulong? OpenLocal(bool unidirectional)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var window = unidirectional ? _localUni : _localBidi;
            var maximum = unidirectional ? _maxUni : _maxBidi;
            var ordinal = window.Opened;

            if (ordinal >= maximum || ordinal >= window.Maximum || ordinal >= 1UL << 60) return null;

            window.Open(ordinal);
            var id = ordinal * 4 + (unidirectional ? 3UL : 1UL);
            GetOutgoing(id);

            if (!unidirectional) GetIncoming(id);

            return id;
        }

        private void AbortRead(ulong id, ulong error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var input = GetIncoming(id);

            if (input == null || input.ReadStopped) return;

            input.ReadStopped = true;
            _transmit(Wire.Frame(5, id, error), true);
            Deliver(id, input);
        }

        private void AbortWrite(ulong id, ulong error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsRetired(id)) ResetSend(id, checked((long)error));
        }

        private void SetReliablePrefix(ulong id, byte[] prefix)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var output = GetOutgoing(id);

            if (
                output.Offset != 0
                || output.Prefix != null
                || output.PendingReset.HasValue
                || _writes.Any(write => write.Id == id)
            )
                throw new InvalidOperationException("Reliable prefix must precede stream writes.");

            ArgumentOutOfRangeException.ThrowIfGreaterThan(prefix.Length, StreamLimit);
            output.Prefix = (byte[])prefix.Clone();
        }

        private void Flush()
        {
            var blocked = new HashSet<ulong>();

            for (var i = 0; i < _writes.Count;)
            {
                var write = _writes[i];
                var output = _outgoing[write.Id];

                if (blocked.Contains(write.Id))
                {
                    i++;
                    continue;
                }

                if (output.Ended)
                {
                    _queuedBytes -= write.Bytes.Length - write.Position;
                    _writes.RemoveAt(i);
                    continue;
                }

                var streamAllowed = (write.Id & 1) == 0 || write.Id / 4 < ((write.Id & 2) != 0 ? _maxUni : _maxBidi);
                var credit = Math.Min(_peerData - _sentData, output.Credit - Math.Min(output.Credit, output.Offset));
                var remaining = write.Bytes.Length - write.Position;

                if (!streamAllowed || remaining != 0 && credit == 0)
                {
                    blocked.Add(write.Id);
                    i++;
                    continue;
                }

                var count = (int)Math.Min(900, Math.Min((ulong)remaining, credit));
                var end = write.Position + count == write.Bytes.Length;

                if (!_canQueue(count)) return;

                using var frame = new MemoryStream();
                Wire.Integer(frame, end && write.Fin ? 15UL : 14UL);
                Wire.Integer(frame, write.Id);
                Wire.Integer(frame, output.Offset);
                Wire.Integer(frame, (ulong)count);
                frame.Write(write.Bytes, write.Position, count);
                output.Deliveries.RemoveAll(item => item.Delivery.Acknowledged);
                var delivery = _transmit(frame.ToArray(), true);
                output.Deliveries.Add((delivery, output.Offset + (ulong)count));

                if (end && write.Fin) output.FinalDelivery = delivery;

                output.Offset += (ulong)count;
                _sentData += (ulong)count;
                _queuedBytes -= count;
                write.Position += count;

                if (end)
                {
                    output.Ended = write.Fin;
                    _writes.RemoveAt(i);
                }

                if (output.PendingReset.HasValue && output.Offset >= (ulong)(output.Prefix?.Length ?? 0))
                    FinishReset(write.Id, output);
            }
        }

        internal void Stop(ulong id, ulong error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidatePeerSendReference(id);

            if (IsRetired(id)) return;

            if (_outgoing.TryGetValue(id, out var output) && output.FinalDelivery?.Acknowledged == true) return;

            Protocol.Stop(id, error);
            ResetSend(id, (long)error);
        }

        private void Abort(ulong id, long error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRetired(id)) return;

            if ((id & 1) == 0) _transmit(Wire.Frame(5, id, (ulong)error), true);

            if ((id & 3) != 2) ResetSend(id, error);
        }

        private void ResetSend(ulong id, long error)
        {
            var output = GetOutgoing(id);

            if (output.PendingReset.HasValue || output.FinalDelivery?.Acknowledged == true) return;

            var prefix = output.Prefix ?? [];

            foreach (var item in output.Deliveries)
                if (item.End > (ulong)prefix.Length) item.Delivery.Acknowledged = true;

            foreach (var write in _writes
                .Where(w => w.Id == id)
                .ToArray())
            {
                _queuedBytes -= write.Bytes.Length - write.Position;
                _writes.Remove(write);
            }

            output.PendingReset = error;

            if (output.Offset < (ulong)prefix.Length)
            {
                // Credit is still required for a reliable stream header. Do not
                // emit a reset that makes the peer lose the session association.
                var remaining = prefix
                    .AsSpan((int)output.Offset)
                    .ToArray();
                _writes.Add(new Write(id, remaining, false));
                _queuedBytes += remaining.Length;
                output.Ended = false;
            }

            else FinishReset(id, output);

            Flush();
        }

        private void FinishReset(ulong id, Outgoing output)
        {
            OutboundResets++;
            var error = output.PendingReset ?? throw new InvalidOperationException("Missing reset state.");
            output.Ended = true;
            var prefix = (ulong)(output.Prefix?.Length ?? 0);
            output.FinalDelivery = _transmit(
                _resetNegotiated
                    ? Wire.Frame(0x24, id, (ulong)error, output.Offset, prefix)
                    : Wire.Frame(4, id, (ulong)error, output.Offset),
                true
            );
        }

        private bool IsRetired(ulong id) => (id & 3) switch
        {
            0 => _incomingBidi.IsClosed(id / 4),
            2 => _incomingUni.IsClosed(id / 4),
            3 => _localUni.IsClosed(id / 4),
            1 => _localBidi.IsClosed(id / 4),
            _ => false
        };

        private void ValidatePeerSendReference(ulong id)
        {
            if (
                (id & 3) == 2
                || (id & 3) == 1 && id / 4 >= _localBidi.Opened
                || (id & 3) == 3 && id / 4 >= _localUni.Opened
            )
                throw new TransportException(0x05, "Peer referenced an unopened or receive-only send stream.");
        }

        private void ValidateClosed(ulong id, ulong extent, bool final)
        {
            if (_closedSizes.TryGetValue(id, out var size) && (extent > size || final && extent != size))
                throw new TransportException(0x06, "Closed stream final size changed.");
        }

        internal void Prune()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Flush();
            Protocol.Poll();

            foreach (var pair in _outgoing.ToArray())
            {
                var output = pair.Value;
                output.Deliveries.RemoveAll(item => item.Delivery.Acknowledged);

                if (!output.Ended || output.FinalDelivery?.Acknowledged != true) continue;

                if (!output.CompletionNotified)
                {
                    output.CompletionNotified = true;
                    Protocol.WritesClosed(pair.Key);
                }

                if ((pair.Key & 3) == 3)
                {
                    _localUni.Retire(pair.Key / 4);
                    _outgoing.Remove(pair.Key);
                    Protocol.Retire(pair.Key);
                    RetiredStreams++;
                }

                else if (_incoming.TryGetValue(pair.Key, out var input) && CanRetire(pair.Key, input))
                    RetireIncoming(pair.Key, input);
            }

            foreach (var pair in _incoming
                .Where(pair => (pair.Key & 2) != 0)
                .ToArray())
                if (CanRetire(pair.Key, pair.Value)) RetireIncoming(pair.Key, pair.Value);
        }

        private bool CanRetire(ulong id, Incoming input) =>
            input.Terminal && input.Released == input.Extent && Protocol.CanRetire(id);

        private void RetireIncoming(ulong id, Incoming input)
        {
            var window = (id & 1) != 0 ? _localBidi : (id & 2) == 0 ? _incomingBidi : _incomingUni;

            if (!window.Retire(id / 4)) throw new InvalidOperationException("Stream already retired.");

            // Retain narrow final-size checks for recent streams without retaining
            // their receive buffers. Older closed IDs remain unavailable by range.
            if (_closedOrder.Count == 64) _closedSizes.Remove(_closedOrder.Dequeue());

            _closedSizes.Add(id, input.Buffer.FinalSize ?? throw new InvalidOperationException("Missing final size."));
            _closedOrder.Enqueue(id);
            _incoming.Remove(id);
            input.Buffer.Dispose();
            _outgoing.Remove(id);
            Protocol.Retire(id);

            if ((id & 1) == 0) _transmit(Wire.Frame((id & 2) == 0 ? 0x12UL : 0x13UL, window.Maximum), true);

            RetiredStreams++;
        }

        private int? DatagramLimit()
        {
            var size = (int)Math.Min(_peerDatagram, 1100);

            if (size < 2) return null;

            return size - (size - 2 >= 64 ? 3 : 2);
        }

        private bool TryDatagram(byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_canQueue(bytes.Length)) return false;

            SendDatagram(bytes);
            return true;
        }

        private void SendDatagram(byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frame = Wire
                .Frame(0x31, (ulong)bytes.Length)
                .Concat(bytes)
                .ToArray();

            if ((ulong)frame.Length > _peerDatagram || frame.Length > 1100)
                throw new InvalidDataException("Datagram exceeds peer limit.");

            _transmit(frame, false);
        }

        internal void Datagram(byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (bytes.Length > 1100) throw new InvalidDataException("Datagram receive limit exceeded.");

            Protocol.ReceiveDatagram(bytes);
        }

        public void Dispose()
        {
            if (_disposed || _disposing) return;

            _disposing = true;

            try
            {
                Protocol.Dispose();
            }

            finally
            {
                _disposed = true;

                foreach (var input in _incoming.Values) input.Buffer.Dispose();

                _incoming.Clear();
                _outgoing.Clear();
                _writes.Clear();
                _closedSizes.Clear();
                _closedOrder.Clear();
                _queuedBytes = 0;
            }
        }
    }
}
