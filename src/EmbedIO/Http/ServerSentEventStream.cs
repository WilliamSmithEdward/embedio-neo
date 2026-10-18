using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>An explicitly streamed event subscription understood by controller bindings.</summary>
    public sealed class ServerSentEventStream
    {
        private static readonly byte[] Connected = WebServer.Utf8NoBomEncoding.GetBytes(": connected\n\n");
        private static readonly byte[] Heartbeat = WebServer.Utf8NoBomEncoding.GetBytes(": keep-alive\n\n");
        private readonly ServerSentEventHub _hub;

        internal ServerSentEventStream(ServerSentEventHub hub) => _hub = hub;

        /// <summary>Subscribes after authorization and streams until completion, cancellation, overflow or transport failure.</summary>
        /// <param name="context">The response context.</param>
        /// <param name="cancellationToken">Additional cooperative cancellation.</param>
        /// <returns>The entire subscription/response lifetime. Failures after commitment abort the response.</returns>
        public async Task WriteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken,
                cancellationToken
            );
            var token = linked.Token;
            token.ThrowIfCancellationRequested();

            if (
                context.Request.HttpVerb == HttpVerbs.Head
                || context.Response.StatusCode < 200
                || context.Response.StatusCode is 204 or 205 or 304
            )
            {
                context.Response.ContentType = "text/event-stream";
                return;
            }

            if (context.Response.Headers[HttpHeaderNames.ContentLength] != null)
                throw new InvalidOperationException("An event stream cannot use Content-Length.");

            if (
                !context.Request.TryNegotiateContentEncoding(false, out var coding, out _)
                || coding != CompressionMethod.None
            )
                throw HttpException.NotAcceptable(HttpHeaderNames.AcceptEncoding);

            using var subscription = _hub.Subscribe(context.Request.Headers["Last-Event-ID"]);
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            context.Response.Headers["Cache-Control"] = "no-cache";
            Stream? output = null;
            Exception? failure = null;

            try
            {
                output = context.OpenResponseStream(false, false);
                await Send(Connected).ConfigureAwait(false);

                while (true)
                {
                    ServerSentEvent? item;

                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        if (_hub.Options.HeartbeatInterval != Timeout.InfiniteTimeSpan)
                            idle.CancelAfter(_hub.Options.HeartbeatInterval);

                        try
                        {
                            item = await subscription
                                .ReadAsync(idle.Token)
                                .ConfigureAwait(false);
                        }

                        catch (OperationCanceledException) when (
                            !token.IsCancellationRequested && idle.IsCancellationRequested
                        )
                        {
                            await Send(Heartbeat).ConfigureAwait(false);
                            continue;
                        }
                    }

                    if (item == null) break;

                    await Send(item.Frame).ConfigureAwait(false);
                }

                var completedOutput = output;
                output = null;
#if NET10_0_OR_GREATER
                await completedOutput
                    .DisposeAsync()
                    .ConfigureAwait(false);
#else
                completedOutput.Dispose();
#endif
            }

            catch (Exception error)
            {
                failure = error;

                if (output != null || ResponseCommitment.IsCommitted(context))
                    await ResponseCommitment
                        .AbortAsync(context, error)
                        .ConfigureAwait(false);

                throw;
            }

            finally
            {
                if (output != null)
                {
                    try
                    {
#if NET10_0_OR_GREATER
                        await output
                            .DisposeAsync()
                            .ConfigureAwait(false);
#else
                        output.Dispose();
#endif
                    }

                    catch (Exception cleanup) when (failure != null)
                    {
                        throw new AggregateException("The event response and its cleanup failed.", failure, cleanup);
                    }
                }
            }

            async Task Send(byte[] bytes)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(_hub.Options.WriteTimeout);

                if (output == null) throw new InvalidOperationException("The event response has not been opened.");

                await output
                    .WriteAsync(bytes, 0, bytes.Length, deadline.Token)
                    .ConfigureAwait(false);
                await output
                    .FlushAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
        }
    }
}
