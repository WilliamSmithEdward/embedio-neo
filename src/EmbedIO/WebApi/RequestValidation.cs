using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using EmbedIO.Problems;

namespace EmbedIO.WebApi
{
    /// <summary>Runs explicitly enabled validation using cached, reflection-free property accessors.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class RequestValidation
    {
        /// <summary>Validates a bound value before the action is called; invalid values throw a 400 problem.</summary>
        /// <typeparam name="T">The bound parameter type.</typeparam>
        /// <param name="value">The value to validate.</param>
        /// <param name="name">Parameter name used as the error-path root.</param>
        /// <param name="plan">Cached model metadata.</param>
        /// <param name="rules">Parameter validation rules.</param>
        /// <param name="options">Traversal and error limits.</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>The original value.</returns>
        internal static T Validate<T>(T value, string name, RequestValidationPlan plan, ValidationAttribute[] rules, ValidateAttribute options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            var run = new Run(options, cancellationToken);
            run.Visit(value, name, plan, rules, value, name, 1);
            run.Complete();
            return value;
        }

        private sealed class Run
        {
            private readonly ValidateAttribute _options;
            private readonly CancellationToken _token;
            private readonly List<object> _ancestors = new();
            private JsonObject? _errors;
            private int _nodes;
            private int _count;

            internal Run(ValidateAttribute options, CancellationToken token) { _options = options; _token = token; }

            internal void Visit(object? value, string path, RequestValidationPlan plan, ValidationAttribute[] rules, object? owner, string member, int depth)
            {
                _token.ThrowIfCancellationRequested();
                if (_count >= _options.MaxErrors) return;
                if (++_nodes > _options.MaxNodes || depth > _options.MaxDepth)
                {
                    Error(path, "The validation traversal limit was exceeded.");
                    return;
                }
                Check(value, owner ?? value ?? new object(), member, path, rules);
                if (value == null || _count >= _options.MaxErrors) return;
                // Only the current ancestry is tracked: shared children are validated at each field path.
                for (var index = 0; index < _ancestors.Count; index++)
                    if (ReferenceEquals(_ancestors[index], value)) return;
                _ancestors.Add(value);
                try
                {
                    Check(value, value, string.Empty, path, plan.Rules);
                    if (_count >= _options.MaxErrors) return;
                    if (plan.Element != null && value is IEnumerable sequence)
                    {
                        var index = 0;
                        foreach (var item in sequence)
                        {
                            Visit(item, path + "[" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]", plan.Element(), Array.Empty<ValidationAttribute>(), item, member, depth + 1);
                            if (_nodes > _options.MaxNodes || _count >= _options.MaxErrors) break;
                        }
                    }
                    else
                    {
                        foreach (var property in plan.Members)
                        {
                            if (_nodes >= _options.MaxNodes || _count >= _options.MaxErrors)
                            {
                                if (_count < _options.MaxErrors) Error(path, "The validation traversal limit was exceeded.");
                                break;
                            }
                            Visit(property.Get(value), path + "." + property.Name, property.Plan(), property.Rules, value, property.Name, depth + 1);
                        }
                    }
                    if (value is IValidatableObject custom && _count < _options.MaxErrors)
                    {
                        foreach (var result in custom.Validate(Context(value, string.Empty)) ?? Array.Empty<ValidationResult>())
                        {
                            _token.ThrowIfCancellationRequested();
                            if (++_nodes > _options.MaxNodes) { Error(path, "The validation traversal limit was exceeded."); break; }
                            if (result == ValidationResult.Success) continue;
                            var members = result.MemberNames.DefaultIfEmpty(string.Empty);
                            foreach (var field in members)
                            {
                                _token.ThrowIfCancellationRequested();
                                if (++_nodes > _options.MaxNodes) { Error(path, "The validation traversal limit was exceeded."); break; }
                                Error(field.Length == 0 ? path : path + "." + field, result.ErrorMessage ?? "The value is invalid.");
                                if (_count >= _options.MaxErrors) break;
                            }
                            if (_count >= _options.MaxErrors) break;
                        }
                    }
                }
                finally { _ancestors.RemoveAt(_ancestors.Count - 1); }
            }

            private void Check(object? value, object owner, string member, string path, ValidationAttribute[] rules)
            {
                if (rules.Length == 0) return;
                var context = Context(owner, member);
                foreach (var rule in rules)
                {
                    _token.ThrowIfCancellationRequested();
                    var result = rule.GetValidationResult(value, context);
                    if (result != ValidationResult.Success) Error(path, result!.ErrorMessage ?? "The value is invalid.");
                    if (_count >= _options.MaxErrors) break;
                }
            }

            // DisplayName is supplied explicitly; ValidationContext need not discover attributes through reflection.
#if NET10_0_OR_GREATER
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DisplayName and MemberName are explicitly set; model members are accessed by cached/generated delegates.")]
#endif
            private static ValidationContext Context(object owner, string member)
                => new(owner) { MemberName = member.Length == 0 ? null : member, DisplayName = member.Length == 0 ? owner.GetType().Name : member };

            private void Error(string path, string message)
            {
                if (_count >= _options.MaxErrors) return;
                _errors ??= new JsonObject();
                if (_errors[path] is not JsonArray errors) _errors[path] = errors = new JsonArray();
                errors.Add((JsonNode?)JsonValue.Create(message));
                _count++;
            }

            internal void Complete()
            {
                _token.ThrowIfCancellationRequested();
                if (_count != 0)
                    throw new ProblemDetailsException(new ProblemDetails(400) { Title = "Request validation failed." }.WithExtension("errors", _errors));
            }
        }
    }
}
