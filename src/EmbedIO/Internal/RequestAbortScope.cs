using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EmbedIO.Internal
{
    internal interface IRequestAbortState
    {
        bool IsRequestAborted { get; }
    }

    // Allocated only on explicit acquisition. The weak key never extends a context/exchange lifetime.
    internal static class RequestAbortRegistry
    {
        private static ConditionalWeakTable<object, RequestAbortScope>? _scopes;
        private static ConditionalWeakTable<object, RequestAbortScope> Scopes()
        {
            var scopes = Volatile.Read(ref _scopes);
            if (scopes == null)
            {
                var created = new ConditionalWeakTable<object, RequestAbortScope>();
                scopes = Interlocked.CompareExchange(ref _scopes, created, null) ?? created;
            }
            return scopes;
        }
        internal static RequestAbortScope Get(object key) => Scopes().GetValue(key, static _ => new RequestAbortScope());
        internal static void Attach(object key, RequestAbortScope scope) => Scopes().GetValue(key, _ => scope);
        internal static RequestAbortScope? Find(object key)
            => Volatile.Read(ref _scopes)?.TryGetValue(key, out var scope) == true ? scope : null;
        internal static void Complete(object key) => Find(key)?.Complete();
    }

    internal sealed class RequestAbortScope
    {
        private readonly object _sync = new();
        private RequestAbortToken? _normal;
        private RequestAbortToken? _inputEnd;
        private bool _completed;
        private bool _inputEnded;
        internal bool IsAborted { get { lock (_sync) return _normal?.IsAborted == true || _inputEnd?.IsAborted == true; } }
        internal bool TryGet(bool inputEnd, out CancellationToken token)
        {
            lock (_sync)
            {
                var existing = inputEnd ? _inputEnd : _normal;
                token = existing?.Token ?? default;
                return existing != null;
            }
        }
        internal CancellationToken Get(bool inputEnd, CancellationToken parent)
        {
            lock (_sync)
            {
                var existing = inputEnd ? _inputEnd : _normal;
                if (existing != null) return existing.Token;
                if (_completed) throw new InvalidOperationException("The HTTP request has already completed or handed off its protocol.");
                var created = new RequestAbortToken(parent, static error => EmbedIO.Diagnostics.LogExtensions.Log(error, "Request abort", "A request-abort cancellation callback failed."));
                if (inputEnd) { _inputEnd = created; if (_inputEnded) created.Abort(); }
                else _normal = created;
                return created.Token;
            }
        }
        internal void InputEnded()
        {
            lock (_sync) { _inputEnded = true; _inputEnd?.Abort(); }
        }
        internal void Abort()
        {
            lock (_sync) { if (_completed) return; _normal?.Abort(); _inputEnd?.Abort(); }
        }
        internal void Complete()
        {
            lock (_sync) { if (_completed) return; _completed = true; _normal?.Complete(); _inputEnd?.Complete(); }
        }
    }

}
