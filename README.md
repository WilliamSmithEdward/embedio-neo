# EmbedIO-Neo

[![NuGet version](https://img.shields.io/nuget/v/EmbedIO-Neo)](https://www.nuget.org/packages/EmbedIO-Neo)
[![Downloads](https://img.shields.io/nuget/dt/EmbedIO-Neo)](https://www.nuget.org/packages/EmbedIO-Neo)
[![CI](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/ci.yml)
[![Security](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/security.yml)
[![Malware scan](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/malware-scan.yml/badge.svg?branch=main)](https://github.com/WilliamSmithEdward/embedio-neo/actions/workflows/malware-scan.yml)
[![OpenSSF Scorecard](https://img.shields.io/ossf-scorecard/github.com/WilliamSmithEdward/embedio-neo?label=openssf%20score)](https://scorecard.dev/viewer/?uri=github.com/WilliamSmithEdward/embedio-neo)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/LICENSE)

![EmbedIO-Neo](https://raw.githubusercontent.com/WilliamSmithEdward/embedio-neo/dcd0e9f3c27d0a618cf4aade10c7fea269db6d57/images/embedio_neo.png)

A small, cross-platform, modular web server for .NET, maintained by William Smith.

For MAUI apps on Mac Catalyst, see the [local server startup guide](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/platforms/maui-mac-catalyst.md), including upstream issue #601 and sandbox entitlements.
This is an independent fork of [EmbedIO](https://github.com/unosquare/embedio).
Original copyright and third-party notices are preserved in [LICENSE](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/LICENSE).

Development focuses on compatible enhancements, simpler internals, bug fixes,
and measured performance improvements. Breaking changes require William's
explicit approval before implementation, with documented migration steps.
See [Contributing](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/CONTRIBUTING.md) for the compatibility policy and build commands.

Report issues and contribute at
[WilliamSmithEdward/embedio-neo](https://github.com/WilliamSmithEdward/embedio-neo).
The API examples below use the existing EmbedIO 3.x namespaces.

- [Overview](#overview)
    - [EmbedIO 3.0 - What's new](#embedio-30---whats-new)
    - [Some usage scenarios](#some-usage-scenarios)
- [Installation](#installation)
- [Usage](#usage)
    - [WebServer Setup](#webserver-setup)
    - [Reading from a POST body as a dictionary (application/x-www-form-urlencoded)](#reading-from-a-post-body-as-a-json-payload-applicationjson)
    - [Reading from a POST body as a JSON payload (application/json)](#reading-from-a-post-body-as-a-json-payload-applicationjson)
    - [Reading from a POST body as a FormData (multipart/form-data)](#reading-from-a-post-body-as-a-formdata-multipartform-data)
    - [Writing a binary stream](#writing-a-binary-stream)
    - [WebSockets Example](#websockets-example)
- [Support for SSL](#support-for-ssl)
- [Included modules](#included-modules)


## Overview

A small, modular, MIT-licensed web server targeting .NET 10 and .NET Standard 2.0.

* Written entirely in C#, using built-in .NET APIs on .NET 10
* Network operations use the async/await pattern: Responses are handled asynchronously
* Multiple implementations support: EmbedIO can use Microsoft `HttpListener` or internal Http Listener based on [Mono](https://www.mono-project.com/)/[websocket-sharp](https://github.com/sta/websocket-sharp/) projects
* The Neo regression suite passes on Windows, Linux and macOS with .NET 10.
  The .NET Standard 2.0 target is retained for legacy consumers;
  older runtimes, including .NET Framework and Mono, have not been validated for Neo.
* Extensible: write your own modules, or use the JsonServer module included in this repository.
* Small memory footprint
* Create REST APIs quickly with the out-of-the-box Web API module
* Serve static or embedded files with 1 line of code (also out-of-the-box)
* Handle sessions with the built-in LocalSessionManager
* WebSockets support
* CORS support. Origin, Header and Method validation with OPTIONS preflight
* HTTP 206 Partial Content support
* And many more options in the same package

### EmbedIO 3.0 - What's new

The major version 3.0 includes a lot of changes in how the webserver process the incoming request and the pipeline of the Web Modules. You can check a complete list of changes and a upgrade guide for v2 users [here](https://github.com/unosquare/embedio/wiki/Upgrade-from-v2).

### Some usage scenarios:

* Write a cross-platform GUI entirely using React/AngularJS/Vue.js or any Javascript framework
* Write a game using Babylon.js and make EmbedIO your serve your code and assets
* Create GUIs for Windows services or Linux daemons

* Write client applications with real-time communication between them using WebSockets

## Installation:

Use the commands below to install `EmbedIO-Neo`, or build the source
and add a project reference to `src/EmbedIO/EmbedIO.csproj`.
The archived `EmbedIO` package is a separate upstream distribution. Neo's package
ID changes, while the library's assembly name and namespaces remain `EmbedIO`.

### Package Manager

```
PM> Install-Package EmbedIO-Neo
```

### .NET CLI

```
> dotnet add package EmbedIO-Neo
```

## Usage

Working with EmbedIO is pretty simple, check the follow sections to start coding right away. You can find more useful recipes and implementation details in the [upstream Cookbook](https://github.com/unosquare/embedio/wiki/Cookbook).

### WebServer Setup

This complete example serves a directory and waits for shutdown. Create `wwwroot`
with the files you intend to expose before running it. Press Ctrl+C to stop.

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer(o => o
        .WithUrlPrefix("http://localhost:9696/")
        .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager()
    .WithStaticFolder("/", Path.GetFullPath("wwwroot"), true);

server.StateChanged += (_, e) => Console.WriteLine($"Server state: {e.NewState}");
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
```

The controller methods below are excerpts for a `WebApiController` subclass.
Supply your application's `SaveData` method and request types, and register the
controller through `WithWebApi` as shown in [CLI.md](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/guides/cli.md#plugins).
### Reading from a POST body as a dictionary (application/x-www-form-urlencoded)

For reading a dictionary from an HTTP Request body inside a WebAPI method you can add an argument to your method with the attribute `FormData`.

```csharp
    [Route(HttpVerbs.Post, "/data")]
    public async Task PostData([FormData] NameValueCollection data)
    {
        // Perform an operation with the data
        await SaveData(data);
    }
```

### Reading from a POST body as a JSON payload (application/json)

For reading a JSON payload and deserialize it to an object from an HTTP Request body you can use [GetRequestDataAsync<T>](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/src/EmbedIO/HttpContextExtensions-Requests.cs). This method works directly from `IHttpContext` and returns an object of the type specified in the generic type.

```csharp
    [Route(HttpVerbs.Post, "/data")]
    public async Task PostJsonData()
    {
        var data = await HttpContext.GetRequestDataAsync<MyData>();

        // Perform an operation with the data
        await SaveData(data);
    }
```

### Reading from a POST body as a FormData (multipart/form-data)

EmbedIO doesn't provide the functionality to read from a Multipart FormData stream. But you can check the [HttpMultipartParser Nuget](https://www.nuget.org/packages/HttpMultipartParser/) and connect the Request input directly to the HttpMultipartParser, very helpful and small library.

A sample code using the previous library:

```csharp
    [Route(HttpVerbs.Post, "/upload")]
    public async Task UploadFile()
    {
        var parser = await MultipartFormDataParser.ParseAsync(Request.InputStream);
        // Now you can access parser.Files
    }
```

There is [another solution](http://stackoverflow.com/questions/7460088/reading-file-input-from-a-multipart-form-data-post) but it requires this [Microsoft Nuget](https://www.nuget.org/packages/Microsoft.AspNet.WebApi.Client).

### Writing a binary stream

You can open the Response Output Stream with the extension `OpenResponseStream`.

```csharp
    [Route(HttpVerbs.Get, "/binary")]
    public async Task GetBinary()
    {
	// Call a fictional external source
	using (var stream = HttpContext.OpenResponseStream())
                await stream.WriteAsync(dataBuffer, 0, dataBuffer.Length);
    }
```

### WebSockets Example

Working with WebSocket is pretty simple, you just need to implement the abstract class `WebSocketModule` and register the module to your Web server as follow:

```csharp
server.WithModule(new WebSocketsChatServer("/chat"));
```

And our web sockets server class looks like:

```csharp
namespace EmbedIONeoExample
{
    using System.Text;
    using System.Threading.Tasks;
    using EmbedIO.WebSockets;

    /// <summary>
    /// Defines a very simple chat server.
    /// </summary>
    public class WebSocketsChatServer : WebSocketModule
    {
        public WebSocketsChatServer(string urlPath)
            : base(urlPath, true)
        {
            // placeholder
        }

        /// <inheritdoc />
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] rxBuffer,
            IWebSocketReceiveResult rxResult)
            => SendToOthersAsync(context, Encoding.GetString(rxBuffer));

        /// <inheritdoc />
        protected override Task OnClientConnectedAsync(IWebSocketContext context)
            => Task.WhenAll(
                SendAsync(context, "Welcome to the chat room!"),
                SendToOthersAsync(context, "Someone joined the chat room."));

        /// <inheritdoc />
        protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            => SendToOthersAsync(context, "Someone left the chat room.");

        private Task SendToOthersAsync(IWebSocketContext context, string payload)
            => BroadcastAsync(payload, c => c != context);
    }
}

```

## Support for SSL

The EmbedIO listener uses the runtime's TLS provider with an application-supplied
private-key certificate. Select `HttpListenerMode.EmbedIO` and `WithCertificate`
for hosting independently of Windows certificate registration. See the
[HTTPS guide](docs/guides/https.md) for desktop and .NET MAUI configuration,
client trust requirements, and platform validation limits. The Windows-only
restriction applies to the automatic certificate-store/`netsh` helpers below.

On Windows, Network Shell (`netsh`) maps an IP-port to a certificate. EmbedIO can
read or register certificates in the default store (My/LocalMachine) and use a
`netsh sslcert` binding for the first registered `https` prefix.

These inherited certificate-configuration examples are not a statement of tested
support for older Windows versions or Mono. Validate HTTPS and certificate setup
on the intended deployment platform.

### Using a PFX file and AutoRegister option

The more practical case to use EmbedIO with SSL is the `AutoRegister` option. You need to create a `WebServerOptions` instance with the path to a PFX file and the `AutoRegister` flag on. This options will try to get or register the certificate to the default certificate store. Then it will use the certificate thumbprint to register with `netsh` the FIRST `https` prefix registered on the options.

### Using AutoLoad option

If you already have a certificate on the default certificate store and the binding is also registered in `netsh`, you can use `Autoload` flag and optionally provide a certificate thumbprint. If the certificate thumbprint is not provided, EmbedIO will read the data from `netsh`. After getting successfully the certificate from the store, the raw data is passed to the WebServer.

## Included modules

The solution contains the core server, test helpers, and
[`EmbedIO.JsonServer`](https://github.com/WilliamSmithEdward/embedio-neo/tree/main/src/EmbedIO.JsonServer). JsonServer serves a JSON file as
REST collections without adding a runtime dependency beyond the core library.

```csharp
using EmbedIO;
using EmbedIO.JsonServer;

using var server = new WebServer("http://localhost:9696/")
    .WithModule(new JsonServerModule("/api/", "database.json"));
await server.RunAsync();
```

For a file containing `{"posts":[{"id":1,"title":"Hello"}]}`, use
`GET /api/posts`, `GET /api/posts/1`, `POST /api/posts`, `PUT /api/posts/1`, or
`DELETE /api/posts/1`. Authenticate access before exposing mutable data.
See [Contributing](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/CONTRIBUTING.md#extras-integration) for provenance, persistence
limits, and the disposition of the other archived Extras modules.

Build with the .NET 10 SDK specified in `global.json`. Libraries retain
.NET Standard 2.0 for existing consumers and also target .NET 10. Tests run on .NET 10. SWAN has been removed. The .NET 10 core has no external runtime packages; .NET Standard 2.0 uses Microsoft System.Text.Json. See the [migration guide](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/compatibility/migration.md) for the approved API and JSON changes.

## Command-line server

The [integrated CLI](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/guides/cli.md) serves local folders and Web API/WebSocket plugins.
It shares this repository's core library and versioning and requires .NET 10.
Run `dotnet run --project src/EmbedIO.Cli -- --help` to get started.

Browse the [documentation index](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/README.md) for usage, platform, and migration guides.

See [Neo baseline and direction](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/project/embed_io_neo.md) for the initial changes,
validation, acknowledgments, and planned work.

For configurable cycle handling in JSON responses, see the [circular-reference guide](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/guides/json-circular-references.md).

For multiple static folders, see the [mount order and fallback guide](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/guides/multiple-static-folders.md).

For asynchronous outbound requests from controllers, see the [async response guide](https://github.com/WilliamSmithEdward/embedio-neo/blob/main/docs/guides/async-outbound-requests.md).
