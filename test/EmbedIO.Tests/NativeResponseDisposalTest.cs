using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class NativeResponseDisposalTest
    {
        private sealed class CountingStream : MemoryStream
        {
            internal int Disposals;
            internal bool Fail;
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Increment(ref Disposals);
                    if (Fail) throw new IOException("dispose failure");
                }
                base.Dispose(disposing);
            }
        }
        private static Stream Wrapper(Stream source, Action prepare)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.SystemResponseStream")
                ?? throw new AssertionException("Missing native response wrapper.");
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { source, prepare, false }, null) as Stream
                ?? throw new AssertionException("Missing wrapper instance.");
        }
        [Test]
        public void RepeatedDisposalPreparesAndReleasesOnce()
        {
            var source = new CountingStream();
            var prepares = 0;
            using var wrapper = Wrapper(source, () => prepares++);
            wrapper.Dispose();
            wrapper.Close();
            wrapper.Dispose();
            Assert.That(prepares, Is.EqualTo(1));
            Assert.That(source.Disposals, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => wrapper.WriteByte(1));
        }
        [Test]
        public async Task ConcurrentDisposalPreparesAndReleasesOnce()
        {
            var source = new CountingStream();
            var prepares = 0;
            using var wrapper = Wrapper(source, () => Interlocked.Increment(ref prepares));
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(wrapper.Dispose)));
            Assert.That(prepares, Is.EqualTo(1));
            Assert.That(source.Disposals, Is.EqualTo(1));
        }
        [Test]
        public void HeaderPreparationFailureStillReleasesTheOwnedStream()
        {
            var source = new CountingStream();
            var expected = new InvalidOperationException("prepare failure");
            var prepares = 0;
            var wrapper = Wrapper(source, () => { prepares++; throw expected; });
            Assert.That(Assert.Throws<InvalidOperationException>(wrapper.Dispose), Is.SameAs(expected));
            Assert.That(source.Disposals, Is.EqualTo(1));
            Assert.DoesNotThrow(wrapper.Dispose);
            Assert.That(prepares, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => wrapper.WriteByte(1));
        }
        [Test]
        public void FailedUnderlyingDisposalDoesNotRepeatPreparationOrClose()
        {
            var source = new CountingStream { Fail = true };
            var prepares = 0;
            var wrapper = Wrapper(source, () => prepares++);
            Assert.Throws<IOException>(wrapper.Dispose);
            Assert.DoesNotThrow(wrapper.Dispose);
            Assert.That(prepares, Is.EqualTo(1));
            Assert.That(source.Disposals, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => wrapper.WriteByte(1));
            source.Fail = false;
            source.Dispose();
        }
        [Test]
        public void AlreadyClosedTransportDoesNotRepeatHeaderPreparation()
        {
            var source = new CountingStream();
            source.Dispose();
            var prepares = 0;
            using var wrapper = Wrapper(source, () => prepares++);
            wrapper.Dispose();
            Assert.That(prepares, Is.Zero);
            Assert.That(source.Disposals, Is.EqualTo(2), "One external close and one owned disposal attempt.");
        }
        [Test]
        public void ReentrantDisposalDoesNotReenterHeaderPreparation()
        {
            var source = new CountingStream();
            var prepares = 0;
            Stream? wrapper = null;
            wrapper = Wrapper(source, () =>
            {
                prepares++;
                if (prepares == 1) (wrapper ?? throw new AssertionException("Missing wrapper")).Dispose();
            });
            wrapper.Dispose();
            Assert.That(prepares, Is.EqualTo(1));
            Assert.That(source.Disposals, Is.EqualTo(1));
        }
    }
}
