using System;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// <para>Asks the EmbedIO-Neo source generator to produce a <see cref="WebApiControllerBinding{TController}"/>
    /// for a controller type, as a static property of the partial class carrying this attribute.</para>
    /// <para>The property has the controller type's name. Register it with
    /// <see cref="WebApiModuleExtensions.WithController{TController}(WebApiModule, WebApiControllerBinding{TController})"/>
    /// instead of the reflection-based <c>WithController&lt;TController&gt;()</c>.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// [GeneratedControllerBinding(typeof(PeopleController))]
    /// internal static partial class ApiControllers
    /// {
    /// }
    ///
    /// server.WithWebApi("/api", m => m.WithController(ApiControllers.PeopleController));
    /// </code>
    /// </example>
    /// <remarks>
    /// Initializes a new instance of the <see cref="GeneratedControllerBindingAttribute"/> class.
    /// </remarks>
    /// <param name="controllerType">The controller type, a non-generic subclass of <see cref="WebApiController"/>.</param>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class GeneratedControllerBindingAttribute(Type controllerType) : Attribute
    {
        /// <summary>
        /// Gets the controller type.
        /// </summary>
        public Type ControllerType { get; } = controllerType;

        /// <summary>
        /// <para>Gets or sets a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> type
        /// that provides metadata for parameters marked with <see cref="JsonDataAttribute"/>.
        /// The generated code reads the context's <c>Default</c> instance.</para>
        /// <para>If this property is <see langword="null"/> (the default), those parameters are
        /// deserialized with EmbedIO's default JSON options, as the reflection-based registration does.
        /// Default options need reflection, so trimmed and Native AOT applications must set this property.</para>
        /// </summary>
        public Type? JsonSerializerContext { get; set; }
    }
}
