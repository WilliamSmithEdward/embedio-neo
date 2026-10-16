using System;

namespace EmbedIO.Authorization
{
    /// <summary>Overrides inherited controller and method authorization requirements for this action.</summary>
    /// <remarks>Does not bypass authentication modules that independently require credentials.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class AllowAnonymousAttribute : Attribute { }
}
