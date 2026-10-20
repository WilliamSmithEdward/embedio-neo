namespace EmbedIO.Quic
{
    // Raw multiplexed peer used to validate the generic transport boundary.
    // This protocol has no HTTP control streams, stream headers or sessions.
    internal sealed class StreamEchoApplication(StreamOperations operations) : StreamApplication
    {
        private readonly Dictionary<ulong, ulong> _outputs = [];
        private readonly HashSet<ulong> _finished = [];
        private ulong _nextUni = 3;

        internal int Completed { get; private set; }
        internal int Retained => _outputs.Count + _finished.Count;

        internal override void Stream(ulong id, byte[] bytes, bool fin)
        {
            var output = id;

            if ((id & 2) != 0)
            {
                if (!_outputs.TryGetValue(id, out output))
                {
                    output = _nextUni;
                    _nextUni += 4;
                    _outputs.Add(id, output);
                }
            }

            operations.Write(output, bytes, fin);
            operations.Consume(id, bytes.Length);

            if (fin) _finished.Add(id);
        }

        internal override void Reset(ulong id, ulong error)
        {
            if ((id & 2) == 0) operations.Abort(id, (long)error);

            else if (_outputs.TryGetValue(id, out var output)) operations.Abort(output, (long)error);

            _finished.Add(id);
        }

        internal override void ReceiveDatagram(byte[] bytes) => operations.Datagram(bytes);

        internal override bool CanRetire(ulong id) => _finished.Contains(id) && !_outputs.ContainsKey(id);

        internal override void Retire(ulong id)
        {
            if ((id & 3) == 3)
            {
                foreach (var pair in _outputs
                    .Where(pair => pair.Value == id)
                    .ToArray()) _outputs.Remove(pair.Key);
            }

            else if (_finished.Remove(id)) Completed++;
        }

        public override void Dispose()
        {
            _outputs.Clear();
            _finished.Clear();
        }
    }
}
