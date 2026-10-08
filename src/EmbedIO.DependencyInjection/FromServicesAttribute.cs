using System;
using System.Threading.Tasks;
using EmbedIO.WebApi;
using Microsoft.Extensions.DependencyInjection;

namespace EmbedIO.DependencyInjection
{
    /// <summary>Explicitly resolves a handler argument from the current request's services.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class FromServicesAttribute : Attribute, IRequestDataAttribute<WebApiController>
    {
        /// <inheritdoc />
        public Task<object?> GetRequestDataAsync(WebApiController controller, Type type, string parameterName)
        {
            if (controller is null) throw new System.NullReferenceException();
            return Task.FromResult<object?>(controller.HttpContext.GetRequestServices().GetRequiredService(type));
        }
    }
}
