using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using EmbedIO.Problems;

namespace EmbedIO
{
    partial class HttpResults
    {
        /// <summary>Returns a copied 400 problem. The problem status must be 400.</summary>
        public static BadRequestProblemResult BadRequest(ProblemDetails problem) => new(problem);

        /// <summary>Returns a copied 404 problem. The problem status must be 404.</summary>
        public static NotFoundProblemResult NotFound(ProblemDetails problem) => new(problem);

        /// <summary>Returns a copied 409 problem. The problem status must be 409.</summary>
        public static ConflictProblemResult Conflict(ProblemDetails problem) => new(problem);

        /// <summary>Returns a 400 problem with a copied field-to-messages map.</summary>
        /// <remarks>Requires at least one error. Limits: 1024 fields/messages, 1024 characters per path,
        /// 4096 per message, and 64 KiB UTF-8 text before JSON escaping. Empty paths denote model-level errors.</remarks>
        public static ValidationProblemResult ValidationProblem(
            IReadOnlyDictionary<string, string[]> errors,
            string? detail = null
        ) => new(errors, detail);

        /// <summary>Returns a 400 problem from standard DataAnnotations results. Success values are ignored.</summary>
        /// <remarks>Copies messages for every member name; unnamed errors use an empty path.
        /// Accepts at most 1024 results and applies the same field/message/text limits as the dictionary overload.
        /// Only pass client-safe messages. This does not enable or replace automatic [Validate] behavior.</remarks>
        public static ValidationProblemResult ValidationProblem(
            IEnumerable<ValidationResult?> validationResults,
            string? detail = null
        ) => new(validationResults, detail);
    }

    /// <summary>A copied RFC 9457 problem whose concrete result type fixes its error status.</summary>
    public abstract class ErrorHttpResult : HttpResult
    {
        private readonly ProblemResultBody _body;

        internal ErrorHttpResult(int status, ProblemDetails problem)
            : base(ValidateProblem(status, problem), ProblemDetailsHandler.MediaType) => _body = new(problem);

        /// <summary>Gets an independent problem snapshot. Mutating it cannot change future executions.</summary>
        public ProblemDetails Problem => _body.Problem;

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer) =>
            _body.WriteAsync(context);

        private static int ValidateProblem(int status, ProblemDetails problem)
        {
            problem = Utilities.Validate.NotNull(nameof(problem), problem);

            if (problem.Status != status)
                throw new ArgumentException("The problem status must match the result status.", nameof(problem));

            return status;
        }
    }

    /// <summary>A 400 problem response.</summary>
    public sealed class BadRequestProblemResult : ErrorHttpResult
    {
        internal BadRequestProblemResult(ProblemDetails problem) : base(400, problem) { }
    }

    /// <summary>A 404 problem response.</summary>
    public sealed class NotFoundProblemResult : ErrorHttpResult
    {
        internal NotFoundProblemResult(ProblemDetails problem) : base(404, problem) { }
    }

    /// <summary>A 409 problem response.</summary>
    public sealed class ConflictProblemResult : ErrorHttpResult
    {
        internal ConflictProblemResult(ProblemDetails problem) : base(409, problem) { }
    }

    /// <summary>A 400 problem with bounded, immutable field validation messages.</summary>
    public sealed class ValidationProblemResult : ErrorHttpResult
    {
        internal ValidationProblemResult(IReadOnlyDictionary<string, string[]> errors, string? detail)
            : this(ValidationProblemData.FromDictionary(errors), detail)
        { }

        internal ValidationProblemResult(IEnumerable<ValidationResult?> validationResults, string? detail)
            : this(ValidationProblemData.FromAnnotations(validationResults), detail)
        { }

        private ValidationProblemResult(ValidationProblemData data, string? detail)
            : base(400, data.CreateProblem(detail)) => Errors = data.Errors;

        /// <summary>Gets the immutable snapshot. An empty path identifies a model-level error.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }
    }

    // Shared with the existing dynamic-status result so all problem responses retain its wire behavior.
    internal readonly struct ProblemResultBody
    {
        private readonly ProblemDetails _problem;

        internal ProblemResultBody(ProblemDetails problem) => _problem = problem.Copy();

        internal ProblemDetails Problem => _problem.Copy();

        internal Task WriteAsync(IHttpContext context) => context.SendProblemAsync(_problem);
    }
}
