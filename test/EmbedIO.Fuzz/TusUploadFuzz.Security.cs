using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.Tus;

internal static partial class TusUploadFuzz
{
    private static async Task VerifyBrowserOverrideAsync(
        DiskTusUploadStore store,
        StateMachineTrace trace,
        CancellationToken token
    )
    {
        const string owner = "browser.fixture.owner";
        using var protection = new BrowserRequestProtection(
            new BrowserRequestProtectionOptions([TestWebServer.DefaultBaseUrl])
        );
        using var server = new TestWebServer();
        server
            .WithSessionManager(new LocalSessionManager())
            .WithModule(
                new BearerAuthenticationModule(
                    "/",
                    (credential, _) => Task.FromResult<ClaimsPrincipal?>(FixturePrincipal(credential))
                )
            )
            .WithModule(
                new ActionModule(
                    "/token",
                    HttpVerbs.Get,
                    c => c.SendStringAsync(protection.GetToken(c), "text/plain", WebServer.Utf8NoBomEncoding)
                )
            )
            .WithModule(new BrowserRequestProtectionModule("/browser/", protection))
            .WithModule(new TusUploadModule("/browser/", store, (c, _) => c.User.Identity?.Name));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = server.RunAsync(stop.Token);

        try
        {
            using var issuance = Message("GET", "/token", owner);
            using var issued = await server.Client.SendAsync(issuance, token);
            issued.EnsureSuccessStatusCode();
            var csrf = await issued.Content.ReadAsStringAsync(token);
            using var create = Message("POST", "/browser/", owner);
            create.Headers.Add("Upload-Length", "1");
            create.Headers.Add(BrowserRequestProtection.TokenHeaderName, csrf);
            using var created = await server.Client.SendAsync(create, token);
            Require(created.StatusCode == HttpStatusCode.Created, "tus.browser-creation");
            var path = created.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("Missing browser upload location.");
            var choice = (uint)trace.Seed;
            var physical = choice % 3 == 0 ? "GET" : choice % 3 == 1 ? "HEAD" : "OPTIONS";
            var effective = choice / 3 % 3 == 0 ? "POST" : choice / 3 % 3 == 1 ? "PATCH" : "DELETE";

            for (var valid = 0; valid < 2; valid++)
            {
                using var request = Message(physical, effective == "POST" ? "/browser/" : path, owner);
                request.Headers.Add("X-HTTP-Method-Override", effective);

                if (valid == 1) request.Headers.Add(BrowserRequestProtection.TokenHeaderName, csrf);

                else if (choice % 2 == 0) request.Headers.Add(BrowserRequestProtection.TokenHeaderName, "invalid");

                if (effective == "POST") request.Headers.Add("Upload-Length", "1");

                if (effective == "PATCH")
                {
                    request.Headers.Add("Upload-Offset", "0");
                    request.Content = new ByteArrayContent([97]);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        "application/offset+octet-stream"
                    );
                }

                using var response = await server.Client.SendAsync(request, token);
                Require(
                    response.StatusCode
                        == (valid == 0
                            ? HttpStatusCode.Forbidden
                            : effective == "POST" ? HttpStatusCode.Created : HttpStatusCode.NoContent),
                    "tus.browser-override-token"
                );

                if (valid == 0)
                    Require(
                        store
                            .GetUploads(owner)
                            .Single().Offset
                            == 0,
                        "tus.browser-denied-side-effect"
                    );
            }

            trace.Cover("tus.browser-override-" + physical + "-" + effective);
        }

        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            foreach (var upload in store.GetUploads(owner)) store.Delete(upload.Id, owner);
        }

        static HttpRequestMessage Message(string method, string path, string owner)
        {
            var message = new HttpRequestMessage(new HttpMethod(method), path);
            message.Headers.Add("Authorization", "Bearer " + owner);
            message.Headers.Add("Tus-Resumable", "1.0.0");
            return message;
        }
    }
}
