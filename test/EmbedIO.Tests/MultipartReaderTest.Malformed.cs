using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class MultipartReaderTest
    {
        [TestCase("")]
        [TestCase("Content-Type: text/plain\r\n")]
        [TestCase("Content-Disposition: attachment; name=x\r\n")]
        [TestCase("Content-Disposition: form-data\r\n")]
        [TestCase("Content-Disposition: form-data; name=x; name=y\r\n")]
        [TestCase("Content-Disposition: form-data; name=x; filename=a; filename=b\r\n")]
        [TestCase("Content-Disposition: form-data; name=x; filename*=UTF-8''bad%GG\r\n")]
        [TestCase("Content-Disposition: form-data; name=\"unterminated\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\r\ncontent-disposition: form-data; name=y\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\r\nContent-Type: text/plain\r\ncontent-type: application/json\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\r\nContent-Transfer-Encoding: base64\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\r\n invalid-fold: value\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\r\nInvalid Header: value\r\n")]
        [TestCase("Content-Disposition: form-data; name=x\n")]
        public async Task MalformedHeadersReturnBadRequest(string headers)
        {
            using var input = new FragmentedStream(Wire(headers, "body"), 1);
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [Test]
        public async Task InvalidUtf8HeadersAreRejected()
        {
            var bytes = Join(Encoding.ASCII.GetBytes("--" + Boundary + "\r\nContent-Disposition: form-data; name=\""),
                new byte[] { 0xC0, 0xAF }, Encoding.ASCII.GetBytes("\"\r\n\r\nbody\r\n--" + Boundary + "--"));
            using var input = new MemoryStream(bytes);
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [Test]
        public async Task NonAsciiPreambleCannotForgeQuestionMarkBoundary()
        {
            var bytes = Join(Encoding.ASCII.GetBytes("--"), new byte[] { 0xFF },
                Encoding.ASCII.GetBytes("\r\n" + FieldHeader + "\r\nbody\r\n--?--"));
            using var input = new FragmentedStream(bytes, 1);
            using var reader = new MultipartReader(input, "?");
            // The only actual boundary is the closing delimiter: preceding opaque
            // text is preamble, not a section opened by the invalid byte.
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [TestCase(0)]
        [TestCase(32)]
        public async Task ValuelessFilenameCannotBeTreatedAsAField(int maxFiles)
        {
            using var input = new MemoryStream(Wire("Content-Disposition: form-data; name=x; filename\r\n", "body"));
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxFiles = maxFiles });
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        [TestCase(16)]
        [TestCase(40)]
        [TestCase(63)]
        public async Task TruncatedEnvelopeIsRejected(int length)
        {
            var wire = Wire(FieldHeader, "body");
            using var input = new MemoryStream(wire, 0, Math.Min(length, wire.Length));
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(6)]
        public async Task PartialClosingBoundaryIsRejected(int missing)
        {
            var wire = Wire(FieldHeader, "body", "");
            using var input = new MemoryStream(wire, 0, wire.Length - missing);
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [TestCase("")]
        [TestCase("trailing ")]
        [TestCase("bad\r\nboundary")]
        [TestCase("unicode-é")]
        [TestCase("bad@boundary")]
        public void InvalidBoundariesAreRejected(string boundary)
        {
            using var input = new MemoryStream();
            Assert.That(() => new MultipartReader(input, boundary), Throws.InstanceOf<ArgumentException>());
        }

        [TestCase(70, true)]
        [TestCase(71, false)]
        public void BoundaryLengthIsBounded(int length, bool valid)
        {
            using var input = new MemoryStream();
            if (valid)
            {
                using var reader = new MultipartReader(input, new string('a', length));
            }
            else
            {
                Assert.That(() => new MultipartReader(input, new string('a', length)), Throws.InstanceOf<ArgumentException>());
            }
        }

        [Test]
        public async Task QuotedEscapesAndUnicodeMetadataArePreserved()
        {
            using var input = new MemoryStream(Wire("content-disposition: form-data; name=\"café\"; filename=\"a\\\"b.txt\"\r\n", "body"));
            using var reader = new MultipartReader(input, Boundary);
            var section = await Next(reader);
            Assert.That(section.Name, Is.EqualTo("café"));
            Assert.That(section.FileName, Is.EqualTo("a\"b.txt"));
            Assert.That(section.Headers.ContainsKey("Content-Disposition"), Is.True);
            await Drain(reader);
        }

        [TestCase("UTF-8''caf%C3%A9.txt", "café.txt")]
        [TestCase("utf-8'en'%E2%82%AC.bin", "€.bin")]
        [TestCase("UTF-8''", "")]
        public async Task ExtendedUtf8FilenameTakesPrecedence(string value, string expected)
        {
            using var input = new MemoryStream(Wire("Content-Disposition: form-data; name=file; filename=fallback; filename*=" + value + "\r\n", "body"));
            using var reader = new MultipartReader(input, Boundary);
            Assert.That((await Next(reader)).FileName, Is.EqualTo(expected));
            await Drain(reader);
        }

        [TestCase("UTF-8''%")]
        [TestCase("UTF-8''%FF")]
        [TestCase("UTF-8''%00")]
        [TestCase("UTF-8''%0D%0A")]
        [TestCase("ISO-8859-1''x")]
        [TestCase("UTF-8'broken")]
        public async Task InvalidExtendedFilenameIsRejected(string value)
        {
            using var input = new MemoryStream(Wire("Content-Disposition: form-data; name=file; filename*=" + value + "\r\n", "body"));
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }

        [Test]
        public async Task ParseFailurePreventsResumingAtAnotherSection()
        {
            using var input = new MemoryStream(Wire("Bad header\r\n", "body\r\n--" + Boundary + "\r\n" + FieldHeader + "\r\nsecond"));
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => reader.ReadNextSectionAsync(), Throws.InstanceOf<HttpException>());
            await Assert.ThatAsync(() => reader.ReadNextSectionAsync(), Throws.Exception);
        }

        [TestCase("--junk")]
        [TestCase(" junk")]
        [TestCase("\tjunk")]
        public async Task MalformedDelimiterTailsAreRejected(string tail)
        {
            // A MIME boundary cannot occur as a line prefix inside a part. Once a
            // delimiter suffix starts, non-padding trailing text is malformed.
            using var input = new FragmentedStream(Wire(FieldHeader, "body\r\n--" + Boundary + tail + "\r\nmore"), 1);
            using var reader = new MultipartReader(input, Boundary);
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(400));
        }
    }
}
