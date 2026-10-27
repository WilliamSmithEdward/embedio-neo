using System;
using System.Collections.Specialized;
using System.Net;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The response's field list stands in for the WebHeaderCollection until something reads
    // Headers. Random sequences of the engine's operations, valid and invalid, run against both
    // and must give the same results, the same exceptions and the same collection afterwards.
    public class ResponseHeaderFieldsTest
    {
        private const string Type = "EmbedIO.Net.Internal.ResponseHeaderFields";

        private static readonly string?[] Names =
        [
            "Content-Type", "content-type", "CONTENT-LENGTH", "Content-Length", "Date", "Server", "Keep-Alive",
            "Alt-Svc", "Connection", "Transfer-Encoding", "Trailer", "Upgrade", "Vary", "Set-Cookie", "Cache-Control",
            "X-Custom", "x-custom", "Capsule-Protocol", "Content-Encoding", "set-cookie", "Bad Name", "a'b", "", "é",
            "Na:me", "Tab\tName", null
        ];

        private static readonly string?[] Values =
        [
            "text/plain", "text/html; charset=utf-8", "0", "13", "keep-alive", "close", "chunked", "a, b",
            "x=1, y=2", "Accept-Encoding, Origin", "\"a,b\", c", "timeout=15, max=100", " lead", "trail ", "",
            "tab\there", "ctl\u0001", "line\r\n next", "line\r\nbad", "été", "\u0001", "  ", "Accept-Encoding", "gzip",
            "a=1", "\"quoted\"", null
        ];

        [Test]
        public void RandomOperationsMatchWebHeaderCollection()
        {
            for (var seed = 0; seed < 2000; seed++)
            {
                var random = new Random(seed);
                // Starting sizes below and above what a sequence needs, so the list also grows.
                var fields = EngineInternals.Create(Type, random.Next(0, 10));
                var reference = new WebHeaderCollection();
                // The base class takes null names, which the engine's callers could pass too.
                NameValueCollection view = reference;
                var operations = random.Next(1, 40);

                for (var step = 0; step < operations; step++)
                {
                    var name = Names[random.Next(Names.Length)];
                    var value = Values[random.Next(Values.Length)];
                    var label = $"seed {seed}, step {step}";

                    switch (random.Next(7))
                    {
                        case 0:
                            Same(() => view.Set(name, value), () => EngineInternals.Call(fields, "Set", name, value), label + " Set");
                            break;
                        case 1:
                            Same(() => view.Add(name, value), () => EngineInternals.Call(fields, "Add", name, value), label + " Add");
                            break;
                        case 2:
                            Same(() => view.Remove(name), () => EngineInternals.Call(fields, "Remove", name), label + " Remove");
                            break;
                        case 3:
                            SameResult(() => view.Get(name), () => EngineInternals.Call(fields, "Value", name), label + " Value");
                            break;
                        case 4:
                            SameResult(() => view.GetValues(name), () => EngineInternals.Call(fields, "Values", name), label + " Values");
                            break;
                        case 5:
                            // A simple list's values are what the collection's GetValues(name) gives.
                            if (EngineInternals.Property(fields, "IsSimple") is true)
                                for (var index = 0; index < reference.Count; index++)
                                    Assert.That(
                                        reference.GetValues(reference.GetKey(index)),
                                        Is.EqualTo(new[] { reference.Get(index) }),
                                        label + " IsSimple"
                                    );
                            break;
                        default:
                            // Reading Headers builds the collection; the fields follow it from then on.
                            if (random.Next(4) == 0) _ = EngineInternals.Property(fields, "Collection");
                            break;
                    }

                    SameState(fields, reference, label);
                }

                var collection = (WebHeaderCollection)(EngineInternals.Property(fields, "Collection") ?? throw new AssertionException("Missing collection."));
                Assert.That(collection.AllKeys, Is.EqualTo(reference.AllKeys), $"seed {seed} keys");

                for (var index = 0; index < reference.Count; index++)
                    Assert.That(collection.GetValues(index), Is.EqualTo(reference.GetValues(index)), $"seed {seed} values {index}");
            }
        }

        [Test]
        public void EngineFieldsStayInTheListUntilHeadersIsRead()
        {
            var fields = EngineInternals.Create(Type, 9);
            EngineInternals.Call(fields, "Add", "Content-Type", "text/plain; charset=utf-8");
            EngineInternals.Call(fields, "Add", "Server", "EmbedIO");
            EngineInternals.Call(fields, "Add", "Date", "Mon, 26 Oct 2026 19:00:00 GMT");
            EngineInternals.Call(fields, "Set", "Content-Length", "13");
            EngineInternals.Call(fields, "Set", "content-length", "14");
            EngineInternals.Call(fields, "Add", "Connection", "keep-alive");
            EngineInternals.Call(fields, "Remove", "CONNECTION");
            Assert.That(EngineInternals.Field(fields, "_collection"), Is.Null, "plain engine writes keep the list");
            Assert.That(EngineInternals.Call(fields, "Values", "Set-Cookie"), Is.Null);
            Assert.That(EngineInternals.Field(fields, "_collection"), Is.Null, "an absent split field needs no collection");
            Assert.That(EngineInternals.Property(fields, "IsSimple"), Is.True);

            var collection = (WebHeaderCollection)(EngineInternals.Property(fields, "Collection") ?? throw new AssertionException("Missing collection."));
            Assert.That(collection.AllKeys, Is.EqualTo(new[] { "Content-Type", "Server", "Date", "Content-Length" }));
            Assert.That(collection["content-length"], Is.EqualTo("14"));
            Assert.That(EngineInternals.Property(fields, "Collection"), Is.SameAs(collection), "built once");

            EngineInternals.Call(fields, "Set", "X-After", "1");
            Assert.That(collection["X-After"], Is.EqualTo("1"), "writes after the collection exists go to it");
        }

        [Test]
        public void WritesTheListCannotHoldBuildTheCollection()
        {
            foreach (var (name, value) in new (string, string?)[]
            {
                ("Content-Type", " text/plain "), ("Content-Type", "text/é"), ("Content-Type", ""), ("Content-Type", null),
                ("X-Name'", "1"), ("Trailer", "a, b")
            })
            {
                var fields = EngineInternals.Create(Type, 7);

                try
                {
                    EngineInternals.Call(fields, "Set", name, value);
                }

                catch (ArgumentException)
                {
                    // An invalid name is refused by the collection, as it always was.
                }

                if (name == "Trailer")
                {
                    Assert.That(EngineInternals.Field(fields, "_collection"), Is.Null, "a plain list value stays in the list");
                    Assert.That(EngineInternals.Property(fields, "IsSimple"), Is.False, "a split field is not simple");
                    Assert.That(EngineInternals.Call(fields, "Values", "Trailer"), Is.EqualTo(new[] { "a", "b" }), "split as the collection splits it");
                    Assert.That(EngineInternals.Field(fields, "_collection"), Is.Not.Null);
                    continue;
                }

                Assert.That(EngineInternals.Field(fields, "_collection"), Is.Not.Null, $"{name}: {value}");
            }
        }

        // Both sides refuse invalid input with ArgumentException or a subclass; anything else fails
        // the test where it is thrown.
        private static void Same(Action reference, Action candidate, string label)
        {
            Exception? expected = null, actual = null;

            try
            {
                reference();
            }

            catch (ArgumentException error)
            {
                expected = error;
            }

            try
            {
                candidate();
            }

            catch (ArgumentException error)
            {
                actual = error;
            }

            Assert.That(actual?.GetType(), Is.EqualTo(expected?.GetType()), label + ": " + (actual ?? expected)?.Message);
        }

        private static void SameResult(Func<object?> reference, Func<object?> candidate, string label)
        {
            object? expected = null, actual = null;
            Exception? refused = null, failed = null;

            try
            {
                expected = reference();
            }

            catch (ArgumentException error)
            {
                refused = error;
            }

            try
            {
                actual = candidate();
            }

            catch (ArgumentException error)
            {
                failed = error;
            }

            Assert.That(failed?.GetType(), Is.EqualTo(refused?.GetType()), label + ": " + (failed ?? refused)?.Message);
            Assert.That(actual, Is.EqualTo(expected), label);
        }

        private static void SameState(object fields, WebHeaderCollection reference, string label)
        {
            Assert.That(EngineInternals.Property(fields, "Count"), Is.EqualTo(reference.Count), label + " Count");

            for (var index = 0; index < reference.Count; index++)
            {
                Assert.That(EngineInternals.Call(fields, "NameAt", index), Is.EqualTo(reference.GetKey(index)), label + " NameAt");
                Assert.That(EngineInternals.Call(fields, "ValueAt", index), Is.EqualTo(reference.Get(index)), label + " ValueAt");
            }
        }
    }
}
