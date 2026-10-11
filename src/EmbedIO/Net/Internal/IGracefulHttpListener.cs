using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal interface IGracefulHttpListener
    {
        Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken);
    }

    internal sealed class ListenerDrainedException : Exception
    {
        internal ListenerDrainedException() : base("The listener finished its requested graceful drain.") { }
    }
}
