using System;
using System.Threading.Tasks;
using EmbedIO.WebApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmbedIO.DependencyInjection
{
    /// <summary>Optional Microsoft DI integration for EmbedIO.</summary>
    public static class DependencyInjectionExtensions
    {
        internal static readonly object ServicesKey = new object();

        /// <summary>
        /// Adds one scope per HTTP request. Add this before modules that use request services.
        /// The application retains ownership of the root provider.
        /// </summary>
        /// <param name="server">The server being configured.</param>
        /// <param name="services">The root service provider.</param>
        /// <returns>The server.</returns>
        public static TServer WithDependencyInjection<TServer>(this TServer server, IServiceProvider services)
            where TServer : class, IWebServer
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (services == null) throw new ArgumentNullException(nameof(services));
            foreach (var module in server.Modules)
            {
                if (module is RequestServicesModule)
                    throw new InvalidOperationException("Dependency injection is already configured for this server.");
            }

            server.WithModule(new RequestServicesModule(services.GetRequiredService<IServiceScopeFactory>()));
            return server;
        }

        /// <summary>Gets the provider shared by modules and controllers within this request.</summary>
        /// <param name="context">The request context.</param>
        /// <returns>The request's scoped provider.</returns>
        public static IServiceProvider GetRequestServices(this IHttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return context.Items.TryGetValue(ServicesKey, out var value) && value is IServiceProvider services
                ? services
                : throw new InvalidOperationException("Request services are unavailable. Add WithDependencyInjection before modules that use DI, and do not use request services after request completion.");
        }

        /// <summary>
        /// Registers a fresh controller activated from request services. The adapter owns
        /// the controller; the request scope owns its resolved dependencies.
        /// Controller registrations in the container are deliberately not used.
        /// </summary>
        /// <typeparam name="TController">The controller type.</typeparam>
        /// <param name="module">The Web API module.</param>
        /// <returns>The module.</returns>
        public static WebApiModule WithControllerServices<TController>(this WebApiModule module)
            where TController : WebApiController
            => WithControllerServices(module, typeof(TController));

        /// <summary>Registers a runtime-discovered controller with request-scoped constructor injection.</summary>
        /// <param name="module">The Web API module.</param>
        /// <param name="controllerType">A concrete controller type.</param>
        /// <returns>The module.</returns>
        public static WebApiModule WithControllerServices(this WebApiModule module, Type controllerType)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (controllerType == null) throw new ArgumentNullException(nameof(controllerType));
            if (controllerType.IsAbstract || controllerType.ContainsGenericParameters || !controllerType.IsSubclassOf(typeof(WebApiController)))
                throw new ArgumentException("A concrete WebApiController type is required.", nameof(controllerType));

            var activate = ActivatorUtilities.CreateFactory(controllerType, Type.EmptyTypes);
            module.RegisterControllerWithContext(controllerType, context =>
            {
                var controller = (WebApiController)activate(context.GetRequestServices(), null);
                context.OnRequestCompleted(() => DisposeAsync(controller));
                return controller;
            }, (_, _) => Task.CompletedTask);
            return module;
        }

        /// <summary>
        /// Allows constructor injection of IHttpContext into request-scoped services and controllers.
        /// Call before building the provider. This does not create an ambient/global context.
        /// </summary>
        /// <param name="services">The application's service registrations.</param>
        /// <returns>The registrations.</returns>
        public static IServiceCollection AddEmbedIORequestContext(this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            services.TryAddScoped<RequestContext>();
            services.TryAddScoped<IHttpContext>(provider => provider.GetRequiredService<RequestContext>().Context
                ?? throw new InvalidOperationException("IHttpContext can only be resolved within an EmbedIO request."));
            return services;
        }

        /// <summary>
        /// Registers a host-owned server. DI is configured before the application modules.
        /// The host starts the listener, cancels it on shutdown and waits for scoped cleanup.
        /// </summary>
        /// <param name="services">The application's service registrations.</param>
        /// <param name="options">Configures listener options.</param>
        /// <param name="configure">Adds modules; root services are for application-lifetime dependencies only.</param>
        /// <returns>The registrations.</returns>
        public static IServiceCollection AddEmbedIO(
            this IServiceCollection services,
            Action<WebServerOptions> options,
            Action<WebServer, IServiceProvider> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            services.AddEmbedIORequestContext();
            services.AddSingleton<IHostedService>(provider =>
            {
                var server = new WebServer(options);
                try
                {
                    server.WithDependencyInjection(provider);
                    configure(server, provider);
                    return new EmbedIOHostedService(server,
                        provider.GetRequiredService<IHostApplicationLifetime>(),
                        provider.GetRequiredService<ILogger<EmbedIOHostedService>>());
                }
                catch
                {
                    server.Dispose();
                    throw;
                }
            });
            return services;
        }

        /// <summary>
        /// After stopping the listener, prevents new DI scopes and waits for active
        /// requests to finish cleanup. Call before disposing the root provider.
        /// </summary>
        /// <param name="server">The stopped server.</param>
        /// <returns>A task representing completion of request cleanup.</returns>
        public static async Task DrainDependencyInjectionAsync(this IWebServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (server.State != WebServerState.Stopped)
                throw new InvalidOperationException("Stop and await the server before draining request services.");
            foreach (var module in server.Modules)
            {
                if (module is RequestServicesModule requests)
                    await requests.DrainAsync().ConfigureAwait(false);
            }
        }
        internal static async Task DisposeAsync(object value)
        {
            if (value is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (value is IDisposable disposable)
                disposable.Dispose();
        }

        internal sealed class RequestContext
        {
            public IHttpContext? Context { get; set; }
        }
    }
}

