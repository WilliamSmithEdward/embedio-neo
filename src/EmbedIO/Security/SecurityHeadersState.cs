using System;
using System.Security.Cryptography;
using EmbedIO.Internal;
using EmbedIO.Net.Internal;

namespace EmbedIO.Security
{
    internal sealed class SecurityHeadersState
    {
        private static readonly object Key = new();
        private static readonly object RecordingKey = new();
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0330:Use 'System.Threading.Lock'",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock."
        )]
        private readonly object _gate = new();
        private readonly IHttpContext _context;
        private SecurityHeadersPolicy _policy;
        private string? _nonce;
        private bool _exposed;
        private bool _committed;

        private SecurityHeadersState(IHttpContext context, SecurityHeadersPolicy policy)
        {
            _context = context;
            _policy = policy;
        }

        private bool HasCommitted =>
            _context.Response is HttpListenerResponse or MultiplexedResponse or IHttpBackendResponse
                ? ResponseCommitment.IsCommitted(_context)
                : _committed;
        internal bool PreventStorage => _policy.UsesNonce || _nonce != null;

        internal static SecurityHeadersState? Find(IHttpContext context) =>
            context is ILazyItems lazy && !lazy.HasItems ? null : context.GetItem<SecurityHeadersState>(Key);

        internal static void Select(IHttpContext context, SecurityHeadersPolicy policy)
        {
            context = global::EmbedIO.Utilities.Validate.NotNull(nameof(context), context);

            if (IsRecording(context) && policy.UsesNonce)
                throw new InvalidOperationException("Nonce responses cannot be recorded for idempotency.");

            while (context is Caching.OutputCacheCapture capture) context = capture.Inner;

            while (context is Idempotency.IdempotencyContext staged) context = staged.Inner;

            if (ResponseCommitment.IsCommitted(context))
                throw new InvalidOperationException("Response headers were already sent.");

            var items = context.Items;

            lock (items)
            {
                var state = Find(context);

                if (state == null)
                {
                    state = new SecurityHeadersState(context, policy);
                    items.Add(Key, state);

                    if (context.Response is HttpListenerResponse http1) http1.SecurityHeaders = state;

                    if (context.Response is MultiplexedResponse multiplexed) multiplexed.SecurityHeaders = state;

                    if (context.Response is IHttpBackendResponse backend) backend.SecurityHeaders = state;
                }

                lock (state._gate)
                {
                    if (state.HasCommitted || state._exposed && !ReferenceEquals(state._policy, policy))
                        throw new InvalidOperationException(
                            "The response policy cannot change after nonce exposure or commitment."
                        );

                    state._policy = policy;
                    state.Render(false);
                }
            }
        }

        internal static bool IsRecording(IHttpContext context) =>
            context is Idempotency.IdempotencyContext
                || (context is not ILazyItems lazy || lazy.HasItems) && context.Items.ContainsKey(RecordingKey);

        internal static IDisposable RecordOperation(IHttpContext context)
        {
            var items = context.Items;

            lock (items)
                items[RecordingKey] = items.TryGetValue(RecordingKey, out var previous) && previous is int count
                    ? count + 1
                    : 1;

            return new RecordingScope(items);
        }

        private sealed class RecordingScope : IDisposable
        {
            private readonly System.Collections.Generic.IDictionary<object, object> _items;

            internal RecordingScope(System.Collections.Generic.IDictionary<object, object> items)
            {
                items = Utilities.Validate.NotNull(nameof(items), items);
                _items = items;
            }

            private bool _disposed;

            public void Dispose()
            {
                lock (_items)
                {
                    if (_disposed) return;

                    _disposed = true;

                    if (_items[RecordingKey] is int count && count > 1) _items[RecordingKey] = count - 1;

                    else _items.Remove(RecordingKey);
                }
            }
        }

        internal string Nonce()
        {
            lock (_gate)
            {
                if (!_policy.UsesNonce)
                    throw new InvalidOperationException("The selected policy has no nonce template.");

                CreateNonce();
                _exposed = true;

                if (!HasCommitted) Render(true);

                return _nonce ?? throw new InvalidOperationException("Nonce creation failed.");
            }
        }

        private void CreateNonce()
        {
            if (_nonce != null) return;

            var bytes = new byte[SecurityHeadersPolicy.NonceBytes];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            _nonce = Convert.ToBase64String(bytes);
        }

        internal void ValidateStatus(int status)
        {
            if (PreventStorage && status is 206 or 304)
                throw new InvalidOperationException(
                    "Nonce responses cannot use partial or conditional cached representations."
                );
        }

        internal void Commit()
        {
            lock (_gate)
            {
                if (HasCommitted) return;

                ValidateStatus(_context.Response.StatusCode);

                if (PreventStorage && _context.Response.Headers["Content-Range"] != null)
                    throw new InvalidOperationException("Nonce responses cannot include Content-Range.");

                Render(true);
                _committed = true;
            }
        }

        private void Render(bool final)
        {
            var headers = _context.Response.Headers;

            foreach (var name in SecurityHeadersPolicy.ManagedHeaders) headers.Remove(name);

            if (final && _policy.UsesNonce) CreateNonce();

            foreach (var field in _policy.Fields) headers[field.Key] = field.Value;

            if (_nonce != null)
                foreach (var field in _policy.NonceFields) headers[field.Key] = string.Join(_nonce, field.Value);

            if (_policy.Hsts != null && _context.Request.IsSecureConnection)
                headers["Strict-Transport-Security"] = _policy.Hsts;

            if (_nonce != null)
            {
                headers["Cache-Control"] = "no-store";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "Thu, 01 Jan 1970 00:00:00 GMT";
                headers.Remove("ETag");
                headers.Remove("Last-Modified");
            }
        }
    }
}
