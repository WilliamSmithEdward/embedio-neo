using System;
using System.Diagnostics;
using EmbedIO.Internal;
using Microsoft.Extensions.Logging;

namespace EmbedIO.Kestrel
{
    internal sealed class KestrelTraceLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

        public void Dispose() { }

        private sealed class Logger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            private static TraceEventType Severity(LogLevel level) => level switch
            {
                LogLevel.Critical => TraceEventType.Critical,
                LogLevel.Error => TraceEventType.Error,
                LogLevel.Warning => TraceEventType.Warning,
                LogLevel.Information => TraceEventType.Information,
                _ => TraceEventType.Verbose
            };

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel != LogLevel.None
                    && (category != "Microsoft.AspNetCore.Hosting.Diagnostics" || logLevel >= LogLevel.Warning)
                    && Diagnostics.Log.IsEnabled(Severity(logLevel));

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (!IsEnabled(logLevel)) return;

                try
                {
                    Diagnostics.Log.Source.TraceEvent(
                        Severity(logLevel),
                        eventId.Id,
                        "{0}: {1}{2}",
                        category,
                        formatter(state, exception),
                        exception == null ? string.Empty : Environment.NewLine + exception
                    );
                }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { }
            }
        }
    }
}
