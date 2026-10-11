using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2ReceiveFlowControlTest
    {
        private sealed class Flow
        {
            private static readonly Type Type = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2ReceiveFlowControl", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            private readonly object _instance;
            internal Flow(int window = 65535) => _instance = (Activator.CreateInstance(Type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { window }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            internal object? Call(string name, params object[] args)
            {
                try { return (Type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
            }
            internal void Open(int id) => Call("Open", id);
            internal void Receive(int id, int bytes) => Call("Receive", id, bytes);
            internal (int Connection, int Stream) Consume(int id, int bytes, bool flush = false)
            {
                var value = (Call("Consume", id, bytes, flush) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                return ((int)((value.GetType().GetProperty("Connection") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(value) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), (int)((value.GetType().GetProperty("Stream") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(value) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")));
            }
        }

        [Test]
        public void ArrivalDoesNotReturnCreditUntilConsumption()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3);
            flow.Receive(1, 65535);
            var error = (Assert.Catch<IOException>(() => flow.Receive(3, 1)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((error.GetType().GetProperty("StreamId") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(0));
            Assert.That(flow.Consume(1, 32768), Is.EqualTo((32768, 32768)));
            flow.Receive(3, 32768);
            Assert.Catch<IOException>(() => flow.Receive(3, 1));
        }

        [Test]
        public void SmallReadsBatchAndFinalFlushReturnsRemainder()
        {
            var flow = new Flow(); flow.Open(1);
            flow.Receive(1, 32000);
            Assert.That(flow.Consume(1, 16000), Is.EqualTo((0, 0)));
            Assert.That(flow.Consume(1, 16000), Is.EqualTo((0, 0)));
            Assert.That(flow.Consume(1, 0, true), Is.EqualTo((32000, 32000)));
            Assert.That(flow.Consume(1, 0, true), Is.EqualTo((0, 0)));
            flow.Receive(1, 65535);
        }

        [Test]
        public void StreamViolationStillCountsConnectionBytesUntilReset()
        {
            var flow = new Flow(16); flow.Open(1); flow.Open(3);
            var error = (Assert.Catch<IOException>(() => flow.Receive(1, 40000)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((error.GetType().GetProperty("StreamId") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(1));
            Assert.That((error.GetType().GetProperty("ErrorCode") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(3u));
            Assert.That(flow.Call("Close", 1), Is.EqualTo(40000));
            Assert.That(flow.Call("Close", 1), Is.Zero);
            Assert.That(flow.Consume(1, 40000, true), Is.EqualTo((0, 0)));
            flow.Receive(3, 16);
            Assert.That(flow.Consume(3, 16), Is.EqualTo((0, 16)));
        }

        [Test]
        public void ClosingPartiallyReadBodyDoesNotDoubleReturnConsumedBytes()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3);
            flow.Receive(1, 50000);
            Assert.That(flow.Consume(1, 10000), Is.EqualTo((10000, 10000)));
            Assert.That(flow.Call("Close", 1), Is.EqualTo(40000));
            flow.Receive(3, 65535);
            Assert.That(flow.Consume(3, 65535), Is.EqualTo((65535, 65535)));
        }

        [Test]
        public void DiscardedLateDataUsesConnectionWindowAndBatches()
        {
            var flow = new Flow();
            Assert.That(flow.Call("Discard", 20000), Is.Zero);
            Assert.That(flow.Call("Discard", 20000), Is.EqualTo(40000));
            Assert.That(flow.Call("Discard", 65535), Is.EqualTo(65535));
            Assert.Catch<IOException>(() => flow.Call("Discard", 65536));
        }

        [Test]
        public void PaddingCountsAgainstCreditAndCanBeConsumedImmediately()
        {
            var flow = new Flow(16); flow.Open(1);
            // 1 pad-length byte, 10 content bytes, 5 padding bytes.
            flow.Receive(1, 16);
            Assert.That(flow.Consume(1, 6), Is.EqualTo((0, 6)));
            Assert.That(flow.Consume(1, 10, true), Is.EqualTo((16, 10)));
            flow.Receive(1, 16);
        }

        [Test]
        public void InvalidConsumptionCannotManufactureCredit()
        {
            var flow = new Flow(); flow.Open(1); flow.Receive(1, 100);
            Assert.Throws<InvalidOperationException>(() => flow.Consume(1, 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => flow.Consume(1, -1));
            Assert.That(flow.Consume(1, 100, true), Is.EqualTo((100, 100)));
            Assert.Throws<InvalidOperationException>(() => flow.Consume(1, 1));
        }

        [Test]
        public void ConcurrentConsumptionAndResetReturnCreditExactlyOnce()
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var flow = new Flow(); flow.Open(1); flow.Open(3); flow.Receive(1, 65535);
                var consumed = 0; var closed = 0;
                Parallel.Invoke(() => consumed = flow.Consume(1, 65535, true).Connection,
                    () => closed = (int)(flow.Call("Close", 1) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")));
                Assert.That(consumed + closed, Is.EqualTo(65535));
                flow.Receive(3, 65535);
                Assert.Catch<IOException>(() => flow.Receive(3, 1));
            }
        }

        [Test]
        public void AbortMakesFurtherAccountingTerminal()
        {
            var flow = new Flow(); flow.Open(1); flow.Receive(1, 10); flow.Call("Abort");
            Assert.Throws<ObjectDisposedException>(() => flow.Open(3));
            Assert.Throws<ObjectDisposedException>(() => flow.Receive(1, 1));
            Assert.Throws<ObjectDisposedException>(() => flow.Consume(1, 10));
            Assert.Throws<ObjectDisposedException>(() => flow.Call("Discard", 1));
        }
    }
}
