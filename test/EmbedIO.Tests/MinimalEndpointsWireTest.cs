using System.Threading.Tasks;
using EmbedIO.Endpoints;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MinimalEndpointsTest
    {
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task MinimalBindingsWorkOverRealHttpConnections(bool generated, int protocol) =>
            await RequestBindingWire.RunAsync(generated, protocol, true);
    }

    [GeneratedEndpointBinding]
    internal static partial class MinimalWireHandlers
    {
        public static string Query([QueryObject, Validate] BindingSearch input) => input.ToString();

        public static string Form([FormObject, Validate] BindingSearch input) => input.ToString();

        public static string Cookie(
            [CookieField("theme", DuplicateValues = RequestDuplicateValues.Last)] string theme
        ) => theme;
    }
}
