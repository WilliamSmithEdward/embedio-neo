using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerParserRegressionTest
    {
        private static readonly Type ConnectionType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true)!;
        private static readonly MethodInfo ReadLine = ConnectionType.GetMethod("ReadLine", BindingFlags.Instance | BindingFlags.NonPublic)!;

        [TestCaseSource(nameof(LineCases))]
        public void LinesPreserveByteMappingAndFragmentBoundaries(byte[] bytes, int fragmentSize, string[] expectedLines, string expectedPartial)
        {
            // Exercise the real line parser without creating sockets or changing listener timing.
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            ConnectionType.GetField("_connectionSync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, new object());
            var lines = new List<string>();
            for (var start = 0; start < bytes.Length; start += fragmentSize)
            {
                var count = Math.Min(fragmentSize, bytes.Length - start);
                var offset = 0;
                while (offset < count)
                {
                    var args = new object[] { bytes, start + offset, count - offset, 0 };
                    string? line;
                    try { line = (string?)ReadLine.Invoke(connection, args); }
                    catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
                    {
                        Assert.That(expectedPartial, Is.EqualTo("INVALID"));
                        return;
                    }
                    var used = (int)args[3];
                    Assert.That(used, Is.InRange(1, count - offset));
                    offset += used;
                    if (line != null) lines.Add(line);
                }
            }

            Assert.That(expectedPartial, Is.Not.EqualTo("INVALID"), "Malformed line must be rejected.");
            Assert.That(lines, Is.EqualTo(expectedLines));
            var partial = ConnectionType.GetField("_currentLine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)?.ToString() ?? string.Empty;
            Assert.That(partial, Is.EqualTo(expectedPartial));
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(128)]
        [TestCase(8192)]
        public void CompleteAndFragmentedHeadersPreserveBodyBoundary(int fragmentSize)
        {
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            ConnectionType.GetField("_connectionSync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, new object());
            var fieldFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var transportField = ConnectionType.GetField("<Stream>k__BackingField", fieldFlags)!;
            transportField.SetValue(connection, Stream.Null);
            ConnectionType.GetMethod("Init", fieldFlags)!.Invoke(connection, null);
            var buffered = (MemoryStream)ConnectionType.GetField("_ms", fieldFlags)!.GetValue(connection)!;
            buffered.Capacity = 8192;
            Array.Fill(buffered.GetBuffer(), (byte)'X');
            var bytes = Encoding.Latin1.GetBytes("\r\nPOST /items?q=42 HTTP/1.1\r\nHost: localhost\r\nX-Value: caf\u00e9\r\nContent-Length: 6\r\n\r\nabcdefignored");
            var process = ConnectionType.GetMethod("ProcessInput", fieldFlags)!;
            var offset = 0;
            var complete = false;
            while (offset < bytes.Length && !complete)
            {
                var count = Math.Min(fragmentSize, bytes.Length - offset);
                buffered.Write(bytes, offset, count);
                offset += count;
                complete = (bool)process.Invoke(connection, new object[] { buffered })!;
            }

            Assert.That(complete, Is.True);
            Assert.That(ConnectionType.GetField("_errorMessage", fieldFlags)!.GetValue(connection), Is.Null);
            var context = (IHttpContext)ConnectionType.GetField("_context", fieldFlags)!.GetValue(connection)!;
            Assert.That(context.Request.HttpMethod, Is.EqualTo("POST"));
            Assert.That(context.Request.RawUrl, Is.EqualTo("/items?q=42"));
            Assert.That(context.Request.Headers["X-Value"], Is.EqualTo("caf\u00e9"));
            using var transport = new MemoryStream(bytes, offset, bytes.Length - offset);
            transportField.SetValue(connection, transport);
            using var body = new MemoryStream();
            context.Request.InputStream.CopyTo(body);
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("abcdef"));
        }

        [TestCase(1, 0)]
        [TestCase(7, 0)]
        [TestCase(8192, 0)]
        [TestCase(1, 32000)]
        [TestCase(7, 32000)]
        [TestCase(8192, 32000)]
        public void HeadersBelowExistingBufferLimitPreserveValuesAcrossFragments(int fragment, int valueLength)
        {
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            ConnectionType.GetField("_connectionSync", fields)!.SetValue(connection, new object());
            ConnectionType.GetField("<Stream>k__BackingField", fields)!.SetValue(connection, Stream.Null);
            ConnectionType.GetMethod("Init", fields)!.Invoke(connection, null);
            using var buffered = (MemoryStream)ConnectionType.GetField("_ms", fields)!.GetValue(connection)!;
            var value = new string('a', valueLength);
            var bytes = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: localhost\r\nX-Value: {value}\r\n\r\n");
            var process = ConnectionType.GetMethod("ProcessInput", fields)!;
            var complete = false;
            for (var offset = 0; offset < bytes.Length && !complete; offset += fragment)
            {
                buffered.Write(bytes, offset, Math.Min(fragment, bytes.Length - offset));
                Assert.That(buffered.Length, Is.LessThanOrEqualTo(32768));
                complete = (bool)process.Invoke(connection, new object[] { buffered })!;
            }
            Assert.That(complete, Is.True);
            Assert.That(ConnectionType.GetField("_errorMessage", fields)!.GetValue(connection), Is.Null);
            var context = (IHttpContext)ConnectionType.GetField("_context", fields)!.GetValue(connection)!;
            Assert.That(context.Request.Headers["X-Value"], Is.EqualTo(value));
        }

        private static IEnumerable<TestCaseData> LineCases()
        {
            var cases = new[]
            {
                ("crlf", "header: value\r\n", new[] { "header: value" }, string.Empty),
                ("lf", "header: value\n", Array.Empty<string>(), "INVALID"),
                ("empty", "\r\n\r\n", new[] { string.Empty, string.Empty }, string.Empty),
                ("embedded-cr", "ab\rcd\r\r\n", Array.Empty<string>(), "INVALID"),
                ("leading-cr", "\rX\rY\nz\n", Array.Empty<string>(), "INVALID"),
                ("partial", "partial\r", Array.Empty<string>(), "partial"),
                ("consecutive", "foo\r\n\r\nbar\r\n", new[] { "foo", string.Empty, "bar" }, string.Empty),
            };
            foreach (var fragmentSize in new[] { 1, 2, 3, 7, 8192 })
            {
                foreach (var (name, input, lines, partial) in cases)
                {
                    yield return new TestCaseData(Encoding.Latin1.GetBytes(input), fragmentSize, lines, partial)
                        .SetName($"LinesPreserveByteMappingAndFragmentBoundaries({name},{fragmentSize})");
                }

                var values = Enumerable.Range(0, 256).Where(value => value != 10 && value != 13).Select(value => (byte)value).ToArray();
                var expected = new string(values.Select(value => (char)value).ToArray());
                yield return new TestCaseData(values.Concat(new byte[] { 13, 10 }).ToArray(), fragmentSize, new[] { expected }, string.Empty)
                    .SetName($"LinesPreserveByteMappingAndFragmentBoundaries(all-byte-values,{fragmentSize})");
            }
        }
    }
}
