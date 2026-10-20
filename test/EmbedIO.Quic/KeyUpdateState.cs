namespace EmbedIO.Quic
{
    internal sealed class KeyUpdateException(string message) : Exception(message);

    // RFC 9001 section 6. Read and write generations are separate: local
    // initiation may put the writer one generation ahead of the peer.
    internal sealed class KeyUpdateState
    {
        internal const int DefaultInterval = 1 << 20;
        private readonly int _interval;
        private ulong _firstWritten;
        private ulong? _acknowledged;
        private ulong? _readMaximum;
        private ulong? _previousMaximum;
        private ulong? _olderMaximum;
        private double _confirmedAt = double.PositiveInfinity;

        internal KeyUpdateState(int interval = DefaultInterval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(interval, 4);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, PacketKeys.MaximumEncryptions - 8);
            _interval = interval;
        }

        internal ulong ReadGeneration { get; private set; }
        internal ulong WriteGeneration { get; private set; }
        internal ulong ReadMinimum { get; private set; }
        internal bool ReadPhase => (ReadGeneration & 1) != 0;
        internal bool WritePhase => (WriteGeneration & 1) != 0;

        internal bool ShouldInitiate(int encrypted, bool handshake, double now, double retention) =>
            encrypted >= _interval
                && handshake
                && ReadGeneration == WriteGeneration
                && _acknowledged >= _firstWritten
                && (WriteGeneration == 0 || now >= _confirmedAt + retention);

        internal bool TryInitiate(ulong nextNumber, int encrypted, bool handshake, double now, double retention)
        {
            if (!ShouldInitiate(encrypted, handshake, now, retention)) return false;

            AdvanceWrite(nextNumber);
            return true;
        }

        internal void ReadCurrent(ulong number)
        {
            if (ReadGeneration != 0 && _previousMaximum >= number)
                throw new KeyUpdateException("Current key packet precedes an older key packet.");

            _readMaximum = Math.Max(_readMaximum ?? 0, number);

            if (ReadGeneration != 0) ReadMinimum = Math.Min(ReadMinimum, number);
        }

        internal void ReadPrevious(ulong number)
        {
            if (ReadGeneration == 0 || number >= ReadMinimum || _olderMaximum >= number)
                throw new KeyUpdateException("Previous key packet exceeds the update boundary.");

            _previousMaximum = Math.Max(_previousMaximum ?? 0, number);
        }

        internal bool PeerUpdated(ulong number, ulong nextNumber)
        {
            if (_readMaximum >= number) throw new KeyUpdateException("Key update precedes an older packet.");

            _olderMaximum = _previousMaximum;
            _previousMaximum = _readMaximum;
            _readMaximum = number;
            ReadMinimum = number;
            ReadGeneration++;

            if (ReadGeneration <= WriteGeneration) return false;

            AdvanceWrite(nextNumber);
            return true;
        }

        internal void Acknowledge(ulong largest, bool currentReadKeys, double now)
        {
            if (!currentReadKeys || ReadGeneration != WriteGeneration || largest < _firstWritten) return;

            _acknowledged = Math.Max(_acknowledged ?? 0, largest);

            if (double.IsPositiveInfinity(_confirmedAt)) _confirmedAt = now;
        }

        private void AdvanceWrite(ulong nextNumber)
        {
            WriteGeneration++;
            _firstWritten = nextNumber;
            _acknowledged = null;
            _confirmedAt = double.PositiveInfinity;
        }
    }
}
