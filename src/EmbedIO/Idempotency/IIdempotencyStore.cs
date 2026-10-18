using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Idempotency
{
    /// <summary>Atomic idempotency persistence contract. Implementations must be linearizable across their supported owners.</summary>
    /// <remarks>Never expire or evict an active claim, never expose its owner token to another acquirer,
    /// and never replace a terminal record before retention expires. Durable claims alone do not atomically
    /// commit application side effects. Coordinate those using the application's transaction mechanism.
    /// A storage error must fail closed. Completion and failure must compare the exact owner token.</remarks>
    public interface IIdempotencyStore
    {
        /// <summary>Atomically acquires an absent key or returns its existing state; rejects a mismatched fingerprint.</summary>
        /// <param name="key">Opaque bounded hashed scope/route/client-key identity.</param>
        /// <param name="fingerprint">Opaque bounded request fingerprint.</param>
        /// <param name="retention">Terminal-record retention for a newly acquired claim.</param>
        /// <param name="cancellationToken">Operation cancellation.</param>
        /// <returns>Acquired, existing, conflicting or full state.</returns>
        Task<IdempotencyRecord> AcquireAsync(
            string key,
            string fingerprint,
            TimeSpan retention,
            CancellationToken cancellationToken
        );

        /// <summary>Atomically records a finite result before delivery. False means ownership/completion was rejected.</summary>
        /// <param name="key">Claim identity.</param>
        /// <param name="ownerToken">Exact acquisition token.</param>
        /// <param name="response">Immutable finite response.</param>
        /// <param name="cancellationToken">Operation cancellation.</param>
        /// <returns>Whether this owner committed the result.</returns>
        Task<bool> CompleteAsync(
            string key,
            string ownerToken,
            IdempotencyResponse response,
            CancellationToken cancellationToken
        );

        /// <summary>Marks an owned active claim uncertain; never removes it or overwrites a completed result.</summary>
        /// <param name="key">Claim identity.</param>
        /// <param name="ownerToken">Exact acquisition token.</param>
        /// <param name="cancellationToken">Operation cancellation.</param>
        /// <returns>Whether this owner marked its claim uncertain.</returns>
        Task<bool> FailAsync(string key, string ownerToken, CancellationToken cancellationToken);
    }

    /// <summary>Atomic acquisition outcome.</summary>
    public enum IdempotencyState
    {
        /// <summary>This caller owns the new operation.</summary>
        Acquired,
        /// <summary>A different caller is still executing.</summary>
        InProgress,
        /// <summary>A recorded response is available.</summary>
        Completed,
        /// <summary>The same key has a different payload or representation fingerprint.</summary>
        Conflict,
        /// <summary>The prior operation's business outcome is uncertain; do not repeat it.</summary>
        Indeterminate,
        /// <summary>Capacity is exhausted; no claim was created.</summary>
        Full
    }

    /// <summary>An immutable store acquisition outcome.</summary>
    public sealed class IdempotencyRecord
    {
        /// <summary>Creates a validated outcome for a custom store.</summary>
        /// <param name="state">Acquisition outcome.</param>
        /// <param name="ownerToken">Required only for a newly acquired claim.</param>
        /// <param name="response">Required only for a completed claim.</param>
        public IdempotencyRecord(
            IdempotencyState state,
            string? ownerToken = null,
            IdempotencyResponse? response = null
        )
        {
            if (state < IdempotencyState.Acquired || state > IdempotencyState.Full)
                throw new ArgumentOutOfRangeException(nameof(state));

            if ((state == IdempotencyState.Acquired) != !string.IsNullOrEmpty(ownerToken) || ownerToken?.Length > 256)
                throw new ArgumentException("Only an acquired record carries an owner token.", nameof(ownerToken));

            if ((state == IdempotencyState.Completed) != (response != null))
                throw new ArgumentException("Only a completed record carries a response.", nameof(response));

            State = state;
            OwnerToken = ownerToken;
            Response = response;
        }

        /// <summary>Gets the state.</summary>
        public IdempotencyState State { get; }

        /// <summary>Gets this new owner's token; existing claim tokens are never disclosed.</summary>
        public string? OwnerToken { get; }

        /// <summary>Gets a recorded response.</summary>
        public IdempotencyResponse? Response { get; }
    }
}
