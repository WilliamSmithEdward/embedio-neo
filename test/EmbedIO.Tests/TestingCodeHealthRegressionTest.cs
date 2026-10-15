using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TestingCodeHealthRegressionTest
    {
        [TestCase("Content-Language")]
        [TestCase("content-language")]
        [TestCase("CONTENT-LANGUAGE")]
        [TestCase("CoNtEnT-LaNgUaGe")]
        public Task ResponseContentHeaderNamesAreCaseInsensitive(string name)
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.Response.Headers[name] = "en";
                    return context.SendStringAsync("body", "text/plain", new UTF8Encoding(false));
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.Content.Headers.ContentLanguage, Is.EqualTo(new[] { "en" }));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });

        [TestCase("Content-Length")]
        [TestCase("content-length")]
        [TestCase("CONTENT-LENGTH")]
        public Task ResponseContentLengthIsCalculatedRegardlessOfHeaderCase(string name)
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.Response.Headers[name] = "999";
                    return context.SendStringAsync("body", "text/plain", new UTF8Encoding(false));
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(4));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });

        [Test]
        public async Task ClosingCompletedTestContextDoesNotRepeatCallbacks()
        {
            IHttpContextImpl? captured = null;
            var calls = 0;
            await TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    captured = (IHttpContextImpl)context;
                    context.OnClose(_ => calls++);
                    return context.SendStringAsync("body", "text/plain", new UTF8Encoding(false));
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });
            Assert.That(calls, Is.EqualTo(1));
            var completed = captured ?? throw new AssertionException("The request context was not captured.");
            await Task.WhenAll(Task.Run(completed.Close), Task.Run(completed.Close));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public async Task CloseCallbacksStayInReverseOrderWhenOneThrows()
        {
            var calls = new List<int>();
            await TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    context.OnClose(_ => calls.Add(1));
                    context.OnClose(_ =>
                    {
                        calls.Add(2);
                        throw new InvalidOperationException("Callback probe.");
                    });
                    context.OnClose(_ => calls.Add(3));
                    return context.SendStringAsync("body", "text/plain", new UTF8Encoding(false));
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                });
            Assert.That(calls, Is.EqualTo(new[] { 3, 2, 1 }));
        }

        [TestCase(-1, 0)]
        [TestCase(-1, -1)]
        [TestCase(int.MinValue, 0)]
        public void NegativeStockResourceRangesAreInvalid(int start, int upperBound)
            => Assert.That(StockResource.GetByteRange("/index.html", start, upperBound), Is.Null);

        [Test]
        public void StockResourceRangeIncludesBothEndpoints()
        {
            var all = StockResource.GetBytes("/index.html");
            Assert.That(StockResource.GetByteRange("/index.html", 0, 1), Is.EqualTo(new[] { all[0], all[1] }));
        }
    }
}
