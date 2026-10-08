using System;

namespace EmbedIO.Internal
{
    // A transport or synchronization object shared with another owner must not be
    // disposed by a borrower. The wrapper makes that lifetime contract explicit.
    internal readonly struct BorrowedResource<T> where T : class
    {
        internal BorrowedResource(T value) => Value = value;
        internal T Value { get; }
    }

    internal static class ExceptionPolicy
    {
        // Request/plugin boundaries may handle arbitrary application exceptions,
        // but cannot safely recover from process/resource corruption.
        internal static bool IsRecoverable(Exception exception)
            => exception is not OutOfMemoryException
                && exception is not StackOverflowException
                && exception is not AccessViolationException;
    }

    internal static class StringOperations
    {
        internal static int IndexOfOrdinal(string text, char value)
        {
#if NETSTANDARD2_0 || NETFRAMEWORK
            return text.IndexOf(value);
#else
            return text.IndexOf(value, StringComparison.Ordinal);
#endif
        }

        internal static string ReplaceOrdinal(string text, string oldValue, string? newValue)
        {
#if NETSTANDARD2_0 || NETFRAMEWORK
            return text.Replace(oldValue, newValue);
#else
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
#endif
        }
    }
}
