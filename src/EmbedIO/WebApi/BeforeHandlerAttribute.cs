using System;

namespace EmbedIO.WebApi
{
    /// <summary>Chooses how an attributed route uses controller preprocessing.</summary>
    public enum BeforeHandlerBehavior
    {
        /// <summary>Runs the controller hook, then the named hook.</summary>
        Append = 0,
        /// <summary>Runs only the named hook.</summary>
        Replace = 1,
        /// <summary>Skips controller preprocessing and any class attribute hook.</summary>
        Skip = 2,
    }

    /// <summary>Configures preprocessing before a controller action's parameters are bound.</summary>
    /// <remarks>A method attribute replaces the class attribute, including inherited defaults.
    /// Authentication, authorization and upstream middleware are unaffected. Named hooks must be
    /// unambiguous public instance methods returning void or Task, with no parameters or one
    /// CancellationToken parameter. Async void and generic hooks are not supported.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class BeforeHandlerAttribute : Attribute
    {
        /// <summary>Creates a declaration without a named hook, for Skip behavior.</summary>
        public BeforeHandlerAttribute() { }

        /// <summary>Creates a declaration for a named controller method.</summary>
        /// <param name="methodName">The hook's name; use nameof to retain rename checking.</param>
        public BeforeHandlerAttribute(string? methodName) => MethodName = methodName;

        /// <summary>Gets the named hook.</summary>
        public string? MethodName { get; }

        /// <summary>Gets or sets how the hook replaces or appends to controller preprocessing.</summary>
        public BeforeHandlerBehavior Behavior { get; set; }
    }
}
