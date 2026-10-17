using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO
{
    public static partial class HttpContextExtensions
    {
        private static readonly object CompletionKey = new();

        /// <summary>
        /// Registers asynchronous cleanup after request handling and response flushing,
        /// before the context is closed. Callbacks run in reverse registration order.
        /// Failures are logged and do not prevent subsequent cleanup.
        /// </summary>
        /// <param name="this">The request context.</param>
        /// <param name="callback">The cleanup callback.</param>
        public static void OnRequestCompleted(this IHttpContext @this, Func<Task> callback)
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!@this.Items.TryGetValue(CompletionKey, out var value))
            {
                value = new Stack<Func<Task>>();
                @this.Items.Add(CompletionKey, value);
            }

            if (value is not Stack<Func<Task>> callbacks)
                throw new InvalidOperationException("Request cleanup has already started.");

            callbacks.Push(callback);
        }

        internal static async Task CompleteRequestAsync(IHttpContext context)
        {
            // Reading Items creates the dictionary; a context that never used it has no callbacks.
            if (context is ILazyItems { HasItems: false }) return;
            if (!context.Items.TryGetValue(CompletionKey, out var value)
                || value is not Stack<Func<Task>> callbacks)
                return;

            // Keep a sentinel so cleanup cannot be registered or executed twice.
            context.Items[CompletionKey] = CompletionKey;
            while (callbacks.Count > 0)
            {
                try
                {
                    await callbacks.Pop()().ConfigureAwait(false);
                }
                catch (Exception exception) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                {
                    exception.Log("HTTP context", "Exception thrown by a request completion callback.");
                }
            }
        }
    }

    // Implemented by contexts that create their Items dictionary on first use.
    internal interface ILazyItems
    {
        bool HasItems { get; }
    }
}
