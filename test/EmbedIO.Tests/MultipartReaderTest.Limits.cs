using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class MultipartReaderTest
    {
        [TestCase("file", 0)]
        [TestCase("file", 1)]
        [TestCase("field", 0)]
        [TestCase("field", 1)]
        [TestCase("total", 0)]
        [TestCase("total", 1)]
        [TestCase("headers", 0)]
        [TestCase("headers", 1)]
        public async Task ByteLimitsAcceptExactAndRejectOneByteOver(string dimension, int excess)
        {
            var headers = dimension == "file" ? FileHeader : FieldHeader;
            var wire = Wire(headers, "12345");
            var options = new MultipartOptions();
            switch (dimension)
            {
                case "file": options.MaxFileBytes = 5 - excess; break;
                case "field": options.MaxFieldBytes = 5 - excess; break;
                case "total": options.MaxTotalBytes = wire.Length - excess; break;
                case "headers": options.MaxHeaderBytes = Encoding.UTF8.GetByteCount(headers) + 2 - excess; break;
            }

            using var input = new FragmentedStream(wire, 1);
            using var reader = new MultipartReader(input, Boundary, options);
            if (excess == 0)
                await Drain(reader);
            else
                await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [TestCase("parts", 1)]
        [TestCase("parts", 2)]
        [TestCase("fields", 1)]
        [TestCase("fields", 2)]
        [TestCase("files", 1)]
        [TestCase("files", 2)]
        [TestCase("headers", 1)]
        [TestCase("headers", 2)]
        public async Task CountLimitsAreEnforced(string dimension, int allowed)
        {
            var options = new MultipartOptions();
            byte[] wire;
            switch (dimension)
            {
                case "files":
                    options.MaxFiles = allowed;
                    wire = Wire(FileHeader, "first\r\n--" + Boundary + "\r\n" + FileHeader + "\r\nsecond");
                    break;
                case "headers":
                    options.MaxHeaders = allowed;
                    wire = Wire(FieldHeader + "Content-Type: text/plain\r\n", "value");
                    break;
                default:
                    if (dimension == "parts") options.MaxParts = allowed;
                    else options.MaxFields = allowed;
                    wire = TwoFields();
                    break;
            }

            using var input = new MemoryStream(wire);
            using var reader = new MultipartReader(input, Boundary, options);
            if (allowed == 2)
                await Drain(reader);
            else
                await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [Test]
        public async Task EmptyFilenameStillCountsAsAFile()
        {
            using var input = new MemoryStream(Wire("Content-Disposition: form-data; name=file; filename=\"\"\r\n", ""));
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxFiles = 0 });
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [Test]
        public async Task SkippingUnreadSectionCannotBypassBodyLimit()
        {
            using var input = new MemoryStream(TwoFields());
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxFieldBytes = 4 });
            await Next(reader);
            await Assert.ThatAsync(() => reader.ReadNextSectionAsync(), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [TestCase("preamble")]
        [TestCase("epilogue")]
        public async Task TotalLimitIncludesIgnoredFramingText(string location)
        {
            var original = Wire(FieldHeader, "value");
            var extra = Encoding.UTF8.GetBytes("ignored\r\n");
            var wire = location == "preamble" ? Join(extra, original) : Join(original, extra);
            using var input = new FragmentedStream(wire, 1);
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxTotalBytes = original.Length });
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [Test]
        public async Task OptionsAreSnapshotted()
        {
            var options = new MultipartOptions { MaxFieldBytes = 4 };
            using var input = new MemoryStream(Wire(FieldHeader, "12345"));
            using var reader = new MultipartReader(input, Boundary, options);
            options.MaxFieldBytes = 100;
            await Assert.ThatAsync(() => Drain(reader), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413));
        }

        [TestCase("MaxTotalBytes")]
        [TestCase("MaxFileBytes")]
        [TestCase("MaxFieldBytes")]
        [TestCase("MaxParts")]
        [TestCase("MaxFiles")]
        [TestCase("MaxFields")]
        [TestCase("MaxHeaderBytes")]
        [TestCase("MaxHeaders")]
        public void NegativeLimitsAreRejected(string propertyName)
        {
            var options = new MultipartOptions();
            var property = typeof(MultipartOptions).GetProperty(propertyName) ?? throw new AssertionException("Missing limit property.");
            property.SetValue(options, Convert.ChangeType(-1, property.PropertyType));
            using var input = new MemoryStream();
            Assert.That(() => new MultipartReader(input, Boundary, options), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public async Task ZeroBodyLimitsPermitEmptyParts()
        {
            using var input = new MemoryStream(Wire(FieldHeader, ""));
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxFieldBytes = 0, MaxFileBytes = 0 });
            await Drain(reader);
        }

        private static async Task Drain(MultipartReader reader)
        {
            while (await reader.ReadNextSectionAsync() is { } section)
                await section.Body.CopyToAsync(Stream.Null);
        }
    }
}
