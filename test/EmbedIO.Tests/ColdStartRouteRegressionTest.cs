using System.Collections.Generic;
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
            var match = matcher.Match(path!);
            if (expected == null) Assert.That(match, Is.Null);
            else
            {
                Assert.That(match!.SubPath, Is.EqualTo(expected));
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
            Assert.That(match!.Count, Is.Zero);
            Assert.That(match.Names, Is.SameAs(names));
            Assert.That(match.SubPath, Is.EqualTo("/item"));
            names.Clear();
            Assert.That(matcher.Match("/compat/item")!.SubPath, Is.EqualTo("/item"));
        }
    }
}
