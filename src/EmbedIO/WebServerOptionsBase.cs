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

        /// <summary>
        /// Locks this instance, preventing further configuration.
        /// </summary>
        public void Lock() => LockConfiguration();
    }
}
