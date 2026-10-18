using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace EmbedIO.WebApi
{
    partial class WebApiModuleBase
    {
        private static Type? ExplicitHttpResultType(Type? type)
        {
            if (type == null) return null;

            if (typeof(HttpResult).IsAssignableFrom(type)) return type;

            if (
                type.IsGenericType
                && (type.GetGenericTypeDefinition() == typeof(Task<>)
                    || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            )
            {
                var item = type.GetGenericArguments()[0];

                if (typeof(HttpResult).IsAssignableFrom(item)) return item;
            }

            return null;
        }

        private static bool IsAwaitedHttpResultType(Type? type) =>
            IsStreamingResultType(type) || ExplicitHttpResultType(type) != null;

        [RequiresDynamicCode(ReflectionMessage)]
        [RequiresUnreferencedCode(ReflectionMessage)]
        private MethodCallExpression? WrapHttpResult(Expression call, Type type, ParameterExpression context)
        {
            var item = ExplicitHttpResultType(type);

            if (item == null) return null;

            var method = type == item
                ? nameof(ControllerBindingServices.ExecuteHttpResultAsync)
                : type.GetGenericTypeDefinition() == typeof(Task<>)
                    ? nameof(ControllerBindingServices.AwaitHttpResultAsync)
                    : nameof(ControllerBindingServices.AwaitValueHttpResultAsync);
            return Expression.Call(
                typeof(ControllerBindingServices),
                method,
                type == item ? null : [item],
                Expression.Constant(Serializer),
                context,
                type == item ? Expression.Convert(call, typeof(HttpResult)) : call
            );
        }
    }
}
