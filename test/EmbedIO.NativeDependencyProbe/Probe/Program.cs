return await QuicRebindProbe.Run(args) ?? throw new ArgumentException("Use the QUIC rebind probe arguments.", nameof(args));
