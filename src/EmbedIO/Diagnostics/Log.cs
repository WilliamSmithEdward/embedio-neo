using System;
using System.Diagnostics;

namespace EmbedIO.Diagnostics
{
    /// <summary>Diagnostics emitted by EmbedIO. Configure listeners through <see cref="Source"/>.</summary>
    public static class Log
    {
        /// <summary>Gets the trace source used for server diagnostics.</summary>
        public static TraceSource Source { get; } = new TraceSource("EmbedIO", SourceLevels.Information);

        // Security criteria observe messages independently of diagnostic verbosity.
        internal static event Action<string>? MessageWritten;
        [ThreadStatic] private static bool _notifying;

        internal static void Write(TraceEventType level, string source, string message)
        {
            if (!_notifying)
            {
                _notifying = true;
                try { MessageWritten?.Invoke(message); }
                finally { _notifying = false; }
            }
            if (!Source.Switch.ShouldTrace(level))
                return;

            // Keep observers' original text for security criteria, but prevent
            // request-derived CR/LF from creating forged trace records.
            Source.TraceEvent(level, 0, "[{0}] {1}",
                source.Replace("\r", "\\r").Replace("\n", "\\n"),
                message.Replace("\r", "\\r").Replace("\n", "\\n"));
        }
    }

    internal static class LogExtensions
    {
        internal static void Info(this string message, string source = "EmbedIO") => Diagnostics.Log.Write(TraceEventType.Information, source, message);
        internal static void Debug(this string message, string source = "EmbedIO") => Diagnostics.Log.Write(TraceEventType.Verbose, source, message);
        internal static void Trace(this string message, string source = "EmbedIO") => Diagnostics.Log.Write(TraceEventType.Verbose, source, message);
        internal static void Warn(this string message, string source = "EmbedIO") => Diagnostics.Log.Write(TraceEventType.Warning, source, message);
        internal static void Error(this string message, string source = "EmbedIO") => Diagnostics.Log.Write(TraceEventType.Error, source, message);
        internal static void Log(this Exception exception, string source = "EmbedIO", string? message = null)
            => Diagnostics.Log.Write(TraceEventType.Error, source, (message == null ? string.Empty : message + " ") + exception);
    }
}
