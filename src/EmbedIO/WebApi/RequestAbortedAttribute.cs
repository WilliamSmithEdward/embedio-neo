using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.WebApi
{
    /// <summary>Binds an opt-in request-abort token to a controller's CancellationToken parameter.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class RequestAbortedAttribute : Attribute, IRequestDataAttribute<WebApiController>
    {
        /// <summary>Gets or sets whether HTTP/1 input closure, including a valid half-close, cancels the token.</summary>
        public bool CancelOnHttp1InputEnd { get; set; }

        /// <inheritdoc />
        public Task<object?> GetRequestDataAsync(WebApiController controller, Type type, string parameterName)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));

            if (type != typeof(CancellationToken))
                throw new InvalidOperationException("RequestAborted requires a CancellationToken parameter.");

            return Task.FromResult<object?>(controller.HttpContext.GetRequestAbortedToken(CancelOnHttp1InputEnd));
        }
    }
}
