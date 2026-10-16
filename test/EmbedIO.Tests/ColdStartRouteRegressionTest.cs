using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EmbedIO.Routing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class ColdStartRouteRegressionTest
    {
        [TestCase("/", "/items", "/items")]
        [TestCase("/a/", "/a", "/")]
        [TestCase("/a/", "/a/", "/")]
        [TestCase("/a/", "/a/item", "/item")]
        [TestCase("/a/", "/ab/item", null)]
        [TestCase("/a/", "/A/item", null)]
        [TestCase("/a/", null, null)]
        [TestCase("/a/", "/a/line\nnext", "/line\nnext")]
        [TestCase("/a+b/", "/a+b/item", "/item")]
        [TestCase("/a+b/", "/ab/item", null)]
        [TestCase("/café/", "/café/item", "/item")]
        [TestCase("//a///", "/a/item", "/item")]
        public void ParameterlessBaseRouteRetainsSubpathAndMatching(string route, string? path, string? expected)
        {
            RouteMatcher.ClearCache();
            var matcher = RouteMatcher.Parse(route, true);
            var match = matcher.Match(path);
            if (expected == null) Assert.That(match, Is.Null);
            else
            {
                Assert.That((match ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SubPath, Is.EqualTo(expected));
                Assert.That(match.Path, Is.EqualTo(path));
                Assert.That(match.Count, Is.Zero);
            }
            Assert.That(RouteMatcher.Parse(route, true), Is.SameAs(matcher));
        }

        [Test]
        public void LegacyMutableParameterListStillHasItsOriginalRegexFallback()
        {
            RouteMatcher.ClearCache();
            var matcher = RouteMatcher.Parse("/compat/", true);
            var names = (List<string>)matcher.ParameterNames;
            names.Add("injected");
            var match = matcher.Match("/compat/item");
            Assert.That(match, Is.Not.Null);
            Assert.That(match.Count, Is.Zero);
            Assert.That(match.Names, Is.SameAs(names));
            Assert.That(match.SubPath, Is.EqualTo("/item"));
            names.Clear();
            Assert.That(((matcher).Match("/compat/item") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SubPath, Is.EqualTo("/item"));
        }
        [Test]
        public void LiteralTerminalRoutesRetainRegexMatchingSemantics()
        {
            var examples = new List<(string Template, string Literal)>
            {
                ("/", "/"), ("//items///", "/items"), ("/a+b.$^[]()|?*", "/a+b.$^[]()|?*"),
                ("/café/İ/ı/K", "/café/İ/ı/K"), ("/a{{b}", "/a{b}"),
                ("/{{{{x}}", "/{{x}}"), ("/a%2Fb+%20", "/a%2Fb+%20"), ("/a\n", "/a\n"),
            };
            var random = new Random(16102026);
            const string alphabet = "abAZ09{}.+*?[]()|^$%+ café\n\r\t";
            for (var sample = 0; sample < 256; sample++)
            {
                var chars = new char[random.Next(1, 50)];
                for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                var literal = "/" + new string(chars);
                examples.Add((literal.Replace("{", "{{", StringComparison.Ordinal), literal));
            }
            foreach (var (template, literal) in examples)
            {
                var matcher = RouteMatcher.Parse(template, false);
                var reference = new Regex("(?sn-imx)^" + Regex.Escape(literal) + "$", RegexOptions.CultureInvariant);
                foreach (var path in new[] { literal, literal + "\n", literal + "\n\n", literal + "\r\n", literal + "/", literal + "x", literal.ToUpperInvariant(), "x" + literal, string.Empty })
                {
                    var match = matcher.Match(path);
                    Assert.That(match != null, Is.EqualTo(reference.IsMatch(path)), $"Template: {template}, path: {path}");
                    if (match == null) continue;
                    Assert.That(match.Path, Is.EqualTo(path));
                    Assert.That(match.Names, Is.SameAs(matcher.ParameterNames));
                    Assert.That(match.Count, Is.Zero);
                    Assert.That(match.SubPath, Is.Null);
                }
                Assert.That(matcher.Match(null), Is.Null);
            }
        }

        [Test]
        public void LiteralTerminalRoutesRetainLegacyMutableParameterNames()
        {
            var matcher = RouteMatcher.Parse("/mutable-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), false);
            var names = (List<string>)matcher.ParameterNames;
            var before = matcher.Match(matcher.Route);
            names.Add("injected");
            try
            {
                var match = matcher.Match(matcher.Route + "\n");
                Assert.That(match, Is.Not.Null);
                Assert.That(match.Names, Is.SameAs(names));
                Assert.That(match.Count, Is.Zero);
                Assert.That(match.SubPath, Is.Null);
                Assert.That(before, Is.Not.Null);
                Assert.That(before.Names, Is.SameAs(names));
                Assert.That(matcher.Match(matcher.Route + "/"), Is.Null);
            }
            finally { names.Clear(); }
            Assert.That(matcher.Match(matcher.Route), Is.Not.Null);
        }

        [Test]
        public void LiteralTerminalRouteMatchesAreIndependentAcrossThreads()
        {
            var matcher = RouteMatcher.Parse("/concurrent-literal", false);
            Parallel.For(0, 1000, i =>
            {
                var path = i % 2 == 0 ? "/concurrent-literal" : "/concurrent-literal\n";
                var match = matcher.Match(path);
                Assert.That(match, Is.Not.Null);
                Assert.That(match.Path, Is.EqualTo(path));
                Assert.That(match.Count, Is.Zero);
                Assert.That(matcher.Match("/concurrent-literal/child"), Is.Null);
            });
        }
    }
}
