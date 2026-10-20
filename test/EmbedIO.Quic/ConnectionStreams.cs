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
            internal byte[]? Prefix;
            internal long? PendingReset;

            internal List<(Delivery Delivery, ulong End)> Deliveries { get; } = [];
        }

        private sealed class Write(ulong id, byte[] bytes, bool fin)
        {
            internal ulong Id { get; } = id;
            internal byte[] Bytes { get; } = bytes;
            internal bool Fin { get; } = fin;

            internal int Position;
        }

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

        internal int ReceiveCreditUpdates { get; private set; }
        internal ulong ReceivedData => _receivedExtent;

        private int _queuedBytes;
        private readonly ulong _peerDatagram;
        private readonly bool _resetNegotiated;

        internal WebTransportApplication Application { get; }
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
            Func<IReadOnlyDictionary<string, string>, bool> authorize
        )
        {
            _transmit = transmit;
            _peerData = peerData;
            _localBidiCredit = localBidiCredit;
            _remoteBidiCredit = remoteBidiCredit;
            _uniCredit = uniCredit;
            _maxUni = maxUni;
            _maxBidi = maxBidi;
            _peerDatagram = peerDatagram;
            _resetNegotiated = reset;
            Application = new WebTransportApplication(
                peerDatagram > 0 && reset,
                SendStream,
                SendDatagram,
                Abort,
                Release,
                authorize
            );
        }

        private Incoming GetIncoming(ulong id)
        {
            if ((id & 1) != 0 || id / 4 >= 16)
                throw new InvalidDataException("Invalid or excessive incoming stream ID.");

            if (!_incoming.TryGetValue(id, out var input)) _incoming.Add(id, input = new Incoming());

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
            var input = GetIncoming(id);

            if (
                offset > StreamReassembly.MaximumOffset || (ulong)bytes.Length > StreamReassembly.MaximumOffset - offset
            )
                throw new InvalidDataException("QUIC stream limit exceeded.");

            Account(input, offset + (ulong)bytes.Length);
            input.Buffer.Add(offset, bytes, fin);
            Deliver(id, input);
        }

        private void Deliver(ulong id, Incoming input)
        {
            if (input.Terminal) return;

            var maximum = input.ResetError.HasValue ? input.ReliableSize : StreamReassembly.MaximumOffset;
            var bytes = input.Buffer.Consume(maximum);
            var fin = !input.ResetError.HasValue && input.Buffer.Complete;

            if (bytes.Length != 0 || fin) Application.Stream(id, bytes, fin);

            if (input.ResetError.HasValue && input.Buffer.Delivered >= input.ReliableSize)
            {
                Application.Reset(id);
                input.Terminal = true;
                ReleaseTo(id, input, input.Extent);
            }

            else input.Terminal = fin;
        }

        internal void Reset(ulong id, ulong error, ulong final, ulong reliable, bool partial)
        {
            if (partial && !_resetNegotiated) throw new InvalidDataException("Reliable reset was not negotiated.");

            if (reliable > final) throw new InvalidDataException("Reliable size exceeds final size.");

            var input = GetIncoming(id);
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

            Deliver(id, input);
        }

        private void Release(ulong id, int count)
        {
            var input = _incoming[id];

            if ((ulong)count > input.Buffer.Delivered - input.Released)
                throw new InvalidOperationException("Application released unreceived stream bytes.");

            ReleaseTo(id, input, input.Released + (ulong)count);
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

            if (!input.Buffer.FinalSize.HasValue && streamTarget - input.Maximum >= StreamLimit / 2)
            {
                input.Maximum = streamTarget;
                _transmit(Wire.Frame(0x11, id, input.Maximum), true);
                ReceiveCreditUpdates++;
            }
        }

        internal void UpdateData(ulong value)
        {
            _peerData = Math.Max(_peerData, value);
            Flush();
        }

        internal void UpdateStream(ulong id, ulong value)
        {
            var output = GetOutgoing(id);
            output.Credit = Math.Max(output.Credit, value);
            Flush();
        }

        internal void UpdateCount(bool uni, ulong value)
        {
            if (value > 1UL << 60) throw new InvalidDataException("Invalid stream count.");

            if (uni) _maxUni = Math.Max(_maxUni, value);

            else _maxBidi = Math.Max(_maxBidi, value);

            Flush();
        }

        private Outgoing GetOutgoing(ulong id)
        {
            if ((id & 3) == 2 || id / 4 >= 16) throw new InvalidDataException("Invalid outbound stream ID.");

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
            var output = GetOutgoing(id);

            if (output.Ended || output.PendingReset.HasValue) return;

            if (_writes.Count >= 128 || bytes.Length > 262144 - _queuedBytes)
                throw new InvalidDataException("Pending output budget exceeded.");

            if ((id & 3) == 3 && output.Prefix == null && id >= 15) output.Prefix = (byte[])bytes.Clone();

            _writes.Add(new Write(id, bytes, fin));
            _queuedBytes += bytes.Length;
            Flush();
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
                using var frame = new MemoryStream();
                Wire.Integer(frame, end && write.Fin ? 15UL : 14UL);
                Wire.Integer(frame, write.Id);
                Wire.Integer(frame, output.Offset);
                Wire.Integer(frame, (ulong)count);
                frame.Write(write.Bytes, write.Position, count);
                output.Deliveries.RemoveAll(item => item.Delivery.Acknowledged);
                output.Deliveries.Add((_transmit(frame.ToArray(), true), output.Offset + (ulong)count));
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
            if (id is 3 or 7 or 11) throw new InvalidDataException("Peer stopped a critical HTTP/3 stream.");

            ResetSend(id, (long)error);
        }

        private void Abort(ulong id, long error)
        {
            if ((id & 1) == 0) _transmit(Wire.Frame(5, id, (ulong)error), true);

            if ((id & 3) != 2) ResetSend(id, error);
        }

        private void ResetSend(ulong id, long error)
        {
            var output = GetOutgoing(id);

            if (output.PendingReset.HasValue) return;

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
            _transmit(
                _resetNegotiated
                    ? Wire.Frame(0x24, id, (ulong)error, output.Offset, prefix)
                    : Wire.Frame(4, id, (ulong)error, output.Offset),
                true
            );
        }

        private void SendDatagram(byte[] bytes)
        {
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
            if (bytes.Length > 1100) throw new InvalidDataException("Datagram receive limit exceeded.");

            Application.ReceiveDatagram(bytes);
        }

        public void Dispose() => Application.Dispose();
    }
}
