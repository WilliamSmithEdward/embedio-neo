using System;
using EmbedIO.Configuration;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>
    /// Base class for web server options.
    /// </summary>
    public abstract class WebServerOptionsBase : ConfiguredObject
    {
        private bool _supportCompressedRequests;
        internal RequestDecompressionPolicy? DecompressionPolicy { get; private set; }

        /// <summary>
        /// <para>Gets or sets a value indicating whether compressed request bodies are supported.</para>
        /// <para>The default value is <see langword="false"/>, because of the security risk
        /// posed by <see href="https://en.wikipedia.org/wiki/Zip_bomb">decompression bombs</see>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set and this instance's
        /// configuration is locked.</exception>
        public bool SupportCompressedRequests
        {
            get => _supportCompressedRequests;
            set
            {
                EnsureConfigurationNotLocked();
                _supportCompressedRequests = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum decoded bytes read from a compressed request body.
        /// The default is null, preserving existing unlimited decompression behavior.
        /// Zero permits only an empty decoded body. Excess content raises HTTP 413.
        /// </summary>
        /// <remarks>This applies to request-stream helpers when compressed requests are enabled.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public long? MaximumDecompressedRequestBodyBytes
        {
            get => DecompressionPolicy?.MaximumBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                DecompressionPolicy = value.HasValue ? new RequestDecompressionPolicy(value.Value) : null;
            }
        }

        private long? _maximumRequestBodyBytes;
        private int? _maximumConcurrentRequests;
        private int _requestQueueLimit;
        private TimeSpan? _requestTimeout;
        private int? _maximumRequestHeaderBytes;
        private int? _maximumRequestHeaderCount;
        private TimeSpan? _requestBodyReadTimeout;

        /// <summary>
        /// Gets or sets how long one read of a request body may wait for data. The default is
        /// null, preserving existing behavior without a read deadline.
        /// </summary>
        /// <remarks>
        /// <para>The deadline applies to each read of <see cref="IHttpRequest.InputStream"/>
        /// separately, so a slow but steady upload is not cut off; a client that stops sending is.
        /// A read that misses it throws an <see cref="HttpException"/> with status 408, and every
        /// later read of the same body does too. On HTTP/1.x the connection is not reused.</para>
        /// <para>The deadline applies to every protocol and listener mode. <c>CONNECT</c> tunnels
        /// are not request bodies and are not timed.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestBodyReadTimeout
        {
            get => _requestBodyReadTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                if (value.HasValue && (value.Value.TotalMilliseconds < 1 || value.Value.TotalMilliseconds > int.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _requestBodyReadTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of request body bytes a handler may read, counted
        /// as transferred, before any content decoding.
        /// The default is null, preserving existing unlimited behavior.
        /// </summary>
        /// <remarks>
        /// <para>A request that declares a larger Content-Length is rejected with HTTP 413 before
        /// any module runs. A body without a declared length, or one longer than declared, fails
        /// with HTTP 413 when a read crosses the limit. On HTTP/1.x the connection is not reused
        /// after either rejection, because the unread remainder of the body cannot be trusted.</para>
        /// <para>Zero permits only requests without body content. When compressed requests are
        /// enabled, <see cref="MaximumDecompressedRequestBodyBytes"/> separately limits decoded bytes.</para>
        /// <para>While a limit is set, a request carrying <c>Expect: 100-continue</c> receives its
        /// interim <c>100 Continue</c> response only when the handler first reads the body, so a
        /// refused body is never invited.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public long? MaximumRequestBodyBytes
        {
            get => _maximumRequestBodyBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestBodyBytes = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of requests the server handles at the same time.
        /// The default is null, preserving existing unlimited dispatch.
        /// </summary>
        /// <remarks>
        /// A request arriving while every slot is busy waits in a bounded queue of
        /// <see cref="RequestQueueLimit"/> entries. When the queue is also full, the request is
        /// rejected with HTTP 503 before any module runs. The limit applies to all listener modes
        /// and protocol versions and counts each HTTP/2 or HTTP/3 stream as one request.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumConcurrentRequests
        {
            get => _maximumConcurrentRequests;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumConcurrentRequests = value;
            }
        }

        /// <summary>
        /// Gets or sets how many requests may wait for a slot when
        /// <see cref="MaximumConcurrentRequests"/> is reached. The default is zero: a request
        /// that finds no free slot is rejected with HTTP 503 immediately.
        /// </summary>
        /// <remarks>Ignored when <see cref="MaximumConcurrentRequests"/> is null.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int RequestQueueLimit
        {
            get => _requestQueueLimit;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _requestQueueLimit = value;
            }
        }

        /// <summary>
        /// Gets or sets how long a request may be handled before the server cancels its context
        /// token. The default is null, preserving existing behavior without a deadline.
        /// </summary>
        /// <remarks>
        /// <para>The deadline starts when the server begins handling the request, including any time
        /// spent waiting for a <see cref="MaximumConcurrentRequests"/> slot. Cancellation is
        /// cooperative: handlers observe it through <see cref="IHttpContext.CancellationToken"/>.
        /// A handler that ignores the token runs to completion.</para>
        /// <para>When a handler stops because of the deadline before its response is committed,
        /// the server answers HTTP 503. After commitment the response is ended visibly instead.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestTimeout
        {
            get => _requestTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                if (value.HasValue && (value.Value <= TimeSpan.Zero || value.Value.TotalMilliseconds > int.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _requestTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum total size of a request's header fields. The default is null,
        /// preserving existing behavior.
        /// </summary>
        /// <remarks>
        /// <para>The size is the sum of each received header field's name and value lengths. The
        /// HTTP/1 request line and HTTP/2 or HTTP/3 pseudo-header fields are not counted. A larger
        /// request is rejected with HTTP 431 before any module runs.</para>
        /// <para>Each protocol parser also keeps its fixed 32 KiB ceiling, which applies first;
        /// this option can only lower the effective limit.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumRequestHeaderBytes
        {
            get => _maximumRequestHeaderBytes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestHeaderBytes = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of header fields a request may carry. The default is
        /// null, preserving existing behavior.
        /// </summary>
        /// <remarks>
        /// Every received field line counts, including repeated names and each HTTP/2 or HTTP/3
        /// cookie field. Pseudo-header fields are not counted. A request with more fields is
        /// rejected with HTTP 431 before any module runs.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumRequestHeaderCount
        {
            get => _maximumRequestHeaderCount;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumRequestHeaderCount = value;
            }
        }

        /// <summary>
        /// Locks this instance, preventing further configuration.
        /// </summary>
        public void Lock() => LockConfiguration();
    }
}
