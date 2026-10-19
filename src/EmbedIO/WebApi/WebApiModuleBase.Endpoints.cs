using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Endpoints;
using EmbedIO.Routing;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        private sealed class EndpointController : WebApiController { }

        internal RouteHandlerCallback PrepareEndpoint(
            EndpointHandler endpoint,
            RouteMatcher matcher,
            EndpointPlan plan,
            HttpVerbs verb
        )
        {
            if (plan.Authorization != null) _authorization.Validate(plan.Authorization);

            ValidateBrowserProtection(plan.Browser);
            var security = ResolveSecurityHeaders(plan.Headers, plan.Idempotency);
            var cache = ResolveOutputCache(plan.Cache);
            var idempotency = ResolveIdempotency(plan.Idempotency, verb, endpoint.Operation.ResultType, plan.Cache);
            var invoker = endpoint.CreateInvoker(this, matcher);

            if (cache != null && !IsStreamingResultType(endpoint.Operation.ResultType))
            {
                var bind = invoker;
                invoker = (controller, context, route, serializer) => Task.FromResult(
                    ControllerBindingServices.ExecuteOutputCachedAsync(controller, context, async capture =>
                    {
                        var work = await bind(controller, capture, route, serializer).ConfigureAwait(false);
                        await work.ConfigureAwait(false);
                    })
                );
            }

            var handler = CreateBoundHandler(
                invoker,
                Serializer,
                static _ => new EndpointController(),
                ReleaseStreamingController,
                false,
                IsStreamingResultType(endpoint.Operation.ResultType) ? null : cache
            );
            var required = endpoint.Operation.Parameters
                .Where(p => p.Source == WebApiParameterSource.RouteOrDefault && p.IsRequired)
                .ToArray();

            if (required.Length != 0 || plan.Before.Count != 0 || plan.After.Count != 0)
            {
                var invoke = handler;
                var before = plan.Before.ToArray();
                var after = plan.After.ToArray();
                handler = async (context, route) =>
                {
                    var failed = false;

                    try
                    {
                        foreach (var hook in before)
                        {
                            await hook(context).ConfigureAwait(false);

                            if (context.IsHandled) return;
                        }

                        foreach (var parameter in required)
                            if (!ControllerBindingServices.TryGetRouteValue(route, parameter.FieldName, out _))
                                throw HttpException.BadRequest("A required route value is missing.");

                        await invoke(context, route).ConfigureAwait(false);
                    }

                    catch
                    {
                        failed = true;
                        throw;
                    }

                    finally
                    {
                        // Every registered completion hook runs even when an earlier one fails.
                        Exception? failure = null;

                        foreach (var hook in after)
                            try
                            {
                                await hook(context).ConfigureAwait(false);
                            }

                            catch (Exception error) when (Internal.ExceptionPolicy.IsRecoverable(error))
                            {
                                failure ??= error;
                            }

                        if (failure != null && !failed)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                                .Capture(failure)
                                .Throw();
                    }
                };
            }

            return WrapSecurityHeaders(
                WrapAuthorization(
                    WrapBrowserProtection(WrapIdempotency(handler, idempotency), plan.Browser),
                    plan.Authorization
                ),
                security
            );
        }

        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        internal static EndpointHandler DescribeEndpoint(Delegate handler)
        {
            handler = global::EmbedIO.Utilities.Validate.NotNull(nameof(handler), handler);

            if (
                handler.GetInvocationList().Length != 1
                || handler.Method.IsGenericMethodDefinition
                || handler.Method.ReturnType.IsByRef
                || handler.Method.ReturnType == typeof(void)
                    && handler.Method.IsDefined(
                        typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute),
                        false
                    )
            )
                throw new ArgumentException(
                    "Use a single closed handler without by-reference return values.",
                    nameof(handler)
                );

            var method = handler.Method;
            var type = (!method.IsStatic ? handler.Target?.GetType() : null)
                ?? method.DeclaringType
                ?? throw new ArgumentException("Handler has no declaring type.", nameof(handler));
            var bodies = method
                .GetParameters()
                .Select(
                    p => p
                        .GetCustomAttributes<Attribute>()
                        .Any(a => a is JsonDataAttribute)
                        ? 1
                        : p
                            .GetCustomAttributes<Attribute>()
                            .Any(a => a is MultipartFormAttribute)
                            ? 2
                            : p
                                .GetCustomAttributes<Attribute>()
                                .Any(a => a is FormObjectAttribute)
                                ? 3
                                : p
                                    .GetCustomAttributes<Attribute>()
                                    .Any(a => a is FormDataAttribute or FormFieldAttribute)
                                    ? 4
                                    : 0
                )
                .Where(k => k != 0)
                .ToArray();

            if (bodies.Length > 1 && bodies.Any(k => k != 4))
                throw new ArgumentException(
                    "Use a single JSON, multipart or form DTO body binding per endpoint.",
                    nameof(handler)
                );

            if (
                handler.Target is WebApiController
                    || method.IsDefined(typeof(BeforeHandlerAttribute), true)
                    || type.IsDefined(typeof(BeforeHandlerAttribute), true)
            )
                throw new ArgumentException(
                    "Register controller instances through the controller API; configure endpoint hooks through fluent callbacks.",
                    nameof(handler)
                );

            var operation = ReflectionEndpointOperation(method, type);
            return new EndpointHandler(
                (module, matcher) => module.CompileEndpoint(handler, matcher),
                operation,
                ReadAuthorization(type, method),
                ReadBrowserProtection(type, method),
                ReadOutputCache(type, method),
                ReadIdempotency(type, method),
                ReadSecurityHeaders(type, method)
            );
        }

        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private WebApiRouteInvoker<WebApiController> CompileEndpoint(Delegate handler, RouteMatcher matcher)
        {
            var controller = Expression.Parameter(typeof(WebApiController), "controller");
            var context = Expression.Parameter(typeof(IHttpContext), "context");
            var route = Expression.Parameter(typeof(RouteMatch), "route");
            var serializer = Expression.Parameter(typeof(ResponseSerializerCallback), "serializer");
            var arguments = BindHandlerArguments(handler.Method, typeof(WebApiController), matcher, controller, route);
            var parameters = handler.Method.GetParameters();

            for (var i = 0; i < parameters.Length; i++)
            {
                if (
                    parameters[i]
                        .GetCustomAttributes<Attribute>()
                        .Any(
                            a => a is QueryObjectAttribute
                                or FormObjectAttribute
                                or HeaderFieldAttribute
                                or CookieFieldAttribute
                                or RouteFieldAttribute
                                || a
                                    .GetType()
                                    .GetInterfaces()
                                    .Any(
                                        t => t.IsGenericType
                                            && (t.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<>)
                                                || t.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<,>))
                                    )
                        )
                ) continue;

                if (parameters[i].ParameterType == typeof(IHttpContext)) arguments[i] = context;

                if (parameters[i].ParameterType == typeof(IHttpRequest))
                    arguments[i] = Expression.Property(context, nameof(IHttpContext.Request));

                if (parameters[i].ParameterType == typeof(IHttpResponse))
                    arguments[i] = Expression.Property(context, nameof(IHttpContext.Response));

                if (parameters[i].ParameterType == typeof(System.Security.Principal.IPrincipal))
                    arguments[i] = Expression.Property(context, nameof(IHttpContext.User));

                if (parameters[i].ParameterType == typeof(CancellationToken))
                    arguments[i] = Expression.Property(context, nameof(IHttpContext.CancellationToken));

                if (parameters[i].ParameterType == typeof(RouteMatch)) arguments[i] = route;
            }

            Expression call = Expression.Invoke(Expression.Constant(handler), arguments);
            var returnType = handler.Method.ReturnType;

            if (
                returnType == typeof(ValueTask)
                || returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)
            )
            {
                call = Expression.Call(call, nameof(ValueTask.AsTask), null);
                returnType = call.Type;
            }

            var work = WrapHandlerResult(call, returnType, context);
            var result = Expression.Call(typeof(Task), nameof(Task.FromResult), [typeof(Task)], work);
            return Expression
                .Lambda<WebApiRouteInvoker<WebApiController>>(result, controller, context, route, serializer)
                .Compile();
        }
    }
}
