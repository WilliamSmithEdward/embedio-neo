using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2StreamRegistryTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type Type(string name) => typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2." + name, true)!;
        private sealed class Registry
        {
            private readonly object _instance;
            private readonly HashSet<int> _seen = new();
            internal Registry(int maximum = 128) => _instance = Activator.CreateInstance(Type("Http2StreamRegistry"), Flags, null, new object[] { maximum }, null)!;
            internal int Count => (int)_instance.GetType().GetProperty("ActiveCount")!.GetValue(_instance)!;
            internal object? Call(string name, params object[] args)
            {
                try { return _instance.GetType().GetMethod(name, Flags)!.Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
            }
            internal object? Frame(byte type, int id, byte flags = 0, byte[]? payload = null, bool pseudo = false)
            {
                payload ??= type == 3 || type == 8 ? new byte[4] : type == 2 ? new byte[5] : Array.Empty<byte>();
                var frame = Activator.CreateInstance(Type("Http2Frame"), Flags, null, new object[] { type, flags, id, payload }, null)!;
                if (type == 1)
                {
                    var pairs = pseudo ? new[] { ":path", "/" } : _seen.Add(id)
                        ? new[] { ":method", "GET", ":scheme", "https", ":authority", "example.com", ":path", "/" } : Array.Empty<string>();
                    var fields = Array.CreateInstance(Type("HpackField"), pairs.Length / 2);
                    for (var i = 0; i < pairs.Length; i += 2)
                        fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags | BindingFlags.Public, null, new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
                    var block = Activator.CreateInstance(Type("Http2HeaderBlock"), Flags, null, new object[] { id, (flags & 1) != 0, fields, 0u }, null)!;
                    frame.GetType().GetProperty("HeaderBlock")!.SetValue(frame, block);
                }
                return Call("Receive", frame);
            }
        }
        private static void Error(Action action, uint code, int id)
        {
            var error = Assert.Catch<IOException>(action)!;
            Assert.That(error.GetType().GetProperty("ErrorCode")!.GetValue(error), Is.EqualTo(code));
            Assert.That(error.GetType().GetProperty("StreamId")!.GetValue(error), Is.EqualTo(id));
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
            Error(() => registry.Frame(2, 3, 0, new byte[] { 0, 0, 0, 3, 0 }), 1, 3);
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
