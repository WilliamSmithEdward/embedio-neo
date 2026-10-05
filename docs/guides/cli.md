# EmbedIO-Neo CLI

The `embedio-cli` command is maintained in this repository, alongside the core,
JsonServer, and tests. It serves local files and loads Web API/WebSocket plugins.
It targets .NET 10 and references our core project directly, with no additional
runtime packages. Library consumers retain .NET Standard 2.0 support.

## Run from source

```sh
dotnet run --project src/EmbedIO.Cli -- --path ./wwwroot --no-browser
```

The server binds to `http://localhost:9696/`. Press Ctrl+C to stop; an interactive
terminal also accepts any key. Redirected input is supported. Unless disabled,
the CLI opens a browser after both listeners have started successfully.

| Option | Behavior |
| --- | --- |
| `-p`, `--path PATH` | Serve this directory. Otherwise use `./wwwroot` if present, or the current directory. |
| `-o`, `--port PORT` | HTTP port, default `9696`. |
| `-a`, `--api PATH` | Load a plugin DLL or scan a directory for DLLs. |
| `--no-watch` | Disable live reload, the companion listener, and HTML script injection. |
| `--no-browser` | Suppress automatic browser launch. |
| `-h`, `--help` | Print usage and exit. |
| `--version` | Print the shared repository version and exit. |

Value options accept either `--port 9696` or `--port=9696`. Quote paths containing
spaces. Watch mode also binds `PORT+1`, so port 65535 requires `--no-watch`.
Invalid arguments exit with code 2; startup/plugin failures exit with code 1;
help, version, and normal shutdown exit with code 0.

Static serving supports `index.html`, directory listings, and the core library's
MIME types and file handling. Watch mode injects a reload script into HTML that
contains a closing body tag and broadcasts changes through `/watcher` on the
companion port. Nested directories are watched and event bursts are coalesced.
It refreshes the browser; it does not rebuild or hot-reload plugin assemblies.
HTML transformed for live reload is served as UTF-8 without caching or partial
responses. `--no-watch` leaves file contents unchanged.

This is a local development tool. The selected directory and its contents are
public to clients that can reach the listener, including directory listings.
Only serve folders you intend to expose; filesystem links can refer elsewhere.
Plugins execute with the tool's process permissions and are not sandboxed.

## Plugins

```sh
# API/WebSocket only; does not expose files in the working directory
embedio-cli --api ./plugins/MyPlugin.dll --no-watch

# Combine plugins with static files
embedio-cli --path ./wwwroot --api ./plugins/MyPlugin.dll
```

Without `--api`, the CLI scans DLLs directly in the current working directory,
preserving the archived CLI's discovery behavior. Use a clean, trusted working
directory. DLL dependencies should be next to the plugin, preferably accompanied
by its `.deps.json`. The loader shares the host's EmbedIO assembly to preserve
controller/module type identity. Invalid explicit plugins fail visibly; native
DLLs encountered in a directory scan are skipped.

Controllers must be public, nonabstract `EmbedIO.WebApi.WebApiController`
subclasses with public parameterless constructors. Their current
`EmbedIO.Routing.Route` attributes define paths beneath `/`:

```csharp
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

public sealed class HelloController : WebApiController
{
    [Route(HttpVerbs.Get, "/hello")]
    public object Hello() => new { message = "Hello" };
}
```

WebSocket plugins must be public, nonabstract `EmbedIO.WebSockets.WebSocketModule`
subclasses with public parameterless constructors. Configure their route in the
base constructor and override `OnMessageReceivedAsync`. See the test fixture at
`test/EmbedIO.Cli.TestPlugin/Plugin.cs` for a working example. API and WebSocket
routes are registered before static files; unmatched API paths fall through.

## Local tool packaging

The CLI uses the same version property and build workflow as the rest of this
repository. The distinct package ID avoids overwriting the archived tool's
identity; the command remains `embedio-cli`.

```sh
dotnet pack src/EmbedIO.Cli -c Release -o ./artifacts
dotnet tool install EmbedIO-Neo.Cli --add-source ./artifacts --tool-path ./tools --version 1.0.0
./tools/embedio-cli --help
```

These commands build and install locally. For the released tool, use
`dotnet tool install --global EmbedIO-Neo.Cli --version 1.0.0` instead. The standards Publish
workflow prepares GitHub assets and NuGet publication through trusted publishing.
The package identities and 1.0.0 release were approved by William. Use an
isolated tool path if the old tool is installed globally.

## Provenance and migration

Ported from [Unosquare's EmbedIO CLI](https://github.com/unosquare/embedio-cli),
commit `a7541483bcbecafcbaaddfb0f5c73ea48774f00b` (archived CLI version 0.3.2).
Its original MIT copyright and permission notice are preserved in
[licenses/embedio-cli-LICENSE](../../licenses/embedio-cli-LICENSE) and the tool package.

William approved the .NET 10/current-API port and plugin migration on October 4,
2026. The old tool targeted .NET Core 2.2 and EmbedIO 2.9.2. Old plugin binaries
and `Unosquare.Labs.EmbedIO` types are not binary-compatible: rebuild against this
repository and migrate controllers/routes and WebSocket handlers as above.
Apply the [JSON and SWAN migration guidance](../compatibility/migration.md) too.

The original path/port/API options, default roots, browser launch, API-only mode,
and companion watch port are retained. SWAN parsing/logging is replaced by .NET
code. The current core file server replaces the archived hand-written file
server, so directory markup, error responses, and HTTP details follow current
EmbedIO behavior. Startup errors are awaited, watcher/listener resources are
disposed on shutdown, `--no-watch` no longer injects a dead reload script, and
watching includes subdirectories. No old tool binaries are bundled.
