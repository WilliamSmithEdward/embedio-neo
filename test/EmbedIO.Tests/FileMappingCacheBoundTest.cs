using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // An immutable provider's mapping cache is keyed by the requested path, so the spellings a
    // client chooses must not decide how much the server remembers.
    public class FileMappingCacheBoundTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static int CacheCount(FileModule module)
        {
            var cache = typeof(FileModule).GetField("_mappingCache", Hidden)?.GetValue(module)
                ?? throw new AssertionException("Missing mapping cache.");
            return ((ICollection)cache).Count;
        }

        // Mirrors FileModule.MappingCacheLimit. Kept as a literal so this test states the bound it
        // requires rather than reading it back from the code under test.
        private const int ExpectedLimit = 1024;

        // On a case-insensitive file system every spelling of a name maps to the same file, and
        // each one used to add a cache entry, so a client could grow the cache without bound.
        [Test]
        public async Task CaseVariantRequestsDoNotGrowTheMappingCacheWithoutBound()
        {
            var directory = Directory.CreateTempSubdirectory("embedio-mapping-cache-");
            try
            {
                const string name = "resource-document-file.html";
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), "<html></html>");
                var module = new FileModule("/", new FileSystemProvider(directory.FullName, isImmutable: true))
                {
                    DefaultDocument = null,
                };
                using var server = new TestWebServer();
                server.WithModule(module).Start();

                const int limit = ExpectedLimit;
                var letters = name.Select((character, index) => (character, index))
                    .Where(pair => char.IsLetter(pair.character))
                    .Select(pair => pair.index)
                    .ToArray();
                Assert.That(1 << letters.Length, Is.GreaterThan(limit + 64), "Not enough spellings to pass the limit.");

                var served = 0;
                for (var spelling = 0; served <= limit + 32; spelling++)
                {
                    var characters = name.ToCharArray();
                    for (var bit = 0; bit < letters.Length; bit++)
                        if ((spelling & (1 << bit)) != 0)
                            characters[letters[bit]] = char.ToUpperInvariant(characters[letters[bit]]);

                    var target = "/" + new string(characters);
                    using var response = await server.Client.GetAsync(target);
                    if (!response.IsSuccessStatusCode)
                    {
                        // A case-sensitive file system serves only the one real spelling, so there
                        // is no growth to bound here. The canonical request must still work.
                        Assert.That(spelling, Is.GreaterThan(0), "The real spelling was not served.");
                        Assert.That(CacheCount(module), Is.LessThanOrEqualTo(limit));
                        return;
                    }

                    served++;
                }

                Assert.That(CacheCount(module), Is.LessThanOrEqualTo(limit),
                    "The mapping cache grew past its limit with the spellings a client chose.");
            }
            finally
            {
                directory.Delete(true);
            }
        }
    }
}
