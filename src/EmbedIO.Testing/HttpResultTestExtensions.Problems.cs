using System;
using System.Collections.Generic;
using EmbedIO.Problems;

namespace EmbedIO.Testing
{
    partial class HttpResultTestExtensions
    {
        /// <summary>Gets an independent Problem Details snapshot from the selected error result.</summary>
        public static ProblemDetails GetProblem(this HttpResult result)
        {
            result = global::EmbedIO.Utilities.Validate.NotNull(nameof(result), result);
            return result.SelectedResult switch
            {
                ErrorHttpResult error => error.Problem,
                ProblemHttpResult problem => problem.Problem,
                _ => throw new InvalidOperationException("The selected response is not a problem result.")
            };
        }

        /// <summary>Gets immutable fields from a selected ValidationProblemResult, including inside alternatives.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetValidationErrors(this HttpResult result)
        {
            result = global::EmbedIO.Utilities.Validate.NotNull(nameof(result), result);
            return result.SelectedResult is ValidationProblemResult validation
                ? validation.Errors
                : throw new InvalidOperationException("The selected response is not a validation problem result.");
        }
    }
}
