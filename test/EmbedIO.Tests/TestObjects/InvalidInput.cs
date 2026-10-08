using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    // Deliberately invoke malformed calls against BCL signatures that reject null.
    // Reflection keeps the invalid value explicit without overriding nullable analysis.
    internal static class InvalidInput
    {
        internal static object? Invoke(Delegate method, params object?[] arguments)
        {
            try { return method.DynamicInvoke(arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal static T Invoke<T>(Delegate method, params object?[] arguments)
            => (T)(Invoke(method, arguments) ?? throw new AssertionException("The malformed call unexpectedly returned null."));
    }
}
