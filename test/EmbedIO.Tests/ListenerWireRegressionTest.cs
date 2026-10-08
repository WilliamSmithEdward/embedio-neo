using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerWireRegressionTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ConnectionType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static readonly Func<int, bool, byte[]> Chunk = ((((typeof(WebServer)).Assembly.GetType("EmbedIO.Net.Internal.ResponseStream", true)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
            .GetMethod("GetChunkSizeBytes", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CreateDelegate<Func<int, bool, byte[]>>();
        private static readonly Func<string?, string?> Charset = ((((typeof(WebServer)).Assembly.GetType("EmbedIO.Net.Internal.HeaderUtility", true)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
            .GetMethod("GetCharset") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CreateDelegate<Func<string?, string?>>();

        [TestCaseSource(nameof(ChunkCases))]
        public void ChunkBytesMatchLegacyFormatAcrossCultures(int size, bool final)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "tr-TR", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    var expected = WebServer.DefaultEncoding.GetBytes($"{size:x}\r\n{(final ? "\r\n" : string.Empty)}");
                    Assert.That(Chunk(size, final), Is.EqualTo(expected), culture);
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("application/json", null)]
        [TestCase("text/plain; charset=utf-8", "utf-8")]
        [TestCase("text/plain; CHARSET = UTF-8", "UTF-8")]
        [TestCase("text/plain; charset=\"utf-8\"", "utf-8")]
        [TestCase("text/plain; charset= ;charset=utf-8", null)]
        [TestCase("text/plain; charset; charset=utf-8", null)]
        [TestCase("text/plain; charset=; charset=utf-8", null)]
        [TestCase("charset-extra=ascii", "ascii")]
        [TestCase("text/plain; x-charset=utf-8", null)]
        [TestCase("text/plain;; charset=utf-8;", "utf-8")]
        [TestCase("text/plain; charset='utf-8'", "'utf-8'")]
        [TestCase("text/plain; charset=utf-8; charset=ascii", "utf-8")]
        [TestCase("text/plain;\u2003charset\u00a0=\u2003utf-8\u00a0", "utf-8")]
        [TestCase(";", null)]
        [TestCase("charset=x\"", "")]
        public void CharsetSelectionPreservesFirstMatchAndWhitespace(string? value, string? expected)
            => Assert.That(Charset(value), Is.EqualTo(expected));

        [TestCase("charset=\"")]
        public void MalformedQuotesPreserveExistingArgumentException(string value)
            => Assert.Throws<ArgumentOutOfRangeException>(() => Charset(value));

        [TestCase(false)]
        [TestCase(true)]
        public async Task MultipleChunkWritesPreserveEntireWireBody(bool asynchronous)
        {
            using var output = new MemoryStream();
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            ((ConnectionType).GetField("_connectionSync", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(connection, new object());
            ((ConnectionType).GetField("<Stream>k__BackingField", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(connection, output);
            ((ConnectionType).GetMethod("Init", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(connection, null);
            var context = (IHttpContext)((((ConnectionType).GetField("_context", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            (context).Response.SendChunked = true;
            var stream = context.Response.OutputStream;
            var expected = new StringBuilder();
            foreach (var size in new[] { 0, 1, 15, 16, 0, 255, 256, 4096, 16384, 20000 })
            {
                var body = Encoding.ASCII.GetBytes(new string('x', size));
                if (asynchronous) await stream.WriteAsync(body, 0, body.Length);
                else stream.Write(body, 0, body.Length);
                if (size != 0) expected.Append(size.ToString("x", CultureInfo.InvariantCulture)).Append("\r\n").Append(new string('x', size)).Append("\r\n");
            }
            stream.Dispose();
            var wire = Encoding.ASCII.GetString(output.ToArray());
            var headerEnd = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(headerEnd, Is.GreaterThan(0));
            Assert.That(wire.Substring(headerEnd + 4), Is.EqualTo(expected.Append("0\r\n\r\n").ToString()));
        }

        private static IEnumerable<TestCaseData> ChunkCases()
        {
            foreach (var size in new[] { 0, 1, 15, 16, 255, 256, 4096, 65535, int.MaxValue, int.MinValue, -1 })
                foreach (var final in new[] { false, true })
                    yield return new TestCaseData(size, final);
        }
    }
}
