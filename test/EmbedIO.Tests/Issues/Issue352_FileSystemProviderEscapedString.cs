using EmbedIO.Testing;
using System.Net.Http;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;

namespace EmbedIO.Tests.Issues
{
    public class Issue352_FileSystemProviderEscapedString
    {
        [Test]
        public async Task FileSystemProvider_Handle_PathWithEscapedString()
        {
            const string ok = "Content";

            var tempPathWithWhitespace = Directory.CreateTempSubdirectory("Folder ").FullName;
            var tempFolder = Path.GetFileName(tempPathWithWhitespace);
            var tempFile = Path.Combine(tempPathWithWhitespace, "index.html");
            try
            {
                File.WriteAllText(tempFile, ok);

                void Configure(IWebServer server) => server
                    .WithStaticFolder("/", Path.GetTempPath(), true);

                async Task Use(HttpClient client)
                {
                    using var response = await client.GetAsync($"/{tempFolder}").ConfigureAwait(false);
                    var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Assert.AreEqual(ok, responseString);
                }

                await TestWebServer.UseAsync(Configure, Use).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tempFile);
                Directory.Delete(tempPathWithWhitespace);
            }
        }
    }
}
