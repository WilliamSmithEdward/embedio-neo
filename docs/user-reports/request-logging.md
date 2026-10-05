# Disable request summaries while keeping diagnostics

[Upstream #580](https://github.com/unosquare/embedio/issues/580), reported by
`faljse`, asks whether GET/POST request logging can be disabled without turning
off logging altogether. No reproduction or follow-up comments were supplied.

EmbedIO-Neo uses `EmbedIO.Diagnostics.Log.Source`, a .NET `TraceSource`, rather
than the original project's SWAN logger. You can filter individual listeners
using existing .NET APIs. No production change is required for this answer.

## Runnable example

Create a .NET 10 console app and install the published package:

```sh
dotnet new console --framework net10.0 --name QuietRequests
cd QuietRequests
dotnet add package EmbedIO-Neo --version 1.0.1
```

Replace `Program.cs` with this complete program. It suppresses the normal
information-level request-completion summaries for **all HTTP verbs**, while
retaining startup/shutdown information, warnings and errors. The listener also
retains information messages that do not match the completion-summary format.

```csharp
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Diagnostics;

// Configure once, before starting the server. This trace source is process-wide.
// This standalone application's console is its only diagnostics destination.
Log.Source.Listeners.Clear();
using var console = new ConsoleTraceListener();
console.Filter = new WithoutRequestSummaries();
Log.Source.Listeners.Add(console);
Log.Source.Switch.Level = SourceLevels.Information;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithUrlPrefix("http://127.0.0.1:8877/")
    .WithMode(HttpListenerMode.EmbedIO));
server.WithModule(new ActionModule("/hello", HttpVerbs.Any,
    context => context.SendStringAsync("hello", "text/plain", Encoding.UTF8)));

Log.Source.TraceEvent(TraceEventType.Warning, 0,
    "Application warning: logging is still enabled.");
Console.WriteLine("Listening at http://127.0.0.1:8877/hello; press Ctrl+C to stop.");
try
{
    await server.RunAsync(stop.Token);
}
finally
{
    Log.Source.Flush();
    Log.Source.Listeners.Remove(console);
}

sealed class WithoutRequestSummaries : TraceFilter
{
    private static readonly Regex Summary = new(
        @"^\[[^\]\r\n]+\] \S+ [^\r\n]*: ""[0-9]{3} [^""\r\n]*"" sent in [^ \r\n]+ms \((chunked|[0-9]+ bytes)\)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public override bool ShouldTrace(TraceEventCache? cache, string source,
        TraceEventType type, int id, string? format, object?[]? arguments,
        object? data, object?[]? dataArray)
        => !(source == "EmbedIO" && type == TraceEventType.Information
            && format == "[{0}] {1}" && arguments is { Length: 2 }
            && arguments[1] is string message && Summary.IsMatch(message));
}
```

Run `dotnet run`, then use another terminal:

```sh
curl -i http://127.0.0.1:8877/hello
curl -i -X POST http://127.0.0.1:8877/hello
curl -i http://127.0.0.1:8877/missing
```

GET and POST return `200` with `hello`. The missing path returns `404` and its
error diagnostic remains visible. The application warning and listener startup
messages also remain visible. Normal lines such as
`[WebServer] [request-id] GET /hello: "200 OK" sent in ...` are suppressed.
Ctrl+C stops the server and leaves shutdown information visible.

## Scope and limits

- A filter applies to one listener, not every destination automatically. In an
  existing application, preserve its listeners and set their `Filter` properties
  deliberately. Assigning a filter replaces that listener's existing filter;
  combine policies when needed. An unfiltered listener still receives summaries.
- `SourceLevels.Warning` removes **all** information messages, including startup
  information. `SourceLevels.Off` disables tracing. Neither is a selective
  request-summary filter.
- Request failures still produce error messages, even when their normal
  completion summaries are hidden. This intentionally keeps useful diagnostics.
  At `SourceLevels.Verbose`, module dispatch, routes and cancellation may also
  produce request-related messages; this example suppresses completion summaries
  only, and does not promise to remove every request-related diagnostic.
- This filter recognizes the current message format, since EmbedIO does not
  expose a dedicated request-summary event ID or category. It fails open if the
  format changes, so diagnostics stay visible. Recheck it when upgrading. Do not
  use text filtering as a privacy boundary or a way to hide secrets.
- `Log.Source` is shared by all servers in a process. The configuration above is
  application-wide, not a per-server switch. Internal security observers remain
  independent of trace-listener filtering.

The example targets .NET 10 and uses its non-backtracking regular-expression
engine. It works with the published 1.0.1 package and the current source on both
listener modes. Validation exercises successful requests, a missing route,
warnings, ordinary information messages, listener shutdown and cancellation.
This answers the support question for Neo; it does not establish how an
unspecified older SWAN-based application configured its own logger.
