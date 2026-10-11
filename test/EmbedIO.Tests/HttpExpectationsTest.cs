using System;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpExpectationsTest
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase(",", false)]
        [TestCase("100-continue", true)]
        [TestCase("100-CoNtInUe", true)]
        [TestCase(" \t100-continue\t ", true)]
        [TestCase(",100-continue,,", true)]
        [TestCase("100-continue,100-continue", true)]
        [TestCase("custom, 100-continue", true)]
        [TestCase("custom=\"x,100-continue,y\"", false)]
        [TestCase("custom=\"x,100-continue,y\",100-continue", true)]
        [TestCase("custom=\"x\\\",100-continue,y\"", false)]
        [TestCase("custom=\"x\\\",y\", 100-continue", true)]
        [TestCase("100-continue=yes", false)]
        [TestCase("100-continue;foo=bar", false)]
        [TestCase("x100-continue", false)]
        [TestCase("100-continueX", false)]
        [TestCase("\"100-continue\"", false)]
        [TestCase("custom=\"unterminated,100-continue", false)]
        [TestCase("custom=\"x\\", false)]
        public void OnlyBareListMembersRequestContinue(string? value, bool expected)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpExpectations", true)
                ?? throw new AssertionException("Missing expectation reader.");
            var contains = (type.GetMethod("ContainsContinue", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing expectation operation.")).CreateDelegate<Func<string?, bool>>();
            Assert.That(contains(value), Is.EqualTo(expected));
        }
    }
}
