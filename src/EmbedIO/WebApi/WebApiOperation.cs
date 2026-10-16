using System;
using System.Collections.Generic;
using System.ComponentModel;
using EmbedIO.OpenApi;
using EmbedIO.Authorization;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// Describes a controller method as the EmbedIO-Neo source generator found it: how each
    /// parameter is bound, what the method returns, and the OpenAPI attributes applied to it.
    /// <see cref="OpenApiDocumentBuilder"/> reads it to describe the method's routes.
    /// </summary>
    public sealed class WebApiOperation
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiOperation"/> class.
        /// This constructor supports generated code and is not intended to be called directly.
        /// </summary>
        /// <param name="controllerName">The controller type's name.</param>
        /// <param name="methodName">The method's name.</param>
        /// <param name="resultType">The type of the serialized result, or <see langword="null"/>
        /// if the method returns <see langword="void"/> or <see cref="System.Threading.Tasks.Task"/>.</param>
        /// <param name="parameters">The method's parameters, in declaration order.</param>
        /// <param name="operation">The method's <see cref="OpenApiOperationAttribute"/>, if any.</param>
        /// <param name="responses">The method's <see cref="OpenApiResponseAttribute"/>s.</param>
        /// <param name="ignored"><see langword="true"/> if the method or its controller carries <see cref="OpenApiIgnoreAttribute"/>.</param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public WebApiOperation(
            string controllerName,
            string methodName,
            Type? resultType,
            WebApiOperationParameter[] parameters,
            OpenApiOperationAttribute? operation,
            OpenApiResponseAttribute[] responses,
            bool ignored)
            : this(controllerName, methodName, resultType, parameters, operation, responses, ignored, null)
        {
        }

        /// <summary>Creates generated operation metadata including authorization requirements.</summary>
        /// <param name="controllerName">The controller name.</param>
        /// <param name="methodName">The method name.</param>
        /// <param name="resultType">The serialized result type.</param>
        /// <param name="parameters">Parameter metadata.</param>
        /// <param name="operation">Optional operation attribute.</param>
        /// <param name="responses">Response attributes.</param>
        /// <param name="ignored">Whether OpenAPI ignores the operation.</param>
        /// <param name="authorization">Optional authorization metadata.</param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public WebApiOperation(string controllerName, string methodName, Type? resultType, WebApiOperationParameter[] parameters,
            OpenApiOperationAttribute? operation, OpenApiResponseAttribute[] responses, bool ignored, AuthorizationMetadata? authorization)
        {
            ControllerName = Validate.NotNull(nameof(controllerName), controllerName);
            MethodName = Validate.NotNull(nameof(methodName), methodName);
            ResultType = resultType;
            Parameters = Validate.NotNull(nameof(parameters), parameters);
            Operation = operation;
            Responses = Validate.NotNull(nameof(responses), responses);
            Ignored = ignored;
            Authorization = authorization;
        }

        /// <summary>Gets effective controller and method authorization metadata.</summary>
        public AuthorizationMetadata? Authorization { get; }

        /// <summary>Gets the controller type's name.</summary>
        public string ControllerName { get; }

        /// <summary>Gets the method's name.</summary>
        public string MethodName { get; }

        /// <summary>Gets the type of the serialized result, or <see langword="null"/> if the method returns no value.</summary>
        public Type? ResultType { get; }

        /// <summary>Gets the method's parameters, in declaration order.</summary>
        public IReadOnlyList<WebApiOperationParameter> Parameters { get; }

        /// <summary>Gets the method's <see cref="OpenApiOperationAttribute"/>, if any.</summary>
        public OpenApiOperationAttribute? Operation { get; }

        /// <summary>Gets the method's <see cref="OpenApiResponseAttribute"/>s.</summary>
        public IReadOnlyList<OpenApiResponseAttribute> Responses { get; }

        /// <summary>Gets a value indicating whether the method is excluded from OpenAPI documents.</summary>
        public bool Ignored { get; }
    }

    /// <summary>Describes how one controller method parameter is bound.</summary>
    public sealed class WebApiOperationParameter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiOperationParameter"/> class.
        /// This constructor supports generated code and is not intended to be called directly.
        /// </summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="fieldName">The name of the query or form field, or of the route parameter.</param>
        /// <param name="source">Where the value comes from.</param>
        /// <param name="type">The parameter's type.</param>
        /// <param name="isRequired"><see langword="true"/> if a request without the value is rejected.</param>
        /// <param name="enumNames">The names of the enumeration the value converts to, if any.</param>
        /// <param name="customAttribute">The request-data attribute's type name, for <see cref="WebApiParameterSource.Custom"/>.</param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public WebApiOperationParameter(
            string name,
            string fieldName,
            WebApiParameterSource source,
            Type type,
            bool isRequired,
            string[]? enumNames,
            string? customAttribute)
        {
            Name = Validate.NotNull(nameof(name), name);
            FieldName = Validate.NotNull(nameof(fieldName), fieldName);
            Source = source;
            Type = Validate.NotNull(nameof(type), type);
            IsRequired = isRequired;
            EnumNames = enumNames ?? Array.Empty<string>();
            CustomAttribute = customAttribute;
        }

        /// <summary>Gets the parameter's name.</summary>
        public string Name { get; }

        /// <summary>Gets the name of the query or form field, or of the route parameter.</summary>
        public string FieldName { get; }

        /// <summary>Gets where the value comes from.</summary>
        public WebApiParameterSource Source { get; }

        /// <summary>Gets the parameter's type.</summary>
        public Type Type { get; }

        /// <summary>Gets a value indicating whether a request without the value is rejected.</summary>
        public bool IsRequired { get; }

        /// <summary>Gets the names of the enumeration the value converts to, or an empty list.</summary>
        public IReadOnlyList<string> EnumNames { get; }

        /// <summary>Gets the request-data attribute's type name, for <see cref="WebApiParameterSource.Custom"/>.</summary>
        public string? CustomAttribute { get; }
    }

    /// <summary>Where a controller method parameter's value comes from.</summary>
    public enum WebApiParameterSource
    {
        /// <summary>A route parameter with the same name, or the parameter's default value.</summary>
        RouteOrDefault,

        /// <summary>A query field (<see cref="QueryFieldAttribute"/>).</summary>
        Query,

        /// <summary>The whole query (<see cref="QueryDataAttribute"/>).</summary>
        QueryCollection,

        /// <summary>A form field (<see cref="FormFieldAttribute"/>).</summary>
        Form,

        /// <summary>The whole form (<see cref="FormDataAttribute"/>).</summary>
        FormCollection,

        /// <summary>The request body as JSON (<see cref="JsonDataAttribute"/>).</summary>
        JsonBody,

        /// <summary>Another request-data attribute, which an OpenAPI document cannot describe.</summary>
        Custom,

        /// <summary>A streaming multipart form (<see cref="MultipartFormAttribute"/>).</summary>
        Multipart,
    }
}
