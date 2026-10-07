# OpenAPI documents and optional Swagger UI

This guide answers [upstream #539](https://github.com/unosquare/embedio/issues/539).
EmbedIO-Neo can serve an application-owned OpenAPI document and Swagger UI using
existing APIs. It does **not** automatically generate an OpenAPI document from
controller attributes. This example works with published EmbedIO-Neo **1.0.2**;
it adds no library API, target or production dependency.

OpenAPI describes an HTTP contract; Swagger UI is a separate browser application
that reads it. ASP.NET-specific Swashbuckle middleware cannot simply be added to
an EmbedIO pipeline. An external generator may be used if its output describes
your actual EmbedIO routes, binding, serialization and response behavior.

## Run the API and serve its document

Create a separate console project:

```sh
dotnet new console --framework net10.0 -n OpenApiExample
cd OpenApiExample
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace `Program.cs` with this complete program:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var parsed = JsonDocument.Parse(File.ReadAllText("openapi.json"));
var specification = parsed.RootElement.Clone();
using var server = new WebServer(o => o.WithUrlPrefix("http://localhost:8877/").WithMode(HttpListenerMode.EmbedIO))
    .WithWebApi("/api", api => api.WithController<DemoController>())
    .WithWebApi("/spec", api => api.WithController(() => new SpecificationController(specification)));
if (Directory.Exists("swagger"))
    server.WithStaticFolder("/docs", Path.GetFullPath("swagger"), isImmutable: false);
Console.WriteLine("Open http://localhost:8877/spec/openapi.json; press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class SpecificationController : WebApiController
{
    private readonly JsonElement _specification;
    public SpecificationController(JsonElement specification) => _specification = specification;
    [Route(HttpVerbs.Get, "/openapi.json")]
    public object GetSpecification() => _specification;
}

public sealed class DemoController : WebApiController
{
    [Route(HttpVerbs.Get, "/hello/{name}")]
    public object Hello(string name, [QueryField(true)] string prefix) => new { message = $"{prefix}, {name}!" };

    [Route(HttpVerbs.Post, "/echo")]
    public object Echo([JsonData] EchoInput input)
    {
        if (input == null || string.IsNullOrWhiteSpace(input.Message))
            throw HttpException.BadRequest("Message is required.");
        return new { message = input.Message };
    }
}

public sealed class EchoInput
{
    public string? Message { get; set; }
}
```

Save this as `openapi.json` beside the project file:

```json
{
  "openapi": "3.0.3",
  "info": {
    "title": "EmbedIO demo API",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "/api"
    }
  ],
  "paths": {
    "/hello/{name}": {
      "get": {
        "operationId": "hello",
        "parameters": [
          {
            "name": "name",
            "in": "path",
            "required": true,
            "schema": {
              "type": "string"
            }
          },
          {
            "name": "prefix",
            "in": "query",
            "required": true,
            "schema": {
              "type": "string"
            }
          }
        ],
        "responses": {
          "200": {
            "description": "Greeting",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Message"
                }
              }
            }
          },
          "400": {
            "description": "Required prefix query field is missing"
          }
        }
      }
    },
    "/echo": {
      "post": {
        "operationId": "echo",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/EchoInput"
              }
            }
          }
        },
        "responses": {
          "200": {
            "description": "Echoed message",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Message"
                }
              }
            }
          },
          "400": {
            "description": "Malformed JSON, null body, or missing/blank message"
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "Message": {
        "type": "object",
        "required": [
          "message"
        ],
        "properties": {
          "message": {
            "type": "string"
          }
        },
        "additionalProperties": false
      },
      "EchoInput": {
        "type": "object",
        "required": [
          "message"
        ],
        "properties": {
          "message": {
            "type": "string",
            "minLength": 1,
            "description": "Must contain a non-whitespace character; the application rejects whitespace-only input."
          }
        }
      }
    }
  }
}
```

Run `dotnet run` **from that project directory**. The document is parsed once at
startup; restart after editing it. No `swagger` directory is required for the API
or specification endpoint. Press Ctrl+C for cancellation and server disposal.

Try these requests (PowerShell users can use `curl.exe`):

```sh
curl http://localhost:8877/spec/openapi.json
curl "http://localhost:8877/api/hello/Neo?prefix=Hello"
curl -X POST http://localhost:8877/api/echo -H "Content-Type: application/json" -d '{"message":"hello"}'
```

The first returns the OpenAPI JSON object, not a quoted JSON string. The other
responses are `{"message":"Hello, Neo!"}` and `{"message":"hello"}`. Missing
`prefix`, malformed JSON, a null JSON body and missing/blank `message` return 400.
The echo is a demonstration response, not persisted application data.

`/api` is the module prefix; controller routes add `/hello/{name}` and `/echo`.
The document's `servers` URL is `/api`, so its `paths` deliberately omit that
prefix. Path parameters are required; `[QueryField(true)]` also makes `prefix`
required. JSON input is bound by `[JsonData]`; the output uses anonymous objects
with explicitly lowercase `message`. Update schemas when changing property names,
custom serializers, validation, status codes or authentication. OpenAPI does not
enforce these declarations or perform input validation for your server.

This uses OpenAPI 3.0.3 for broad tooling interoperability, not as a claim that it
is the newest specification. See the [OpenAPI specification](https://spec.openapis.org/oas/v3.0.3).

## Optional: serve Swagger UI on the same origin

Swagger UI is an optional third-party JavaScript/CSS application, **not a NuGet
runtime dependency of EmbedIO-Neo**. Its Apache-2.0 license and bundled notices
must accompany copied assets. The following PowerShell commands download the
verified **5.33.1** release at its exact Git commit into your example project:

```ps1
New-Item -ItemType Directory -Force swagger | Out-Null
$swaggerCommit = 'cac3d136b5e37bfbe3288c8591f6f4fcf9870599'
$swaggerRoot = "https://raw.githubusercontent.com/swagger-api/swagger-ui/$swaggerCommit"
foreach ($asset in @('swagger-ui.css', 'swagger-ui-bundle.js', 'swagger-ui-bundle.js.LICENSE.txt')) {
    Invoke-WebRequest "$swaggerRoot/dist/$asset" -OutFile "swagger/$asset"
}
foreach ($notice in @('LICENSE', 'NOTICE')) {
    Invoke-WebRequest "$swaggerRoot/$notice" -OutFile "swagger/$notice"
}
```

Save this complete HTML as `swagger/index.html`:

```html
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Demo API</title><link rel="stylesheet" href="./swagger-ui.css"></head>
<body><div id="swagger-ui"></div><script src="./swagger-ui-bundle.js"></script>
<script>
window.onload = () => SwaggerUIBundle({
  url: "/spec/openapi.json",
  dom_id: "#swagger-ui",
  validatorUrl: null,
  persistAuthorization: false
});
</script></body></html>
```

Restart the application after creating the directory and open
`http://localhost:8877/docs/`. The UI and specification are served from the same
origin, so this example needs no CORS policy. Choose the GET or POST operation,
select **Try it out**, fill its parameters/body and execute it. Both operations
use `/api`; they do not accidentally target `/docs/api` or `/spec/api`.

`validatorUrl: null` disables Swagger UI's optional external validation service;
it does not validate your schema locally. Keep authorization persistence disabled
unless your application explicitly needs it. Serve only intended assets from the
static directory, and protect/omit documentation endpoints if your application
requires restricted API documentation. Behind a proxy, set `servers` to the
correct public API path and adjust the specification URL if its public mount
changes; these values are application configuration.

See Swagger UI's [installation](https://swagger.io/docs/open-source-tools/swagger-ui/usage/installation/)
and [configuration](https://swagger.io/docs/open-source-tools/swagger-ui/usage/configuration/)
documentation. The copied version is a reproducible example; applications own
their UI asset updates and notices.

## Scope of the answer

The original discussion suggested an external Swagger Codegen generator, C#
source generator or MSBuild task. No automatic generator is implemented here.
Controller metadata alone cannot infer every custom serializer, validation rule,
authentication policy or possible response. Any future generator needs an agreed
supported subset and explicit schema/response customization; handwritten documents
must likewise be kept in sync with real requests. This guide provides a verified
integration using existing APIs, rather than claiming that broader feature exists.

## Validation

The complete program compiled against published 1.0.2. An ignored temporary
harness executed it with timed cancellation as the only program change, using
the exact published core assembly and current source in separate runs. Eighteen
HTTP requests verified document JSON/content type/path declarations, path/query
binding, UTF-8 echo, missing/malformed/null/blank input, exact-route misses and
the local UI assets/notices; shutdown completed. Swagger UI 5.33.1 also rendered
both operations in a browser and its GET/POST Execute buttons returned the
expected 200 responses from the intended `/api` URLs. These checks validate the
specific example, not automatic schema inference or every OpenAPI feature.
