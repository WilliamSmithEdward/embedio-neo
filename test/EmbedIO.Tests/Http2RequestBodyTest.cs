using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2RequestBodyTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type Type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2RequestBody", true)!;
        private static Stream Body(long? length, Action<int> consumed) => (Stream)Activator.CreateInstance(Type, Flags, null, new object?[] { 1, length, consumed }, null)!;
        private static void Call(Stream body, string method, params object[] args)
        {
            try { Type.GetMethod(method, Flags)!.Invoke(body, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
        private static void Append(Stream body, byte[] bytes, bool end = false) => Call(body, "Append", bytes, 0, bytes.Length, end);

        [Test]
        public async Task AsyncReadWaitsThenDeliversExactBytesAndEof()
        {
            var consumed = 0;
            using var body = Body(5, count => consumed += count);
            var buffer = new byte[10];
            var pending = body.ReadAsync(buffer, 2, 8);
            Assert.That(pending.IsCompleted, Is.False);
            Append(body, new byte[] { 1, 2, 3, 4, 5 }, true);
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(5));
            Assert.That(buffer[2..7], Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
            Assert.That(await body.ReadAsync(buffer), Is.Zero);
            Assert.That(consumed, Is.EqualTo(5));
        }

        [Test]
        public async Task TinyFramesAreCoalescedAndProducerArraysAreNotRetained()
        {
            var consumed = 0;
            using var body = Body(65535, count => consumed += count);
            var frame = new byte[16384];
            for (var i = 0; i < 65535; i++)
            {
                frame[123] = (byte)i;
                Call(body, "Append", frame, 123, 1, i == 65534);
            }
            Array.Fill(frame, (byte)0);
            var queue = Type.GetField("_chunks", Flags)!.GetValue(body)!;
            Assert.That((int)queue.GetType().GetProperty("Count")!.GetValue(queue)!, Is.EqualTo(16));
            var actual = new byte[65535];
            await body.ReadExactlyAsync(actual);
            for (var i = 0; i < actual.Length; i++) Assert.That(actual[i], Is.EqualTo((byte)i));
            Assert.That(consumed, Is.EqualTo(65535));
        }

        [Test]
        public async Task CancelingReadLeavesDataAndNextReadUsable()
        {
            var consumed = 0;
            using var body = Body(null, count => consumed += count);
            using var cancel = new CancellationTokenSource();
            var pending = body.ReadAsync(new byte[4], 0, 4, cancel.Token);
            cancel.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            Append(body, new byte[] { 9 }, true);
            var bytes = new byte[1];
            Assert.That(await body.ReadAsync(bytes), Is.EqualTo(1));
            Assert.That(bytes[0], Is.EqualTo(9));
            Assert.That(consumed, Is.EqualTo(1));
        }

        [Test]
        public async Task ConcurrentReadFailsWithoutPoisoningOwner()
        {
            using var body = Body(null, _ => { });
            var pending = body.ReadAsync(new byte[1], 0, 1);
            await Assert.ThatAsync(async () => await body.ReadAsync(new byte[1], 0, 1), Throws.InstanceOf<InvalidOperationException>());
            Append(body, Array.Empty<byte>(), true);
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
        }

        [Test]
        public async Task ResetDiscardsBuffersReturnsCreditAndWakesReader()
        {
            var consumed = 0;
            using var body = Body(null, count => consumed += count);
            Append(body, new byte[12345]);
            Call(body, "Fail", new IOException("reset"));
            Assert.That(consumed, Is.EqualTo(12345));
            await Assert.ThatAsync(async () => await body.ReadAsync(new byte[1]), Throws.InstanceOf<IOException>());
            body.Dispose();
            Assert.That(consumed, Is.EqualTo(12345));
            using var waitingBody = Body(null, _ => { });
            var pending = waitingBody.ReadAsync(new byte[1], 0, 1);
            Call(waitingBody, "Fail", new IOException("reset"));
            await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<IOException>());
        }

        [Test]
        public async Task DisposeReturnsOnlyUnreadCreditAndUnblocksPendingRead()
        {
            var consumed = 0;
            var body = Body(null, count => consumed += count);
            Append(body, new byte[100]);
            Assert.That(await body.ReadAsync(new byte[20]), Is.EqualTo(20));
            body.Dispose(); body.Dispose();
            Assert.That(consumed, Is.EqualTo(100));
            var waiting = Body(null, _ => { });
            var read = waiting.ReadAsync(new byte[1], 0, 1);
            waiting.Dispose();
            await Assert.ThatAsync(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void ContentLengthAndCapacityAreEnforcedBeforeEnqueue()
        {
            using var body = Body(2, _ => { });
            Assert.Catch<IOException>(() => Append(body, new byte[3]));
            Assert.Catch<IOException>(() => Append(body, new byte[1], true));
            Append(body, new byte[2], true);
            Assert.Catch<IOException>(() => Append(body, Array.Empty<byte>()));
            using var bounded = Body(null, _ => { });
            Append(bounded, new byte[65535]);
            Assert.Catch<IOException>(() => Append(bounded, new byte[1]));
        }

        [Test]
        public async Task EmptyReadsDoNotWaitOrFinishTheBody()
        {
            using var body = Body(null, _ => { });
            Assert.That(await body.ReadAsync(Array.Empty<byte>()), Is.Zero);
            Append(body, new byte[] { 1 });
            Assert.That(await body.ReadAsync(new byte[1]), Is.EqualTo(1));
            Append(body, Array.Empty<byte>(), true);
            Assert.That(await body.ReadAsync(new byte[1]), Is.Zero);
        }
    }
}
