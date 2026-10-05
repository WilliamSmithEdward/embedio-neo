using System;
using System.Collections;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue575_XmlResponses
    {
        public static IEnumerable Cases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var buffer in new[] { false, true })
                    foreach (var mime in new[] { "application/xml", "text/xml" })
                        foreach (var route in new[] { "text", "element", "document" })
                            yield return new object[] { mode, buffer, mime, route };
        }

        [TestCaseSource(nameof(Cases))]
        public async Task ExplicitSerializerSendsXmlAndLeavesJsonDefaultsIntact(HttpListenerMode mode, bool buffer, string mime, string route)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/xml", ResponseSerializer.None(buffer), m => m.WithController(() => new XmlController(mime)))
                .WithWebApi("/json", m => m.WithController(() => new XmlController(mime)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(url + "xml/" + route);
                response.EnsureSuccessStatusCode();
                Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(mime));
                Assert.That(response.Content.Headers.ContentType.CharSet, Is.Null.Or.EqualTo("utf-8"));
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var xml = XElement.Parse(Encoding.UTF8.GetString(bytes));
                Assert.That(XNode.DeepEquals(xml, XmlController.CreateElement()), Is.True);
                Assert.That(bytes[0], Is.EqualTo((byte)'<'), "XML must not be JSON-quoted or prefixed with a BOM.");
                if (buffer) Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(bytes.Length));
                using var json = await client.GetAsync(url + "json/text");
                Assert.That(json.Content.Headers.ContentType!.MediaType, Is.EqualTo(MimeType.Json));
                Assert.That(JsonSerializer.Deserialize<string>(await json.Content.ReadAsStringAsync()),
                    Is.EqualTo(XmlController.CreateElement().ToString()));
                using var manual = await client.GetAsync(url + "json/manual");
                manual.EnsureSuccessStatusCode();
                Assert.That(manual.Content.Headers.ContentType!.MediaType, Is.EqualTo(mime));
                Assert.That(XNode.DeepEquals(XElement.Parse(await manual.Content.ReadAsStringAsync()), XmlController.CreateElement()), Is.True);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public sealed class XmlController : WebApiController
        {
            private readonly string _mime;
            public XmlController(string mime) => _mime = mime == "application/xml" ? MimeType.Xml : MimeType.TextXml;
            public static XElement CreateElement() => new("contact", new XAttribute("id", "a&b"), new XElement("name", "Hoho & café 漢字"));
            private void SetXmlHeaders()
            {
                Response.ContentType = _mime;
                Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            }
            [Route(HttpVerbs.Get, "/manual")]
            public Task Manual()
            {
                SetXmlHeaders();
                return HttpContext.SendDataAsync(ResponseSerializer.None(false), CreateElement());
            }
            [Route(HttpVerbs.Get, "/text")]
            public string Text() { SetXmlHeaders(); return CreateElement().ToString(); }
            [Route(HttpVerbs.Get, "/element")]
            public async Task<XElement> Element() { await Task.Yield(); SetXmlHeaders(); return CreateElement(); }
            [Route(HttpVerbs.Get, "/document")]
            public async Task<XDocument> Document()
            {
                await Task.Yield(); SetXmlHeaders();
                return new XDocument(new XDeclaration("1.0", "utf-16", null), CreateElement());
            }
        }
    }
}

