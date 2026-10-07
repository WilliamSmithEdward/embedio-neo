using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

namespace EmbedIO.PlatformTests
{
    // Isolated benchmark fixture, never included in a production package.
    internal static class BenchmarkEndpoints
    {
        private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("Hello, World!");

        internal static WebServer CreateServer(string url, HttpListenerMode mode)
            => new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .PreferNoCompressionFor("text/*")
                .PreferNoCompressionFor("application/json")
                .WithModule(new ActionModule("/", HttpVerbs.Get, RespondAsync));

        internal static async Task RespondAsync(IHttpContext context)
        {
            byte[] payload;
            switch (context.Request.Url.AbsolutePath)
            {
                case "/json":
                    // A fresh object and actual serialization for every request.
                    payload = JsonSerializer.SerializeToUtf8Bytes(new { message = "Hello, World!" });
                    context.Response.ContentType = "application/json";
                    break;
                case "/plaintext":
                    payload = Plaintext;
                    context.Response.ContentType = "text/plain";
                    break;
                default:
                    throw HttpException.NotFound();
            }

            context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length,
                context.CancellationToken).ConfigureAwait(false);
        }
    }
}
