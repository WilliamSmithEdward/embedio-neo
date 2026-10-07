# Return XML from a controller

[Upstream #575](https://github.com/unosquare/embedio/issues/575), reported by
`Muhomorik` on EmbedIO 3.5.2, asks how to return XML text, `XElement` or
`XDocument` without manually opening a response writer. Changing ContentType
alone did not work because the controller's default serializer still wrote JSON.

Choose `ResponseSerializer.None(false)` for an XML API module. It writes a
returned string unchanged, or calls `ToString()` on other objects, including
LINQ-to-XML nodes. `Task<XElement>` and `Task<XDocument>` results are awaited
before writing. Set the content type in the controller before returning.
This is explicit opt-in; the default remains JSON regardless of a controller's
earlier content-type assignment. There is no automatic content negotiation.

## Complete program using published 1.0.1

```sh
dotnet new console --framework net10.0 --name XmlApi
cd XmlApi
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace `Program.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithWebApi("/api", ResponseSerializer.None(false),
        module => module.WithController<ContactController>());
Console.WriteLine("http://127.0.0.1:8877/api/contact; press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class ContactController : WebApiController
{
    [Route(HttpVerbs.Get, "/contact")]
    public async Task<XElement> GetContact()
    {
        await Task.Yield(); // Substitute your actual asynchronous application work.
        Response.ContentType = "application/xml";
        Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
        return new XElement("contact", new XElement("name", "Hohoho"));
    }
}
```

Run `dotnet run`, then `curl -i http://127.0.0.1:8877/api/contact` in another
terminal. Expect `200`, media type `application/xml`, and XML rather than a
JSON-quoted string:

```xml
<contact>
  <name>Hohoho</name>
</contact>
```

Ctrl+C cancels the listener and allows `RunAsync` to complete. To return an
already serialized XML string, change the method's result to `Task<string>`
and return your XML text. To return a document, use `Task<XDocument>` and wrap
the element in `new XDocument(element)`. Use `"text/xml"` when required by the
legacy API contract. `ResponseSerializer.None(true)` buffers the result instead.

`MimeType.Xml` (`application/xml`) and `MimeType.TextXml` (`text/xml`) are new
constants included starting with EmbedIO-Neo 1.0.2. The literals above also work
with 1.0.1; using the constants does not change response behavior. No extra XML package is required.

## Mixed JSON and XML APIs

The serializer applies to its entire WebApi module. Keep JSON routes in a
separate module using its default serializer, or explicitly write an XML
response from a non-generic `Task` controller method. This is a **partial
controller method**, not a replacement for the whole program:

```csharp
[Route(HttpVerbs.Get, "/contact")]
public Task GetContact()
{
    Response.ContentType = "application/xml";
    Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
    var contact = new XElement("contact", new XElement("name", "Hohoho"));
    return HttpContext.SendDataAsync(ResponseSerializer.None(false), contact);
}
```

When explicitly writing a response, return `Task`, not `Task<string>` or
`Task<XElement>` with another result to serialize. Do not configure `None` for
a mixed module and expect ordinary DTOs to remain JSON: it uses `ToString()`,
not `XmlSerializer` or a general object-to-XML mapping.

## Encoding and validation

Set the media type without parameters and set ContentEncoding separately.
The native listener may omit a charset parameter for XML, while the managed
listener adds one; the verified response bytes are UTF-8 without a BOM.
LINQ-to-XML nodes escape element/attribute values. Already serialized strings
are sent as supplied, with no validation or repair.

`XDocument.ToString()` does not include its XML declaration. Consequently a
document's declaration does not select the response encoding. If a contract
requires a declaration or specific XML writer settings, provide an explicit
serializer callback that uses a matching `XmlWriter` encoding instead of
assuming `None` performs full XML serialization.

Twenty-four real-listener cases cover both media types, buffering modes and
backends with XML strings and awaited elements/documents, non-ASCII text,
escaping, parsed XML equivalence, content length for buffering, and unchanged
JSON defaults. Existing serializer behavior, public defaults and dependencies
are preserved; only the MIME constants are additive production changes.
