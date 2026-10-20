using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Routing;

namespace EmbedIO.WebApi
{
    public abstract partial class WebApiModuleBase
    {
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static WebApiOperation ReflectionEndpointOperation(MethodInfo method, Type owner)
        {
            var parameters = new List<WebApiOperationParameter>();

            foreach (var parameter in method.GetParameters())
            {
                var name = parameter.Name
                    ?? throw new InvalidOperationException("Endpoint parameters must retain their names.");
                var type = parameter.ParameterType;
                var attributes = parameter
                    .GetCustomAttributes<Attribute>()
                    .ToArray();
                var validation = ReflectionRequestValidation.For(parameter);

                if (attributes.Any(a => a.GetType().FullName == "EmbedIO.DependencyInjection.FromServicesAttribute"))
                    continue;

                if (attributes.Any(a => a is QueryObjectAttribute or FormObjectAttribute))
                {
                    var form = attributes.Any(a => a is FormObjectAttribute);

                    foreach (var property in type
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.SetMethod?.IsPublic == true))
                    {
                        var field = property.GetCustomAttribute<RequestFieldAttribute>();
                        Add(
                            property.Name,
                            field?.Name ?? property.Name,
                            form ? WebApiParameterSource.FormObjectField : WebApiParameterSource.QueryObjectField,
                            property.PropertyType,
                            field?.Required == true,
                            validation?.ForMember(property.Name)
                        );
                    }

                    continue;
                }

                var source = WebApiParameterSource.RouteOrDefault;
                var fieldName = name;
                var isRequired = !parameter.HasDefaultValue;
                string? custom = null;

                if (
                    attributes
                        .OfType<HeaderFieldAttribute>()
                        .FirstOrDefault() is { } header
                )
                {
                    source = WebApiParameterSource.Header;
                    fieldName = header.Name ?? name;
                    isRequired = header.Required;
                }

                else if (
                    attributes
                        .OfType<CookieFieldAttribute>()
                        .FirstOrDefault() is { } cookie
                )
                {
                    source = WebApiParameterSource.Cookie;
                    fieldName = cookie.Name ?? name;
                    isRequired = cookie.Required;
                }

                else if (
                    attributes
                        .OfType<RouteFieldAttribute>()
                        .FirstOrDefault() is { } route
                )
                {
                    fieldName = route.Name ?? name;
                    isRequired = route.Required;
                }

                else if (
                    attributes
                        .OfType<QueryFieldAttribute>()
                        .FirstOrDefault() is { } query
                )
                {
                    source = WebApiParameterSource.Query;
                    fieldName = query.FieldName ?? name;
                    isRequired = query.BadRequestIfMissing;
                }

                else if (
                    attributes
                        .OfType<FormFieldAttribute>()
                        .FirstOrDefault() is { } form
                )
                {
                    source = WebApiParameterSource.Form;
                    fieldName = form.FieldName ?? name;
                    isRequired = form.BadRequestIfMissing;
                }

                else if (attributes.Any(a => a is JsonDataAttribute))
                {
                    source = WebApiParameterSource.JsonBody;
                    isRequired = true;
                }

                else if (attributes.Any(a => a is MultipartFormAttribute))
                {
                    source = WebApiParameterSource.Multipart;
                    isRequired = true;
                }

                else if (attributes.Any(a => a is QueryDataAttribute)) source = WebApiParameterSource.QueryCollection;

                else if (attributes.Any(a => a is FormDataAttribute)) source = WebApiParameterSource.FormCollection;

                else if (
                    type == typeof(IHttpContext)
                    || type == typeof(IHttpRequest)
                    || type == typeof(IHttpResponse)
                    || type == typeof(CancellationToken)
                    || type == typeof(RouteMatch)
                    || type == typeof(System.Security.Principal.IPrincipal)
                ) continue;

                else if (attributes.Any(a => a
                        .GetType()
                        .GetInterfaces()
                        .Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<>) || i.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<,>)))))
                {
                    source = WebApiParameterSource.Custom;
                    custom = "Explicit request-data attribute";
                    isRequired = false;
                }

                Add(name, fieldName, source, type, isRequired, validation, custom);
            }

            var result = method.ReturnType;

            if (
                result.IsGenericType
                && (result.GetGenericTypeDefinition() == typeof(Task<>)
                    || result.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ) result = result.GetGenericArguments()[0];

            var responses = method
                .GetCustomAttributes<OpenApiResponseAttribute>(true)
                .ToList();
            AddResultResponses(result, responses);
            return new WebApiOperation(
                owner.Name,
                method.Name,
                result == typeof(void) || result == typeof(Task) || result == typeof(ValueTask) ? null : result,
                [.. parameters],
                method.GetCustomAttribute<OpenApiOperationAttribute>(true),
                [.. responses],
                method.IsDefined(typeof(OpenApiIgnoreAttribute), true)
                    || owner.IsDefined(typeof(OpenApiIgnoreAttribute), true),
                ReadAuthorization(owner, method)
            );

            void Add(
                string name,
                string fieldName,
                WebApiParameterSource source,
                Type type,
                bool isRequired,
                RequestValidationParameter? validation,
                string? custom = null
            )
            {
                var element = type.IsArray ? type.GetElementType() ?? type : type;
                var scalar = Nullable.GetUnderlyingType(element) ?? element;
                parameters.Add(
                    new WebApiOperationParameter(
                        name,
                        fieldName,
                        source,
                        type,
                        isRequired,
                        scalar.IsEnum ? Enum.GetNames(scalar) : null,
                        custom,
                        validation
                    )
                );
            }
        }

        private static void AddResultResponses(Type result, List<OpenApiResponseAttribute> responses)
        {
            if (result == typeof(NoContentResult))
            {
                Add(204, null);
                return;
            }

            var problem = HttpResultMetadata.FixedProblemStatus(result);

            if (problem != 0)
            {
                Add(problem, result);
                return;
            }

            if (!result.IsGenericType) return;

            var definition = result.GetGenericTypeDefinition();
            var arguments = result.GetGenericArguments();

            if (definition == typeof(HttpResult<,>) || definition == typeof(HttpResult<,,>))
            {
                foreach (var argument in arguments) AddResultResponses(argument, responses);

                return;
            }

            var status = definition == typeof(OkResult<>)
                ? 200
                : definition == typeof(CreatedResult<>)
                    ? 201
                    : definition == typeof(AcceptedResult<>)
                        ? 202
                        : definition == typeof(NotFoundResult<>)
                            ? 404
                            : definition == typeof(BadRequestResult<>)
                                ? 400
                                : definition == typeof(ConflictResult<>) ? 409 : 0;

            if (status != 0) Add(status, arguments[0]);

            void Add(int code, Type? body)
            {
                if (!responses.Any(r => r.StatusCode == code))
                    responses.Add(new OpenApiResponseAttribute(code) { Type = body });
            }
        }
    }
}
