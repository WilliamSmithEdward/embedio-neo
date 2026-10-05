using System;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Utilities
{
    public class UrlEncodedDataParserTest
    {
        [Test]
        public void DecodesUnicodeSpacesAndSeparatorsWithoutSplittingDecodedValues()
        {
            var data = UrlEncodedDataParser.Parse("?na%6De=hello+world&city=Z%C3%BCrich&literal=%2B%26%3D&token=a=b", false);
            Assert.That(data["name"], Is.EqualTo("hello world"));
            Assert.That(data["city"], Is.EqualTo("Zürich"));
            Assert.That(data["literal"], Is.EqualTo("+&="));
            Assert.That(data["token"], Is.EqualTo("a=b"));
        }

        [Test]
        public void RepeatedAndEncodedIndexedKeysRetainValueOrder()
        {
            var data = UrlEncodedDataParser.Parse("item%5B0%5D=first&item[]=second&item=third", false);
            Assert.That(data.AllKeys, Is.EqualTo(new[] { "item" }));
            Assert.That(data.GetValues("item"), Is.EqualTo(new[] { "first", "second", "third" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FlagsAndEmptyTokensRespectGrouping(bool groupFlags)
        {
            var data = UrlEncodedDataParser.Parse("flag&&key=&", groupFlags);
            Assert.That(data["key"], Is.Empty);
            if (groupFlags)
                Assert.That(data.GetValues(null), Is.EqualTo(new[] { "flag", "", "" }));
            else
            {
                Assert.That(data["flag"], Is.Empty);
                Assert.That(data.GetValues(""), Is.EqualTo(new[] { "", "" }));
            }
        }

        [TestCase("")]
        [TestCase("?")]
        [TestCase("a=1")]
        public void ReadOnlyResultsRejectMutationIncludingEmptyResults(string source)
        {
            var data = UrlEncodedDataParser.Parse(source, false, mutableResult: false);
            Assert.Throws<NotSupportedException>(() => data.Add("new", "value"));
            Assert.Throws<NotSupportedException>(() => data.Set("a", "changed"));
            Assert.Throws<NotSupportedException>(() => data.Remove("a"));
            Assert.Throws<NotSupportedException>(() => data.Clear());
        }

        [Test]
        public void DefaultResultsCanBeModified()
        {
            var data = UrlEncodedDataParser.Parse("a=1", false);
            data.Set("a", "2");
            data.Add("b", "3");
            Assert.That(data["a"], Is.EqualTo("2"));
            Assert.That(data["b"], Is.EqualTo("3"));
        }
    }
}
