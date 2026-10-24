using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

// Flood child process: junk UDP datagrams at the server's HTTP/3 port through a sample's
// measurement window, to show whether the listener keeps serving its connections while its
// receive path is saturated. Each datagram is a 1-RTT packet for an unknown connection, the
// cheapest input to reject, so the flood keeps the server's receive loop as busy as it can.
internal static class UdpFlood
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        var port = options.Integer("--port", 0);
        var threads = options.Integer("--threads", 1);
        var bytes = options.Integer("--bytes", 1200);
        if (port is < 1 or > 65535 || threads is < 1 or > 16 || bytes is < 32 or > 1472)
            throw new ArgumentException("Flood needs --port, 1-16 --threads and 32-1472 --bytes.");
        var target = new IPEndPoint(IPAddress.Loopback, port).Serialize();
        using var stop = new CancellationTokenSource();
        var sent = new long[threads];
        var errors = new long[threads];
        var senders = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var datagram = new byte[bytes];
            RandomNumberGenerator.Fill(datagram);
            var id = (ulong)index << 56;
            while (!stop.IsCancellationRequested)
            {
                // Fixed bit set and long-header bit clear; the next eight bytes are the
                // destination connection ID, a different one each time.
                datagram[0] = (byte)(0x40 | (datagram[0] & 0x3f));
                BinaryPrimitives.WriteUInt64LittleEndian(datagram.AsSpan(1, 8), ++id);
                try
                {
                    socket.SendTo(datagram, SocketFlags.None, target);
                    sent[index]++;
                }
                catch (SocketException)
                {
                    errors[index]++;
                }
            }
        })
        { IsBackground = true, Name = "flood-" + index }).ToArray();
        var clock = Stopwatch.StartNew();
        foreach (var sender in senders) sender.Start();
        Control.Write("FLOODING");
        var command = await Console.In.ReadLineAsync().ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        foreach (var sender in senders) sender.Join();
        var seconds = clock.Elapsed.TotalSeconds;
        using var self = Process.GetCurrentProcess();
        Control.Write(JsonSerializer.Serialize(new
        {
            command,
            threads,
            bytes,
            seconds,
            datagrams = sent.Sum(),
            datagramsPerSecond = sent.Sum() / seconds,
            sendErrors = errors.Sum(),
            cpuSeconds = self.TotalProcessorTime.TotalSeconds,
        }));
        return 0;
    }
}
