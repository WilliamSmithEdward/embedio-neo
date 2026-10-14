using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal interface IGracefulHttpListener
    {
        Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken);
    }

    // A listener that hands each admitted request straight to one dispatcher,
    // on the admitting connection's thread, instead of queueing it for
    // GetContextAsync. The server installs the dispatcher once; the pull API
    // then serves only listeners without one.
    internal interface IDispatchingHttpListener
    {
        void SetDispatcher(Func<IHttpContextImpl, Task> dispatcher);
    }

    internal sealed class ListenerDrainedException : Exception
    {
        internal ListenerDrainedException() : base("The listener finished its requested graceful drain.") { }
    }
}
