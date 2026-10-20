namespace EmbedIO.Quic
{
    internal enum ConnectionPhase
    {
        Active,
        Closing,
        Draining,
        Closed
    }

    // RFC 9000 sections 10.1-10.2. The owner supplies monotonic milliseconds.
    internal sealed class ConnectionLifetime
    {
        private double _activity;
        private bool _mayExtendOnSend = true;
        private double _deadline;
        private double _nextResponse;
        private double _responseInterval;
        private bool _responsePending;

        internal ConnectionPhase Phase { get; private set; }
        internal double IdleTimeout { get; set; } = 10000;
        internal bool IdleExpired { get; private set; }

        internal void Authenticated(double now)
        {
            if (Phase == ConnectionPhase.Active)
            {
                _activity = now;
                _mayExtendOnSend = true;
            }

            else if (Phase == ConnectionPhase.Closing && now >= _nextResponse) _responsePending = true;
        }

        internal void SentEliciting(double now)
        {
            if (Phase != ConnectionPhase.Active || !_mayExtendOnSend) return;

            _activity = now;
            _mayExtendOnSend = false;
        }

        internal void Close(double now, double retention)
        {
            if (Phase != ConnectionPhase.Active) return;

            Phase = ConnectionPhase.Closing;
            _deadline = now + retention;
            _nextResponse = now;
            _responseInterval = Math.Max(1, retention / 3);
            _responsePending = true;
        }

        internal void Drain(double now, double retention)
        {
            if (Phase is ConnectionPhase.Draining or ConnectionPhase.Closed) return;

            if (Phase == ConnectionPhase.Active) _deadline = now + retention;

            Phase = ConnectionPhase.Draining;
            _responsePending = false;
        }

        internal bool TakeCloseResponse(double now)
        {
            if (Phase != ConnectionPhase.Closing || !_responsePending || now < _nextResponse) return false;

            _responsePending = false;
            _nextResponse = now + _responseInterval;
            _responseInterval *= 2;
            return true;
        }

        internal void Tick(double now, double retention)
        {
            if (Phase == ConnectionPhase.Active)
            {
                if (now < _activity + Math.Max(IdleTimeout, retention)) return;

                IdleExpired = true;
                Phase = ConnectionPhase.Closed;
            }

            else if (now >= _deadline) Phase = ConnectionPhase.Closed;
        }
    }
}
