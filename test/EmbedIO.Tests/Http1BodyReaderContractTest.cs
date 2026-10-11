using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1BodyReaderContractTest
    {
        public enum ReadKind { Synchronous, ArrayAsync, MemoryAsync, Span }

        [TestCase(ReadKind.Synchronous)]
        [TestCase(ReadKind.ArrayAsync)]
        [TestCase(ReadKind.MemoryAsync)]
        [TestCase(ReadKind.Span)]
        public async Task SlicedReadsPreserveTheBodyBoundaryAndDestinationGuards(ReadKind kind)
        {
            var body = Encoding.ASCII.GetBytes("body-data");
            var following = Encoding.ASCII.GetBytes("GET /next HTTP/1.1\r\n\r\n");
            foreach (var buffered in new[] { 0, 3, body.Length })
            {
                var prefix = new byte[5 + buffered + (buffered == body.Length ? following.Length : 0)];
                body.AsSpan(0, buffered).CopyTo(prefix.AsSpan(5));
                if (buffered == body.Length) following.CopyTo(prefix, 5 + buffered);
                using var transport = new MemoryStream();
                transport.Write(body, buffered, body.Length - buffered);
                if (buffered != body.Length) transport.Write(following);
                transport.Position = 0;
                using var input = Reader(transport, prefix, 5, prefix.Length - 5, body.Length);
                using var received = new MemoryStream();
                var destination = new byte[7];
                Array.Fill(destination, (byte)0xa5);
                int count;
                while ((count = await Read(input, destination, kind)) != 0)
                {
                    Assert.That(destination[0], Is.EqualTo(0xa5));
                    Assert.That(destination[1], Is.EqualTo(0xa5));
                    Assert.That(destination[5], Is.EqualTo(0xa5));
                    Assert.That(destination[6], Is.EqualTo(0xa5));
                    received.Write(destination, 2, count);
                }
                Assert.That(received.ToArray(), Is.EqualTo(body));
                Assert.That(transport.Position, Is.EqualTo(body.Length - buffered));
                if (buffered != body.Length) Assert.That(transport.ReadByte(), Is.EqualTo((int)'G'));
                else
                {
                    var tail = (ArraySegment<byte>)(input.GetType().GetProperty("BufferedRemainder", Hidden)?.GetValue(input)
                        ?? throw new MissingMemberException("BufferedRemainder"));
                    Assert.That(tail.ToArray(), Is.EqualTo(following));
                }
            }
        }

        [TestCase(ReadKind.Synchronous)]
        [TestCase(ReadKind.ArrayAsync)]
        [TestCase(ReadKind.MemoryAsync)]
        [TestCase(ReadKind.Span)]
        public async Task TruncationRemainsTheSameFramingFailureAcrossReadPaths(ReadKind initialKind)
        {
            using var transport = new MemoryStream(new byte[] { 1, 2 });
            using var input = Reader(transport, Array.Empty<byte>(), 0, 0, 3);
            var destination = new byte[7];
            Assert.That(await Read(input, destination, initialKind), Is.EqualTo(2));
            var failure = await Assert.ThrowsAsync<EndOfStreamException>(async () => await Read(input, destination, initialKind));
            var origin = (failure?.StackTrace ?? throw new AssertionException("Missing initial framing failure stack."))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            foreach (var kind in Enum.GetValues<ReadKind>())
            {
                var repeated = await Assert.ThrowsAsync<EndOfStreamException>(async () => await Read(input, destination, kind));
                Assert.That(repeated, Is.SameAs(failure));
                Assert.That(repeated?.StackTrace, Does.Contain(origin), "Repeated reads must preserve the original failure location.");
            }
            var wrapped = new IOException("Application read wrapper", failure);
            var recognized = input.GetType().GetMethod("IsFramingError", Hidden)?.Invoke(input, new object[] { wrapped });
            Assert.That(recognized, Is.EqualTo(true));
        }

        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static Stream Reader(Stream transport, byte[] buffer, int offset, int count, long length)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.RequestStream", true) ?? throw new TypeLoadException();
            return (Stream)(Activator.CreateInstance(type, Hidden, null, new object[] { transport, buffer, offset, count, length }, null)
                ?? throw new InvalidOperationException("Body reader was not constructed."));
        }

        private static async Task<int> Read(Stream input, byte[] destination, ReadKind kind)
        {
            switch (kind)
            {
                case ReadKind.Synchronous: return input.Read(destination, 2, 3);
                case ReadKind.ArrayAsync: return await input.ReadAsync(destination, 2, 3, CancellationToken.None);
                case ReadKind.MemoryAsync: return await input.ReadAsync(destination.AsMemory(2, 3));
                case ReadKind.Span: return input.Read(destination.AsSpan(2, 3));
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
