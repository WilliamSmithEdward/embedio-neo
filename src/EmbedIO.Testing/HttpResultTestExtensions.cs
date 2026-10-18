using System;

namespace EmbedIO.Testing
{
    /// <summary>Inspects explicit controller results without executing an HTTP response or requiring a test framework.</summary>
    public static class HttpResultTestExtensions
    {
        /// <summary>Gets the selected typed payload, throwing when the selected result is empty or has another payload type.</summary>
        public static T? GetPayload<T>(this HttpResult result)
        {
            result = global::EmbedIO.Utilities.Validate.NotNull(nameof(result), result);

            return result.SelectedResult is HttpResult<T> typed && typed.HasValue
                ? typed.Value
                : throw new InvalidOperationException("The selected response does not have that payload type.");
        }
    }
}
