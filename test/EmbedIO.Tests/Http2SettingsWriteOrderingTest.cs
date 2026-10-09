using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        [TestCase(0)]
        [TestCase(1024)]
        public async Task SettingsAckCannotBeFollowedByDataReservedUnderTheOldWindow(int nextWindow)
        {
            using var source = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(source, _ => Interlocked.Increment(ref failures));
            var blocked = SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var flow = ConnectionType.GetProperty("SendFlow", Flags)?.GetValue(connection)
                ?? throw new AssertionException("Missing flow control.");
            object? Flow(string name, params object[] arguments) => (flow.GetType().GetMethod(name, Flags)
                ?? throw new AssertionException("Missing flow method.")).Invoke(flow, arguments);
            Flow("Open", 1);
            Assert.That(await (Task<int>)(Flow("ReserveAsync", 1, 16384, CancellationToken.None)
                ?? throw new AssertionException("Missing reservation.")), Is.EqualTo(16384));
            var settingsPayload = new byte[] { 0, 4, 0, 0, (byte)(nextWindow >> 8), (byte)nextWindow };
            var settings = (Task)(ConnectionType.GetMethod("ProcessControlAsync", Flags)?.Invoke(connection,
                new object[] { Frame(4, 0, 0, settingsPayload), CancellationToken.None })
                ?? throw new AssertionException("Missing settings task."));
            var frames = Array.CreateInstance(FrameType, 1);
            frames.SetValue(Frame(0, 0, 1, new byte[16384]), 0);
            var queued = (Task)(ConnectionType.GetMethod("SendStreamAsync", Flags)?.Invoke(connection,
                new object[] { frames, CancellationToken.None }) ?? throw new AssertionException("Missing DATA task."));
            try
            {
                source.Release.TrySetResult(true);
                await Task.WhenAll(blocked, settings, queued).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(source.Length, Is.EqualTo(26),
                    "The SETTINGS ACK must not be followed by a DATA frame that exceeds the newly acknowledged window.");
                Assert.That(failures, Is.Zero, "Revoking an unstarted DATA reservation must not fail shared output.");
                using var cancellation = new CancellationTokenSource();
                var resumed = (Task<int>)(Flow("ReserveAsync", 1, 16384, cancellation.Token)
                    ?? throw new AssertionException("Missing renewed reservation."));
                if (nextWindow == 0)
                {
                    Assert.That(resumed.IsCompleted, Is.False, "A zero stream window must block outside the shared writer.");
                    Flow("Update", 1, 1024);
                }
                Assert.That(await resumed.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1024));
                await SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
                Assert.That(source.Length, Is.EqualTo(43), "Blocked DATA must leave controls writable.");
            }
            finally
            {
                source.Release.TrySetResult(true);
                await Task.WhenAll(blocked, settings, queued).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }
    public partial class Http2InteroperabilityTest
    {
        [Test]
        public async Task ResponseAfterWindowShrinkPreservesEveryByteAndEndStream()
        {
            var body = Enumerable.Range(0, 16400).Select(i => (byte)(i * 17)).ToArray();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await SendWire(wire, 4, 0, 0, new byte[] { 0, 4, 0, 0, 0, 0 }, token);
                    var ack = await Until(wire, 4, 0, token);
                    Assert.That(ack.Flags, Is.EqualTo(1));
                    release.TrySetResult();
                    await Until(wire, 1, 1, token);
                    await SendWire(wire, 6, 0, 0, new byte[8], token);
                    Assert.That((await Until(wire, 6, 0, token)).Flags, Is.EqualTo(1),
                        "A zero-window response must not monopolize shared output.");
                    using var received = new MemoryStream();
                    while (received.Length < body.Length)
                    {
                        await SendWire(wire, 8, 0, 1, new byte[] { 0, 0, 4, 0 }, token);
                        var data = await Until(wire, 0, 1, token);
                        Assert.That(data.Payload.Length, Is.InRange(1, 1024));
                        received.Write(data.Payload);
                        Assert.That((data.Flags & 1) != 0, Is.EqualTo(received.Length == body.Length),
                            "END_STREAM must describe the committed body, including the final short frame.");
                    }
                    Assert.That(received.ToArray(), Is.EqualTo(body));
                }
                finally { release.TrySetResult(); }
            }, app: async exchange =>
            {
                await release.Task;
                await (Task)(exchange.GetType().GetMethod("RespondAsync", Flags)?.Invoke(exchange,
                    new object[] { body, CancellationToken.None }) ?? throw new AssertionException("Missing response task."));
            });
        }
    }

}
