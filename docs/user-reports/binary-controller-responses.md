# Return an image from a controller

[Upstream #505](https://github.com/unosquare/embedio/issues/505) reported that
clearing headers and setting `image/jpeg` still produced JSON. `Headers.Clear()`
removes the current header collection; it does not disable the serializer that
runs after a controller returns. The default Web API serializer sets JSON
metadata and serializes a returned `byte[]` as a JSON/base64 string.

Use the existing `ResponseSerializer.None` for a module whose controller results
are raw media. Return `byte[]`, set the media type through `Response.ContentType`,
and set `ContentEncoding = null` for binary data that needs no charset parameter.
`Headers.Add` appends values and can produce duplicate content types; clearing
every header is unnecessary.

## Complete program

This example uses APIs available in published EmbedIO-Neo 1.0.3:

```sh
dotnet new console --framework net10.0 --name RoomImages
cd RoomImages
dotnet add package EmbedIO-Neo --version 1.0.3
mkdir rooms
```

Copy a JPEG you own into `rooms/1.jpg`. Run from this project folder: the program
resolves `rooms` from its working directory. Keep only the files this application
should serve there. Replace `Program.cs` with this entire program:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

var imageFolder = Path.GetFullPath("rooms");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .WithWebApi("/api", api => api.WithController<StatusController>())
    .WithWebApi("/rooms", ResponseSerializer.None(bufferResponse: true),
        api => api.WithController(() => new RoomController(imageFolder)));

Console.WriteLine("Image: http://localhost:9696/rooms/1");
Console.WriteLine("JSON:  http://localhost:9696/api/status");
Console.WriteLine("Press Ctrl+C to stop.");
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

public sealed class RoomController : WebApiController
{
    private readonly string _imageFolder;
    public RoomController(string imageFolder) => _imageFolder = imageFolder;

    [Route(HttpVerbs.Get, "/{roomId}")]
    public async Task<byte[]> GetRoomImage(string roomId)
    {
        if (!ushort.TryParse(roomId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw HttpException.BadRequest("Room ID must be an integer from 0 through 65535.");

        var path = Path.Combine(_imageFolder, id.ToString(CultureInfo.InvariantCulture) + ".jpg");
        byte[] image;
        try
        {
            image = await File.ReadAllBytesAsync(path, HttpContext.CancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw HttpException.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            throw HttpException.NotFound();
        }

        Response.ContentType = "image/jpeg";
        Response.ContentEncoding = null;
        return image;
    }
}

public sealed class StatusController : WebApiController
{
    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new { status = "ok" };
}
```

The controller reads the route argument as a string and explicitly validates it
before accessing a file. This makes the application's 400 policy visible and
does not depend on automatic typed-route conversion. Converting the parsed ID
back to a number for the filename prevents the route from choosing arbitrary
filesystem paths. The required `/{roomId}` route returns 404 when the ID is
missing; use a missing file for 404 rather than returning an exception string.

Run `dotnet run`, then open the image URL in a browser or check it with curl:

```sh
curl -i http://localhost:9696/api/status
curl -D headers.txt -o downloaded.jpg http://localhost:9696/rooms/1
curl -i http://localhost:9696/rooms/not-a-number
curl -i http://localhost:9696/rooms/65536
curl -i http://localhost:9696/rooms/2
```

Expect JSON `{"status":"ok"}`, then HTTP 200 with one `Content-Type: image/jpeg`
and the original image bytes. The next two requests return 400; the last returns
404 if `rooms/2.jpg` does not exist. The server does not validate or transcode
JPEG files, so supply a real JPEG for browser rendering. Press Ctrl+C to stop.

## Serialization, framing and compression are separate

The serializer applies to every returned result in its module. Keep ordinary
JSON endpoints in a separate default-serializer module, as above. `None` sends
strings as text, byte arrays as binary, and other objects through `ToString()`;
it is not an automatic media-type picker or a mixed JSON/media policy.

`bufferResponse: true` collects the response before sending a Content-Length.
Use `false` for chunked HTTP/1.1 output when a known length is unnecessary. This
example reads the whole file into a byte array either way; buffering does not
make it a constant-memory file streamer. For larger files and caching/ranges,
consider [FileModule](../guides/getting-started/files.md).

Passthrough still uses the normal compression negotiation. A client requesting
gzip may receive compressed wire bytes; browsers decode them before displaying
the image. Compare decoded content to the file, or use curl's `--compressed`
option. A compressed Content-Length describes the encoded body, not the original
file length. Supplying a JPEG MIME label alone is not a compression override.

Normal Date, Server, connection and framing headers may still be generated when
the response commits. Clearing a header collection does not promise a response
containing only Content-Type. Response properties and header collections participate in backend-specific
serialization. Set the final media metadata through the response properties
and choose the serializer explicitly. This guide preserves existing
serialization and listener defaults.

## Investigation limits

The original payload and filesystem implementation were not supplied. The
maintainer's old passthrough suggestion is now an existing library API; the old
sample declared a string return type while returning byte arrays, which must be
corrected in consumer code. This support answer does not claim to repair an
unconfirmed header-clear defect or reproduce the original application.

Real-listener tests cover header clearing followed by JSON serialization, exact
binary transport, one media type, null charset encoding, both buffering modes,
gzip decoding, custom headers and independent JSON endpoints under both listener
modes. A separate automatic `ushort` route-binding investigation currently
reproduces HTTP 500 for malformed input before controller execution; the explicit validation
in the complete example avoids depending on that behavior.
