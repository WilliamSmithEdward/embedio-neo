using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        [TestCase(0, true)]
        [TestCase(4, true)]
        [TestCase(9, true)]
        [TestCase(11, true)]
        [TestCase(0, false)]
        [TestCase(4, false)]
        [TestCase(9, false)]
        [TestCase(11, false)]
        public async Task AbortedSocketReadIsCancellationOnlyWhenItsTokenWasCanceled(int boundary, bool cancel)
        {
            using var stop = new CancellationTokenSource();
            using var source = new AbortedSource(stop, boundary, cancel, SocketError.OperationAborted);
            using var transport = (IDisposable)Transport(source);
            Exception? observed = null;
            try { await Read(transport, stop.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { observed = error; }
            if (cancel)
            {
                Assert.That(observed, Is.InstanceOf<OperationCanceledException>());
                var canceled = observed as OperationCanceledException ?? throw new AssertionException("Expected cancellation.");
                Assert.That(canceled.CancellationToken, Is.EqualTo(stop.Token));
                Assert.That(canceled.InnerException, Is.SameAs(source.Error));
            }
            else Assert.That(observed, Is.SameAs(source.Error));
            var reads = source.Reads;
            await Assert.ThatAsync(async () => await Read(transport), Throws.InstanceOf<IOException>());
            Assert.That(source.Reads, Is.EqualTo(reads), "An interrupted frame cannot be resumed with another token.");
        }

        [TestCase(SocketError.ConnectionReset)]
        [TestCase(SocketError.TimedOut)]
        [TestCase(SocketError.Success)]
        public async Task CancellationDoesNotHideUnrelatedReadFailures(SocketError errorCode)
        {
            using var stop = new CancellationTokenSource();
            using var source = new AbortedSource(stop, 4, true, errorCode);
            using var transport = (IDisposable)Transport(source);
            IOException? observed = null;
            try { await Read(transport, stop.Token); }
            catch (IOException error) { observed = error; }
            Assert.That(observed, Is.SameAs(source.Error));
        }

        private sealed class AbortedSource : MemoryStream
        {
            private readonly CancellationTokenSource _stop;
            private readonly int _boundary;
            private readonly bool _cancel;
            internal AbortedSource(CancellationTokenSource stop, int boundary, bool cancel, SocketError error)
                : base(new byte[] { 0, 0, 4, 0, 0, 0, 0, 0, 1, 10, 20, 30, 40 }, false)
            {
                _stop = stop; _boundary = boundary; _cancel = cancel;
                Error = error == SocketError.Success ? new IOException("Injected unrelated I/O failure.")
                    : new IOException("Injected socket read failure.", new SocketException((int)error));
            }
            internal IOException Error { get; }
            internal int Reads { get; private set; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ++Reads;
                if (Position < _boundary) return Task.FromResult(Read(buffer, offset, Math.Min(count, _boundary - (int)Position)));
                if (_cancel) _stop.Cancel();
                return Task.FromException<int>(Error);
            }
        }
    }
}
