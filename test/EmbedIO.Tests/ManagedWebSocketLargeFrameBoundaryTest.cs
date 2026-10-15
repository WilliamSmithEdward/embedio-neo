using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedWebSocketLargeFrameBoundaryTest
    {
        [TestCase(int.MaxValue - 65535)]
        [TestCase(int.MaxValue - 65534)]
        [TestCase(int.MaxValue - 1)]
        [TestCase(int.MaxValue)]
        public async Task AnnouncedLargePayloadWithoutDataReportsTruncation(int length)
        {
            // Only a masked binary header is supplied; no large payload is allocated.
            var header = new byte[14];
            header[0] = 0x82;
            header[1] = 0xff;
            for (var i = 0; i < 8; i++) header[2 + i] = (byte)((ulong)length >> (56 - 8 * i));
            using var stream = new MemoryStream(header);
            var assembly = typeof(WebServer).Assembly;
            var socketType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)
                ?? throw new AssertionException("Missing managed socket.");
            var socket = Activator.CreateInstance(socketType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { stream, (Action)(() => { }) }, null)
                ?? throw new AssertionException("Missing socket constructor.");
            // Do not start the socket's receive loop: this test owns the frame reader.
            GC.SuppressFinalize(socket);
            try
            {
                var readerType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrameStream", true)
                    ?? throw new AssertionException("Missing frame reader.");
                var reader = Activator.CreateInstance(readerType, new object[] { stream, false })
                    ?? throw new AssertionException("Missing frame reader constructor.");
                var read = readerType.GetMethod("ReadFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing frame read method.");
                var task = (Task)(read.Invoke(reader, new[] { socket })
                    ?? throw new AssertionException("Missing frame read task."));
                var failure = await Assert.ThrowsAsync<EmbedIO.WebSockets.WebSocketException>(
                    async () => await task.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.That(failure?.Message, Does.Contain("payload data"));
                Assert.That(stream.Position, Is.EqualTo(header.Length));
            }
            finally { ((IDisposable)socket).Dispose(); }
        }
    }
}
