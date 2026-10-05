using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace EmbedIO.DependencyInjection
{
    // Long-lived middleware; only the scope factory is captured, never scoped services.
    internal sealed class RequestServicesModule : WebModuleBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly object _sync = new object();
        private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private bool _stopping;

        internal RequestServicesModule(IServiceScopeFactory scopeFactory) : base("/")
        {
            _scopeFactory = scopeFactory;
        }

        public override bool IsFinalHandler => false;

        protected override Task OnRequestAsync(IHttpContext context)
        {
            lock (_sync)
            {
                if (_stopping) throw new OperationCanceledException("The DI server is stopping.");
                _active++;
            }

            // Register tracking first, so it runs after controller and scope cleanup.
            context.OnRequestCompleted(() =>
            {
                lock (_sync)
                {
                    _active--;
                    if (_stopping && _active == 0) _drained.TrySetResult(true);
                }

                return Task.CompletedTask;
            });

            var scope = _scopeFactory.CreateScope();
            context.OnRequestCompleted(async () =>
            {
                try
                {
                    await DependencyInjectionExtensions.DisposeAsync(scope).ConfigureAwait(false);
                }
                finally
                {
                    context.Items.Remove(DependencyInjectionExtensions.ServicesKey);
                }
            });
            context.Items.Add(DependencyInjectionExtensions.ServicesKey, scope.ServiceProvider);
            var accessor = scope.ServiceProvider.GetService<DependencyInjectionExtensions.RequestContext>();
            if (accessor != null) accessor.Context = context;
            return Task.CompletedTask;
        }

        internal Task DrainAsync()
        {
            lock (_sync)
            {
                _stopping = true;
                if (_active == 0) _drained.TrySetResult(true);
                return _drained.Task;
            }
        }
    }
}
