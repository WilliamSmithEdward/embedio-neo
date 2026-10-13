using System;
using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QueryStringFieldsParityTest
    {
        private static readonly Type Parser = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.QueryStringFields", true)
            ?? throw new AssertionException("Missing query parser.");
        private static readonly Func<string, NameValueCollection> Parse = (Parser.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing query parser entry point.")).CreateDelegate<Func<string, NameValueCollection>>();
        private static readonly Action<string, NameValueCollection> AddTo = (Parser.GetMethod("AddTo", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing query population entry point.")).CreateDelegate<Action<string, NameValueCollection>>();

        [TestCase("")]
        [TestCase("?")]
        [TestCase("?&")]
        [TestCase("?&&a=1&&")]
        [TestCase("?flag&empty=&=value&=")]
        [TestCase("?a=1&A=2&a=3&flag&flag")]
        [TestCase("?a=b=c&%3D=%26%3D")]
        [TestCase("?a+b=c+d&x=%2B%20&%61=second")]
        [TestCase("?utf8=%E2%82%AC&bad=%E2%82&literal=%GG%")]
        [TestCase("?encoded=%26x%3D1&nul=%00&spaces= %09 ")]
        [TestCase("a=1&flag&")]
        public void PairsMatchSplitSemantics(string query)
        {
            ArgumentNullException.ThrowIfNull(query);
            AssertPairs(Parse(query), Previous(query));
        }

        [Test]
        public void PopulatingAnExistingCollectionPreservesValuesAndOrdering()
        {
            var actual = new NameValueCollection { { "a", "before" }, { null, "first" } };
            var expected = new NameValueCollection(actual);
            const string query = "?A=after&flag&a=last&&=empty-name";
            expected.Add(Previous(query));
            AddTo(query, actual);
            AssertPairs(actual, expected);
            AddTo(string.Empty, actual);
            AssertPairs(actual, expected);
        }

        [Test]
        public void SeededPairsPreserveEncodedDelimitersAndMalformedEscapes()
        {
            var random = new Random(182);
            const string alphabet = "aA=&?+%20G9é";
            for (var sample = 0; sample < 1000; sample++)
            {
                var chars = new char[random.Next(80)];
                for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                var query = "?" + new string(chars);
                AssertPairs(Parse(query), Previous(query));
            }
        }

        private static NameValueCollection Previous(string query)
        {
            var result = new NameValueCollection();
            if (query.Length == 0) return result;
            foreach (var part in (query[0] == '?' ? query.Substring(1) : query).Split('&'))
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0) result.Add(null, WebUtility.UrlDecode(part));
                else result.Add(WebUtility.UrlDecode(part.Substring(0, equals)), WebUtility.UrlDecode(part.Substring(equals + 1)));
            }
            return result;
        }

        private static void AssertPairs(NameValueCollection actual, NameValueCollection expected)
        {
            Assert.That(actual.AllKeys, Is.EqualTo(expected.AllKeys));
            for (var i = 0; i < expected.Count; i++) Assert.That(actual.GetValues(i), Is.EqualTo(expected.GetValues(i)));
        }
    }
}
