using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ChunkedTrailerAllocationTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type StreamType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ChunkedRequestStream", true)
            ?? throw new AssertionException("Missing chunked reader.");
        private delegate bool Snapshot(out IReadOnlyList<KeyValuePair<string, string>> trailers);

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task TrailerTrimmingPreservesOctetsDuplicatesAndTheNextRequest(bool asynchronous, bool fragmented)
        {
            var values = new List<string> { "", " ", "\t", " \t ", " value ", "\tvalue\t", " a b\tc ", " \u00a0value\u00a0 ", new string(' ', 1000) };
            for (var value = 32; value <= 255; value++)
                if (value != 127) values.Add(" \t" + (char)value + "\t ");
            const string following = "GET /next HTTP/1.1\r\nHost: localhost\r\n\r\n";
            var encoded = "1\r\nz\r\n0\r\n" + string.Concat(values.Select(value => "X-Value:" + value + "\r\n")) + "\r\n";
            var bytes = Encoding.Latin1.GetBytes(encoded + following);
            using var source = new FragmentedSource(fragmented ? bytes : Array.Empty<byte>());
            using var input = Create(source, fragmented ? Array.Empty<byte>() : bytes);
            var snapshot = (StreamType.GetMethod("TryGetTrailers", Flags) ?? throw new AssertionException("Missing snapshot.")).CreateDelegate<Snapshot>(input);
            Assert.That(snapshot(out var initial), Is.False);
            Assert.That(initial, Is.Empty);
            var buffer = new byte[8];
            async Task<int> Read() => asynchronous ? await input.ReadAsync(buffer, 0, buffer.Length) : input.Read(buffer, 0, buffer.Length);
            Assert.That(await Read(), Is.EqualTo(1));
            Assert.That(buffer[0], Is.EqualTo((byte)'z'));
            Assert.That(snapshot(out _), Is.False);
            Assert.That(await Read(), Is.Zero);
            Assert.That(snapshot(out var trailers), Is.True);
            Assert.That(trailers.Select(field => field.Key), Is.All.EqualTo("X-Value"));
            Assert.That(trailers.Select(field => field.Value), Is.EqualTo(values.Select(value => value.Trim(' ', '\t'))));
            var mutable = trailers as IList<KeyValuePair<string, string>> ?? throw new AssertionException("Missing list view.");
            Assert.That(mutable.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => mutable[0] = new KeyValuePair<string, string>("changed", "changed"));
            var remaining = (ArraySegment<byte>)((StreamType.GetProperty("BufferedRemainder", Flags) ?? throw new AssertionException("Missing remainder.")).GetValue(input) ?? throw new AssertionException("Missing remainder."));
            if (fragmented)
            {
                Assert.That(remaining.Count, Is.Zero);
                Assert.That(source.Position, Is.EqualTo(Encoding.Latin1.GetByteCount(encoded)));
            }
            else Assert.That(Encoding.Latin1.GetString(remaining), Is.EqualTo(following));
            Assert.That(await Read(), Is.Zero);
        }

        [TestCase("X-Value: \vbad")]
        [TestCase("X-Value: \u007fbad")]
        [TestCase("X-Value: \rbroken")]
        [TestCase("X-Value: \nbroken")]
        [TestCase("Bad Name: value")]
        [TestCase("Host: localhost")]
        public void MalformedTrailersNeverPublishAPartialSnapshot(string invalid)
        {
            var bytes = Encoding.Latin1.GetBytes("0\r\nX-Value: accepted\r\n" + invalid + "\r\n\r\n");
            using var input = Create(Stream.Null, bytes);
            var snapshot = (StreamType.GetMethod("TryGetTrailers", Flags) ?? throw new AssertionException("Missing snapshot.")).CreateDelegate<Snapshot>(input);
            Assert.Throws<InvalidDataException>(() => _ = input.Read(new byte[1], 0, 1));
            Assert.That(snapshot(out var trailers), Is.False);
            Assert.That(trailers, Is.Empty);
        }

        private static Stream Create(Stream source, byte[] bytes) => (Stream)(Activator.CreateInstance(StreamType, Flags, null,
            new object[] { source, bytes, 0, bytes.Length }, null) ?? throw new AssertionException("Missing reader."));

        private sealed class FragmentedSource(byte[] bytes) : MemoryStream(bytes)
        {
            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }
        }
    }
}
