using System;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Tests.Issues;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1ResponseModelTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ResetContentNeverEmitsContentOrChunkFraming(bool chunked)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            fixture.Response.StatusCode = 205;
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ABCDEF"));
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(boundary, Is.GreaterThan(0));
            Assert.That(wire[..boundary], Does.StartWith("HTTP/1.1 205 "));
            Assert.That(wire[(boundary + 4)..], Is.Empty, "205 responses cannot carry content.");
            Assert.That(wire[..boundary], Does.Contain("Content-Length: 0\r\n"));
            Assert.That(wire[..boundary], Does.Not.Contain("Transfer-Encoding:"));
        }

        // The engine keeps the head's fields in a list until something reads Headers, which builds
        // the WebHeaderCollection. Reading Headers first takes the collection path throughout; the
        // head on the wire and Headers after it must be the same either way.
        [TestCase(false, 200, false)]
        [TestCase(true, 200, false)]
        [TestCase(false, 404, true)]
        [TestCase(true, 201, true)]
        [TestCase(false, 204, false)]
        [TestCase(false, 205, false)]
        [TestCase(true, 304, false)]
        public async Task HeadAndHeadersMatchWhetherOrNotHeadersWasRead(bool chunked, int status, bool closing)
        {
            var list = await Respond(chunked, status, closing, false);
            var collection = await Respond(chunked, status, closing, true);
            Assert.That(list.Wire, Is.EqualTo(collection.Wire));
            Assert.That(list.Headers, Is.EqualTo(collection.Headers));
            Assert.That(list.Headers, Does.Contain("Server: "), "Headers afterwards holds the fields the engine added");
        }

        private static async Task<(string Wire, string Headers)> Respond(bool chunked, int status, bool closing, bool readFirst)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);

            if (readFirst) Assert.That(fixture.Response.Headers.Count, Is.EqualTo(chunked ? 0 : 1));

            fixture.Response.StatusCode = status;
            fixture.Response.ContentType = "application/json";

            if (closing) fixture.Response.KeepAlive = false;

            if (status is 200 or 201 or 404) await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ABCDEF"));

            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            var headers = new StringBuilder();

            for (var index = 0; index < fixture.Response.Headers.Count; index++)
                headers
                    .Append(fixture.Response.Headers.GetKey(index))
                    .Append(": ")
                    .AppendJoin("|", fixture.Response.Headers.GetValues(index) ?? [])
                    .Append('\n');

            return (WithoutDate(wire, "\r\n"), WithoutDate(headers.ToString(), "\n"));
        }

        // The Date field differs from one second to the next.
        private static string WithoutDate(string text, string lineEnd)
        {
            var start = text.IndexOf("Date: ", StringComparison.Ordinal);

            if (start < 0) return text;

            var end = text.IndexOf(lineEnd, start, StringComparison.Ordinal);
            return text[..(start + 6)] + text[end..];
        }
    }
}
