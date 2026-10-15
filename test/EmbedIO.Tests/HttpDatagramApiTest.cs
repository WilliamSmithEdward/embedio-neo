using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpDatagramApiTest
    {
        private sealed class Owner : IDisposable
        {
            internal int Count;
            public void Dispose() => Interlocked.Increment(ref Count);
        }
        private static HttpDatagram Packet(byte[] bytes, IDisposable owner)
            => (HttpDatagram)(Activator.CreateInstance(typeof(HttpDatagram), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { new ReadOnlyMemory<byte>(bytes), owner }, null) ?? throw new AssertionException("Missing packet factory."));
        private static HttpDatagramChannel Channel(Func<CancellationToken, ValueTask<HttpDatagram?>> receive,
            Func<ReadOnlyMemory<byte>, HttpDatagramSendStatus> send, Func<bool> canSend, Func<int> maximum, Action close)
            => (HttpDatagramChannel)(Activator.CreateInstance(typeof(HttpDatagramChannel), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { receive, send, canSend, maximum, close }, null) ?? throw new AssertionException("Missing channel factory."));
        private static void Attach(HttpTunnel tunnel, HttpDatagramChannel channel)
            => (typeof(HttpTunnel).GetMethod("AttachDatagrams", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing datagram attachment.")).Invoke(tunnel, new object[] { channel });
        [Test]
        public void PacketRetainsItsOwnerUntilOnceOnlyDisposal()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var owner = new Owner();
            var packet = Packet(bytes, owner);
            Assert.That(packet.Payload.ToArray(), Is.EqualTo(bytes));
            Assert.That(owner.Count, Is.Zero);
            Parallel.For(0, 32, _ => packet.Dispose());
            Assert.That(owner.Count, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => _ = packet.Payload);
        }
        [Test]
        public async Task EmptyPacketAndEndOfInputAreDistinct()
        {
            var owner = new Owner();
            var packet = Packet(Array.Empty<byte>(), owner);
            var calls = 0;
            var channel = Channel(_ => new ValueTask<HttpDatagram?>(calls++ == 0 ? packet : null),
                _ => HttpDatagramSendStatus.Queued, () => true, () => 0, () => { });
            Assert.That(channel.CanSend, Is.True, "A zero limit can permit an empty payload.");
            Assert.That(channel.MaximumPayloadLength, Is.Zero);
            using var received = await channel.ReceiveAsync();
            Assert.That(received, Is.SameAs(packet));
            Assert.That(received?.Payload.Length, Is.Zero);
            Assert.That(await channel.ReceiveAsync(), Is.Null);
        }
        [Test]
        public async Task CompletingOutputPreservesIncomingDelivery()
        {
            var sending = true;
            var owner = new Owner();
            var packet = Packet(new byte[] { 7 }, owner);
            var channel = Channel(_ => new ValueTask<HttpDatagram?>(packet),
                _ => sending ? HttpDatagramSendStatus.Queued : HttpDatagramSendStatus.Closed,
                () => sending, () => sending ? 1000 : 0, () => { });
            using var stream = new MemoryStream();
            var tunnel = new HttpTunnel(stream, _ => { sending = false; return Task.CompletedTask; });
            Attach(tunnel, channel);
            await tunnel.CompleteOutputAsync();
            Assert.That(channel.CanSend, Is.False);
            using var received = await channel.ReceiveAsync();
            Assert.That(received?.Payload.ToArray(), Is.EqualTo(new byte[] { 7 }));
            Assert.That(channel.TrySend(ReadOnlyMemory<byte>.Empty), Is.EqualTo(HttpDatagramSendStatus.Closed));
            await tunnel.CloseAsync();
        }
        [Test]
        public async Task ConcurrentTunnelCloseClosesDatagramsOnceAndStopsSubmission()
        {
            var closed = 0;
            var submits = 0;
            var channel = Channel(_ => new ValueTask<HttpDatagram?>((HttpDatagram?)null),
                _ => { submits++; return HttpDatagramSendStatus.Queued; }, () => true, () => 1000,
                () => Interlocked.Increment(ref closed));
            var stream = new MemoryStream();
            var tunnel = new HttpTunnel(stream, _ => Task.CompletedTask);
            Attach(tunnel, channel);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => tunnel.CloseAsync()));
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(channel.CanSend, Is.False);
            Assert.That(channel.MaximumPayloadLength, Is.Zero);
            Assert.That(await channel.ReceiveAsync(), Is.Null);
            Assert.That(channel.TrySend(ReadOnlyMemory<byte>.Empty), Is.EqualTo(HttpDatagramSendStatus.Closed));
            Assert.That(submits, Is.Zero);
            Assert.That(stream.CanRead, Is.False);
        }
        [Test]
        public async Task FullCloseEndsDatagramDeliveryBeforeOutputCompletionSettles()
        {
            var incoming = new TaskCompletionSource<HttpDatagram?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = Channel(_ => new ValueTask<HttpDatagram?>(incoming.Task),
                _ => HttpDatagramSendStatus.Queued, () => true, () => 1000, () => incoming.TrySetResult(null));
            var stream = new MemoryStream();
            var tunnel = new HttpTunnel(stream, _ => output.Task);
            Attach(tunnel, channel);
            var receiving = channel.ReceiveAsync();
            var closing = tunnel.CloseAsync();
            try
            {
                Assert.That(channel.CanSend, Is.False);
                Assert.That(await receiving.AsTask().WaitAsync(TimeSpan.FromSeconds(2)), Is.Null);
                Assert.That(closing.IsCompleted, Is.False);
                Assert.That(stream.CanRead, Is.True);
            }
            finally { output.TrySetResult(); await closing; }
            Assert.That(stream.CanRead, Is.False);
        }
        [Test]
        public async Task DatagramCleanupFailureStillDisposesTheStreamAndPreservesCompletionFailure()
        {
            var channel = Channel(_ => new ValueTask<HttpDatagram?>((HttpDatagram?)null),
                _ => HttpDatagramSendStatus.Closed, () => false, () => 0,
                () => throw new IOException("datagram-cleanup"));
            var stream = new MemoryStream();
            var tunnel = new HttpTunnel(stream, _ => Task.FromException(new InvalidOperationException("output-completion")));
            Attach(tunnel, channel);
            var error = await Assert.ThrowsAsync<AggregateException>(async () => await tunnel.CloseAsync());
            Assert.That(error?.Flatten().InnerExceptions.Select(value => value.Message),
                Is.EquivalentTo(new[] { "output-completion", "datagram-cleanup" }));
            Assert.That(stream.CanRead, Is.False);
        }
        [Test]
        public void APreCanceledReceiveDoesNotEnterTheBackend()
        {
            var receives = 0;
            var channel = Channel(_ => { receives++; return new ValueTask<HttpDatagram?>((HttpDatagram?)null); },
                _ => HttpDatagramSendStatus.Unavailable, () => false, () => 0, () => { });
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            Assert.Throws<OperationCanceledException>(() => _ = channel.ReceiveAsync(stop.Token));
            Assert.That(receives, Is.Zero);
        }
    }
}
