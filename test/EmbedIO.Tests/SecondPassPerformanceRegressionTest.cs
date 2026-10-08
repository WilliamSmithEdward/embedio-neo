using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class SecondPassPerformanceRegressionTest
    {
        private static readonly Assembly Core = typeof(WebServer).Assembly;
        private static readonly Regex Quality = new Regex(@";[ \t]*q=(?:(?:1(?:\.(?:0{1,3}))?)|(?:(0)(?:\.(\d{1,3}))?))[ \t]*(?:;|,|$)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        [Test]
        public void QualityParsingMatchesRecordedAlgorithmIncludingMalformedAndUnicodeValues()
        {
            var random = new Random(1729);
            const string alphabet = "gzipqQ;=,.0123456789 *\t\r\n\u0661\u0669";
            var cases = new List<string?> {
                null, "", "gzip, deflate, br", "gzip;q=0.001,deflate;q=1.000;extension=foo",
                "text/html;level=1;q=0.9", "gzip;q=0.9999", "gzip;q=.8", "gzip;Q=0.3",
                "gzip;q=0.\u0669", "gzip;q=0,gzip;q=1", " , , ", "gzip;q=0.5;ignored;q=0.1"
            };
            for (var i = 0; i < 2000; i++)
                cases.Add(new string(Enumerable.Range(0, random.Next(0, 60)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
            foreach (var text in cases)
            {
                var list = new QValueList(true, text);
                Assert.That(list.QValues, Is.EquivalentTo(ReferenceQuality(new[] { text })));
            }
            var headers = new[] { "gzip;q=0.5,deflate;q=0.5", null, "br, gzip;q=0.8" };
            Assert.That(new QValueList(true, headers).QValues, Is.EquivalentTo(ReferenceQuality(headers)));
            Assert.That(new QValueList(true, (IEnumerable<string?>?)null).QValues, Is.Empty);
        }


        [Test]
        public void QualityFractionParsingMatchesLegacyRegexForEveryUtf16Character()
        {
            for (var i = 0; i <= char.MaxValue; i++)
            {
                var text = "gzip;q=0." + (char)i + ";extension=ignored";
                Assert.That(new QValueList(true, text).QValues, Is.EquivalentTo(ReferenceQuality(new[] { text })), $"U+{i:X4}");
            }
            foreach (var text in new[] {
                "gzip;q=0.5\n", "gzip;q=1.000 \t\n", "gzip;q=0.5\r\n", "gzip;q=0.5\n\n",
                "gzip;q=0.5\n ", "gzip;q=0.5\n,deflate", "gzip;q=0.\U0001D7D8",
                "gzip;q=1.0000;q=0.5", "gzip;q=0.1234;q=1", "gzip;level=1;q=0.9;extension=q=0"
            })
                Assert.That(new QValueList(true, text).QValues, Is.EquivalentTo(ReferenceQuality(new[] { text })));
        }

        [Test]
        public void QualityCollectionsRemainIndependentAndNegotiationDoesNotExposeSharedChoices()
        {
            var first = new QValueList(true, "");
            var second = new QValueList(true, "");
            ((IDictionary<string, (int Weight, int Ordinal)>)first.QValues).Add("gzip", (1000, 0));
            Assert.That(second.QValues, Is.Empty);
            Assert.That(first.TryNegotiateContentEncoding(true, out var method, out var methodName), Is.True);
            Assert.That(method, Is.EqualTo(CompressionMethod.Gzip));
            Assert.That(methodName, Is.EqualTo("gzip"));
            Assert.That(second.TryNegotiateContentEncoding(true, out method, out _), Is.True);
            Assert.That(method, Is.EqualTo(CompressionMethod.None));
        }

        [TestCase("gzip,deflate", true, CompressionMethod.Gzip)]
        [TestCase("deflate,gzip", true, CompressionMethod.Deflate)]
        [TestCase("gzip;q=0.5,deflate;q=0.5", true, CompressionMethod.Gzip)]
        [TestCase("gzip,deflate", false, CompressionMethod.None)]
        [TestCase("identity;q=0,*;q=0.5", true, CompressionMethod.Gzip)]
        [TestCase("identity;q=0,*;q=0.5", false, CompressionMethod.Gzip)]
        public void NegotiationPreservesWeightsWildcardsAndTieOrder(string header, bool preferCompression, CompressionMethod expected)
        {
            Assert.That(new QValueList(true, header).TryNegotiateContentEncoding(preferCompression, out var actual, out _), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void HeaderTokenMatchingPreservesEmptyWhitespaceCultureAndInvalidComparisonBehavior()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "tr-TR" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    foreach (var header in new string?[] { null, "", ",", "Upgrade, keep-alive", " Upgrade , , custom ", "\u2003é, e\u0301, I,i,\"a,b\",last," })
                        foreach (var value in new string?[] { null, "", "Upgrade", "upgrade", " custom ", "é", "e\u0301", "I", "i", "last", "missing" })
                            foreach (var comparison in Enum.GetValues<StringComparison>())
                            {
                                var collection = new NameValueCollection();
                                if (header != null) collection.Add("value", header);
                                var expected = header?.Split(',').Any(token => string.Equals(token.Trim(), value?.Trim(), comparison)) ?? false;
                                Assert.That(collection.Contains("value", value, comparison), Is.EqualTo(expected));
                            }
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
            var populated = new NameValueCollection { ["value"] = "x,y" };
            Assert.Throws<ArgumentException>(() => populated.Contains("value", "z", (StringComparison)99));
            Assert.That(new NameValueCollection().Contains("missing", "z", (StringComparison)99), Is.False);
        }

        [Test]
        public void TokenValidationPreservesEveryUtf16CharacterAndErrorContracts()
        {
            const string allowed = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&'*+-.^_`|~";
            var validate = ((typeof(Validate)).GetMethod("IsRfc2616Token", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                .CreateDelegate<Func<string?, bool>>();
            for (var i = 0; i <= char.MaxValue; i++)
            {
                var character = (char)i;
                Assert.That(validate(character.ToString()), Is.EqualTo(allowed.IndexOf((character).ToString(), System.StringComparison.Ordinal) >= 0), $"U+{i:X4}");
            }
            Assert.That(validate(null), Is.False);
            Assert.That(validate(""), Is.False);
            Assert.That(Validate.Rfc2616Token("value", "valid-token+json"), Is.EqualTo("valid-token+json"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => Validate.Rfc2616Token("value", null)).ParamName, Is.EqualTo("value"));
            Assert.That(Assert.Throws<ArgumentException>(() => Validate.Rfc2616Token("value", "x/y")).ParamName, Is.EqualTo("value"));
        }

        [Test]
        public void RouteKeyLookupPreservesOrdinalAndNullNameBehavior()
        {
            var names = new string?[] { "id", null, "Name" };
            var match = (RouteMatch)(Activator.CreateInstance(typeof(RouteMatch), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object?[] { "/", names, new[] { "1", "2", "3" }, null }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            foreach (var key in new[] { "id", "ID", null, "Name", "name", "missing" })
                Assert.That(match.ContainsKey(key), Is.EqualTo(names.Any(name => name == key)));
            Assert.That(RouteMatch.None.ContainsKey("id"), Is.False);
        }

        [TestCase(125UL)]
        [TestCase(126UL)]
        [TestCase(255UL)]
        [TestCase(256UL)]
        [TestCase(65535UL)]
        [TestCase(65536UL)]
        [TestCase(4294967296UL)]
        [TestCase(9223372036854775807UL)]
        [TestCase(18446744073709551615UL)]
        public void WebSocketLengthsDecodeNetworkByteOrderWithoutChangingSource(ulong length)
        {
            var frameType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true);
            var frame = NewFrame();
            byte[] extended;
            if (length < 126) extended = Array.Empty<byte>();
            else if (length <= ushort.MaxValue) extended = BitConverter.GetBytes((ushort)length);
            else extended = BitConverter.GetBytes(length);
            if (BitConverter.IsLittleEndian) Array.Reverse(extended);
            var original = (byte[])extended.Clone();
            ((frameType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetProperty("PayloadLength") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(frame, (byte)(length < 126 ? length : length <= ushort.MaxValue ? 126UL : 127UL));
            ((frameType).GetProperty("ExtendedPayloadLength") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(frame, extended);
            Assert.That(((frameType).GetProperty("FullPayloadLength", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(frame), Is.EqualTo(length));
            Assert.That(extended, Is.EqualTo(original));
        }

        [TestCase(1000, null)]
        [TestCase(1001, "")]
        [TestCase(65535, "normal closure")]
        [TestCase(1000, "é\uD83D\uDE00")]
        [TestCase(1000, "\uD800")]
        public void WebSocketClosePayloadPreservesCodeUtf8AndUnpairedSurrogateFallback(int code, string? reason)
        {
            var payloadType = Core.GetType("EmbedIO.WebSockets.Internal.PayloadData", true);
            var append = (payloadType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetMethod("Append", BindingFlags.Static | BindingFlags.NonPublic);
            var expected = new byte[] { (byte)(code >> 8), (byte)code }.Concat(Encoding.UTF8.GetBytes(reason ?? "")).ToArray();
            var actual = (byte[])((append ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object?[] { (ushort)code, reason }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            Assert.That(actual, Is.EqualTo(expected));
            var payload = Activator.CreateInstance(payloadType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { actual }, null);
            Assert.That(((payloadType).GetProperty("Code", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(payload), Is.EqualTo((ushort)code));
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(16384, 16384, 4096)]
        [TestCase(16384, 4096, 4096)]
        [TestCase(16384, 4097, 4096)]
        [TestCase(12000, 12000, 4096)]
        [TestCase(12000, 11999, 4096)]
        [TestCase(16384, 0, 4096)]
        public async Task FrameReadsReturnOnlyAvailableBytesAndIndependentBuffers(int requested, int available, int bufferSize)
        {
            var read = ((((Core).GetType("EmbedIO.Internal.StreamExtensions", true)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                .GetMethod("ReadBytesAsync", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                .CreateDelegate<Func<Stream, int, int, Task<byte[]>>>();
            var source = new byte[available];
            new Random(1729).NextBytes(source);
            using var stream = new ShortReadStream(source);
            var result = await read(stream, requested, bufferSize);
            Assert.That(result, Is.EqualTo(source));
            stream.Position = 0;
            var second = await read(stream, requested, bufferSize);
            if (available > 0)
            {
                result[0] ^= 0xff;
                Assert.That(second, Is.EqualTo(source));
                Assert.That(source[0], Is.Not.EqualTo(result[0]));
            }
        }

        private static object NewFrame() => (Activator.CreateInstance((Core.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] {
                Enum.Parse((Core.GetType("EmbedIO.WebSockets.Internal.Fin", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), "Final"),
                Enum.Parse((Core.GetType("EmbedIO.WebSockets.Opcode", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), "Binary"), (object)Array.Empty<byte>(), false
            }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private static Dictionary<string, (int Weight, int Ordinal)> ReferenceQuality(IEnumerable<string?> headers)
        {
            var result = new Dictionary<string, (int Weight, int Ordinal)>();
            foreach (var text in headers)
            {
                if (string.IsNullOrEmpty(text)) continue;
                var ordinal = 0;
                for (var start = 0; start < text.Length; ordinal++)
                {
                    var end = text.IndexOf(',', start);
                    if (end < 0) end = text.Length;
                    var match = Quality.Match(text, start, end - start);
                    var value = text.Substring(start, (match.Success ? match.Index : end) - start).Trim();
                    var weight = 1000;
                    if (match.Success && match.Groups[1].Success)
                    {
                        weight = 0;
                        var digits = match.Groups[2].Value;
                        foreach (var digit in digits) weight = weight * 10 + digit - '0';
                        for (var i = digits.Length; i < 3; i++) weight *= 10;
                    }
                    if (value.Length > 0) result[value] = (weight, ordinal);
                    start = end + 1;
                }
            }
            return result;
        }

        private sealed class ShortReadStream(byte[] source) : MemoryStream(source)
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => base.ReadAsync(buffer, offset, Math.Min(count, 137), cancellationToken);
        }
    }
}
