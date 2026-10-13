using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestBindingCleanupTest
    {
        private static readonly Type BindingType = typeof(WebServer).Assembly.GetType("EmbedIO.WebApi.RequestFieldBinding", true)
            ?? throw new AssertionException("Field binding helper was not found.");
        private static readonly Func<NameValueCollection, string, bool, string, string?> GetString = BindingMethod("GetString")
            .CreateDelegate<Func<NameValueCollection, string, bool, string, string?>>();
        private static readonly Func<NameValueCollection, string, bool, string, string[]> GetStrings = BindingMethod("GetStrings")
            .CreateDelegate<Func<NameValueCollection, string, bool, string, string[]>>();
        private static readonly Func<NameValueCollection, string, bool, string, string, TypeConverter?, int> GetInt = BindingMethod("Get")
            .MakeGenericMethod(typeof(int)).CreateDelegate<Func<NameValueCollection, string, bool, string, string, TypeConverter?, int>>();
        private static readonly Func<NameValueCollection, string, bool, string, TypeConverter?, int[]> GetInts = BindingMethod("GetArray")
            .MakeGenericMethod(typeof(int)).CreateDelegate<Func<NameValueCollection, string, bool, string, TypeConverter?, int[]>>();
        private static readonly Func<string?, Encoding> RequestEncoding = (typeof(WebServer).Assembly
            .GetType("EmbedIO.Net.Internal.HeaderUtility", true)?.GetMethod("GetEncoding")
            ?? throw new AssertionException("Request encoding helper was not found.")).CreateDelegate<Func<string?, Encoding>>();

        [TestCase("string")]
        [TestCase("strings")]
        [TestCase("int")]
        [TestCase("ints")]
        public void OptionalBindingDoesNotEnumerateUnrelatedKeys(string binding)
        {
            var data = new LookupOnlyCollection();
            for (var i = 0; i < 100; i++) data.Add("unrelated" + i, "unused");
            data.Add("value", "12");
            data.Add("value", "34");
            switch (binding)
            {
                case "string": Assert.That(GetString(data, "value", false, "query"), Is.EqualTo("34")); break;
                case "strings": Assert.That(GetStrings(data, "value", false, "form"), Is.EqualTo(new[] { "12", "34" })); break;
                case "int": Assert.That(GetInt(data, "value", false, "query", "value", new Int32Converter()), Is.EqualTo(34)); break;
                case "ints": Assert.That(GetInts(data, "value", false, "form", new Int32Converter()), Is.EqualTo(new[] { 12, 34 })); break;
            }
        }

        [TestCase("query")]
        [TestCase("form")]
        public void RequiredFieldRetainsCaseSensitivePresenceWhileOptionalLookupIgnoresCase(string kind)
        {
            var data = new NameValueCollection { { "VALUE", "first" }, { "VALUE", "last" } };
            Assert.That(GetString(data, "value", false, kind), Is.EqualTo("last"));
            var error = Assert.Throws<HttpException>(() => GetString(data, "value", true, kind));
            Assert.That(error?.Message, Is.EqualTo($"Missing {kind} field value."));
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("text/plain", null)]
        [TestCase("text/plain; charset=", null)]
        [TestCase("text/plain; charset=not-an-encoding", null)]
        [TestCase("text/plain; charset=ascii", "us-ascii")]
        [TestCase("text/plain; charset=\"utf-16\"", "utf-16")]
        public void RequestEncodingPreservesCharsetAndDefaultFallback(string? contentType, string? expected)
        {
            var encoding = RequestEncoding(contentType);
            if (expected == null) Assert.That(encoding, Is.SameAs(WebServer.DefaultEncoding));
            else Assert.That(encoding.WebName, Is.EqualTo(expected));
        }

        private static MethodInfo BindingMethod(string name)
            => BindingType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException($"Field binding method {name} was not found.");

        private sealed class LookupOnlyCollection : NameValueCollection
        {
            public override KeysCollection Keys => throw new AssertionException("Optional field binding must not enumerate unrelated keys.");
        }
    }
}
