using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Writes UTF-8 server-sent events to one HTTP response.</summary>
    /// <remarks>
    /// Await each write before starting another. This writer does not own or close the response
    /// stream. Transport errors propagate to the caller; server cancellation is always observed.
    /// Client disconnects do not cancel the server token. Event IDs do not implement replay.
    /// </remarks>
    public sealed class ServerSentEventWriter
    {
        private readonly Stream _stream;
        private readonly CancellationToken _serverToken;
        private int _writing;

        internal ServerSentEventWriter(Stream stream, CancellationToken serverToken)
        {
            _stream = stream;
            _serverToken = serverToken;
        }

        /// <summary>Writes and flushes one event, preserving multiline data.</summary>
        /// <param name="data">Event data, including empty data if desired.</param>
        /// <param name="eventName">Optional event type; null uses the browser's message event.</param>
        /// <param name="id">Optional event ID; an empty ID resets the browser's last event ID.</param>
        /// <param name="cancellationToken">Additional cancellation for this write.</param>
        /// <returns>A task that completes after the complete frame is flushed.</returns>
        /// <exception cref="ArgumentNullException">Data is null.</exception>
        /// <exception cref="ArgumentException">An event name or ID contains CR, LF or NUL.</exception>
        /// <exception cref="InvalidOperationException">Another write is in progress.</exception>
        public Task WriteAsync(string data, string? eventName = null, string? id = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            ValidateField(eventName, nameof(eventName));
            ValidateField(id, nameof(id));
            var frame = new StringBuilder();
            if (eventName != null) frame.Append("event: ").Append(eventName).Append('\n');
            if (id != null) frame.Append("id: ").Append(id).Append('\n');
            AppendLines(frame, "data: ", data);
            frame.Append('\n');
            return SendAsync(frame.ToString(), cancellationToken);
        }

        /// <summary>Writes and flushes a comment, such as a heartbeat, without dispatching an event.</summary>
        /// <param name="comment">Comment text, which can contain multiple lines.</param>
        /// <param name="cancellationToken">Additional cancellation for this write.</param>
        /// <returns>A task that completes after the comment frame is flushed.</returns>
        public Task WriteCommentAsync(string comment = "keep-alive", CancellationToken cancellationToken = default)
        {
            if (comment == null) throw new ArgumentNullException(nameof(comment));
            var frame = new StringBuilder();
            AppendLines(frame, ": ", comment);
            frame.Append('\n');
            return SendAsync(frame.ToString(), cancellationToken);
        }

        private static void ValidateField(string? value, string name)
        {
            if (value != null && value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Event names and IDs must not contain CR, LF or NUL.", name);
        }

        private static void AppendLines(StringBuilder frame, string prefix, string text)
        {
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                frame.Append(prefix).Append(line).Append('\n');
        }

        private async Task SendAsync(string frame, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
                throw new InvalidOperationException("Await the previous event write before starting another.");
            try
            {
                using var linked = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_serverToken, cancellationToken)
                    : null;
                var token = linked?.Token ?? _serverToken;
                token.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(frame);
                await _stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        }
    }
}
