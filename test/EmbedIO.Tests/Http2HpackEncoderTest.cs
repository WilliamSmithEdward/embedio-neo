using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2HpackEncoderTest
    {
        private sealed class Encoder
        {
            private static readonly Type Type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackEncoder", true)!;
            private static readonly Type Field = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)!;
            private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
            private readonly object _instance;
            internal readonly Action<int> Maximum;
            internal Encoder(int limit = 32768)
            {
                _instance = Activator.CreateInstance(Type, Flags, null, new object[] { limit }, null)!;
                Maximum = Type.GetMethod("SetMaximumTableSize", Flags)!.CreateDelegate<Action<int>>(_instance);
            }
            internal byte[] Write(params (string Name, string Value, bool Sensitive)[] fields)
            {
                var input = Array.CreateInstance(Field, fields.Length);
                for (var i = 0; i < fields.Length; i++)
                    input.SetValue(Activator.CreateInstance(Field, Flags, null, new object[] { fields[i].Name, fields[i].Value, fields[i].Sensitive }, null), i);
                try { return (byte[])Type.GetMethod("Encode", Flags)!.Invoke(_instance, new object[] { input })!; }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
            }
        }

        [Test]
        public void EncoderMatchesPublishedHuffmanRequestSequence()
        {
            var encoder = new Encoder();
            Assert.That(Convert.ToHexString(encoder.Write((":method", "GET", false), (":scheme", "http", false), (":path", "/", false), (":authority", "www.example.com", false))).ToLowerInvariant(),
                Is.EqualTo("828684418cf1e3c2e5f23a6ba0ab90f4ff"));
            Assert.That(Convert.ToHexString(encoder.Write((":method", "GET", false), (":scheme", "http", false), (":path", "/", false), (":authority", "www.example.com", false), ("cache-control", "no-cache", false))).ToLowerInvariant(),
                Is.EqualTo("828684be5886a8eb10649cbf"));
            Assert.That(Convert.ToHexString(encoder.Write((":method", "GET", false), (":scheme", "https", false), (":path", "/index.html", false), (":authority", "www.example.com", false), ("custom-key", "custom-value", false))).ToLowerInvariant(),
                Is.EqualTo("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf"));
        }

        [TestCase("authorization", false)]
        [TestCase("proxy-authorization", false)]
        [TestCase("cookie", false)]
        [TestCase("set-cookie", false)]
        [TestCase("x-secret", true)]
        public void SensitiveFieldsAlwaysRemainNeverIndexed(string name, bool sensitive)
        {
            var encoder = new Encoder();
            var first = encoder.Write((name, "private", sensitive));
            Assert.That(first[0] & 0xf0, Is.EqualTo(0x10));
            Assert.That(encoder.Write((name, "private", sensitive)), Is.EqualTo(first));
        }

        [Test]
        public void CapacityReductionIsEmittedBeforeRestorationAndClearsPriorEntries()
        {
            var encoder = new Encoder();
            var first = encoder.Write(("x", "v", false));
            Assert.That(encoder.Write(("x", "v", false)), Is.EqualTo(new byte[] { 0xbe }));
            encoder.Maximum(0);
            encoder.Maximum(4096);
            Assert.That(encoder.Write(), Is.EqualTo(Convert.FromHexString("203fe11f")));
            Assert.That(encoder.Write(("x", "v", false)), Is.EqualTo(first));
        }

        [Test]
        public void InvalidFieldsDoNotMutatePreviouslyEncodedState()
        {
            var encoder = new Encoder();
            encoder.Write(("x", "v", false));
            Assert.That(() => encoder.Write(("y", "new", false), ("x", "\u0100", false)), Throws.TypeOf<ArgumentException>());
            Assert.That(encoder.Write(("x", "v", false)), Is.EqualTo(new byte[] { 0xbe }));
            Assert.That(() => new Encoder(33).Write(("x", "v", false)), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void IncompressibleOctetsUseRawLiteralEncoding()
        {
            var encoder = new Encoder();
            Assert.That(encoder.Write(("x", "\u00ff", false)), Is.EqualTo(Convert.FromHexString("40017801ff")));
        }
    }
}
