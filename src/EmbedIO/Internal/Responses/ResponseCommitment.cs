using System;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO.Internal
{
    // A response is replaceable until its final headers reach the wire. After that
    // an exception handler cannot change the status or body, so the only honest
    // outcome is to end the response in a way the client can detect: an HTTP/1
    // connection closes without the body terminator and a multiplexed stream is
    // reset instead of ending the partial body with END_STREAM.
    internal static class ResponseCommitment
    {
        internal static bool IsCommitted(IHttpContext context) => context switch
        {
            Caching.OutputCacheCapture capture => IsCommitted(capture.Inner),
            HttpListenerContext http1 => http1.HttpListenerResponse.HeadersSent,
            MultiplexedContext multiplexed => multiplexed.ResponseHeadersSent,
            _ => false,
        };

        internal static Task AbortAsync(IHttpContext context, Exception cause)
        {
            switch (context)
            {
                case Caching.OutputCacheCapture capture:
                    return AbortAsync(capture.Inner, cause);
                case HttpListenerContext http1:
                    http1.HttpListenerResponse.Abort();
                    return Task.CompletedTask;
                case MultiplexedContext multiplexed:
                    return multiplexed.AbortExchangeAsync(cause);
                default:
                    return Task.CompletedTask;
            }
        }
    }
}
