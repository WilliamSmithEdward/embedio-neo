using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ContextAndSessionTest
    {
        [Test]
        public Task TypedContextItemsDistinguishMissingAndWrongTypesAndStayRequestLocal()
            => TestWebServer.UseAsync(
                server => server.OnAny(context =>
                {
                    Assert.That(context.Items, Is.Empty);
                    context.Items["number"] = 42;
                    Assert.That(context.TryGetItem<int>("number", out var number), Is.True);
                    Assert.That(number, Is.EqualTo(42));
                    Assert.That(context.GetItem<int>("number"), Is.EqualTo(42));
                    Assert.That(context.TryGetItem<string>("number", out var wrong), Is.False);
                    Assert.That(wrong, Is.Null);
                    Assert.That(context.GetItem<string>("number"), Is.Null);
                    Assert.That(context.TryGetItem<int>("missing", out var missing), Is.False);
                    Assert.That(missing, Is.Zero);
                    Assert.That(context.GetItem<int>("missing"), Is.Zero);
                    return context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("ok"));
                    Assert.That(await client.GetStringAsync("/"), Is.EqualTo("ok"));
                });

        [Test]
        public Task TypedSessionValuesSupportFallbacksRemovalAndIndependentSnapshots()
            => TestWebServer.UseAsync(
                server => server.WithSessionManager(new LocalSessionManager()).OnAny(context =>
                {
                    context.Session["number"] = 42;
                    Assert.That(context.Session.TryGetValue<int>("number", out var value), Is.True);
                    Assert.That(value, Is.EqualTo(42));
                    Assert.That(context.Session.GetValue<int>("number"), Is.EqualTo(42));
                    Assert.That(context.Session.GetOrDefault("number", 99), Is.EqualTo(42));
                    Assert.That(context.Session.TryGetValue<string>("number", out var wrong), Is.False);
                    Assert.That(wrong, Is.Null);
                    Assert.That(context.Session.GetValue<string>("number"), Is.Null);
                    Assert.That(context.Session.GetOrDefault("number", "fallback"), Is.EqualTo("fallback"));
                    Assert.That(context.Session.GetValue<int>("missing"), Is.Zero);
                    Assert.That(context.Session.GetOrDefault("missing", 99), Is.EqualTo(99));
                    var snapshot = context.Session.TakeSnapshot();
                    Assert.That(context.Session.TryRemove("number", out var removed), Is.True);
                    Assert.That(removed, Is.EqualTo(42));
                    Assert.That(context.Session.TryRemove("number", out _), Is.False);
                    Assert.That(context.Session.ContainsKey("number"), Is.False);
                    Assert.That(snapshot, Has.Count.EqualTo(1));
                    Assert.That(snapshot[0].Value, Is.EqualTo(42));
                    return Task.CompletedTask;
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                });

        [Test]
        public Task RegeneratingSessionChangesIdAndDiscardsPreviousData()
            => TestWebServer.UseAsync(
                server => server.WithSessionManager(new LocalSessionManager()).OnAny(context =>
                {
                    context.Session["old"] = "value";
                    var oldId = context.Session.Id;
                    context.Session.Regenerate();
                    Assert.That(context.Session.Id, Is.Not.EqualTo(oldId));
                    Assert.That(context.Session.ContainsKey("old"), Is.False);
                    context.Session["new"] = "value";
                    Assert.That(context.Session.Count, Is.EqualTo(1));
                    return Task.CompletedTask;
                }),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                });

        [Test]
        public Task ZeroDurationSessionIsRemovedWhenRequestEndsWithoutSleeping()
        {
            string? firstId = null;
            return TestWebServer.UseAsync(
                server => server.WithSessionManager(new LocalSessionManager { SessionDuration = TimeSpan.Zero })
                    .OnAny(context =>
                    {
                        context.Session["value"] = 42;
                        var id = context.Session.Id;
                        if (firstId == null)
                            firstId = id;
                        else
                            Assert.That(id, Is.Not.EqualTo(firstId));
                        return context.SendStringAsync(id, "text/plain", Encoding.UTF8);
                    }),
                async client =>
                {
                    var first = await client.GetStringAsync("/");
                    var second = await client.GetStringAsync("/");
                    Assert.That(second, Is.Not.EqualTo(first));
                });
        }
    }
}
