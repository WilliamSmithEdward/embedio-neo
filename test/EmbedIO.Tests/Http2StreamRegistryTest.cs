using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2StreamRegistryTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type Type(string name) => (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2." + name, true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private sealed class Registry
        {
            private readonly object _instance;
            private readonly HashSet<int> _seen = new();
            internal Registry(int maximum = 128) => _instance = (Activator.CreateInstance(Type("Http2StreamRegistry"), Flags, null, new object[] { maximum }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            internal int Count => (int)((_instance.GetType().GetProperty("ActiveCount") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(_instance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            internal int Pending => (int)(_instance.GetType().GetProperty("PendingPriorityCount")?.GetValue(_instance) ?? throw new AssertionException("Missing pending count."));
            internal object? Call(string name, params object[] args)
            {
                try { return (_instance.GetType().GetMethod(name, Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
            }
            internal object? Frame(byte type, int id, byte flags = 0, byte[]? payload = null, bool pseudo = false, string? priority = null)
            {
                payload ??= type == 3 || type == 8 ? new byte[4] : type == 2 ? new byte[5] : Array.Empty<byte>();
                var frame = (Activator.CreateInstance(Type("Http2Frame"), Flags, null, new object[] { type, flags, id, payload }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                if (type == 1)
                {
                    var pairs = pseudo ? new[] { ":path", "/" } : _seen.Add(id)
                        ? new[] { ":method", "GET", ":scheme", "https", ":authority", "example.com", ":path", "/" } : Array.Empty<string>();
                    if (priority != null) pairs = new List<string>(pairs) { "priority", priority }.ToArray();
                    var fields = Array.CreateInstance(Type("HpackField"), pairs.Length / 2);
                    for (var i = 0; i < pairs.Length; i += 2)
                        fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags | BindingFlags.Public, null, new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
                    var block = (Activator.CreateInstance(Type("Http2HeaderBlock"), Flags, null, new object[] { id, (flags & 1) != 0, fields, 0u }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                    (frame.GetType().GetProperty("HeaderBlock") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).SetValue(frame, block);
                }
                return Call("Receive", frame);
            }
        }
        private static void Error(Action action, uint code, int id)
        {
            var error = (Assert.Catch<IOException>(action) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((error.GetType().GetProperty("ErrorCode") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(code));
            Assert.That((error.GetType().GetProperty("StreamId") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(id));
        }

        private static byte[] PriorityWire(int id, string field)
        {
            var value = Encoding.ASCII.GetBytes(field); var bytes = new byte[4 + value.Length];
            bytes[0] = (byte)(id >> 24); bytes[1] = (byte)(id >> 16); bytes[2] = (byte)(id >> 8); bytes[3] = (byte)id;
            value.CopyTo(bytes, 4); return bytes;
        }
        private static (int, bool) Priority(object? stream)
        {
            var value = stream?.GetType().GetProperty("Priority")?.GetValue(stream) ?? throw new AssertionException("Missing priority.");
            return ((int)(value.GetType().GetProperty("Urgency")?.GetValue(value) ?? throw new AssertionException("Missing urgency.")),
                (bool)(value.GetType().GetProperty("Incremental")?.GetValue(value) ?? throw new AssertionException("Missing incremental.")));
        }
        [TestCase(0, "00000000", 1u)]
        [TestCase(1, "00000001", 1u)]
        [TestCase(0, "", 6u)]
        [TestCase(0, "000000", 6u)]
        [TestCase(0, "00000002", 1u)]
        [TestCase(0, "0000000180", 1u)]
        [TestCase(0, "00000001753d", 1u)]
        public void InvalidPriorityUpdateHasConnectionScope(int streamId, string payload, uint code)
        { var registry = new Registry(); Error(() => registry.Frame(16, streamId, 0, Convert.FromHexString(payload)), code, 0); }
        [Test]
        public void EarlyAndLatePriorityUpdatesOverrideRequestHeaders()
        {
            var registry = new Registry();
            registry.Frame(16, 0, 255, Convert.FromHexString("80000001753d302c69"));
            var stream = registry.Frame(1, 1, priority: "u=7");
            Assert.That(Priority(stream), Is.EqualTo((0, true)));
            Assert.That(registry.Pending, Is.Zero);
            registry.Frame(16, 0, 0, PriorityWire(1, "u=5"));
            Assert.That(Priority(stream), Is.EqualTo((5, false)));
            registry.Frame(16, 0, 0, PriorityWire(1, ""));
            Assert.That(Priority(stream), Is.EqualTo((3, false)));
            Error(() => registry.Frame(16, 0, 0, PriorityWire(1, "u=0,")), 1, 0);
            Assert.That(Priority(stream), Is.EqualTo((3, false)));
        }
        [Test]
        public void HeadersSupplyPriorityWhenNoUpdateExists()
        {
            var registry = new Registry();
            Assert.That(Priority(registry.Frame(1, 1, priority: "u=6,i")), Is.EqualTo((6, true)));
            Assert.That(Priority(registry.Frame(1, 3, priority: "u=0,i,")), Is.EqualTo((3, false)));
        }
        [Test]
        public void IdlePriorityTargetsAndActiveStreamsShareTheAdvertisedLimit()
        {
            var registry = new Registry(2); registry.Frame(1, 1, 1);
            registry.Frame(16, 0, 0, PriorityWire(3, "u=0"));
            registry.Frame(16, 0, 0, PriorityWire(3, "u=2"));
            Error(() => registry.Frame(16, 0, 0, PriorityWire(5, "u=1")), 1, 0);
            Assert.That(registry.Pending, Is.EqualTo(1));
            registry.Call("EndLocal", 1);
            registry.Frame(16, 0, 0, PriorityWire(5, "u=4"));
            Assert.That(Priority(registry.Frame(1, 5)), Is.EqualTo((4, false)));
            Assert.That(registry.Pending, Is.Zero, "Opening stream five retires idle stream three.");
            registry.Frame(16, 0, 0, PriorityWire(1, "u=0"));
            Assert.That(registry.Pending, Is.Zero);
        }
        [Test]
        public void NewUnprioritizedStreamCannotOverrunIdlePriorityBudget()
        {
            var registry = new Registry(1); registry.Frame(16, 0, 0, PriorityWire(3, "i"));
            Error(() => registry.Frame(1, 1), 7, 1);
            Assert.That(registry.Count, Is.Zero);
            Assert.That(Priority(registry.Frame(1, 3)), Is.EqualTo((3, true)));
        }
        [Test]
        public void EndedResponsesIgnoreUpdatesAndAbortClearsIdleTargets()
        {
            var registry = new Registry(); var stream = registry.Frame(1, 1, priority: "u=5");
            registry.Call("EndLocal", 1);
            registry.Frame(16, 0, 0, PriorityWire(1, "u=0"));
            Assert.That(Priority(stream), Is.EqualTo((5, false)));
            registry.Frame(16, 0, 0, PriorityWire(3, "u=2"));
            registry.Call("Abort"); Assert.That(registry.Pending, Is.Zero);
        }

        [Test]
        public void HalfClosedStreamsCountUntilBothDirectionsEnd()
        {
            var registry = new Registry(1);
            registry.Frame(1, 1, 1);
            Assert.That(registry.Count, Is.EqualTo(1));
            Error(() => registry.Frame(1, 3, 1), 7, 3);
            registry.Call("EndLocal", 1);
            Assert.That(registry.Count, Is.Zero);
            Assert.That(registry.Frame(1, 5, 1), Is.Not.Null);
        }

        [Test]
        public void EarlyResponseDoesNotPreventRemainingRequestData()
        {
            var registry = new Registry(); registry.Frame(1, 1);
            registry.Call("EndLocal", 1);
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.Frame(0, 1, 0, new byte[10]), Is.Not.Null);
            registry.Frame(0, 1, 1);
            Assert.That(registry.Count, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(3)]
        [TestCase(8)]
        public void KnownFramesOnIdleStreamsAreConnectionErrors(byte type)
        {
            var registry = new Registry();
            Error(() => registry.Frame(type, 1), 1, 0);
            Error(() => registry.Frame(type, 2), 1, 0);
        }

        [Test]
        public void ClientCannotOpenEvenStreamsOrPush()
        {
            var registry = new Registry();
            Error(() => registry.Frame(1, 2), 1, 0);
            Error(() => registry.Frame(5, 1, 0, new byte[4]), 1, 0);
        }

        [Test]
        public void PriorityAndUnknownFramesDoNotOpenStreams()
        {
            var registry = new Registry();
            Assert.That(registry.Frame(2, 99), Is.Null);
            Assert.That(registry.Frame(240, 101), Is.Null);
            Assert.That(registry.Count, Is.Zero);
            Assert.That(registry.Frame(1, 1), Is.Not.Null);
            Assert.That(registry.Frame(2, 3, 0, new byte[] { 0, 0, 0, 3, 0 }), Is.Null);
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void DataAfterRemoteEndIsStreamErrorButControlIsAllowed()
        {
            var registry = new Registry(); registry.Frame(1, 1, 1);
            Error(() => registry.Frame(0, 1), 5, 1);
            Assert.That(registry.Frame(8, 1), Is.Not.Null);
            registry.Frame(3, 1);
            Assert.That(registry.Count, Is.Zero);
        }

        [Test]
        public void TrailersMustEndStreamAndContainNoPseudoHeaders()
        {
            var registry = new Registry(); registry.Frame(1, 1);
            Error(() => registry.Frame(1, 1), 1, 1);
            Error(() => registry.Frame(1, 1, 1, pseudo: true), 1, 1);
            Assert.That(registry.Frame(1, 1, 1), Is.Not.Null);
            Error(() => registry.Frame(1, 1, 1), 5, 1);
        }

        [Test]
        public void ClosedAndSkippedIdsNeverReopenOrRetainObjects()
        {
            var registry = new Registry(1);
            for (var id = 1; id < 10000; id += 4)
            {
                registry.Frame(1, id);
                registry.Call("Reset", id);
                Assert.That(registry.Frame(0, id), Is.Null);
                Assert.That(registry.Frame(1, id), Is.Null);
                if (id > 1) Assert.That(registry.Frame(1, id - 2), Is.Null);
                Assert.That(registry.Count, Is.Zero);
            }
        }

        [Test]
        public void ConnectionAbortRetiresEveryStreamAndPreventsFurtherInput()
        {
            var registry = new Registry(); registry.Frame(1, 1); registry.Frame(1, 3);
            registry.Call("Abort");
            Assert.That(registry.Count, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => registry.Frame(1, 5));
        }
    }
}
