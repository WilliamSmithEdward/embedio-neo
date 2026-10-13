using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class CharsetParsingParityTest
    {
        private static readonly Type HeaderType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HeaderUtility", true)
            ?? throw new AssertionException("Missing header helper.");
        private static readonly Func<string?, string?> Charset = (HeaderType.GetMethod("GetCharset", BindingFlags.Public | BindingFlags.Static)
            ?? throw new AssertionException("Missing charset parser.")).CreateDelegate<Func<string?, string?>>();
        private static readonly Func<string, string?> AttributeValue = (HeaderType.GetMethod("GetAttributeValue", BindingFlags.Public | BindingFlags.Static)
            ?? throw new AssertionException("Missing attribute parser.")).CreateDelegate<Func<string, string?>>();

        [Test]
        public void CharsetSegmentsPreserveValuesWhitespaceFirstMatchAndMalformedQuotes()
        {
            var values = new List<string?>
            {
                null, "", ";", "text/plain", "text/plain; charset=utf-8", "CHARSET=ascii",
                "text/plain;; charset=utf-8;", "text/plain; charset; charset=ascii",
                "text/plain; charset=; charset=ascii", "text/plain; charset= ; charset=ascii",
                "text/plain; charsetX=ascii", "text/plain; x-charset=ascii", "text/plain; charse=utf-8",
                "text/plain; charset=\"utf-8\"", "text/plain; charset='utf-8'", "charset=\"", "charset=x\"",
                "text/plain; charset=\"a;b\"; charset=ascii", "text/plain; note=\"charset=ascii\"; charset=utf-8",
                "text/plain; charset = \"utf-8\"", "charset=  \"", "charset=\"\"", "charset=\"x\"tail",
            };
            for (var code = char.MinValue; ; code++)
            {
                if (char.IsWhiteSpace(code))
                {
                    values.Add($"text/plain;{code}ChArSeT{code}={code}utf-8{code}");
                    values.Add($"{code}text/plain{code};{code}ignored=value{code};{code}charsetX=ascii{code}");
                }
                if (code == char.MaxValue) break;
            }
            var random = new Random(182);
            const string alphabet = "charsetCHARSETX=; \t\r\n\"'utf-8é\u00a0\u2003";
            for (var sample = 0; sample < 1500; sample++)
            {
                var suffix = new string(Enumerable.Range(0, random.Next(80)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                values.Add(suffix);
                values.Add("text/plain; charset=" + suffix);
            }
            foreach (var value in values)
                Assert.That(Observe(Charset, value), Is.EqualTo(Observe(PreviousCharset, value)), value);
        }

        private static (string? Value, Type? ExceptionType, string? Parameter) Observe(Func<string?, string?> parse, string? value)
        {
            try { return (parse(value), null, null); }
            catch (ArgumentException error) { return (null, error.GetType(), error.ParamName); }
        }

        private static string? PreviousCharset(string? contentType)
        {
            if (contentType == null) return null;
            var start = 0;
            while (true)
            {
                var separator = contentType.IndexOf(';', start);
                var part = contentType.Substring(start, (separator < 0 ? contentType.Length : separator) - start).Trim();
                if (part.StartsWith("charset", StringComparison.OrdinalIgnoreCase)) return AttributeValue(part);
                if (separator < 0) return null;
                start = separator + 1;
            }
        }
    }
}
