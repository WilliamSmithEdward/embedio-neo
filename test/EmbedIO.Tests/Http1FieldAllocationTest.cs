using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1FieldAllocationTest
    {
        private delegate string? ReadField(string line, out string name, out string value);

        [Test]
        public void HeaderWhitespaceAndValidationMatchThePreviousParser()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http1RequestHead", true) ?? throw new AssertionException("Missing head parser.");
            var head = Activator.CreateInstance(type, true) ?? throw new AssertionException("Missing head.");
            var read = (type.GetMethod("ReadField", flags) ?? throw new AssertionException("Missing field parser.")).CreateDelegate<ReadField>(head);
            var fields = (NameValueCollection)((type.GetProperty("Fields", flags) ?? throw new AssertionException("Missing fields.")).GetValue(head) ?? throw new AssertionException("Missing fields."));
            var values = new List<string> { "", " ", "\t", " \t ", " value ", "\tvalue\t", " a b\tc ", "\u00a0value\u00a0", "\u2003value\u2003", new string(' ', 1000) };
            for (var value = 0; value <= 255; value++) values.Add(" \t" + (char)value + "\t ");
            var random = new Random(182);
            const string alphabet = "abc :;\t\r\n\0é\u00a0\u2003";
            for (var sample = 0; sample < 1500; sample++)
                values.Add(new string(Enumerable.Range(0, random.Next(80)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
            foreach (var input in values)
            {
                fields.Clear();
                var error = read("X-Test:" + input, out var name, out var value);
                if (input.Any(character => character < 32 && character != '\t' || character == 127))
                {
                    Assert.That(error, Is.EqualTo("Invalid header value."));
                    Assert.That(name, Is.Empty);
                    Assert.That(value, Is.Empty);
                    Assert.That(fields.Count, Is.Zero);
                }
                else
                {
                    Assert.That(error, Is.Null);
                    Assert.That(name, Is.EqualTo("X-Test"));
                    Assert.That(value, Is.EqualTo(input.Trim(' ', '\t')));
                    Assert.That(fields["X-Test"], Is.EqualTo(value));
                }
            }
        }
    }
}
