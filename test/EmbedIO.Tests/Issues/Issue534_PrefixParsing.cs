using System;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue534_PrefixParsing
    {
        [TestCase("http://localhost/", 80)]
        [TestCase("https://localhost/", 443)]
        [TestCase("http://[::1]/", 80)]
        [TestCase("https://[::1]/", 443)]
        [TestCase("http://[::1]:65535/", 65535)]
        [TestCase("http://*/", 80)]
        [TestCase("https://+/", 443)]
        [TestCase("http://localhost/files:archive/", 80)]
        public void SupportedPrefixesPassValidationAndRetainTheirPort(string prefix, int port)
        {
            using var listener = new EmbedIO.Net.HttpListener();
            listener.AddPrefix(prefix);
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ListenerPrefix", true)!;
            var parsed = Activator.CreateInstance(type, new object[] { prefix })!;
            Assert.That(type.GetProperty("Port")!.GetValue(parsed), Is.EqualTo(port));
            Assert.That(type.GetProperty("Secure")!.GetValue(parsed), Is.EqualTo(prefix.StartsWith("https://", StringComparison.Ordinal)));
        }

        [TestCase("http://localhost:0/")]
        [TestCase("http://localhost:65536/")]
        [TestCase("http://localhost:abc/")]
        [TestCase("http://localhost/path")]
        [TestCase("ftp://localhost/")]
        public void InvalidPrefixesKeepArgumentValidation(string prefix)
        {
            using var listener = new EmbedIO.Net.HttpListener();
            Assert.Throws<ArgumentException>(() => listener.AddPrefix(prefix));
        }
    }
}
