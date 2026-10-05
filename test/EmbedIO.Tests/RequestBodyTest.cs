using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestBodyTest
    {
        [TestCase("bytes")]
        [TestCase("stream")]
        [TestCase("text")]
        public Task BodyHelpersPreserveUtf8Data(string helper)
        {
            const string payload = "hello Zürich €";
            return TestWebServer.UseAsync(
                server => server.OnAny(async context =>
                {
                    string text;
                    if (helper == "bytes")
                        text = Encoding.UTF8.GetString(await context.GetRequestBodyAsByteArrayAsync());
                    else if (helper == "stream")
                    {
                        using var stream = await context.GetRequestBodyAsMemoryStreamAsync();
                        Assert.That(stream.Position, Is.Zero);
                        Assert.That(stream.CanWrite, Is.False);
                        Assert.Throws<NotSupportedException>(() => stream.WriteByte(0));
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        text = await reader.ReadToEndAsync();
                    }
                    else
                        text = await context.GetRequestBodyAsStringAsync();

                    await context.SendStringAsync(text, "text/plain", Encoding.UTF8);
                }),
                async client =>
                {
                    using var response = await client.PostAsync("/", new StringContent(payload, Encoding.UTF8, "text/plain"));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(payload));
                });
        }

        [TestCase("gzip", true, HttpStatusCode.OK)]
        [TestCase("deflate", true, HttpStatusCode.OK)]
        [TestCase("gzip", false, HttpStatusCode.BadRequest)]
        [TestCase("deflate", false, HttpStatusCode.BadRequest)]
        [TestCase("br", true, HttpStatusCode.BadRequest)]
        public async Task CompressedRequestsRequireExplicitSupport(string encoding, bool supported, HttpStatusCode expected)
        {
            const string payload = "decompressed Zürich €";
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                options.SupportCompressedRequests = supported;
            });
            server.OnAny(async context =>
                await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", Encoding.UTF8));
            using var stop = new System.Threading.CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var bytes = new MemoryStream();
                using (Stream compressor = encoding == "deflate"
                    ? new DeflateStream(bytes, CompressionMode.Compress, true)
                    : new GZipStream(bytes, CompressionMode.Compress, true))
                {
                    var data = Encoding.UTF8.GetBytes(payload);
                    compressor.Write(data, 0, data.Length);
                }

                using var content = new ByteArrayContent(bytes.ToArray());
                content.Headers.ContentEncoding.Add(encoding);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.PostAsync(url, content);
                Assert.That(response.StatusCode, Is.EqualTo(expected));
                if (expected == HttpStatusCode.OK)
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(payload));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
