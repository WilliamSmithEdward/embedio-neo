# Streaming multipart file uploads

Streaming multipart uploads are introduced in EmbedIO-Neo 2.1.0.
`MultipartReader` reads `multipart/form-data` one section at a time. File bytes
flow directly to your destination stream; the reader does not buffer whole files
or create temporary files. Existing JSON and URL-encoded form bindings are unchanged.

## A controller that publishes one fully validated upload

The following complete program accepts one file at `POST /api/uploads`. Build it
against EmbedIO-Neo 2.1.0 or later. It writes to a private staging
directory, validates the complete multipart request, and only then moves the file
to its final name. Both directories live on the same filesystem. Keep the upload
root outside any static-file mount and grant write access only to the server account.

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
using var server = new WebServer("http://localhost:9696/")
    .WithWebApi("/api", api => api.WithController<UploadController>());
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

public sealed class UploadController : WebApiController
{
    [Route(HttpVerbs.Post, "/uploads")]
    public async Task<object> Upload(
        [MultipartForm(MaxTotalBytes = 17 * 1024 * 1024,
            MaxFileBytes = 16 * 1024 * 1024, MaxFiles = 1,
            MaxFields = 0, MaxParts = 1)] MultipartReader uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        using (uploads)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "private-uploads");
            var staging = Path.Combine(root, ".staging");
            Directory.CreateDirectory(staging);
            var id = Guid.NewGuid().ToString("N");
            var temporaryPath = Path.Combine(staging, id);
            var finalPath = Path.Combine(root, id);
            var received = false;
            try
            {
                MultipartSection? section;
                while ((section = await uploads.ReadNextSectionAsync(HttpContext.CancellationToken)) != null)
                {
                    if (section.Name != "file" || section.FileName == null)
                        throw new HttpException(400, "Expected a file section named file.");

                    using (var destination = new FileStream(temporaryPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await section.Body.CopyToAsync(destination, 81920, HttpContext.CancellationToken);
                    }

                    received = true;
                }

                if (!received)
                    throw new HttpException(400, "A file is required.");

                // Only null from ReadNextSectionAsync confirms complete validation,
                // including the closing boundary and any trailing epilogue.
                HttpContext.CancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, finalPath);
                return new { id };
            }
            finally
            {
                // Delete is harmless after the successful move or before creation.
                File.Delete(temporaryPath);
            }
        }
    }
}
```

The server generates storage names. `FileName`, `Name` and `ContentType` are
untrusted client metadata, not validated paths or content classifications. Do not
combine a supplied filename with a storage directory. Apply authentication,
authorization, disk quotas, content inspection and retention rules appropriate to
your application before publishing an upload. For multiple files, stage the whole
batch and commit it together after complete validation; publishing each section
immediately can leave a partial batch when a later section fails.

Serve this page from the same origin as the API (or integrate it into your existing
page). Let the browser generate the `Content-Type` boundary:

```html
<input id="upload" type="file">
<button id="send">Upload</button>
<pre id="result"></pre>
<script>
document.querySelector('#send').onclick = async () => {
  const file = document.querySelector('#upload').files[0];
  if (!file) return;
  const form = new FormData();
  form.append('file', file);
  const response = await fetch('/api/uploads', { method: 'POST', body: form });
  document.querySelector('#result').textContent =
    `${response.status}: ${await response.text()}`;
};
</script>
```

## Limits and completion

`MultipartOptions` and `[MultipartForm]` expose these limits:

| Option | Default | Scope |
| --- | ---: | --- |
| `MaxTotalBytes` | 134217728 (128 MiB) | Entire decoded multipart input, including framing and epilogue |
| `MaxFileBytes` | 67108864 (64 MiB) | Body of each section with a filename |
| `MaxFieldBytes` | 65536 (64 KiB) | Body of each section without a filename |
| `MaxParts` | 128 | All sections |
| `MaxFiles` | 32 | Sections with a filename, including an empty filename |
| `MaxFields` | 128 | Sections without a filename |
| `MaxHeaderBytes` | 16384 (16 KiB) | Headers of each section including CRLF and the terminating blank line; also bounds framing lines |
| `MaxHeaders` | 32 | Header count of each section |

Negative limits are invalid; zero permits no bytes or items in that category.
These limits bound individual requests, not aggregate application storage or
concurrency. Combine them with [request limits](request-limits.md),
[request-body byte limits](request-body-limits.md) and admission control. The
multipart total applies after content decoding; transport body limits can also
bound encoded bytes. Exceeding a multipart limit raises HTTP 413, malformed
multipart raises HTTP 400, and a non-multipart outer content type raises HTTP 415.

An unread section is drained with the same limits when advancing to the next one.
Disposing its `Body` does not bypass limits or close the request. Only a `null`
result from `ReadNextSectionAsync` confirms the whole input was validated.
Do not treat a successfully copied first file, reader disposal or an early return
as proof of a valid complete request. After a read fails, stop using that reader.

## Ownership, cancellation and binding

For modules, use `context.OpenMultipartReader(options)` in a `using` statement.
It opens the decoded request stream and registers reader cleanup with context
closure. The controller binding uses this same path. The public constructor
`new MultipartReader(input, boundary, options, cancellationToken)` borrows `input`:
disposing the reader leaves that caller-owned stream open. Do not read the input
separately while a reader owns parsing.

Read sections and their bodies sequentially, awaiting each operation. Pass the
request cancellation token to your destination writes too; multipart parsing
cannot cancel storage operations it does not own. The reader never manages your
destination streams or deletes your files. The example's `finally` removes staged
data after errors or cancellation. A production service should also clean stale
staging files left by a process crash.

`[MultipartForm]` works with reflection and generated controller bindings.
Generated OpenAPI metadata identifies a `multipart/form-data` request body; it
cannot infer application-specific part names or file types from a streaming
reader. See [OpenAPI documents](openapi.md) and
[generated controller bindings](trimming-and-native-aot.md#web-api-controllers).
Repeated field names remain distinct ordered sections. Field values are streams,
so applications choose their text encoding and decoding policy explicitly.
Ordinary `filename` metadata is unquoted but not percent-decoded or sanitized into
a filesystem path. UTF-8 extended `filename*` parameters are accepted for .NET
client interoperability and take precedence over `filename`; their percent escapes
are decoded and invalid encoding is rejected. RFC 7578 excludes `filename*` from
multipart forms, so prefer ordinary browser-style filenames when constructing
requests yourself. Part headers require CRLF framing and valid UTF-8. Duplicate
headers or disposition parameters and `Content-Transfer-Encoding` are rejected.
Nested multipart content is exposed as
raw section bytes, not recursively expanded; use separate top-level file sections.

## Validation and fuzzing

Regression coverage exercises framing, fragmented reads, limits, cancellation,
ownership and controller integration. FsCheck checks multipart round trips and
fragmentation alongside the existing URL/query properties. The shared SharpFuzz
driver exercises the same checks under AFL++ and libFuzzer, using arbitrary raw
bodies plus independently constructed binary file bodies. Expected HTTP 400/413
rejections are accepted; unexpected exceptions and payload mismatches fail the
campaign. See [property testing](property-testing.md) for replay commands.
