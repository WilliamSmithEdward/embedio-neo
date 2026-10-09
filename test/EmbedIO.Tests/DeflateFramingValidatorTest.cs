using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class DeflateFramingValidatorTest
    {
        private sealed class Validator
        {
            private readonly Type _type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.DeflateFramingValidator")
                ?? throw new AssertionException("Missing DEFLATE framing validator.");
            private readonly object _instance;
            internal Validator() => _instance = Activator.CreateInstance(_type, true) ?? throw new AssertionException("Missing validator instance.");
            private object? Call(string method, params object[] arguments)
            {
                try { return (_type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new AssertionException("Missing validator method.")).Invoke(_instance, arguments); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
            }
            internal int Feed(byte[] bytes, int offset, int count) => (int)(Call("Feed", bytes, offset, count) ?? throw new AssertionException("Missing consumed count."));
            internal void Complete() => Call("Complete");
            internal ulong Output => (ulong)(_type.GetProperty("DecodedBytes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_instance) ?? throw new AssertionException("Missing output count."));
        }
        private static byte[] Encode(byte[] body, CompressionLevel level)
        {
            using var output = new MemoryStream();
            using (var encoder = new DeflateStream(output, level, true)) encoder.Write(body);
            return body.Length == 0 ? new byte[] { 3, 0 } : output.ToArray();
        }
        public static IEnumerable ValidCases()
        {
            foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
                foreach (var repeated in new[] { false, true })
                    foreach (var length in new[] { 0, 31, 65536 })
                        foreach (var chunk in new[] { 1, 13, 4096 })
                            yield return new object[] { level, repeated, length, chunk };
        }
        [TestCaseSource(nameof(ValidCases))]
        public void ExactEndAcrossBlocksAndFragmentedInput(CompressionLevel level, bool repeated, int length, int chunk)
        {
            var body = new byte[length];
            if (repeated) Array.Fill(body, (byte)0x41);
            else new Random(20261009).NextBytes(body);
            var raw = Encode(body, level);
            var wire = new byte[raw.Length + 8];
            raw.CopyTo(wire, 0);
            Array.Fill(wire, (byte)0x42, raw.Length, 8);
            var validator = new Validator();
            var offset = 0;
            while (offset < wire.Length)
            {
                var offered = Math.Min(chunk, wire.Length - offset);
                var consumed = validator.Feed(wire, offset, offered);
                offset += consumed;
                if (consumed != offered) break;
            }
            validator.Complete();
            Assert.That(offset, Is.EqualTo(raw.Length), "Trailer bytes must remain outside the DEFLATE boundary.");
            Assert.That(validator.Output, Is.EqualTo((ulong)body.Length));
        }
        [TestCase(CompressionLevel.NoCompression)]
        [TestCase(CompressionLevel.Fastest)]
        [TestCase(CompressionLevel.SmallestSize)]
        public void EveryProperPrefixIsTruncated(CompressionLevel level)
        {
            var raw = Encode(System.Text.Encoding.ASCII.GetBytes("truncation corpus with repeated repeated repeated text"), level);
            for (var count = 0; count < raw.Length; count++)
            {
                var validator = new Validator();
                validator.Feed(raw, 0, count);
                Assert.Throws<InvalidDataException>(validator.Complete, $"Prefix {count}/{raw.Length}");
                Assert.Throws<InvalidDataException>(() => validator.Feed(raw, count, raw.Length - count));
            }
        }
        [Test]
        public void RawBodyWithZlibHeaderPrefixIsStillValid()
        {
            var raw = new byte[166];
            raw[0] = 0x78; raw[1] = 0x9c; raw[3] = 0x63; raw[4] = 0xff;
            Array.Fill(raw, (byte)0x41, 5, 156);
            raw[161] = 1; raw[164] = 0xff; raw[165] = 0xff;
            var validator = new Validator();
            Assert.That(validator.Feed(raw, 0, raw.Length), Is.EqualTo(raw.Length));
            validator.Complete();
            Assert.That(validator.Output, Is.EqualTo(156UL));
        }
        private sealed class Bits
        {
            private readonly System.Collections.Generic.List<byte> _bytes = new();
            private int _current;
            private int _count;
            internal void Write(int value, int count)
            {
                for (var bit = 0; bit < count; bit++)
                {
                    _current |= ((value >> bit) & 1) << _count++;
                    if (_count == 8) { _bytes.Add((byte)_current); _current = 0; _count = 0; }
                }
            }
            internal byte[] Finish()
            {
                if (_count != 0) _bytes.Add((byte)_current);
                return _bytes.ToArray();
            }
        }
        public static IEnumerable InvalidCases()
        {
            yield return new object[] { "reserved-block", new byte[] { 7 } };
            yield return new object[] { "stored-length-complement", new byte[] { 1, 0, 0, 0, 0 } };
            yield return new object[] { "reserved-literal-count", new byte[] { 0xfd, 0, 0 } };
            foreach (var kind in new[] { "empty-code-tree", "incomplete-code-tree", "oversubscribed-code-tree", "repeat-without-previous" })
            {
                var bits = new Bits();
                bits.Write(5, 3);
                bits.Write(0, 14);
                for (var index = 0; index < 4; index++)
                    bits.Write(kind == "oversubscribed-code-tree" || kind == "repeat-without-previous" && index < 2 || kind == "incomplete-code-tree" && index == 3 ? 1 : 0, 3);
                if (kind == "repeat-without-previous") bits.Write(0, 1);
                yield return new object[] { kind, bits.Finish() };
            }
            var distance = new Bits();
            distance.Write(3, 3);
            distance.Write(64, 7); // Fixed length symbol 257, transmitted canonical code 0000001.
            distance.Write(0, 5); // Distance one before any history exists.
            yield return new object[] { "distance-before-history", distance.Finish() };
            var reservedLength = new Bits();
            reservedLength.Write(3, 3);
            reservedLength.Write(99, 8); // Reserved fixed symbol 286: canonical 11000110, reversed.
            yield return new object[] { "reserved-length-symbol", reservedLength.Finish() };
            var reservedDistance = new Bits();
            reservedDistance.Write(3, 3);
            reservedDistance.Write(64, 7);
            reservedDistance.Write(15, 5);
            yield return new object[] { "reserved-distance-symbol", reservedDistance.Finish() };
            var missingEnd = new Bits();
            missingEnd.Write(5, 3);
            missingEnd.Write(14 << 10, 14);
            for (var i = 0; i < 18; i++) missingEnd.Write(i == 3 || i == 17 ? 1 : 0, 3);
            for (var i = 0; i < 258; i++) missingEnd.Write(0, 1);
            yield return new object[] { "missing-end-of-block-symbol", missingEnd.Finish() };
            var overflowingRepeat = new Bits();
            overflowingRepeat.Write(5, 3);
            overflowingRepeat.Write(0, 14);
            for (var i = 0; i < 4; i++) overflowingRepeat.Write(i >= 2 ? 1 : 0, 3);
            for (var i = 0; i < 2; i++) { overflowingRepeat.Write(1, 1); overflowingRepeat.Write(127, 7); }
            yield return new object[] { "repeat-beyond-tree", overflowingRepeat.Finish() };
        }
        [Test]
        public void SeededDifferentialCorpusMatchesTheRuntimeOutputLength()
        {
            var random = new Random(20261009);
            for (var sample = 0; sample < 1000; sample++)
            {
                var body = new byte[random.Next(1, 8193)];
                random.NextBytes(body);
                if ((sample & 1) == 0)
                    for (var i = 0; i < body.Length; i++) body[i] &= 3;
                var level = sample % 3 == 0 ? CompressionLevel.NoCompression
                    : sample % 3 == 1 ? CompressionLevel.Fastest : CompressionLevel.SmallestSize;
                var raw = Encode(body, level);
                var validator = new Validator();
                var offset = 0;
                while (offset < raw.Length)
                {
                    var count = Math.Min(random.Next(1, 33), raw.Length - offset);
                    Assert.That(validator.Feed(raw, offset, count), Is.EqualTo(count));
                    offset += count;
                }
                validator.Complete();
                Assert.That(validator.Output, Is.EqualTo((ulong)body.Length));
                using var source = new MemoryStream(raw);
                using var decoder = new DeflateStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                decoder.CopyTo(output);
                Assert.That(output.ToArray(), Is.EqualTo(body));
            }
        }
        [TestCaseSource(nameof(InvalidCases))]
        public void InvalidStructureIsRejectedAndFailureIsSticky(string name, byte[] raw)
        {
            Assert.That(name, Is.Not.Empty);
            var validator = new Validator();
            Assert.Throws<InvalidDataException>(() => validator.Feed(raw, 0, raw.Length));
            Assert.Throws<InvalidDataException>(() => validator.Feed(Array.Empty<byte>(), 0, 0));
            Assert.Throws<InvalidDataException>(validator.Complete);
        }
    }
}



