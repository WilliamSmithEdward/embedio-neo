using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2RequestHeadersTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type Type(string name) => (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2." + name, true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static readonly string[] Basic = { ":method", "GET", ":scheme", "https", ":authority", "example.com", ":path", "/" };
        private static object? Invoke(string method, string[] pairs, bool end = false, bool extended = false)
        {
            var fields = Array.CreateInstance(Type("HpackField"), pairs.Length / 2);
            for (var i = 0; i < pairs.Length; i += 2)
                fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags, null, new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
            var block = (Activator.CreateInstance(Type("Http2HeaderBlock"), Flags, null, new object[] { 3, end, fields, 0u }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            try { return ((Type("Http2RequestHeaders").GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(null, method == "Parse" ? new object[] { block, extended } : new[] { block })); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
        }
        private static T Property<T>(object? value, string name) => (T)(((value ?? throw new AssertionException("Expected parsed request headers.")).GetType().GetProperty(name) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(value) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static string[] Extra(params string[] pairs) => Basic.Concat(pairs).ToArray();
        private static void Reject(string[] pairs, bool end = false, bool extended = false)
        {
            var error = (Assert.Catch<IOException>(() => Invoke("Parse", pairs, end, extended)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(error, "ErrorCode"), Is.EqualTo(1u));
            Assert.That(Property<int>(error, "StreamId"), Is.EqualTo(3));
        }

        [Test]
        public void OrdinaryRequestNormalizesHostAndCookieCrumbs()
        {
            var value = Invoke("Parse", Extra("cookie", "a=b", "cookie", "c=d", "te", "trailers"), true);
            var headers = Property<NameValueCollection>(value, "Headers");
            Assert.That(headers["Cookie"], Is.EqualTo("a=b; c=d"));
            Assert.That(headers["Host"], Is.EqualTo("example.com"));
            Assert.That(Property<string>(value, "Method"), Is.EqualTo("GET"));
        }

        [TestCase("connection", "close")]
        [TestCase("proxy-connection", "keep-alive")]
        [TestCase("keep-alive", "timeout=10")]
        [TestCase("transfer-encoding", "chunked")]
        [TestCase("upgrade", "websocket")]
        [TestCase("te", "gzip")]
        [TestCase("te", "trailers, gzip")]
        [TestCase("Bad", "value")]
        [TestCase("bad:name", "value")]
        [TestCase("bad name", "value")]
        [TestCase("bad(name)", "value")]
        [TestCase("", "value")]
        [TestCase("x", " value")]
        [TestCase("x", "value\t")]
        [TestCase("x", "v\r\nx")]
        [TestCase("x", "v\0x")]
        [TestCase("x", "v\u007fx")]
        [TestCase("content-length", "-1")]
        [TestCase("content-length", "+1")]
        [TestCase("content-length", "1,2")]
        [TestCase("content-length", "1,")]
        [TestCase("content-length", "9223372036854775808")]
        [TestCase("content-length", "")]
        [TestCase("host", "evil.example")]
        public void InvalidFieldsAreStreamProtocolErrors(string name, string value) => Reject(Extra(name, value));

        [TestCase(":method")]
        [TestCase(":scheme")]
        [TestCase(":path")]
        public void RequiredPseudoHeadersCannotBeMissing(string name)
        {
            Reject(Basic.Chunk(2).Where(pair => pair[0] != name).SelectMany(pair => pair).ToArray());
        }

        [Test]
        public void DuplicateUnknownAndMisorderedPseudoHeadersAreRejected()
        {
            Reject(Extra(":method", "GET"));
            Reject(Extra(":status", "200"));
            Reject(Extra("x", "y", ":path", "/"));
        }

        [Test]
        public void IdenticalContentLengthsAreAcceptedButConflictsAndPrematureEndFail()
        {
            var value = Invoke("Parse", Extra("content-length", "003, 3", "content-length", "3"));
            Assert.That(Property<long?>(value, "ContentLength"), Is.EqualTo(3));
            Reject(Extra("content-length", "3", "content-length", "4"));
            Reject(Extra("content-length", "3"), true);
            Invoke("Parse", Extra("content-length", "0"), true);
        }

        [TestCase("EXAMPLE.COM:443")]
        [TestCase("example.com")]
        public void AuthorityComparisonNormalizesHostAndDefaultPort(string host) => Invoke("Parse", Extra("host", host));

        [TestCase("user@example.com")]
        [TestCase("example.com/path")]
        [TestCase("example.com:65536")]
        [TestCase("example.com?x")]
        [TestCase("")]
        public void InvalidAuthorityIsRejected(string authority)
        {
            var pairs = (string[])Basic.Clone(); pairs[5] = authority; Reject(pairs);
        }

        [Test]
        public void EmptyPortUsesTheSchemeDefault(
            [Values("http", "https")] string scheme,
            [Values("example.com", "[::1]")] string host,
            [Values(false, true)] bool hostFallback)
        {
            var pairs = new[] { ":method", "GET", ":scheme", scheme, ":path", "/" };
            pairs = pairs.Concat(hostFallback
                ? new[] { "host", host + ":" }
                : new[] { ":authority", host + ":", "host", host + (scheme == "http" ? ":80" : ":443") }).ToArray();
            var parsed = Invoke("Parse", pairs);
            Assert.That(Property<string>(parsed, "Authority"), Is.EqualTo(host + ":"));
            Assert.That(Property<NameValueCollection>(parsed, "Headers")["Host"], Is.EqualTo(host + ":"));
        }

        [TestCase("example.com:")]
        [TestCase("[::1]:")]
        public void ExtendedConnectUsesTheSchemeDefaultForAnEmptyPort(string authority)
        {
            var pairs = (string[])Basic.Clone(); pairs[1] = "CONNECT"; pairs[5] = authority;
            pairs = pairs.Concat(new[] { ":protocol", "websocket" }).ToArray();
            Assert.That(Property<string>(Invoke("Parse", pairs, extended: true), "Authority"), Is.EqualTo(authority));
        }

        [Test]
        public void ClassicConnectRequiresExplicitAuthorityPortAndNoSchemeOrPath()
        {
            var pairs = new[] { ":method", "CONNECT", ":authority", "[::1]:443" };
            Invoke("Parse", pairs);
            Reject(new[] { ":method", "CONNECT", ":authority", "example.com" });
            Reject(new[] { ":method", "CONNECT", ":authority", "example.com:" });
            Reject(new[] { ":method", "CONNECT", ":authority", "[::1]:" });
            Reject(pairs.Concat(new[] { ":path", "/" }).ToArray());
            Reject(pairs.Concat(new[] { ":scheme", "https" }).ToArray());
        }

        [Test]
        public void ExtendedConnectRequiresNegotiationAndAllPseudoHeaders()
        {
            var pairs = (string[])Basic.Clone(); pairs[1] = "CONNECT";
            pairs = pairs.Concat(new[] { ":protocol", "websocket" }).ToArray();
            Reject(pairs);
            Invoke("Parse", pairs, extended: true);
            Reject(pairs.Chunk(2).Where(pair => pair[0] != ":authority").SelectMany(pair => pair).ToArray(), extended: true);
            Reject(Extra(":protocol", "websocket"), extended: true);
        }

        [Test]
        public void HostFallbackAndOptionsAsteriskAreSupported()
        {
            var pairs = Basic.Chunk(2).Where(pair => pair[0] != ":authority").SelectMany(pair => pair).Concat(new[] { "host", "example.com" }).ToArray();
            Assert.That(Property<string>(Invoke("Parse", pairs), "Authority"), Is.EqualTo("example.com"));
            pairs = (string[])Basic.Clone(); pairs[1] = "OPTIONS"; pairs[7] = "*"; Invoke("Parse", pairs);
            pairs[1] = "GET"; Reject(pairs);
            pairs[7] = "/#fragment"; Reject(pairs);
        }

        [TestCase(":path", "/")]
        [TestCase("content-length", "0")]
        [TestCase("host", "example.com")]
        [TestCase("connection", "close")]
        public void InvalidTrailerFieldsAreRejected(string name, string value)
            => Assert.Catch<IOException>(() => Invoke("ValidateTrailers", new[] { name, value }, true));

        [Test]
        public void ValidTrailersMustTerminateRequest()
        {
            Invoke("ValidateTrailers", new[] { "x-checksum", "abc" }, true);
            Assert.Catch<IOException>(() => Invoke("ValidateTrailers", new[] { "x-checksum", "abc" }));
        }
    }
}
