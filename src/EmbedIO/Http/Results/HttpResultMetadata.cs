using System;
using System.Linq;

namespace EmbedIO
{
    internal static class HttpResultMetadata
    {
        internal static bool HasDynamicStatus(Type type)
        {
            if (type == typeof(NoContentResult)) return false;

            if (!type.IsGenericType) return true;

            var definition = type.GetGenericTypeDefinition();

            if (definition == typeof(HttpResult<,>) || definition == typeof(HttpResult<,,>))
                return type
                    .GetGenericArguments()
                    .Any(HasDynamicStatus);

            return definition != typeof(OkResult<>)
                && definition != typeof(JsonHttpResult<>)
                && definition != typeof(CreatedResult<>)
                && definition != typeof(AcceptedResult<>)
                && definition != typeof(NotFoundResult<>)
                && definition != typeof(BadRequestResult<>)
                && definition != typeof(ConflictResult<>);
        }
    }
}
