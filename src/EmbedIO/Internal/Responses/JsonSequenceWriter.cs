using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    internal sealed class JsonSequenceWriter<T>
    {
        private static readonly byte[] Start = { (byte)'[' };
        private static readonly byte[] End = { (byte)']' };
        private static readonly byte[] Comma = { (byte)',' };
        private static readonly byte[] Line = { (byte)'\n' };
        private readonly IAsyncEnumerable<T> _source;
        private readonly JsonTypeInfo<T> _typeInfo;
        private readonly JsonStreamOptions _options;
        private readonly bool _ndjson;

        internal JsonSequenceWriter(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo, JsonStreamOptions options, bool ndjson)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
            _options = options;
            _ndjson = ndjson;
        }

        internal async Task WriteAsync(IHttpContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            // A HEAD response or a status without an entity must not consume an unbounded producer.
            if (context.Request.HttpVerb == HttpVerbs.Head || context.Response.StatusCode < 200
                || context.Response.StatusCode is 204 or 205 or 304)
            {
                context.Response.ContentType = _ndjson ? "application/x-ndjson" : MimeType.Json;
                return;
            }

            if (context.Response.Headers[HttpHeaderNames.ContentLength] != null)
                throw new InvalidOperationException("A JSON stream cannot use a predeclared Content-Length.");

            IAsyncEnumerator<T>? enumerator = null;
            Stream? output = null;
            Exception? failure = null;
            try
            {
                enumerator = _source.GetAsyncEnumerator(token);
                using var record = new JsonRecordBuffer(_options.MaxItemBytes);
                var hasItem = await enumerator.MoveNextAsync().ConfigureAwait(false);
                if (hasItem) Serialize(record, enumerator.Current, token);
                // Producer and first-record failures still use the ordinary error pipeline.
                context.Response.ContentType = _ndjson ? "application/x-ndjson" : MimeType.Json;
                context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
                output = context.OpenResponseStream(false, _options.PreferCompression);
                if (!_ndjson) await output.WriteAsync(Start, 0, 1, token).ConfigureAwait(false);
                var batch = 0;
                var first = true;
                while (hasItem)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_ndjson && !first) await output.WriteAsync(Comma, 0, 1, token).ConfigureAwait(false);
                    await output.WriteAsync(record.Buffer, 0, record.Count, token).ConfigureAwait(false);
                    if (_ndjson) await output.WriteAsync(Line, 0, 1, token).ConfigureAwait(false);
                    first = false;
                    if (++batch == _options.FlushEvery)
                    {
                        await output.FlushAsync(token).ConfigureAwait(false);
                        batch = 0;
                    }
                    // Never prefetch: transport writes/flushes finish before advancing the producer.
                    hasItem = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasItem) Serialize(record, enumerator.Current, token);
                }
                // Cleanup failures must not turn a partial stream into an apparently successful response.
                var completed = enumerator;
                enumerator = null;
                await completed.DisposeAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!_ndjson) await output.WriteAsync(End, 0, 1, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
#if NET10_0_OR_GREATER
                await output.DisposeAsync().ConfigureAwait(false);
#else
                output.Dispose();
#endif
                output = null;
            }
            catch (Exception error)
            {
                failure = error;
                if (output != null || ResponseCommitment.IsCommitted(context))
                    await ResponseCommitment.AbortAsync(context, error).ConfigureAwait(false);
                throw;
            }
            finally
            {
                try
                {
                    if (enumerator != null)
                    {
                        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) when (failure != null) { throw new AggregateException("The producer and its cleanup failed.", failure, cleanup); }
                    }
                }
                finally
                {
                    if (output != null)
                    {
                        try
                        {
#if NET10_0_OR_GREATER
                            await output.DisposeAsync().ConfigureAwait(false);
#else
                            output.Dispose();
#endif
                        }
                        catch (Exception cleanup) when (failure != null) { throw new AggregateException("The stream and its cleanup failed.", failure, cleanup); }
                    }
                }
            }
        }

        private void Serialize(JsonRecordBuffer record, T value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            record.Reset();
            using (var writer = new Utf8JsonWriter(record, new JsonWriterOptions { Encoder = _typeInfo.Options.Encoder, MaxDepth = _typeInfo.Options.MaxDepth == 0 ? 64 : _typeInfo.Options.MaxDepth }))
            {
                JsonSerializer.Serialize(writer, value, _typeInfo);
                writer.Flush();
            }
            token.ThrowIfCancellationRequested();
            if (_ndjson)
                for (var i = 0; i < record.Count; i++)
                    if (record.Buffer[i] is 10 or 13) throw new JsonException("NDJSON converters must produce compact JSON without literal CR or LF.");
        }
    }
}
