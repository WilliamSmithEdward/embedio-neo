using System.Net.Quic;
using System.Runtime.Versioning;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    internal static class ProductionTransportTests
    {
        internal static void Run()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Classification();

                for (var mode = 0; mode < 3; mode++) BodyCancellation(mode, [7, 8, 9]);

                CompletedRead();
                TerminalAbort(false);
                TerminalAbort(true);
                Datagrams();
            }
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0 || (data[0] & 31) != 0) return;

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                BodyCancellation(data[0] % 3, data[1..Math.Min(data.Length, 64)].ToArray());
                TerminalAbort((data[0] & 32) != 0);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static void Classification()
        {
            Check(typeof(QuicConnection).Assembly == typeof(Http3TransportStream).Assembly, "actual-library-engine");

            foreach (var remote in new[] { false, true })
            {
                var stream = new QuicStreamFailure(42, remote);
                Check(
                    Http3TransportFailure.IsTransport(stream) && Http3TransportFailure.IsStream(stream),
                    "stream-failure"
                );
                Check(Http3TransportFailure.IsLocalReadAbort(stream) != remote, "local-abort");
                using var readStop = new CancellationTokenSource();
                Http3QuicConnection.WatchRequestReadDirection(Task.FromException(stream), readStop);
                Check(readStop.IsCancellationRequested == remote, "request-read-classification");
                using var writeStop = new CancellationTokenSource();
                Http3QuicConnection.WatchRequestDirection(Task.FromException(stream), writeStop);
                Check(writeStop.IsCancellationRequested, "request-write-classification");
                var connection = new QuicConnectionFailure(42, true, remote);
                Check(
                    Http3TransportFailure.IsTransport(connection)
                        && !Http3TransportFailure.IsStream(connection)
                        && !Http3TransportFailure.IsLocalReadAbort(connection),
                    "connection-failure"
                );
            }

            Check(!Http3TransportFailure.IsTransport(new IOException()), "unrelated-io-error");
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static void BodyCancellation(int mode, byte[] response)
        {
            using var peer = new AsyncStreamTests.Peer();
            peer.Streams.Receive(0, 0, [], false);
            using var stream = new EmbedIOQuicTransportStream(
                Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                )
            );
            using var request = new CancellationTokenSource();
            using var local = new CancellationTokenSource();
            using var readToken = CancellationTokenSource.CreateLinkedTokenSource(request.Token, local.Token);

            if (mode != 2) stream.EnableApplicationBodyCancellation(request.Token);

            var buffer = new byte[8];
            var read = stream
                .ReadAsync(buffer, readToken.Token)
                .AsTask();
            Check(!read.IsCompleted, "pending-body");

            if (mode == 1) request.Cancel();

            else local.Cancel();

            Expect<OperationCanceledException>(() => Await(read));
            Array.Fill(buffer, (byte)77);
            peer.Streams.Receive(0, 0, [1, 2, 3, 4], false);
            Check(buffer.All(value => value == 77), "borrowed-buffer-released");
            var frame = new WireReader(peer.Sent.First(item => item.Bytes[0] == 5).Bytes);
            var expected = mode == 0 ? 0x100UL : 0x10cUL;
            Check(frame.Integer() == 5 && frame.Integer() == 0 && frame.Integer() == expected, "stop-code");
            var failure = Expect<QuicStreamFailure>(() => Await(stream.ReadsClosed));
            Check(failure.ErrorCode == expected && !failure.Remote, "read-failure-provenance");
            var writing = stream
                .WriteAsync(response, true, default)
                .AsTask();
            peer.Pump();
            Await(writing);
            peer.Ack();
            Await(stream.WritesClosed);
            Check(stream.Type == QuicStreamType.Bidirectional && stream.Id == 0, "stream-identity");
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static void CompletedRead()
        {
            using var peer = new AsyncStreamTests.Peer();
            peer.Streams.Receive(0, 0, [3], false);
            using var stream = new EmbedIOQuicTransportStream(
                Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                )
            );
            stream.EnableApplicationBodyCancellation(default);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Expect<OperationCanceledException>(
                () => Await(
                    stream
                        .ReadAsync(new byte[8], canceled.Token)
                        .AsTask()
                )
            );
            using var token = new CancellationTokenSource();
            var buffer = new byte[8];
            Check(
                Await(
                    stream
                        .ReadAsync(buffer, token.Token)
                        .AsTask()
                )
                    == 1
                    && buffer[0] == 3,
                "precanceled-preserves-input"
            );
            token.Cancel();
            peer.Streams.Receive(0, 1, [4], true);
            Check(
                Await(
                    stream
                        .ReadAsync(buffer)
                        .AsTask()
                )
                    == 1
                    && buffer[0] == 4,
                "completed-read-detaches-token"
            );
            Check(
                Await(
                    stream
                        .ReadAsync(buffer)
                        .AsTask()
                )
                    == 0,
                "clean-fin"
            );
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static void TerminalAbort(bool remote)
        {
            using var peer = new AsyncStreamTests.Peer();
            peer.Streams.Receive(0, 0, [], false);
            using var stream = new EmbedIOQuicTransportStream(
                Await(
                    peer.Application
                        .AcceptInboundStreamAsync()
                        .AsTask()
                )
            );
            var reading = stream
                .ReadAsync(new byte[1])
                .AsTask();
            var writing = stream
                .WriteAsync(new byte[1], true, default)
                .AsTask();

            if (remote) peer.Application.ConnectionClosed(42, true);

            else peer.Application.Dispose();

            var readFailure = Expect<QuicConnectionFailure>(() => Await(reading));
            var writeFailure = Expect<QuicConnectionFailure>(() => Await(writing));
            Check(
                ReferenceEquals(readFailure, writeFailure)
                    && readFailure.Remote == remote
                    && readFailure.ErrorCode == (remote ? 42UL : 0UL),
                "terminal-failure-provenance"
            );

            foreach (var direction in new[]
            {
                QuicAbortDirection.Read, QuicAbortDirection.Write, QuicAbortDirection.Both
            })
                stream.Abort(direction, 0x10c);

            Expect<ArgumentOutOfRangeException>(() => stream.Abort((QuicAbortDirection)99, 0x10c));
            Expect<ArgumentOutOfRangeException>(() => stream.Abort(QuicAbortDirection.Both, -1));
            peer.Pump();
            Check(peer.Sent.Count == 0, "terminal-abort-does-not-queue");
        }

        private static void Datagrams()
        {
            using (var peer = new AsyncStreamTests.Peer(datagram: 0))
                Check(
                    new EmbedIOQuicDatagramTransport(peer.Application).TrySend(new byte[1])
                        == Http3DatagramSendStatus.Unavailable,
                    "datagram-unavailable"
                );

            using var active = new AsyncStreamTests.Peer();
            var transport = new EmbedIOQuicDatagramTransport(active.Application);
            Check(transport.MaximumSendLength > 0, "negotiated-datagram-limit");
            Check(
                transport.TrySend(new byte[transport.MaximumSendLength + 1]) == Http3DatagramSendStatus.TooLarge,
                "datagram-oversize"
            );
            Check(transport.TrySend(new byte[1]) == Http3DatagramSendStatus.Queued, "datagram-queued");
            var filled = false;

            for (var index = 0; index < 256; index++)
                if (transport.TrySend(new byte[1]) == Http3DatagramSendStatus.QueueFull)
                {
                    filled = true;
                    break;
                }

            Check(filled, "bounded-datagram-queue");
            active.Application.ReceiveDatagram([5, 8, 13]);
            using var packet = Await(
                transport
                    .ReceiveAsync(default)
                    .AsTask()
            )
                ?? throw new InvalidOperationException("Missing packet.");
            Check(packet.Payload.Span.SequenceEqual(new byte[] { 5, 8, 13 }), "owned-datagram");
            packet.StripHeader(1);
            Check(packet.Payload.Span.SequenceEqual(new byte[] { 8, 13 }), "sliced-datagram");
            packet.Dispose();
            packet.Dispose();
            Expect<ObjectDisposedException>(() => _ = packet.Payload);
            using var cancel = new CancellationTokenSource();
            var reading = transport
                .ReceiveAsync(cancel.Token)
                .AsTask();
            cancel.Cancel();
            Expect<OperationCanceledException>(() => Await(reading));
            active.Application.Dispose();
            Check(transport.TrySend(new byte[1]) == Http3DatagramSendStatus.Closed, "datagram-closed");
            Check(
                Await(
                    transport
                        .ReceiveAsync(default)
                        .AsTask()
                )
                    == null,
                "datagram-closed-receive"
            );
        }

        private static void Check(bool passed, string invariant)
        {
            if (!passed) throw new InvalidOperationException("HTTP/3 transport invariant: " + invariant);
        }

        private static T Await<T>(Task<T> task) =>
            task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();

        private static void Await(Task task) =>
            task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();

        private static T Expect<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }

            catch (T error)
            {
                return error;
            }

            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
