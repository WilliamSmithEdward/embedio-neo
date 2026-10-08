using System;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpPriorityTest
    {
        private static (bool Success, int Urgency, bool Incremental) Parse(string field)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpPriority", true)
                ?? throw new AssertionException("Missing priority parser.");
            var arguments = new object?[] { field, null };
            var success = (bool)(type.GetMethod("TryParse", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, arguments)
                ?? throw new AssertionException("Missing parser result."));
            var value = arguments[1] ?? throw new AssertionException("Missing parsed value.");
            return (success, (int)(type.GetProperty("Urgency")?.GetValue(value) ?? throw new AssertionException("Missing urgency.")),
                (bool)(type.GetProperty("Incremental")?.GetValue(value) ?? throw new AssertionException("Missing incremental.")));
        }
        [TestCase("", 3, false)]
        [TestCase("u=0", 0, false)]
        [TestCase("u=7, i", 7, true)]
        [TestCase("i=?0,u=2", 2, false)]
        [TestCase("u=-0", 0, false)]
        [TestCase("u=8,i=?1", 3, true)]
        [TestCase("u=-1", 3, false)]
        [TestCase("u=3.0,i=1", 3, false)]
        [TestCase("u=\"1\",i=token", 3, false)]
        [TestCase("u=(1 2);x,i", 3, true)]
        [TestCase("u=1,u=7", 7, false)]
        [TestCase("u=1,u=token", 3, false)]
        [TestCase("i,i=9", 3, false)]
        [TestCase("u=2; ignored=\"x\",i; flag=?0", 2, true)]
        [TestCase("x=(token;a=1 3.14;z :YQ==:);p, u=6", 6, false)]
        [TestCase("x=:YQ:,u=5", 5, false)]
        [TestCase("x=::,i", 3, true)]
        [TestCase("x=@-123,u=4", 4, false)]
        [TestCase("x=%\"%c3%a9\",u=2", 2, false)]
        [TestCase("x=%\"%00%22%25\",i", 3, true)]
        [TestCase("x=\"a\\\"b\\\\c\",u=1", 1, false)]
        [TestCase(" u=6\t,\ti=?1 ", 6, true)]
        [TestCase("x=999999999999999,u=5", 5, false)]
        [TestCase("x=999999999999.999,u=4", 4, false)]
        [TestCase("*x=abc:/!#$%&'*+-.^_`|~,u=6", 6, false)]
        public void ValidDictionaryUsesLastMemberAndIgnoresUnknownTypes(string field, int urgency, bool incremental)
        {
            var result = Parse(field);
            Assert.That(result, Is.EqualTo((true, urgency, incremental)));
        }
        [TestCase("u=")]
        [TestCase("u=1,")]
        [TestCase("u =1")]
        [TestCase("u= 1")]
        [TestCase("U=1")]
        [TestCase("u=1,,i")]
        [TestCase("u=1\r\ni")]
        [TestCase("u=1;")]
        [TestCase("u=1;\tx")]
        [TestCase("x=(1\t2)")]
        [TestCase("x=((1))")]
        [TestCase("x=(1")]
        [TestCase("x=1.")]
        [TestCase("x=1.2345")]
        [TestCase("x=1234567890123.1")]
        [TestCase("x=1234567890123456")]
        [TestCase("x=@1.0")]
        [TestCase("x=?2")]
        [TestCase("x=\"a\\q\"")]
        [TestCase("x=\"\u00e9\"")]
        [TestCase("x=:a:")]
        [TestCase("x=:Y Q==:")]
        [TestCase("x=:YQ=Q:")]
        [TestCase("x=:YQ===:")]
        [TestCase("x=:YQ=:")]
        [TestCase("x=%\"%C3%a9\"")]
        [TestCase("x=%\"%c0%af\"")]
        [TestCase("x=%\"%ed%a0%80\"")]
        [TestCase("x=%\"%f4%90%80%80\"")]
        [TestCase("x=%\"%c3\"")]
        [TestCase("x=%\"%gg\"")]
        [TestCase("x=%\"%a\"")]
        [TestCase("x=%\"abc")]
        [TestCase("u=1;a=(bad)")]
        [TestCase("u=1\u0000")]
        [TestCase("x=abc[")]
        [TestCase("x=\"a\n\"")]
        public void MalformedDictionaryDoesNotRetainPartialPriority(string field)
        {
            var result = Parse("u=0,i," + field);
            Assert.That(result, Is.EqualTo((false, 3, false)));
        }
        [Test]
        public void IndependentHttpWorkingGroupDictionaryCorpus()
        {
            using var source = typeof(HttpPriorityTest).Assembly.GetManifestResourceStream("EmbedIO.Tests.Data.StructuredFields.dictionary.json")
                ?? throw new AssertionException("Missing pinned Structured Fields corpus.");
            using var document = JsonDocument.Parse(source);
            Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(432));
            foreach (var test in document.RootElement.EnumerateArray())
            {
                var raw = string.Join(",", test.GetProperty("raw").EnumerateArray().Select(x => x.GetString()));
                var parsed = Parse(raw);
                var mustFail = test.TryGetProperty("must_fail", out var mf) && mf.GetBoolean();
                var canFail = test.TryGetProperty("can_fail", out var cf) && cf.GetBoolean();
                var name = test.GetProperty("source").GetString() + ": " + test.GetProperty("name").GetString();
                if (!parsed.Success && canFail) continue;
                Assert.That(parsed.Success, Is.EqualTo(!mustFail), name);
                if (mustFail) continue;
                var urgency = 3; var incremental = false;
                foreach (var member in test.GetProperty("expected").EnumerateArray())
                {
                    var key = member[0].GetString();
                    var value = member[1][0];
                    if (key == "u") urgency = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                        && number >= 0 && number <= 7 ? number : 3;
                    if (key == "i") incremental = value.ValueKind == JsonValueKind.True;
                }
                Assert.That(parsed, Is.EqualTo((true, urgency, incremental)), name);
            }
        }

        [Test]
        public void LeadingTabIsNotInitialStructuredFieldWhitespace()
            => Assert.That(Parse("\tu=1").Success, Is.False);
        [Test]
        public void InputBudgetIsEnforcedBeforeParsing()
            => Assert.That(Parse(new string(' ', 16385)), Is.EqualTo((false, 3, false)));
    }
}
