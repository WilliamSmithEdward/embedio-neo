using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1HeadReaderTest
    {
        private sealed class Reader
        {
            private static readonly Type Target = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http1HeadReader", true)
                ?? throw new AssertionException("Missing HTTP/1 head reader.");
            private static readonly MethodInfo ReadMethod = Target.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing reader operation.");
            private static readonly MethodInfo ResetMethod = Target.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing reader reset.");
            private readonly object _reader = Activator.CreateInstance(Target) ?? throw new AssertionException("Missing reader instance.");
            internal void Reset() => ResetMethod.Invoke(_reader, null);
            internal (string Result, int Used, string? Line) Read(byte[] bytes, int offset, int count)
            {
                object?[] args = { bytes, offset, count, 0, null };
                object? result;
                try { result = ReadMethod.Invoke(_reader, args); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
                return (result?.ToString() ?? "missing", (int)(args[3] ?? -1), (string?)args[4]);
            }
        }

        private static (bool Complete, int Used, List<string> Lines) Parse(Reader reader, byte[] bytes, int fragment)
        {
            var offset = 0;
            var lines = new List<string>();
            while (offset < bytes.Length)
            {
                var available = Math.Min(fragment, bytes.Length - offset);
                while (available > 0)
                {
                    var item = reader.Read(bytes, offset, available);
                    Assert.That(item.Used, Is.InRange(0, available));
                    offset += item.Used;
                    available -= item.Used;
                    if (item.Result == "Complete") return (true, offset, lines);
                    if (item.Result == "NeedMoreData") Assert.That(available, Is.Zero);
                    else lines.Add(item.Result + ":" + item.Line);
                    Assert.That(item.Used, Is.GreaterThan(0));
                }
            }
            return (false, offset, lines);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(7)]
        [TestCase(8192)]
        public void FragmentationPreservesLatin1AndLeavesBodyUntouched(int fragment)
        {
            const string head = "\r\n\r\nPOST / HTTP/1.1\r\nX-Value: éÿ\r\nContent-Length: 4\r\n\r\n";
            var bytes = Encoding.Latin1.GetBytes(head + "\0\r\nXGET /next HTTP/1.1\r\n\r\n");
            var reader = new Reader();
            var parsed = Parse(reader, bytes, fragment);
            Assert.That(parsed.Complete, Is.True);
            Assert.That(parsed.Used, Is.EqualTo(head.Length));
            Assert.That(parsed.Lines, Is.EqualTo(new[] { "RequestLine:POST / HTTP/1.1", "Header:X-Value: éÿ", "Header:Content-Length: 4" }));
            var terminal = reader.Read(bytes, parsed.Used, bytes.Length - parsed.Used);
            Assert.That(terminal.Result, Is.EqualTo("Complete"));
            Assert.That(terminal.Used, Is.Zero);
        }

        [TestCase("GET / HTTP/1.1\n", 1)]
        [TestCase("GET / HTTP/1.1\n", 8192)]
        [TestCase("GET / HTTP/1.1\rX", 1)]
        [TestCase("GET / HTTP/1.1\rX", 8192)]
        [TestCase("GET / HTTP/1.1\r\nX: a\n", 1)]
        [TestCase("GET / HTTP/1.1\r\nX: a\n", 8192)]
        [TestCase("GET / HTTP/1.1\r\nX: a\rX", 1)]
        [TestCase("GET / HTTP/1.1\r\nX: a\rX", 8192)]
        public void InvalidLineEndingsAreTerminalUntilReset(string input, int fragment)
        {
            var reader = new Reader();
            Assert.That(() => Parse(reader, Encoding.ASCII.GetBytes(input), fragment), Throws.InstanceOf<InvalidDataException>());
            var valid = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
            Assert.That(() => Parse(reader, valid, fragment), Throws.InstanceOf<InvalidDataException>());
            reader.Reset();
            Assert.That(Parse(reader, valid, fragment).Complete, Is.True);
        }

        [TestCase(32767, 1)]
        [TestCase(32768, 1)]
        [TestCase(32769, 1)]
        [TestCase(32767, 65536)]
        [TestCase(32768, 65536)]
        [TestCase(32769, 65536)]
        public void HeadBudgetIncludesTerminatorsButExcludesBody(int length, int fragment)
        {
            const string prefix = "GET / HTTP/1.1\r\nX: ";
            var head = prefix + new string('a', length - prefix.Length - 4) + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head + new string('b', 65536));
            if (length > 32768)
                Assert.That(() => Parse(new Reader(), bytes, fragment), Throws.InstanceOf<InvalidDataException>());
            else
            {
                var parsed = Parse(new Reader(), bytes, fragment);
                Assert.That(parsed.Complete, Is.True);
                Assert.That(parsed.Used, Is.EqualTo(length));
            }
        }

        [TestCase(181)]
        [TestCase(9931)]
        [TestCase(20261008)]
        public void SeededMutationsHaveFragmentationIndependentOutcomes(int seed)
        {
            var random = new Random(seed);
            var corpus = new[]
            {
                "GET / HTTP/1.1\r\nHost: localhost\r\n\r\nbody\r\n",
                "\r\nPOST / HTTP/1.1\r\nX: éÿ\r\nContent-Length: 4\r\n\r\n\0abc",
                "GET / HTTP/1.1\r\nX: partial\r",
                "\r\n\r\n",
            };
            for (var iteration = 0; iteration < 2000; ++iteration)
            {
                var bytes = new List<byte>(Encoding.Latin1.GetBytes(corpus[random.Next(corpus.Length)]));
                var mutations = random.Next(9);
                for (var mutation = 0; mutation < mutations; ++mutation)
                {
                    var index = random.Next(bytes.Count + 1);
                    var value = (byte)random.Next(256);
                    switch (random.Next(3))
                    {
                        case 0: bytes.Insert(index, value); break;
                        case 1: if (index < bytes.Count) bytes.RemoveAt(index); break;
                        default: if (index < bytes.Count) bytes[index] = value; break;
                    }
                }
                var input = bytes.ToArray();
                var expected = Outcome(input, Math.Max(1, input.Length));
                foreach (var fragment in new[] { 1, 2, random.Next(3, 34) })
                    Assert.That(Outcome(input, fragment), Is.EqualTo(expected),
                        $"Seed {seed}, iteration {iteration}, fragment {fragment}, input {Convert.ToHexString(input)}");
            }
        }

        private static string Outcome(byte[] bytes, int fragment)
        {
            try
            {
                var result = Parse(new Reader(), bytes, fragment);
                return result.Complete + ":" + result.Used + ":" + string.Join("\n", result.Lines);
            }
            catch (InvalidDataException)
            {
                return "Rejected";
            }
        }

        [Test]
        public void ResetDiscardsPartialLineAndRequestState()
        {
            var reader = new Reader();
            var partial = Encoding.ASCII.GetBytes("GET /old HTTP/1.1\r\nX: partial\r");
            Assert.That(Parse(reader, partial, 1).Complete, Is.False);
            reader.Reset();
            var next = Parse(reader, Encoding.ASCII.GetBytes("GET /new HTTP/1.1\r\n\r\n"), 1);
            Assert.That(next.Complete, Is.True);
            Assert.That(next.Lines, Is.EqualTo(new[] { "RequestLine:GET /new HTTP/1.1" }));
        }
    }
}
