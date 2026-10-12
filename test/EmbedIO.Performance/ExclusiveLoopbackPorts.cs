using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

// Test-only client source-port selection. Connected sockets transfer to the
// client's NetworkStream; this owner retains only rejected, unconnected binds.
internal sealed class ExclusiveLoopbackPorts : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<int> _selected = new();
    private readonly List<Socket> _reservations = new();
    private bool _disposed;

    internal ExclusiveLoopbackPorts(IPEndPoint destination)
    {
        // A fresh process/listener can inherit an old tuple's TIME_WAIT. Capture
        // existing partners before requests start, then remember every new port.
        foreach (var row in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            if (row.LocalEndPoint.Equals(destination) && row.RemoteEndPoint.Address.Equals(destination.Address))
                _selected.Add(row.RemoteEndPoint.Port);
            if (row.RemoteEndPoint.Equals(destination) && row.LocalEndPoint.Address.Equals(destination.Address))
                _selected.Add(row.LocalEndPoint.Port);
        }
        PreexistingCount = _selected.Count;
    }

    internal int PreexistingCount { get; }
    internal int SelectedCount { get { lock (_gate) return _selected.Count - PreexistingCount; } }
    internal int ReservationCount { get { lock (_gate) return _reservations.Count; } }
    internal int OpenReservations { get { lock (_gate) return _reservations.Count(socket => !socket.SafeHandle.IsClosed); } }

    internal Socket Bind(IPAddress address, CancellationToken token, int firstCandidate = 0)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Explicit binding avoids implicit SO_REUSE_UNICASTPORT, but
                // Windows can still select a port used by an earlier request.
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(address, firstCandidate));
                firstCandidate = 0;
                var port = ((IPEndPoint)(socket.LocalEndPoint ?? throw new InvalidOperationException("Missing bound port."))).Port;
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(ExclusiveLoopbackPorts));
                    if (_selected.Add(port)) return socket;
                    // Reserve this rejected candidate so allocation advances.
                    // It is never connected; no network request is retried.
                    _reservations.Add(socket);
                }
            }
            catch { socket.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var socket in _reservations) socket.Dispose();
        }
    }

    internal static async Task VerifyAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var destination = (IPEndPoint)listener.LocalEndpoint;
        using var previous = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        previous.ExclusiveAddressUse = true;
        previous.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var previousPort = ((IPEndPoint)(previous.LocalEndPoint ?? throw new InvalidOperationException("Missing previous source."))).Port;
        await previous.ConnectAsync(destination, stop.Token).ConfigureAwait(false);
        using var accepted = await listener.AcceptSocketAsync(stop.Token).ConfigureAwait(false);
        accepted.Shutdown(SocketShutdown.Send);
        if (await previous.ReceiveAsync(new byte[1], stop.Token).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Expected the server's FIN.");
        previous.Shutdown(SocketShutdown.Send);
        if (await accepted.ReceiveAsync(new byte[1], stop.Token).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Expected the client's FIN.");
        accepted.Dispose();
        previous.Dispose();
        while (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(row =>
            row.State == TcpState.TimeWait && row.LocalEndPoint.Equals(destination) && row.RemoteEndPoint.Port == previousPort))
            await Task.Delay(10, stop.Token).ConfigureAwait(false);

        using var ports = new ExclusiveLoopbackPorts(destination);
        using var selected = ports.Bind(IPAddress.Loopback, stop.Token, previousPort);
        if (((IPEndPoint)(selected.LocalEndPoint ?? throw new InvalidOperationException("Missing new source."))).Port == previousPort
            || ports.PreexistingCount < 1 || ports.SelectedCount != 1 || ports.ReservationCount != 1 || ports.OpenReservations != 1)
            throw new InvalidOperationException("A previous TIME_WAIT partner was not rejected and reserved.");
        ports.Dispose();
        if (ports.OpenReservations != 0) throw new InvalidOperationException("Port reservation cleanup failed.");
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.ExclusiveAddressUse = true;
        probe.Bind(new IPEndPoint(IPAddress.Loopback, previousPort));
    }
}
