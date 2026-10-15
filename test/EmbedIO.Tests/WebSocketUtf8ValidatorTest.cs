using System;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class WebSocketUtf8ValidatorTest
    {
        private static readonly UTF8Encoding Strict = new(false, true);
        private static readonly Type Validator = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.Utf8MessageValidator", true)
            ?? throw new InvalidOperationException("Missing UTF-8 validator.");
        private static readonly MethodInfo Validate = Validator.GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing validation method.");
        private static Func<byte[], bool, bool> Create()
        {
            var state = Activator.CreateInstance(Validator) ?? throw new InvalidOperationException("Missing validator state.");
            return Validate.CreateDelegate<Func<byte[], bool, bool>>(state);
        }

        [Test]
        public void EveryUnicodeScalarSurvivesOneByteFragmentsAndMessageReuse()
        {
            var validate = Create();
            Span<byte> encoded = stackalloc byte[4];
            var single = new byte[1];
            var scalars = 0;
            for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
            {
                if (!Rune.TryCreate(scalar, out var rune)) continue;
                var count = rune.EncodeToUtf8(encoded);
                for (var index = 0; index < count; index++)
                {
                    single[0] = encoded[index];
                    if (!validate(single, index == count - 1))
                        throw new AssertionException($"Rejected U+{scalar:X} at byte {index}.");
                    if (!validate(Array.Empty<byte>(), index == count - 1))
                        throw new AssertionException($"Rejected empty fragment after U+{scalar:X}, byte {index}.");
                }
                scalars++;
            }
            Assert.That(scalars, Is.EqualTo(1112064));
        }

        [Test]
        public void EveryOneAndTwoByteSequenceMatchesStrictDecoderAcrossBoundaries()
        {
            for (var first = 0; first <= 255; first++)
            {
                Compare(new[] { (byte)first }, 0);
                Compare(new[] { (byte)first }, 1);
                for (var second = 0; second <= 255; second++)
                    for (var split = 0; split <= 2; split++)
                        Compare(new[] { (byte)first, (byte)second }, split);
            }
        }

        [Test]
        public void EveryByteMutationOfBoundaryScalarsMatchesStrictDecoder()
        {
            foreach (var source in new[] { "C280", "DFBF", "E0A080", "ED9FBF", "EE8080", "EFBFBF", "F0908080", "F48FBFBF" })
            {
                var original = Convert.FromHexString(source);
                for (var position = 0; position < original.Length; position++)
                    for (var value = 0; value <= 255; value++)
                    {
                        var mutated = (byte[])original.Clone();
                        mutated[position] = (byte)value;
                        var padded = new byte[64 + mutated.Length];
                        Array.Fill(padded, (byte)'A');
                        mutated.CopyTo(padded, 32);
                        for (var split = 0; split <= mutated.Length; split++)
                        {
                            Compare(mutated, split);
                            Compare(padded, 32 + split);
                        }
                    }
            }
        }

        [Test]
        public void SeededLongerInputsMatchStrictDecoderIncludingIncompletePrefixes()
        {
            var random = new Random(6455);
            for (var iteration = 0; iteration < 20000; iteration++)
            {
                var bytes = new byte[random.Next(0, 65)];
                random.NextBytes(bytes);
                if ((iteration & 3) == 0)
                {
                    var text = new StringBuilder();
                    for (var index = 0; index < bytes.Length; index++)
                    {
                        var scalar = random.Next(0x110000);
                        if (Rune.TryCreate(scalar, out var rune)) text.Append(rune.ToString());
                    }
                    bytes = Strict.GetBytes(text.ToString());
                }
                Compare(bytes, random.Next(bytes.Length + 1));
            }
        }

        private static void Compare(byte[] bytes, int split)
        {
            var validate = Create();
            var decoder = Strict.GetDecoder();
            var characters = new char[bytes.Length + 1];
            var parts = new[] { bytes.AsSpan(0, split).ToArray(), Array.Empty<byte>(), bytes.AsSpan(split).ToArray() };
            for (var index = 0; index < parts.Length; index++)
            {
                var final = index == parts.Length - 1;
                var expected = true;
                try
                {
                    decoder.Convert(parts[index], 0, parts[index].Length, characters, 0, characters.Length,
                        final, out var consumed, out _, out var completed);
                    Assert.That(consumed, Is.EqualTo(parts[index].Length));
                    Assert.That(completed, Is.True);
                }
                catch (DecoderFallbackException) { expected = false; }
                var actual = validate(parts[index], final);
                if (actual != expected)
                    throw new AssertionException($"UTF-8 mismatch for {Convert.ToHexString(bytes)}, split {split}, part {index}: expected {expected}.");
                if (!expected) break;
            }
        }
    }
}
