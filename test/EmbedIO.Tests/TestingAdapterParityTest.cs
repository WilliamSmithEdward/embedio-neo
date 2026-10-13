using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TestingAdapterParityTest
    {
        [Test]
        public Task ResponseCookieCollectionProducesSeparateWireHeaders()
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.Response.SetCookie(new Cookie("first", "one", "/scope") { HttpOnly = true });
                    context.Response.SetCookie(new Cookie("second", "two", "/")
                    {
                        Expires = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    });
                    context.Response.Headers.Add("Set-Cookie", "raw=three; Path=/");
                    return context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.Headers.GetValues("Set-Cookie"), Is.EquivalentTo(new[]
                    {
                        "raw=three; Path=/",
                        "first=one; Path=/scope; HttpOnly",
                        "second=two; Expires=Thu, 02 Jan 2020 03:04:05 GMT; Path=/",
                    }));
                });

        [TestCase(0, "quoted=value; Path=/scope")]
        [TestCase(1, "Version=1; quoted=value; Path=\"/scope\"")]
        public Task ResponseCookieVersionPreservesWireQuoting(int version, string expected)
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.Response.SetCookie(new Cookie("quoted", "value", "/scope") { Version = version });
                    return context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.Headers.GetValues("Set-Cookie"), Is.EqualTo(new[] { expected }));
                });
        [Test]
        public Task StoredClientCookieIsVisibleThroughRequestCookies()
            => TestWebServer.UseAsync(
                server => server.OnAny(context => context.SendStringAsync(context.Request.Cookies["stored"]?.Value ?? "missing", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    var testClient = (TestHttpClient)client;
                    testClient.CookieContainer.Add(client.BaseAddress ?? throw new AssertionException("Missing base URI."), new Cookie("stored", "value"));
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("value"));
                });

        [Test]
        public Task SessionCookiePreservesStateAcrossIndependentRequests()
            => TestWebServer.UseAsync(
                server => server.WithLocalSessionManager().OnAny(context =>
                {
                    var count = context.Session["count"] is int previous ? previous + 1 : 1;
                    context.Session["count"] = count;
                    return context.SendStringAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("1"));
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("2"));
                });

        [Test]
        public async Task RequestContentHeadersAndLengthReachTheHandler()
        {
            long length = -1;
            string? contentType = null;
            string? contentLanguage = null;
            await TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    length = context.Request.ContentLength64;
                    contentType = context.Request.Headers["Content-Type"];
                    contentLanguage = context.Request.Headers["Content-Language"];
                    return context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var content = new StringContent("body", Encoding.UTF8, "text/plain");
                    content.Headers.ContentLanguage.Add("en");
                    using var response = await client.PostAsync("/", content);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                });
            Assert.Multiple(() =>
            {
                Assert.That(length, Is.EqualTo(4));
                Assert.That(contentType, Is.EqualTo("text/plain; charset=utf-8"));
                Assert.That(contentLanguage, Is.EqualTo("en"));
            });
        }

        [Test]
        public Task ExplicitRequestCookiesAndContainerCookiesAreBothVisible()
            => TestWebServer.UseAsync(
                server => server.OnAny(context => context.SendStringAsync(
                    context.Request.Cookies["explicit"]?.Value + ":" + context.Request.Cookies["stored"]?.Value,
                    "text/plain", Encoding.UTF8)),
                async client =>
                {
                    var uri = client.BaseAddress ?? throw new AssertionException("Missing base URI.");
                    ((TestHttpClient)client).CookieContainer.Add(uri, new Cookie("stored", "two"));
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                    request.Headers.Add("Cookie", "explicit=one");
                    using var response = await client.SendAsync(request);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("one:two"));
                    Assert.That(string.Join(";", request.Headers.GetValues("Cookie")), Is.EqualTo("explicit=one"));
                });

        [Test]
        public Task ResponseCookiesRespectPathAndExpiry()
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    if (context.Request.Url.AbsolutePath == "/set")
                        context.Response.SetCookie(new Cookie("scoped", "value", "/scope"));
                    if (context.Request.Url.AbsolutePath == "/expire")
                        context.Response.SetCookie(new Cookie("scoped", "gone", "/scope") { Expires = DateTime.UtcNow.AddDays(-1) });
                    return context.SendStringAsync(context.Request.Cookies["scoped"]?.Value ?? "missing", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    await client.GetStringAsync("/set");
                    Assert.That(await client.GetStringAsync("/scope/read"), Is.EqualTo("value"));
                    Assert.That(await client.GetStringAsync("/outside"), Is.EqualTo("missing"));
                    await client.GetStringAsync("/expire");
                    Assert.That(await client.GetStringAsync("/scope/read"), Is.EqualTo("missing"));
                });

        [Test]
        public Task SeparateRawSetCookieFieldsAreRetained()
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    if (context.Request.Url.AbsolutePath == "/set")
                    {
                        context.Response.Headers.Add("Set-Cookie", "first=one; Path=/");
                        context.Response.Headers.Add("Set-Cookie", "second=two; Path=/");
                    }
                    return context.SendStringAsync(context.Request.Cookies["first"]?.Value + ":" + context.Request.Cookies["second"]?.Value,
                        "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    await client.GetStringAsync("/set");
                    Assert.That(await client.GetStringAsync("/read"), Is.EqualTo("one:two"));
                });

        [Test]
        public Task ExplicitResponseContentTypeHeaderKeepsItsCharset()
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.Response.Headers["Content-Type"] = "text/plain; charset=utf-8";
                    return context.SendStringAsync("body", "application/example", Encoding.UTF8);
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.Content.Headers.ContentType?.ToString(), Is.EqualTo("text/plain; charset=utf-8"));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });
        [Test]
        public Task ResponseContentTypeAndCharsetReachTheClient()
            => TestWebServer.UseAsync(
                server => server.OnAny(context => context.SendStringAsync("body", "text/plain", Encoding.Unicode)),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.Multiple(() =>
                    {
                        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
                        Assert.That(response.Content.Headers.ContentType?.CharSet, Is.EqualTo("utf-16"));
                    });
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });
    }
}
