using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO.Files;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class FileCacheRegressionTest
    {
        private static readonly Type SectionType = (typeof(FileCache).GetNestedType("Section", BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static readonly Type ItemType = (typeof(FileCache).Assembly.GetType("EmbedIO.Files.Internal.FileCacheItem", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        [Test]
        public void FirstItemCanBeEvictedAndSectionCanBeReused()
        {
            var section = NewSection();
            Add(section, "first", NewItem(section));
            Assert.That(Size(section), Is.GreaterThan(0));
            Assert.That(Evict(section), Is.GreaterThan(0));
            Assert.That(Size(section), Is.Zero);
            Add(section, "second", NewItem(section));
            Assert.That(Evict(section), Is.GreaterThan(0));
            Assert.That(Size(section), Is.Zero);
        }

        [Test]
        public void RefreshAndReplacementPreserveLeastRecentlyUsedOrder()
        {
            var section = NewSection();
            Add(section, "a", NewItem(section));
            Add(section, "b", NewItem(section));
            Add(section, "c", NewItem(section));
            Assert.That(Contains(section, "a"), Is.True);
            Add(section, "b", NewItem(section));
            var itemSize = Size(section) / 3;
            Assert.That(Evict(section), Is.EqualTo(itemSize));
            Assert.That(Contains(section, "c"), Is.False);
            Assert.That(Evict(section), Is.EqualTo(itemSize));
            Assert.That(Contains(section, "a"), Is.False);
            Assert.That(Evict(section), Is.EqualTo(itemSize));
            Assert.That(Contains(section, "b"), Is.False);
            Assert.That(Size(section), Is.Zero);
        }

        [TestCase("remove")]
        [TestCase("clear")]
        [TestCase("replace")]
        [TestCase("evict")]
        public void LateContentOnDetachedItemDoesNotIncreaseSectionSize(string detach)
        {
            var section = NewSection();
            var item = NewItem(section);
            Add(section, "file", item);
            if (detach == "clear") Call(section, "Clear");
            else if (detach == "replace") Add(section, "file", NewItem(section));
            else if (detach == "evict") Evict(section);
            else Call(section, "Remove", "file");
            var before = Size(section);
            Call(item, "SetContent", CompressionMethod.None, new byte[4096]);
            Assert.That(Size(section), Is.EqualTo(before));
            if (detach == "replace") Evict(section);
            Add(section, "new", NewItem(section));
            Evict(section);
            Assert.That(Size(section), Is.Zero);
        }

        [Test]
        public void ConcurrentReplacementAndContentUpdatesLeaveNoGhostSize()
        {
            var section = NewSection();
            Parallel.For(0, 1000, i =>
            {
                var item = NewItem(section);
                Add(section, "file", item);
                Call(item, "SetContent", CompressionMethod.None, new byte[128 + i % 17]);
                Call(item, "SetContent", CompressionMethod.Gzip, new byte[32]);
            });
            Assert.That(Evict(section), Is.GreaterThan(0));
            Assert.That(Size(section), Is.Zero);
        }

        [Test]
        public void RemovingASectionTwiceKeepsOtherSectionsCleanerAlive()
        {
            var cache = new FileCache();
            Call(cache, "AddSection", "a");
            Call(cache, "AddSection", "b");
            try
            {
                Call(cache, "RemoveSection", "a");
                Call(cache, "RemoveSection", "a");
                var cleaner = typeof(FileCache).GetField("_cleaner", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That((cleaner ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(cache), Is.Not.Null);
                Call(cache, "RemoveSection", "b");
                Assert.That(cleaner.GetValue(cache), Is.Null);
                Assert.That(((typeof(FileCache)).GetField("_sectionCount", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(cache), Is.EqualTo(0));
            }
            finally
            {
                Call(cache, "RemoveSection", "a");
                Call(cache, "RemoveSection", "b");
            }
        }


        [Test]
        public async Task PurgeAcrossSectionsRetainsMostRecentlyUsedFiles()
        {
            var cache = new FileCache { MaxSizeKb = 3 };
            var a = Call(cache, "AddSection", "a");
            var b = Call(cache, "AddSection", "b");
            try
            {
                foreach (var entry in new[] { (Section: a, Path: "a1"), (Section: b, Path: "b1"),
                    (Section: a, Path: "a2"), (Section: b, Path: "b2") })
                {
                    var item = NewItem((entry.Section ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
                    Add(entry.Section, entry.Path, item);
                    Call(item, "SetContent", CompressionMethod.None, new byte[1024]);
                }
                Assert.That(Contains((a ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), "a1"), Is.True);
                await (Task)((((typeof(FileCache)).GetMethod("CheckMaxSize", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                    .Invoke(cache, new object[] { System.Threading.CancellationToken.None })) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
                Assert.That(Contains(a, "a1"), Is.True);
                Assert.That(Contains((b ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), "b2"), Is.True);
                Assert.That(Contains(a, "a2"), Is.False);
                Assert.That(Contains(b, "b1"), Is.False);
                Assert.That(Size(a) + Size(b), Is.LessThan(3 * 1024));
            }
            finally
            {
                Call(cache, "RemoveSection", "a");
                Call(cache, "RemoveSection", "b");
            }
        }

        [TestCase(CompressionMethod.None, CompressionMethod.Gzip)]
        [TestCase(CompressionMethod.None, CompressionMethod.Deflate)]
        [TestCase(CompressionMethod.Gzip, CompressionMethod.None)]
        [TestCase(CompressionMethod.Gzip, CompressionMethod.Deflate)]
        [TestCase(CompressionMethod.Deflate, CompressionMethod.None)]
        [TestCase(CompressionMethod.Deflate, CompressionMethod.Gzip)]
        public void CompressionConversionPreservesLargePayload(CompressionMethod sourceMethod, CompressionMethod targetMethod)
        {
            var payload = new byte[200000];
            new Random(42).NextBytes(payload);
            var source = Encode(payload, sourceMethod);
            var convert = ((typeof(FileCache)).Assembly.GetType("EmbedIO.Internal.CompressionUtility", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                .GetMethod("ConvertCompression", BindingFlags.Public | BindingFlags.Static);
            var converted = (byte[])((convert ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { source, sourceMethod, targetMethod }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            Assert.That(Decode((converted), targetMethod), Is.EqualTo(payload));
        }

        [TestCase("remove")]
        [TestCase("clear")]
        [TestCase("replace")]
        [TestCase("evict")]
        public void BrotliContentAccountingDoesNotSurviveDetachment(string detach)
        {
            var section = NewSection();
            var item = NewItem(section);
            Add(section, "file", item);
            var supports = typeof(FileCache).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName.StartsWith(".NETCoreApp", StringComparison.Ordinal) == true;
            if (!supports)
            {
                var beforeUnsupported = Size(section);
                var error = Assert.Throws<TargetInvocationException>(() => Call(item, "SetContent", CompressionMethod.Brotli, new byte[257]));
                Assert.That(error?.InnerException, Is.TypeOf<NotSupportedException>());
                Assert.That(Size(section), Is.EqualTo(beforeUnsupported));
                Evict(section);
                Assert.That(Size(section), Is.Zero);
                return;
            }
            var baseline = Size(section);
            Call(item, "SetContent", CompressionMethod.Brotli, new byte[257]);
            Assert.That(Size(section), Is.GreaterThan(baseline + 257));
            Call(item, "SetContent", CompressionMethod.Brotli, null);
            Assert.That(Size(section), Is.EqualTo(baseline));
            Call(item, "SetContent", CompressionMethod.Brotli, new byte[257]);
            if (detach == "clear") Call(section, "Clear");
            else if (detach == "replace") Add(section, "file", NewItem(section));
            else if (detach == "evict") Evict(section);
            else Call(section, "Remove", "file");
            var afterDetach = Size(section);
            Call(item, "SetContent", CompressionMethod.Brotli, new byte[1024]);
            Assert.That(Size(section), Is.EqualTo(afterDetach));
            if (detach == "replace") Evict(section);
            Assert.That(Size(section), Is.Zero);
        }
        private static byte[] Encode(byte[] bytes, CompressionMethod method)
        {
            if (method == CompressionMethod.None) return bytes;
            using var output = new MemoryStream();
            using (Stream compressor = method == CompressionMethod.Gzip
                ? new GZipStream(output, CompressionMode.Compress, true)
                : new DeflateStream(output, CompressionMode.Compress, true))
                compressor.Write(bytes, 0, bytes.Length);
            return output.ToArray();
        }

        private static byte[] Decode(byte[] bytes, CompressionMethod method)
        {
            if (method == CompressionMethod.None) return bytes;
            using var input = new MemoryStream(bytes);
            using Stream decompressor = method == CompressionMethod.Gzip
                ? new GZipStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }

        private static object NewSection() => (Activator.CreateInstance(SectionType, true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static object NewItem(object section) => (Activator.CreateInstance(ItemType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { section, (object)DateTime.UtcNow, 0L }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static void Add(object section, string path, object item) => Call(section, "Add", path, item);
        private static long Size(object section) => (long)(Call(section, "GetTotalSize") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static long Evict(object section) => (long)(Call(section, "RemoveLeastRecentItem") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static bool Contains(object section, string path) => (bool)(Call(section, "TryGet", path, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static object? Call(object target, string name, params object?[] args)
            => ((target).GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(target, args);
    }
}
