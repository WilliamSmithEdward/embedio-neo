using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestDataCacheTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public Task QueryAndFormCachesRemainIndependent(bool queryFirst)
            => TestWebServer.UseAsync(
                server => server.OnAny(async context =>
                {
                    if (queryFirst)
                        context.GetRequestQueryData();
                    var firstForm = await context.GetRequestFormDataAsync();
                    var query = context.GetRequestQueryData();
                    var secondForm = await context.GetRequestFormDataAsync();
                    await context.SendDataAsync(new
                    {
                        Query = query["value"],
                        FirstForm = firstForm["value"],
                        SecondForm = secondForm["value"],
                        QueryCached = ReferenceEquals(query, context.GetRequestQueryData()),
                        FormCached = ReferenceEquals(firstForm, secondForm)
                    });
                }),
                async client =>
                {
                    using var content = new StringContent("value=form-value", Encoding.UTF8, "application/x-www-form-urlencoded");
                    using var response = await client.PostAsync("/?value=query-value", content);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.That(json.RootElement.GetProperty("QueryCached").GetBoolean(), Is.True);
                    Assert.That(json.RootElement.GetProperty("FormCached").GetBoolean(), Is.True);
                    Assert.That(json.RootElement.GetProperty("Query").GetString(), Is.EqualTo("query-value"));
                    Assert.That(json.RootElement.GetProperty("FirstForm").GetString(), Is.EqualTo("form-value"));
                    Assert.That(json.RootElement.GetProperty("SecondForm").GetString(), Is.EqualTo("form-value"));
                });
    }
}
