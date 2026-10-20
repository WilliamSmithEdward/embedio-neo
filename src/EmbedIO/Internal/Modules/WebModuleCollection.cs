using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Utilities;

namespace EmbedIO.Internal
{
    internal sealed class WebModuleCollection : DisposableComponentCollection<IWebModule>
    {
        private readonly string _logSource;

        internal WebModuleCollection(string logSource) => _logSource = logSource;

        internal void StartAll(CancellationToken cancellationToken)
        {
            foreach (var (name, module) in SafeNamedComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                $"Starting module {name}...".Debug(_logSource);
                module.Start(cancellationToken);
            }
        }

        internal async Task DispatchRequestAsync(IHttpContext context)
        {
            if (context.IsHandled)
                return;

            var requestedPath = context.RequestedPath;
            // A module group dispatches with its own base route as the template prefix.
            var telemetry = RequestTelemetry.Of(context);
            var prefix = telemetry?.Route;

            foreach (var (name, module) in SafeNamedComponents)
            {
                var routeMatch = module.MatchUrlPath(requestedPath);

                if (routeMatch == null)
                    continue;

                $"[{context.Id}] Processing with {name}.".Debug(_logSource);
                telemetry?.Route = RequestTelemetry.Combine(prefix, module.BaseRoute);
                context.GetImplementation().Route = routeMatch;
                await module
                    .HandleRequestAsync(context)
                    .ConfigureAwait(false);

                if (context.IsHandled)
                    break;
            }
        }
    }
}
