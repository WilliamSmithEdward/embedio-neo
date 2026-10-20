using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        private sealed class BeforeHandlerPlan
        {
            private readonly bool _runController;
            private readonly Func<WebApiController, CancellationToken, Task>? _invoke;

            internal BeforeHandlerPlan(bool runController, Func<WebApiController, CancellationToken, Task>? invoke)
            {
                _runController = runController;
                _invoke = invoke;
            }

            internal async Task ExecuteAsync(WebApiController controller, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();

                if (_runController) controller.PreProcessRequest();

                token.ThrowIfCancellationRequested();

                if (_invoke != null)
                    await (_invoke(controller, token)
                        ?? throw new InvalidOperationException(
                            "The before-handler hook returned a null task."
                        )).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
            }
        }

        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static BeforeHandlerPlan? ReadBeforeHandler(Type controllerType, MethodInfo action)
        {
            var attribute = action.GetCustomAttribute<BeforeHandlerAttribute>(true)
                ?? controllerType.GetCustomAttribute<BeforeHandlerAttribute>(true);

            if (attribute == null) return null;

            if (attribute.Behavior < BeforeHandlerBehavior.Append || attribute.Behavior > BeforeHandlerBehavior.Skip)
                throw new ArgumentException("Unknown before-handler behavior.");

            if (attribute.Behavior == BeforeHandlerBehavior.Skip)
            {
                if (attribute.MethodName != null)
                    throw new ArgumentException("Skip cannot name a before-handler hook.");

                return new BeforeHandlerPlan(false, null);
            }

            if (string.IsNullOrWhiteSpace(attribute.MethodName))
                throw new ArgumentException("A before-handler hook name is required.");

            var hooks = controllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == attribute.MethodName)
                .ToArray();

            if (hooks.Length != 1)
                throw new ArgumentException("The before-handler hook must identify one public instance method.");

            var hook = hooks[0];
            var parameters = hook.GetParameters();

            if (
                hook.ContainsGenericParameters
                || (hook.ReturnType != typeof(void) && hook.ReturnType != typeof(Task))
                || (hook.ReturnType == typeof(void) && hook.IsDefined(typeof(AsyncStateMachineAttribute), false))
                || (parameters.Length != 0
                    && (parameters.Length != 1 || parameters[0].ParameterType != typeof(CancellationToken)))
            )
                throw new ArgumentException(
                    "A before-handler hook must return void or Task and accept no arguments or one CancellationToken."
                );

            var instance = Expression.Parameter(typeof(WebApiController), "controller");
            var token = Expression.Parameter(typeof(CancellationToken), "token");
            var call = Expression.Call(
                Expression.Convert(instance, controllerType),
                hook,
                parameters.Length == 0 ? [] : [token]
            );
            Expression body = hook.ReturnType == typeof(void)
                ? Expression.Block(call, Expression.Constant(Task.CompletedTask))
                : call;
            return new BeforeHandlerPlan(
                attribute.Behavior == BeforeHandlerBehavior.Append,
                Expression
                    .Lambda<Func<WebApiController, CancellationToken, Task>>(body, instance, token)
                    .Compile()
            );
        }

        [RequiresDynamicCode(ReflectionMessage)]
        private static RouteHandlerCallback CompileHookedOwnedHandler(
            Type controllerType,
            ParameterExpression controller,
            Expression factoryExpression,
            Expression callMethod,
            ParameterExpression context,
            ParameterExpression route,
            BeforeHandlerPlan before
        )
        {
            var instance = Expression.Parameter(typeof(WebApiController), "instance");
            var invoke = Expression
                .Lambda<Func<WebApiController, IHttpContext, RouteMatch, Task>>(
                    Expression.Block(
                        [controller],
                        Expression.Assign(controller, Expression.Convert(instance, controllerType)),
                        callMethod
                    ),
                    instance,
                    context,
                    route
                )
                .Compile();
            var factory = Expression
                .Lambda<Func<WebApiController>>(Expression.Convert(factoryExpression, typeof(WebApiController)))
                .Compile();
            return async (httpContext, match) =>
            {
                var created = factory();
                created.HttpContext = httpContext;
                created.Route = match;
                Task work;

                try
                {
                    await before
                        .ExecuteAsync(created, httpContext.CancellationToken)
                        .ConfigureAwait(false);
                    work = invoke(created, httpContext, match);
                }

                finally
                {
                    if (created is IDisposable disposable) disposable.Dispose();
                }

                await work.ConfigureAwait(false);
            };
        }
    }
}
