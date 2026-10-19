using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.RateLimiting
{
    /// <summary>
    /// <para>Admits requests under its base route through a <see cref="PartitionedRateLimiter{TResource}"/>
    /// from <c>System.Threading.RateLimiting</c>: fixed window, sliding window, token bucket,
    /// concurrency, or a chain of them, partitioned by a key the application chooses.</para>
    /// <para>A request that gets a permit continues to the next module, and the permit is held
    /// until the request completes, so concurrency limits count requests in progress. A request
    /// that does not get one is answered with 429 Too Many Requests and, when the limiter can say
    /// how long to wait, a <c>Retry-After</c> header.</para>
    /// <para>The module does not own the limiter: dispose it after the server stops.</para>
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="RateLimitingModule"/> class.
    /// </remarks>
    /// <param name="baseRoute">The base route of the requests to limit.</param>
    /// <param name="limiter">The limiter, for example from <see cref="PartitionedRateLimiter.Create{TResource, TPartitionKey}"/>
    /// with a key from <see cref="ClientKey(IHttpContext)"/> or an authenticated identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseRoute"/> or <paramref name="limiter"/> is <see langword="null"/>.</exception>
    public sealed class RateLimitingModule(string baseRoute, PartitionedRateLimiter<IHttpContext> limiter) : WebModuleBase(baseRoute)
    {
        private readonly PartitionedRateLimiter<IHttpContext> _limiter = Validate.NotNull(nameof(limiter), limiter);
        private int _rejectionStatusCode = 429;
        private Func<IHttpContext, RateLimitLease, Task>? _onRejected;

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <summary>
        /// Gets or sets the status code of a rejected request. The default is 429 Too Many Requests.
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is not between 400 and 599.</exception>
        public int RejectionStatusCode
        {
            get => _rejectionStatusCode;
            set
            {
                EnsureConfigurationNotLocked();

                if (value < 400 || value > 599)
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        "A rejection is a client or server error status, 400 to 599."
                    );

                _rejectionStatusCode = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets a callback that writes the response to a rejected request, for example
        /// a JSON body. Its status code and any <c>Retry-After</c> header are already set.</para>
        /// <para>When <see langword="null"/>, the default, the module answers with EmbedIO's
        /// HTTP exception handling, as for any <see cref="HttpException"/>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public Func<IHttpContext, RateLimitLease, Task>? OnRejected
        {
            get => _onRejected;
            set
            {
                EnsureConfigurationNotLocked();
                _onRejected = value;
            }
        }

        /// <summary>
        /// <para>A partition key for the client's network address, from the connection's remote
        /// end point. Forwarded headers are not read: behind a proxy every client shares the
        /// proxy's address, so derive a key from trusted proxy metadata instead.</para>
        /// <para>IPv4 clients, including IPv4-mapped IPv6 addresses, are keyed by address. IPv6
        /// clients are keyed by their /64 prefix, since one client typically controls a whole
        /// /64 and could otherwise create a partition per address.</para>
        /// </summary>
        /// <param name="context">The request's context.</param>
        /// <returns>The key.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public static string ClientKey(IHttpContext context) =>
            ClientKey(Validate.NotNull(nameof(context), context).RemoteEndPoint.Address);

        /// <summary>
        /// A partition key for a client address obtained some other way, such as from trusted
        /// proxy metadata, keyed as <see cref="ClientKey(IHttpContext)"/> keys the remote address.
        /// </summary>
        /// <param name="address">The client's address.</param>
        /// <returns>The key.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
        public static string ClientKey(IPAddress address)
        {
            address = Validate.NotNull(nameof(address), address);

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return address.ToString();

            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes).ToString() + "/64";
        }

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            // A queued request waits for a permit; the server's shutdown or the client's
            // disconnection cancels the wait.
            var lease = await _limiter
                .AcquireAsync(context, 1, context.CancellationToken)
                .ConfigureAwait(false);

            if (lease.IsAcquired)
            {
                try
                {
                    context.OnClose(_ => lease.Dispose());
                }

                catch
                {
                    lease.Dispose();
                    throw;
                }

                return;
            }

            using (lease)
            {
                if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = Math.Max(0, (long)Math.Ceiling(retryAfter.TotalSeconds));
                    context.Response.Headers.Set(
                        HttpHeaderNames.RetryAfter,
                        seconds.ToString(CultureInfo.InvariantCulture)
                    );
                }

                var onRejected = _onRejected ?? throw new HttpException(_rejectionStatusCode);
                context.Response.StatusCode = _rejectionStatusCode;
                await onRejected(context, lease).ConfigureAwait(false);
                context.SetHandled();
            }
        }
    }
}
