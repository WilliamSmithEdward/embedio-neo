using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue601_MacCatalystStartup
    {
        [Test]
        public void CoreDoesNotReferenceSwanOrConsoleApis()
        {
            // The original screenshot fails in SWAN's Console.WindowHeight probe.
            // Inspect metadata rather than pretending desktop tests run Mac Catalyst.
            using var stream = File.OpenRead(typeof(WebServer).Assembly.Location);
            using var assembly = new PEReader(stream);
            var metadata = assembly.GetMetadataReader();
            var references = metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name));
            Assert.That(references.Any(name => name.StartsWith("Swan", StringComparison.OrdinalIgnoreCase)), Is.False);
            var consoleReferences = metadata.TypeReferences.Select(metadata.GetTypeReference)
                .Where(type => metadata.GetString(type.Namespace) == "System"
                    && metadata.GetString(type.Name) == "Console");
            Assert.That(consoleReferences, Is.Empty);
        }

        [Test]
        public async Task ReportedConfigurationServesStaticHtmlThroughRealListener()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                const string html = "<!doctype html><html><body>issue 601</body></html>";
                File.WriteAllText(Path.Combine(root, "index.html"), html);
                var url = Resources.GetServerAddress();
                using var server = new WebServer(options => options
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO))
                    .WithStaticFolder("/", root, true);
                using var stop = new CancellationTokenSource();
                var running = server.RunAsync(stop.Token);
                try
                {
                    Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var response = await client.GetAsync(url);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(html));
                }
                finally
                {
                    stop.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
