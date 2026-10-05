# Your first JSON endpoint

Run a small .NET server and get a JSON response in your browser. You only need
the .NET 10 SDK and a terminal.

## Create the app

```sh
dotnet new console --framework net10.0 --name HelloEmbedIO
cd HelloEmbedIO
dotnet add package EmbedIO-Neo --version 1.0.0
```

The package is called `EmbedIO-Neo`; the C# namespace is `EmbedIO`.

## Replace Program.cs

Copy this entire program into `Program.cs`:

```csharp
using System;
using System.Threading;
using EmbedIO;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .OnGet("/api/status", context =>
        context.SendDataAsync(new { status = "ok" }));

Console.WriteLine("Open http://localhost:9696/api/status");
Console.WriteLine("Press Ctrl+C to stop.");
try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
```

## Run it

```sh
dotnet run
```

Open [localhost:9696/api/status](http://localhost:9696/api/status). You should see:

```json
{"status":"ok"}
```

Press Ctrl+C in the terminal to stop the server.

## What you just used

- `WebServer` chooses the address and port.
- `OnGet` registers a callback for GET requests under the given base path.
- `SendDataAsync` writes your object as JSON.
- `RunAsync` starts listening and waits until shutdown. `using var` disposes
  the server when the program leaves its scope.

`OnGet` uses a base path: `/api/status` also matches requests beneath that path.
For exact routes, parameters, and multiple operations, use a
[controller](controllers.md).

The constructor already selects the portable EmbedIO listener. You do not need
an options callback, an explicit listener mode, or session configuration for
this example.

## If it does not run

| What happens | What to check |
| --- | --- |
| `EmbedIO` cannot be found | Run the package command in the folder containing `HelloEmbedIO.csproj`. |
| The server cannot bind | Stop another app using port 9696, or change 9696 in both the code and browser address. |
| The browser cannot connect | Keep `dotnet run` running and use `http://`, with the same port as the code. |

These examples use localhost for development on the same computer. Mobile apps
have additional setup: see [MAUI Android](../../platforms/maui-android.md) or
[Mac Catalyst](../../platforms/maui-mac-catalyst.md).

## Choose your next step

- [Serve HTML and files](files.md), optionally alongside your JSON endpoint.
- [Use a controller](controllers.md) when your API grows.
- [Routes, verbs, and parameters](requests.md): GET, POST, PUT, DELETE, and JSON bodies.
- [More guides](../../README.md#usage-guides) for HTTPS and other specific tasks.
