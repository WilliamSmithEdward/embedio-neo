using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    // Reflection access to the library's internal types. The test project has no
    // InternalsVisibleTo grant; a missing type or member fails the calling test.
    // A member's own exception reaches the caller unwrapped.
    internal static class EngineInternals
    {
        internal const BindingFlags Members = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic;
        private const BindingFlags Constructors = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DoNotWrapExceptions;
        private static readonly Assembly Library = typeof(WebServer).Assembly;

        // The named internal type, or null when this build of the library lacks it.
        internal static Type? Find(string name) => Library.GetType(name);

        internal static Type Type(string name) => Find(name) ?? throw new AssertionException("Missing type: " + name);

        internal static object Create(Type type, params object?[] arguments) =>
            Activator.CreateInstance(type, Constructors, null, arguments, null)
                ?? throw new AssertionException("Could not create " + type.Name + ".");

        internal static object Create(string type, params object?[] arguments) => Create(Type(type), arguments);

        internal static object? Call(object target, string name, params object?[] arguments) =>
            Invoke(target.GetType(), target, name, arguments);

        internal static object? CallStatic(Type type, string name, params object?[] arguments) =>
            Invoke(type, null, name, arguments);

        internal static object? Property(object target, string name) =>
            (target
                .GetType()
                .GetProperty(name, Members)
                ?? throw new AssertionException("Missing property: " + name)).GetValue(
                target,
                BindingFlags.DoNotWrapExceptions,
                null,
                null,
                null
            );

        internal static object? Field(object target, string name) =>
            (target
                .GetType()
                .GetField(name, Members)
                ?? throw new AssertionException("Missing field: " + name)).GetValue(target);

        internal static void SetField(object target, string name, object? value) =>
            (target
                .GetType()
                .GetField(name, Members)
                ?? throw new AssertionException("Missing field: " + name)).SetValue(target, value);

        // Awaits a Task, ValueTask or ValueTask<T> returned through reflection and returns its result.
        internal static async Task<object?> AwaitAsync(object? pending)
        {
            var value = pending ?? throw new AssertionException("Missing pending operation.");
            var task = value as Task
                ?? value
                    .GetType()
                    .GetMethod("AsTask")?.Invoke(value, null) as Task
                ?? throw new AssertionException("Not an awaitable result: " + value.GetType().Name);
            await task.ConfigureAwait(false);
            return task
                .GetType()
                .GetProperty("Result")
                ?.GetValue(task);
        }

        private static object? Invoke(Type type, object? target, string name, object?[] arguments) =>
            (type.GetMethod(name, Members) ?? throw new AssertionException("Missing method: " + name)).Invoke(
                target,
                BindingFlags.DoNotWrapExceptions,
                null,
                arguments,
                null
            );
    }
}
