using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        [TestCase("GET", "POST")]
        [TestCase("HEAD", "POST")]
        [TestCase("OPTIONS", "POST")]
        [TestCase("GET", "PATCH")]
        [TestCase("HEAD", "PATCH")]
        [TestCase("OPTIONS", "PATCH")]
        [TestCase("GET", "DELETE")]
        [TestCase("HEAD", "DELETE")]
        [TestCase("OPTIONS", "DELETE")]
        public async Task UnsafeOverridesCannotSkipBrowserTokenValidation(string physical, string effective)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            using var protection = new BrowserRequestProtection(
                new BrowserRequestProtectionOptions([TestWebServer.DefaultBaseUrl])
            );
            await TestWebServer.UseAsync(server => ConfigureBrowserUploads(server, store, protection), async client =>
            {
                var token = await UploadBrowserToken(client);
                var path = await CreateProtectedUpload(client, token);

                foreach (var suppliedToken in new string?[] { null, "invalid" })
                {
                    using var deniedRequest = OverrideRequest(physical, effective, path, suppliedToken);
                    using var denied = await client.SendAsync(deniedRequest);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                        Assert.That(store.Count, Is.EqualTo(1));
                        Assert.That(
                            store
                                .GetUploads("alice")
                                .Single().Offset,
                            Is.Zero
                        );
                    }
                }

                using var allowedRequest = OverrideRequest(physical, effective, path, token);
                using var allowed = await client.SendAsync(allowedRequest);
                Assert.That(allowed.StatusCode, Is.EqualTo(effective == "POST" ? HttpStatusCode.Created : HttpStatusCode.NoContent));

                if (effective == "DELETE") Assert.That(store.Count, Is.Zero);

                if (effective == "PATCH")
                    Assert.That(
                        store
                            .GetUploads("alice")
                            .Single().Offset,
                        Is.EqualTo(1)
                    );

                if (effective == "POST") Assert.That(store.Count, Is.EqualTo(2));
            });
        }

        [TestCase("POST")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        public async Task SafeOverridesCannotRemovePhysicalUnsafeMethodProtection(string physical)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            using var protection = new BrowserRequestProtection(
                new BrowserRequestProtectionOptions([TestWebServer.DefaultBaseUrl])
            );
            await TestWebServer.UseAsync(server => ConfigureBrowserUploads(server, store, protection), async client =>
            {
                var token = await UploadBrowserToken(client);
                var path = await CreateProtectedUpload(client, token);
                using var deniedRequest = OverrideRequest(physical, "HEAD", path, null);
                using var denied = await client.SendAsync(deniedRequest);
                Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                using var allowedRequest = OverrideRequest(physical, "HEAD", path, token);
                using var allowed = await client.SendAsync(allowedRequest);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(Header(allowed, "Upload-Offset"), Is.EqualTo("0"));
                }
            });
        }

        private static IWebServer ConfigureBrowserUploads(
            IWebServer server,
            DiskTusUploadStore store,
            BrowserRequestProtection protection,
            Action<IHttpContext>? observe = null
        ) =>
            server
                .WithSessionManager(new LocalSessionManager())
                .WithModule(Authentication())
                .WithModule(
                    new ActionModule(
                        "/token",
                        HttpVerbs.Get,
                        c => c.SendStringAsync(protection.GetToken(c), "text/plain", WebServer.Utf8NoBomEncoding)
                    )
                )
                .WithModule(new BrowserRequestProtectionModule("/uploads/", protection))
                .WithModule(new TusUploadModule("/uploads/", store, (c, _) =>
                    {
                        observe?.Invoke(c);
                        return c.User.Identity?.Name;
                    }));

        private static async Task<string> UploadBrowserToken(HttpClient client)
        {
            using var request = Request("GET", "/token");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private static async Task<string> CreateProtectedUpload(HttpClient client, string token)
        {
            using var request = Request("POST");
            request.Headers.Add("Upload-Length", "1");
            request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token);
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            return Header(response, "Location");
        }

        private static HttpRequestMessage OverrideRequest(string physical, string effective, string path, string? token)
        {
            var request = Request(
                physical,
                effective == "POST" ? "/uploads/" : path,
                effective == "PATCH" ? "a" : null
            );
            request.Headers.Add("X-HTTP-Method-Override", effective);

            if (token != null) request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token);

            if (effective == "POST") request.Headers.Add("Upload-Length", "1");

            if (effective == "PATCH") request.Headers.Add("Upload-Offset", "0");

            return request;
        }
    }
}
