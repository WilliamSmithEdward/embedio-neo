using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Idempotency;
using EmbedIO.Security;
using EmbedIO.Testing;
using NUnit.Framework;
using static EmbedIO.Tests.SecurityHeadersFixtures;

namespace EmbedIO.Tests
{
    public sealed partial class SecurityHeadersTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task NoncePolicyIsRejectedBeforeIdempotentOperation(bool late)
        {
            var calls = 0;
            var coordinator = new IdempotencyCoordinator();
            var policy = new IdempotencyPolicy("fixture", _ => "trusted", TimeSpan.FromMinutes(1));
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(async c =>
                    {
                        if (!late) NoncePolicy().Apply(c);

                        await coordinator.ExecuteAsync(c, policy, async staged =>
                        {
                            if (late) NoncePolicy().Apply(staged);

                            Interlocked.Increment(ref calls);
                            await staged.SendStringAsync("must not execute", "text/plain", WebServer.Utf8NoBomEncoding);
                        });
                    })),
                async client =>
                {
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/");
                    request.Headers.Add("Idempotency-Key", "one");
                    request.Content = new System.Net.Http.StringContent("body");
                    using var response = await client.SendAsync(request);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                        Assert.That(calls, Is.Zero);
                    }
                }
            );
        }

        [Test]
        public async Task StaticIdempotencyReplayUsesCurrentPolicy()
        {
            var coordinator = new IdempotencyCoordinator();
            var policy = new IdempotencyPolicy("fixture", _ => "trusted", TimeSpan.FromMinutes(1));
            var current = new SecurityHeadersPolicy("default-src https://first.example");
            var calls = 0;
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(c =>
                    {
                        current.Apply(c);
                        return coordinator.ExecuteAsync(c, policy, async staged =>
                        {
                            Interlocked.Increment(ref calls);
                            await staged.SendStringAsync("one outcome", "text/plain", WebServer.Utf8NoBomEncoding);
                        });
                    })),
                async client =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var request = new System.Net.Http.HttpRequestMessage(
                            System.Net.Http.HttpMethod.Post,
                            "/"
                        );
                        request.Headers.Add("Idempotency-Key", "one");
                        request.Content = new System.Net.Http.StringContent("body");
                        using var response = await client.SendAsync(request);

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("one outcome"));
                            Assert.That(
                                Header(response, "Content-Security-Policy"),
                                Is.EqualTo(
                                    i == 0 ? "default-src https://first.example" : "default-src https://second.example"
                                )
                            );
                        }

                        current = new SecurityHeadersPolicy("default-src https://second.example");
                    }

                    Assert.That(calls, Is.EqualTo(1));
                }
            );
        }

        [Test]
        public async Task OriginalContextCannotCreateNonceDuringIdempotencyRecording()
        {
            var coordinator = new IdempotencyCoordinator();
            var policy = new IdempotencyPolicy("fixture", _ => "trusted", TimeSpan.FromMinutes(1));
            var blocked = false;
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(c => coordinator.ExecuteAsync(c, policy, async staged =>
                        {
                            Assert.That(() => NoncePolicy().Apply(c), Throws.InvalidOperationException);
                            blocked = true;
                            await staged.SendStringAsync("finite", "text/plain", WebServer.Utf8NoBomEncoding);
                        }))),
                async client =>
                {
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/");
                    request.Headers.Add("Idempotency-Key", "one");
                    request.Content = new System.Net.Http.StringContent("body");
                    using var response = await client.SendAsync(request);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(blocked, Is.True);
                    }
                }
            );
        }
    }
}
