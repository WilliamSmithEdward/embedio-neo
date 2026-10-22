using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace EmbedIO.Tests.TestObjects
{
    // Test-only, bounded observation of exceptions caught inside the QUIC owner.
    // No packet bytes, keys or production logging are added.
    internal sealed class QuicProtocolFailureTrace : IDisposable
    {
        [ThreadStatic]
        private static bool _observing;
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private bool _enabled;
        private bool _disposed;
        private int _captureFailed;

        internal bool CaptureFailed => Volatile.Read(ref _captureFailed) != 0;

        internal static QuicProtocolFailureTrace Start()
        {
            var trace = new QuicProtocolFailureTrace();
            AppDomain.CurrentDomain.FirstChanceException += trace.Observe;
            return trace;
        }

        internal void Select(bool enabled)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _events.Clear();
                Volatile.Write(ref _captureFailed, 0);
                Volatile.Write(ref _enabled, enabled);
            }
        }

        private void Observe(object? sender, FirstChanceExceptionEventArgs data)
        {
            if (!Volatile.Read(ref _enabled) || _observing) return;

            _observing = true;

            try
            {
                var error = data.Exception;
                var type = error.GetType();

                if (
                    type != typeof(InvalidDataException)
                    && type.FullName is not ("EmbedIO.Quic.TransportException" or "EmbedIO.Quic.KeyUpdateException")
                ) return;

                var stack = error.StackTrace ?? string.Empty;

                if (!stack.Contains("EmbedIO.Quic.", StringComparison.Ordinal)) return;

                lock (_gate)
                {
                    if (_disposed || !_enabled || _events.Count == 16) return;

                    var text = type.FullName + ": " + error.Message + Environment.NewLine + stack;
                    _events.Add(text[..Math.Min(text.Length, 4096)]);
                }
            }

            catch (Exception error) when (
                error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)
            )
            {
                // Observation cannot replace the exception being diagnosed.
                Volatile.Write(ref _captureFailed, 1);
            }

            finally
            {
                _observing = false;
            }
        }

        internal string[] Snapshot()
        {
            lock (_gate) return [.. _events];
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;
                Volatile.Write(ref _enabled, false);
                AppDomain.CurrentDomain.FirstChanceException -= Observe;
            }
        }
    }
}
