using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2HeaderBlockTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type FrameType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Frame", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static readonly Type BlocksType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2HeaderBlocks", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static T Property<T>(object value, string name) => (T)((value.GetType().GetProperty(name) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(value) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private sealed class Blocks : IDisposable
        {
            private readonly object _instance;
            internal Blocks(int bytes = 65536, int fragments = 1024)
                => _instance = (Activator.CreateInstance(BlocksType, Flags, null, new object[] { bytes, fragments }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            internal object? Process(byte type, byte flags, int id, byte[] payload)
            {
                var frame = (Activator.CreateInstance(FrameType, Flags, null, new object[] { type, flags, id, payload }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                return Invoke("Process", new[] { frame });
            }
            internal void Complete() => Invoke("CompleteInput", null);
            private object? Invoke(string name, object[]? args)
            {
                try { return (BlocksType.GetMethod(name, Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
            }
            public void Dispose() => ((IDisposable)_instance).Dispose();
        }

        [Test]
        public void EveryTwoFrameSplitDecodesAndPreservesOriginalEndStream()
        {
            var wire = Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff");
            for (var split = 0; split <= wire.Length; split++)
            {
                using var blocks = new Blocks();
                Assert.That(blocks.Process(1, 1, 1, wire[..split]), Is.Null);
                var block = (blocks.Process(9, 4, 1, wire[split..]) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                Assert.That(Property<int>(block, "StreamId"), Is.EqualTo(1));
                Assert.That(Property<bool>(block, "EndStream"), Is.True);
                var fields = Property<Array>(block, "Fields");
                Assert.That(fields.Length, Is.EqualTo(4));
                Assert.That(Property<string>((fields.GetValue(3) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "Value"), Is.EqualTo("www.example.com"));
                blocks.Complete();
            }
        }

        [Test]
        public void PaddingAndPriorityAreExcludedFromHpack()
        {
            using var blocks = new Blocks();
            var block = (blocks.Process(1, 45, 1, new byte[] { 2, 0, 0, 0, 3, 16, 0x82, 0xaa, 0xbb }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(block, "StreamError"), Is.Zero);
            Assert.That(Property<bool>(block, "EndStream"), Is.True);
            var fields = Property<Array>(block, "Fields");
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(Property<string>((fields.GetValue(0) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "Value"), Is.EqualTo("GET"));
        }

        [Test]
        public void IgnoredSelfDependencyStillUpdatesCompressionStateForOtherStreams()
        {
            using var blocks = new Blocks();
            var first = (blocks.Process(1, 36, 1, Convert.FromHexString("80000001104001610162")) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(first, "StreamError"), Is.Zero);
            var second = (blocks.Process(1, 4, 3, new byte[] { 0xbe }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(second, "StreamError"), Is.Zero);
            var fields = Property<Array>(second, "Fields");
            Assert.That(Property<string>((fields.GetValue(0) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "Name"), Is.EqualTo("a"));
            Assert.That(Property<string>((fields.GetValue(0) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "Value"), Is.EqualTo("b"));
        }

        [TestCase(6, 0)]
        [TestCase(4, 0)]
        [TestCase(0, 1)]
        [TestCase(1, 3)]
        [TestCase(9, 3)]
        [TestCase(240, 1)]
        public void NoFrameCanInterruptContinuation(byte type, int id)
        {
            using var blocks = new Blocks();
            blocks.Process(1, 0, 1, new byte[] { 0x82 });
            var error = Assert.Catch<IOException>(() => blocks.Process(type, 0, id, Array.Empty<byte>()));
            Assert.That(Property<uint>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "ErrorCode"), Is.EqualTo(1));
            Assert.That(Property<int>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "StreamId"), Is.Zero);
            Assert.Catch<IOException>(() => blocks.Process(9, 4, 1, Array.Empty<byte>()));
        }

        [Test]
        public void EndOfInputAndUnsolicitedContinuationAreErrors()
        {
            using var blocks = new Blocks();
            blocks.Process(1, 0, 1, Array.Empty<byte>());
            Assert.Catch<IOException>(() => blocks.Complete());
            using var other = new Blocks();
            Assert.Catch<IOException>(() => other.Process(9, 4, 1, Array.Empty<byte>()));
        }

        [Test]
        public void EncodedSizeAndEmptyFragmentFloodAreBounded()
        {
            using var bytes = new Blocks(2);
            bytes.Process(1, 0, 1, new byte[] { 0x82, 0x84 });
            var error = Assert.Catch<IOException>(() => bytes.Process(9, 4, 1, new byte[] { 0x86 }));
            Assert.That(Property<uint>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "ErrorCode"), Is.EqualTo(11));
            using var fragments = new Blocks(65536, 2);
            fragments.Process(1, 0, 1, Array.Empty<byte>());
            fragments.Process(9, 0, 1, Array.Empty<byte>());
            error = Assert.Catch<IOException>(() => fragments.Process(9, 4, 1, Array.Empty<byte>()));
            Assert.That(Property<uint>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "ErrorCode"), Is.EqualTo(11));
        }

        [TestCase("80")]
        [TestCase("40")]
        public void CompressionFailuresHaveConnectionScope(string wire)
        {
            using var blocks = new Blocks();
            var error = Assert.Catch<IOException>(() => blocks.Process(1, 4, 1, Convert.FromHexString(wire)));
            Assert.That(Property<uint>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "ErrorCode"), Is.EqualTo(9));
            Assert.That(Property<int>((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "StreamId"), Is.Zero);
        }

        [Test]
        public void ContinuationCannotChangeEndStreamAndCompletedBlocksReleaseState()
        {
            using var blocks = new Blocks();
            blocks.Process(1, 0, 1, new byte[] { 0x82 });
            var block = (blocks.Process(9, 5, 1, Array.Empty<byte>()) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<bool>(block, "EndStream"), Is.False);
            Assert.That(blocks.Process(6, 0, 0, new byte[8]), Is.Null);
            blocks.Dispose();
            Assert.That(() => blocks.Process(1, 4, 3, new byte[] { 0x82 }), Throws.TypeOf<ObjectDisposedException>());
        }
    }
}
