using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;

namespace EmbedIO.Idempotency
{
    /// <summary>Coordinates explicitly opted-in finite writes and records results before delivery.</summary>
    public sealed class IdempotencyCoordinator
    {
        private readonly IIdempotencyStore _store;
        private readonly int _maxConcurrentRequests;
        private int _active;
        /// <summary>Creates a coordinator. Share its store across every owner serving the same key space.</summary>
        /// <param name="store">Atomic persistence, or a bounded process-local store by default.</param>
        /// <param name="maxConcurrentRequests">Maximum simultaneous participants, including bounded-body readers and waiters.</param>
        public IdempotencyCoordinator(IIdempotencyStore? store = null, int maxConcurrentRequests = 32)
        {
            if (maxConcurrentRequests < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
            _store = store ?? new MemoryIdempotencyStore(); _maxConcurrentRequests = maxConcurrentRequests;
        }
        /// <summary>Wraps a fluent finite handler. Authentication, authorization and browser protection must run first.</summary>
        /// <param name="policy">Immutable policy.</param>
        /// <param name="handler">Handler that awaits all work and writes a finite response.</param>
        /// <returns>An opted-in route callback.</returns>
        public RouteHandlerCallback Protect(IdempotencyPolicy policy, RouteHandlerCallback handler)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return (context, route) => ExecuteAsync(context, policy, staged => handler(staged, route));
        }
        /// <summary>Runs or replays a finite write. Never retain its staged context or launch unawaited work.</summary>
        /// <param name="context">Current request after all admission/security guards.</param>
        /// <param name="policy">Explicit policy and trusted caller scope.</param>
        /// <param name="handler">Complete finite operation, serialization and cleanup.</param>
        /// <returns>The result-delivery task.</returns>
        public async Task ExecuteAsync(IHttpContext context, IdempotencyPolicy policy, Func<IHttpContext, Task> handler)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var token = context.CancellationToken; token.ThrowIfCancellationRequested();
            if (!WriteVerb(context.Request.HttpVerb) || context.Request.IsWebSocketRequest || Internal.ResponseCommitment.IsCommitted(context))
                throw HttpException.BadRequest("Idempotency requires an uncommitted finite write request.");
            var keys = context.Request.Headers.GetValues("Idempotency-Key");
            if (keys == null || keys.Length != 1 || keys[0].Length < 1 || keys[0].Length > 128 || keys[0].Any(c => c < 33 || c > 126 || c == ','))
                throw HttpException.BadRequest("One visible ASCII Idempotency-Key of at most 128 characters is required.");
            var scope = policy.GetCallerScope(context);
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > 4096) throw new InvalidOperationException("A bounded trusted caller scope is required.");
            if (context.Request.RawTarget.Length > 8192) throw HttpException.BadRequest("Idempotency target is too long.");
            if (!VaryCovered(context.Response, policy)) throw HttpException.BadRequest("Idempotency policy does not cover response Vary.");
            var key = Hash(Fields(policy.Partition, scope, context.Request.Url.GetLeftPart(UriPartial.Authority), context.Request.RawTarget, context.Request.HttpMethod, keys[0]));
            var headerValues = new StringBuilder();
            foreach (var header in policy.Headers)
            {
                var values = context.Request.Headers.GetValues(header) ?? Array.Empty<string>();
                headerValues.Append(Fields(header.ToUpperInvariant(), values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                foreach (var value in values) { if (value.Length > 8192) throw HttpException.BadRequest("Idempotency vary value is too long."); headerValues.Append(Fields(value)); }
                if (headerValues.Length > 16384) throw HttpException.BadRequest("Idempotency vary metadata is too large.");
            }
            if (Interlocked.Increment(ref _active) > _maxConcurrentRequests)
            { Interlocked.Decrement(ref _active); throw new HttpException(503, "Idempotency participant capacity is exhausted."); }
            try
            {
                var body = await ReadBodyAsync(context, policy.MaxRequestBytes).ConfigureAwait(false);
                var fingerprint = Hash(Fields(headerValues.ToString(), Convert.ToBase64String(HashBytes(body))));
                var started = Stopwatch.GetTimestamp();
                var waited = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var record = await _store.AcquireAsync(key, fingerprint, policy.Retention, token).ConfigureAwait(false);
                    switch (record.State)
                    {
                        case IdempotencyState.Completed:
                            await (record.Response ?? throw new InvalidOperationException("Store omitted the completed response.")).ReplayAsync(context).ConfigureAwait(false); return;
                        case IdempotencyState.Conflict: throw new HttpException(409, "Idempotency key has a different request fingerprint.");
                        case IdempotencyState.Indeterminate: throw new HttpException(409, "The prior operation outcome is uncertain; reconcile it before retrying.");
                        case IdempotencyState.Full: throw new HttpException(503, "Idempotency storage capacity is exhausted.");
                        case IdempotencyState.InProgress:
                            waited = true;
                            if ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >= policy.DuplicateWaitTimeout.TotalSeconds)
                                throw new HttpException(409, "An operation with this key is still in progress.");
                            await Task.Delay(25, token).ConfigureAwait(false); continue;
                        case IdempotencyState.Acquired:
                            var owner = record.OwnerToken ?? throw new InvalidOperationException("Store omitted the owner token.");
                            if (waited)
                            {
                                // Never turn an already-waiting retry into a fresh operation after expiry/store loss.
                                await _store.FailAsync(key, owner, CancellationToken.None).ConfigureAwait(false);
                                throw new HttpException(409, "The original operation record disappeared while waiting; reconcile its outcome.");
                            }
                            await ExecuteOwnedAsync(context, policy, handler, body, key, owner).ConfigureAwait(false); return;
                        default: throw new InvalidOperationException("Unknown store outcome.");
                    }
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        private async Task ExecuteOwnedAsync(IHttpContext context, IdempotencyPolicy policy, Func<IHttpContext, Task> handler, byte[] body, string key, string owner)
        {
            IdempotencyResponse response;
            try
            {
                using var staged = IdempotencyContext.Create(context, body, policy.MaxResponseBytes);
                await handler(staged).ConfigureAwait(false);
                response = staged.Complete(policy);
                // The record must precede delivery. A failed socket write must never make an operation executable again.
                if (!await _store.CompleteAsync(key, owner, response, CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("The idempotency result could not be committed.");
            }
            catch
            {
                await _store.FailAsync(key, owner, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            await response.ReplayAsync(context).ConfigureAwait(false);
        }
        internal static bool VaryCovered(IHttpResponse response, IdempotencyPolicy policy)
            => (response.Headers.GetValues("Vary") ?? Array.Empty<string>()).SelectMany(v => v.Split(',')).Select(v => v.Trim())
                .All(v => v.Length == 0 || policy.Headers.Contains(v, StringComparer.OrdinalIgnoreCase));
        internal static bool WriteVerb(HttpVerbs verb) => verb == HttpVerbs.Post || verb == HttpVerbs.Put || verb == HttpVerbs.Patch || verb == HttpVerbs.Delete;
        private static async Task<byte[]> ReadBodyAsync(IHttpContext context, int limit)
        {
            if (context.Request.ContentLength64 > limit) throw new HttpException(413, "Idempotency request body is too large.");
            using var output = new MemoryStream(); var buffer = new byte[Math.Min(limit + 1, 8192)];
            while (true)
            {
                var read = await context.Request.InputStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, limit - (int)output.Length + 1), context.CancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > limit) throw new HttpException(413, "Idempotency request body is too large.");
                output.Write(buffer, 0, read);
            }
        }
        private static string Fields(params string[] fields) { var text = new StringBuilder(); foreach (var field in fields) text.Append(field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(field); return text.ToString(); }
        private static string Hash(string value) => Convert.ToBase64String(HashBytes(new UTF8Encoding(false, true).GetBytes(value)));
        private static byte[] HashBytes(byte[] value) { using var hash = SHA256.Create(); return hash.ComputeHash(value); }
    }
}
