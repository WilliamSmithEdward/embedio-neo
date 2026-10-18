using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Tus;

namespace EmbedIO.PlatformTests
{
    // Test-only credentials are fixture identities, not a production validator.
    internal static class TusUploadSmoke
    {
        internal sealed class State : IDisposable
        {
            private readonly string _root = Path.Combine(
                Path.GetTempPath(),
                "embedio-tus-platform-"
                    + Guid
                        .NewGuid()
                        .ToString("N")
            );
            internal readonly DiskTusUploadStore Store;
            internal int Completed;

            internal State() => Store = new DiskTusUploadStore(new TusUploadOptions(_root, 64, 128, 4, 32));

            public void Dispose()
            {
                Store.Dispose();

                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
        }

        internal static void Configure(WebServer server, State state) =>
            server
                .WithModule(
                    new BearerAuthenticationModule(
                        "/tus-smoke/",
                        (credential, _) => Task.FromResult<ClaimsPrincipal?>(
                            new ClaimsPrincipal(
                                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, credential) }, "fixture")
                            )
                        )
                    )
                )
                .WithModule(
                    new TusUploadModule("/tus-smoke/", state.Store, (context, _) => context.User.Identity?.Name)
                    {
                        OnCompleted = (_, _) =>
                        {
                            state.Completed++;
                            return Task.CompletedTask;
                        }
                    }
                );

        internal static async Task<int> RunAsync(string prefix, State state)
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var create = Message("POST", prefix + "tus-smoke/");
            create.Headers.Add("Upload-Length", "6");
            using var created = await client.SendAsync(create);
            Check(created.StatusCode == HttpStatusCode.Created, "create");
            var path = new Uri(
                new Uri(prefix),
                created.Headers.Location ?? throw new InvalidOperationException("No tus location.")
            );
            using var first = await Patch("abc", 0);
            Check(first.StatusCode == HttpStatusCode.NoContent, "first patch");
            using var foreign = Message("HEAD", path.ToString(), "owner-b");
            using var denied = await client.SendAsync(foreign);
            Check(denied.StatusCode == HttpStatusCode.NotFound, "owner isolation");
            using var head = Message("HEAD", path.ToString());
            using var progress = await client.SendAsync(head);
            Check(
                progress.Headers
                    .GetValues("Upload-Offset")
                    .Single()
                    == "3",
                "progress"
            );
            using var stale = await Patch("bad", 0);
            Check(stale.StatusCode == HttpStatusCode.Conflict, "stale offset");
            using var second = await Patch("def", 3);
            Check(second.StatusCode == HttpStatusCode.NoContent && state.Completed == 1, "completion");
            var id = path.Segments.Last();
            using var bytes = new MemoryStream();
            await state.Store.CopyCompletedToAsync(id, "owner-a", bytes);
            Check(Encoding.ASCII.GetString(bytes.ToArray()) == "abcdef", "file bytes");
            state.Store.Delete(id, "owner-a");
            Check(state.Store.Count == 0 && state.Store.ReservedBytes == 0, "cleanup");
            return 8;

            async Task<HttpResponseMessage> Patch(string body, long offset)
            {
                using var request = Message("PATCH", path.ToString());
                request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
                request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
                return await client.SendAsync(request);
            }
        }

        private static HttpRequestMessage Message(string method, string path, string owner = "owner-a")
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Authorization", "Bearer " + owner);
            request.Headers.Add("Tus-Resumable", "1.0.0");
            return request;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Platform tus smoke failed: " + name);
        }
    }
}
