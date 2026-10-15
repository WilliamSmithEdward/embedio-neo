using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TcpProtocolInputTest
    {
        private const string Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(8192)]
        public async Task FragmentedPrefaceRetainsEveryFollowingProtocolByte(int fragment)
        {
            var wire = Encoding.ASCII.GetBytes(Preface + "following-frame-bytes");
            using var transport = new FragmentedInput(wire, fragment);
            var input = new byte[8192];
            var selection = await Select(transport, input);
            Assert.That(Value<bool>(selection, "IsHttp2"), Is.True);
            Assert.That(Value<bool>(selection, "Buffered"), Is.False);
            AssertRetained(transport, input, Value<int>(selection, "Count"), wire);
            Assert.That(transport.CanRead, Is.True, "Negotiation borrows the transport.");
        }

        [TestCase("POST / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 3\r\n\r\nabc")]
        [TestCase("PATCH / HTTP/1.1\r\nHost: localhost\r\n\r\n")]
        [TestCase("PRI / HTTP/1.1\r\nHost: localhost\r\n\r\n")]
        [TestCase("PRI * HTTP/2.1\r\nHost: localhost\r\n\r\n")]
        public async Task PrefaceMismatchReturnsAllInputToHttp1(string request)
        {
            var wire = Encoding.ASCII.GetBytes(request);
            using var transport = new FragmentedInput(wire, 1);
            var input = new byte[8192];
            var selection = await Select(transport, input);
            Assert.That(Value<bool>(selection, "IsHttp2"), Is.False);
            AssertRetained(transport, input, Value<int>(selection, "Count"), wire);
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(12)]
        [TestCase(23)]
        public async Task TruncatedCandidateFailsWithoutDisposingTransport(int length)
        {
            using var transport = new FragmentedInput(Encoding.ASCII.GetBytes(Preface.Substring(0, length)), 1);
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await Select(transport, new byte[8192]));
            Assert.That(transport.CanRead, Is.True);
        }

        private static async Task<object> Select(Stream transport, byte[] input)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpProtocolInput")
                ?? throw new AssertionException("Missing protocol input selector.");
            var method = type.GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing selector entry point.");
            var operation = method.Invoke(null, new object?[] { transport, input, false, true, false, null }) as Task
                ?? throw new AssertionException("Missing selection operation.");
            await operation;
            return operation.GetType().GetProperty("Result")?.GetValue(operation)
                ?? throw new AssertionException("Missing selection result.");
        }

        private static T Value<T>(object selection, string name)
            => selection.GetType().GetProperty(name, Hidden)?.GetValue(selection) is T value
                ? value : throw new AssertionException("Missing selection property: " + name);

        private static void AssertRetained(FragmentedInput transport, byte[] input, int count, byte[] wire)
        {
            using var retained = new MemoryStream();
            retained.Write(input, 0, count);
            transport.CopyTo(retained);
            Assert.That(retained.ToArray(), Is.EqualTo(wire));
        }

        private sealed class FragmentedInput : MemoryStream
        {
            private readonly int _fragment;
            internal FragmentedInput(byte[] bytes, int fragment) : base(bytes, false) => _fragment = fragment;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => base.ReadAsync(buffer, offset, Math.Min(count, _fragment), token);
        }
    }
}
