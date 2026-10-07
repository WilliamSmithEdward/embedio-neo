# Send Neo diagnostics to your application's logging providers

[Upstream #548](https://github.com/unosquare/embedio/issues/548), proposed by
original maintainer `rdeago`, sought to remove SWAN, especially its terminal and
JSON dependencies. Neo already completed that removal with William's approval
for the 1.0.0 baseline. The [migration guide](../compatibility/migration.md)
documents those earlier API changes; this guide introduces no further breaking
change. In the linked design discussion, [gabriele-ricci-kyklos](https://github.com/unosquare/embedio/issues/546#issuecomment-1054160980)
recommended Microsoft's abstractions so applications could select providers.

Neo emits through `EmbedIO.Diagnostics.Log.Source`, a `TraceSource`. An
application-owned listener can forward those events to its `ILoggerFactory`.
The core remains free of logging-provider packages; installing the example's
Console provider is an application dependency. No new Neo logging API or
adapter package is required for this approach.

## Run the complete Console example

Create a .NET 10 application using the verified package versions:

```sh
dotnet new console --framework net10.0 --name NeoLogging
cd NeoLogging
dotnet add package EmbedIO-Neo --version 1.0.2
dotnet add package Microsoft.Extensions.Logging.Console --version 10.0.12
```

Save the following three complete files in that project. Replace the generated
`Program.cs`, then run `dotnet run`. Request
`http://127.0.0.1:8877/hello` with a browser or:

```sh
curl http://127.0.0.1:8877/hello
```

The response is `hello`. Console output uses the `EmbedIO` logger category and
includes startup, request and shutdown diagnostics. Press Ctrl+C to cancel the
server. The application disposes its factory after server shutdown and bridge
removal, allowing the Console provider to finish queued output.

### Program.cs

```csharp
using System;
using System.Threading;
using Microsoft.Extensions.Logging;

using var factory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true));
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
Console.WriteLine("GET http://127.0.0.1:8877/hello ; press Ctrl+C to stop.");
await LoggingExample.RunAsync(stopping.Token, factory, "http://127.0.0.1:8877/");
```

### LoggingExample.cs

```csharp
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using Microsoft.Extensions.Logging;
using NeoLog = EmbedIO.Diagnostics.Log;

public static class LoggingExample
{
    public static async Task RunAsync(CancellationToken stopping, ILoggerFactory factory, string url)
    {
        // The caller owns the factory; attach one bridge at application startup.
        using (var listener = new MicrosoftLoggingTraceListener(factory.CreateLogger("EmbedIO")))
        {
            NeoLog.Source.Listeners.Add(listener);
            try
            {
                using (var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                    .WithModule(new ActionModule("/hello", HttpVerbs.Get,
                        c => c.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding))))
                {
                    try { await server.RunAsync(stopping).ConfigureAwait(false); }
                    catch (System.OperationCanceledException) when (stopping.IsCancellationRequested) { }
                } // Keep the listener attached through server disposal.
            }
            finally { NeoLog.Source.Listeners.Remove(listener); }
        } // Disposes only the bridge, not the caller's factory.
    }
}
```

### MicrosoftLoggingTraceListener.cs

```csharp
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;

// Application-owned bridge. This is not an EmbedIO package API.
public sealed class MicrosoftLoggingTraceListener : TraceListener
{
    private readonly object gate = new object();
    private readonly ILogger logger;
    private bool closed;
    private int forwardingFailures;
    private int recursiveEventsDropped;
    [ThreadStatic] private static bool forwarding;

    public MicrosoftLoggingTraceListener(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override bool IsThreadSafe => true;
    public int ForwardingFailures => Volatile.Read(ref forwardingFailures);
    public int RecursiveEventsDropped => Volatile.Read(ref recursiveEventsDropped);

    public override void Write(string? message) => Forward(null, "", TraceEventType.Information, 0, message, null);
    public override void WriteLine(string? message) => Write(message);

    public override void TraceEvent(TraceEventCache? cache, string source,
        TraceEventType type, int id, string? message)
        => Forward(cache, source, type, id, message, null);

    public override void TraceEvent(TraceEventCache? cache, string source,
        TraceEventType type, int id, string? format, params object?[]? args)
        => Forward(cache, source, type, id, format, args);

    private void Forward(TraceEventCache? cache, string source,
        TraceEventType type, int id, string? format, object?[]? args)
    {
        if (forwarding)
        {
            Interlocked.Increment(ref recursiveEventsDropped);
            return;
        }

        lock (gate)
        {
            if (closed) return;
            forwarding = true;
            try
            {
                var level = Map(type);
                if (level == LogLevel.None) return;
                if (Filter != null && !Filter.ShouldTrace(cache, source, type, id, format, args, null, null)) return;
                if (!logger.IsEnabled(level)) return;
                var text = args == null ? format ?? ""
                    : string.Format(CultureInfo.InvariantCulture, format ?? "", args);
                text = text.Replace("\r", "\\r").Replace("\n", "\\n");
                // The constant template preserves braces in the message as data.
                logger.Log(level, new EventId(id), null, "{TraceMessage}", text);
            }
            catch (Exception)
            {
                // Never report this through the same source: that would recurse.
                Interlocked.Increment(ref forwardingFailures);
            }
            finally { forwarding = false; }
        }
    }

    private static LogLevel Map(TraceEventType type)
    {
        switch (type)
        {
            case TraceEventType.Critical: return LogLevel.Critical;
            case TraceEventType.Error: return LogLevel.Error;
            case TraceEventType.Warning: return LogLevel.Warning;
            case TraceEventType.Information: return LogLevel.Information;
            case TraceEventType.Verbose: return LogLevel.Debug;
            default: return LogLevel.None;
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (gate) { closed = true; }
        base.Dispose(disposing);
    }
}
```

## Use the factory you already own

In a Generic Host application, obtain its existing `ILoggerFactory` and pass it
to `LoggingExample.RunAsync` instead of creating another factory. The following
is a partial replacement inside an already configured host; `host`, `stopping`
and the URL are application-owned:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var factory = host.Services.GetRequiredService<ILoggerFactory>();
await LoggingExample.RunAsync(stopping.Token, factory, "http://127.0.0.1:8877/");
```

The host disposes that factory. The bridge borrows its logger and never disposes
the factory/provider. Providers selected by the application continue to control
their destinations and filtering; the Console provider is just one verified
example. See Microsoft's [logging guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging)
and [provider overview](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/providers).

## Mapping and filtering

| Neo trace event | ILogger level |
| --- | --- |
| Critical | Critical |
| Error | Error |
| Warning | Warning |
| Information | Information |
| Verbose | Debug |

Neo's current debug and trace helpers both emit Verbose; the bridge cannot
distinguish them afterward. Activity events such as Start/Stop/Transfer are
ignored, rather than represented as severity levels. This helper targets Neo's
`TraceEvent` output, not general `TraceData` or activity-correlation translation.

The caller chooses category `EmbedIO`; the existing `[component]` prefix stays
in the message. Event IDs are preserved (Neo currently emits zero). The fixed
`{TraceMessage}` template supplies the whole message as one structured field,
keeping literal braces as data. It does not infer new categories, request IDs,
original message-template arguments or exception objects from formatted text.

There are independent filters: Neo's source switch, this listener's optional
`TraceFilter`, and the application's ILogger/provider rules. All must allow an
event. Disabled provider/filter checks happen before message formatting. The
example leaves Neo's default Information threshold intact. For debug output,
explicitly configure both the source and the factory/provider to allow Debug:

```csharp
// Partial configuration: apply before startup, with the existing factory builder.
EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Verbose;
// In LoggerFactory.Create: builder.SetMinimumLevel(LogLevel.Debug);
```

Configure filters before attaching the listener; this example does not promise
safe concurrent mutation of its Filter property. It does serialize delivery and
disposal. `Write`/`WriteLine` are treated as independent Information messages,
not combined into a multi-call text buffer.

## Exceptions, ownership and failure behavior

Neo currently converts exceptions to text before tracing. The provider receives
that text with a null exception argument. Parsing it cannot restore the original
exception instance. Application-owned code needing structured exception data
should call its ILogger directly with the original exception. No exception or
diagnostics contract is changed by this bridge.

The source is process-wide. Attach once at application startup, before server
construction, and keep the listener through server disposal. Remove only that
listener, then dispose it. Do not clear other listeners or close the shared
source. The complete example also removes its registration on startup failure.
Disposing the bridge alone does not unregister it from a source collection.

Callbacks are synchronous and serialized. Use a provider that returns promptly;
a blocking provider can delay the request emitting the diagnostic. No extra
unbounded queue or background worker is introduced by this bridge. Normal
disposal waits for its current callback and prevents later forwarding. Shutdown
the server before disposing the borrowed factory. A provider should not wait
for another thread to dispose the bridge from inside its own callback.

Filter, formatting and provider exceptions are contained and counted by
`ForwardingFailures`. Same-thread recursive forwarding is dropped and counted
by `RecursiveEventsDropped`; the guard covers these bridge instances on that
thread. Observe those counters through an independent health mechanism, not by
logging failures back into the same source. The helper protects its own path;
other application listeners still control their own failure behavior.

CR/LF are escaped for destination records, including direct application trace
events. Neo's internal security observers retain the original message and are
independent of source/provider verbosity. The bridge does not register, remove
or replace those observers.

## Validation and limits

The complete Console setup and helper server were compiled against published
EmbedIO-Neo 1.0.2 and Logging.Console 10.0.12. Twenty-three local cases passed
against both current source and the exact published core on Windows/.NET 10.0.11:
level/category/ID/template mapping, literal braces, disabled filters before
formatting, failure recovery, line-break protection, concurrent whole records,
disposal, recursion, original security-observer text, real HTTP, cancellation,
startup cleanup and actual Console-provider shutdown output.

The helper files also compile for .NET Standard 2.0 with pinned
Logging.Abstractions 10.0.12. That is compilation evidence, not certification of
every old runtime, Unity, UWP or mobile provider. Existing replacement/diagnostics
regressions pass without changing production APIs, defaults, targets, dependencies
or test baselines. The temporary application-helper harness lives under ignored
TestResults; the source listener and provider dependency are not added to Neo's
production packages. Package/assembly metadata auditing confirms SWAN is absent;
historical migration and parity-test references are documentation of prior work.
