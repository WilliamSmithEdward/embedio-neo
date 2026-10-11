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
    }
}
