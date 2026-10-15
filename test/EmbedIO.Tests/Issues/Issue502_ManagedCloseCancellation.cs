using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue502_ManagedCloseCancellation
    {
        [Test]
        public async Task CallerCancellationInterruptsTheSilentPeerAcknowledgementWait()
        {
            var assembly = typeof(WebServer).Assembly;
            using var transport = new ProbeStream();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true);
            var closed = 0;
            var socket = (IWebSocket)(Activator.CreateInstance((type ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")),
                flags, null, new object[] { transport, (Action)(() => Interlocked.Increment(ref closed)) }, null)
                ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            ((type).GetField("_exitReceiving", flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(socket, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            using var cancel = new CancellationTokenSource();
            var closing = Task.Run(() => (socket).CloseAsync(cancel.Token));
            await transport.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThatAsync(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(closed, Is.EqualTo(1));
            Assert.That((socket).State, Is.EqualTo(System.Net.WebSockets.WebSocketState.Closed));
            socket.Dispose();
        }

        private sealed class ProbeStream : MemoryStream
        {
            public TaskCompletionSource<bool> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var result = base.WriteAsync(buffer, offset, count, token);
                Written.TrySetResult(true);
                return result;
            }
        }
    }
}
