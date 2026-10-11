using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2ResponseHeadersTest
    {
        private static object Validate(string[] pairs, string method = "GET", bool end = false)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var assembly = typeof(WebServer).Assembly;
            var fieldType = (assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            var fields = Array.CreateInstance(fieldType, pairs.Length / 2);
            for (var i = 0; i < pairs.Length; i += 2)
                fields.SetValue(Activator.CreateInstance(fieldType, flags, null, new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
            try { return (((assembly.GetType("EmbedIO.Net.Internal.Http2.Http2ResponseHeaders", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(null, new object[] { fields, method, end }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
        }
        [TestCase("101")]
        [TestCase("099")]
        [TestCase("600")]
        [TestCase("20")]
        [TestCase("2000")]
        [TestCase("2a0")]
        public void InvalidStatusCannotReachHpack(string status)
            => Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", status }));
        [TestCase("connection", "close")]
        [TestCase("te", "trailers")]
        [TestCase("transfer-encoding", "chunked")]
        [TestCase(":status", "201")]
        [TestCase(":path", "/")]
        [TestCase("content-length", "-1")]
        public void InvalidResponseFieldsAreRejected(string name, string value)
            => Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", "200", name, value }));
        [TestCase("103")]
        [TestCase("204")]
        public void ForbiddenContentLengthIsRejected(string status)
            => Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", status, "content-length", "0" }));
        [Test]
        public void InformationalResponseCannotEndStream()
        {
            Validate(new[] { ":status", "103" });
            Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", "103" }, end: true));
        }
        [TestCase("HEAD", "200")]
        [TestCase("GET", "304")]
        public void MetadataContentLengthDoesNotRequireData(string method, string status)
        {
            var result = Validate(new[] { ":status", status, "content-length", "123" }, method, true);
            Assert.That((result.GetType().GetProperty("BodyAllowed") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(result), Is.EqualTo(false));
        }
        [Test]
        public void OrdinaryResponseCannotEndBeforeDeclaredLength()
            => Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", "200", "content-length", "1" }, end: true));
        [Test]
        public void SuccessfulConnectForbidsContentLength()
            => Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", "200", "content-length", "0" }, "CONNECT"));
        [Test]
        public void ResetContentCannotHaveBody()
        {
            var result = Validate(new[] { ":status", "205", "content-length", "0" }, end: true);
            Assert.That((result.GetType().GetProperty("BodyAllowed") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(result), Is.EqualTo(false));
            Assert.Throws<InvalidDataException>(() => Validate(new[] { ":status", "205", "content-length", "1" }));
        }
    }
}
