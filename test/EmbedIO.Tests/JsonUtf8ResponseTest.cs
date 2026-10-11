using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using Json = EmbedIO.Serialization.Json;

namespace EmbedIO.Tests
{
    public sealed class JsonUtf8ResponseTest
    {
        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        public async Task HttpJsonPreservesExactUtf8BytesAndOptionsSnapshot(HttpListenerMode mode, bool buffered, bool custom)
        {
            var options = Json.CreateOptions();
            options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.WriteIndented = true;
            var expectedOptions = new JsonSerializerOptions(options);
            var serializer = custom ? ResponseSerializer.Json(buffered, options) : ResponseSerializer.Json(buffered);
            // A factory snapshots its options; later caller changes must not alter output.
            options.WriteIndented = false;
            options.Encoder = JavaScriptEncoder.Default;
            var values = new object?[]
            {
                new { Text = "caf\u00e9 <>& \"\\\n \ud83d\ude00", Amount = 123.4500m, Fields = new[] { 1, 2, 3 } },
                null,
                new string('x', 256 * 1024) + "\u00e9\ud83d\ude00",
            };
            var selected = 0;
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => serializer(context, values[Volatile.Read(ref selected)])));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                for (var index = 0; index < values.Length; index++)
                {
                    Volatile.Write(ref selected, index);
                    var expected = Encoding.UTF8.GetBytes(custom ? Json.Serialize(values[index], expectedOptions) : Json.Serialize(values[index]));
                    using var response = await client.GetAsync(url);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(MimeType.Json));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected), "Same bytes as the existing public string serializer, with no BOM.");
                    if (buffered) Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(expected.LongLength));
                    else Assert.That(response.Headers.TransferEncodingChunked, Is.True);
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
