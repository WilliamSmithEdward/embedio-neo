using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class HttpUpgradeProtocolsTest
    {
        private static readonly Type Protocols = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpUpgradeProtocols", true)
            ?? throw new AssertionException("Missing Upgrade parser.");
        private static IReadOnlyList<string> Parse(string field)
            => (IReadOnlyList<string>)(Protocols.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { field })
                ?? throw new AssertionException("Missing parser."));
        private static bool Matches(string requested, string selected)
            => (bool)(Protocols.GetMethod("Matches", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { requested, selected })
                ?? throw new AssertionException("Missing protocol matcher."));
        [TestCase("example/1.0, OTHER", new[] { "example/1.0", "OTHER" })]
        [TestCase(" , example,\tother/Version ,", new[] { "example", "other/Version" })]
        public void OfferedProtocolsPreserveOrderAndVersionSpelling(string field, string[] expected)
            => Assert.That(Parse(field), Is.EqualTo(expected));

        [TestCase("example/bad/version")]
        [TestCase("example/")]
        [TestCase("/version")]
        [TestCase("example, bad protocol")]
        [TestCase("example;parameter")]
        [TestCase("\"example\"")]
        [TestCase("éxample")]
        public void InvalidOfferDoesNotPermitPartialSelection(string field)
            => Assert.That(Parse(field), Is.Empty);

        [TestCase("Example", "example", true)]
        [TestCase("Example/V1", "example/V1", true)]
        [TestCase("Example/V1", "example/v1", false)]
        [TestCase("Example", "example/V1", false)]
        [TestCase("Example/V1", "example/V2", false)]
        [TestCase("Example/V1", "other/V1", false)]
        [TestCase("Example/V1", "Example//V1", false)]
        public void SelectionMatchesNamesWithoutCaseAndVersionsExactly(string offered, string selected, bool expected)
            => Assert.That(Matches(offered, selected), Is.EqualTo(expected));
    }
}
