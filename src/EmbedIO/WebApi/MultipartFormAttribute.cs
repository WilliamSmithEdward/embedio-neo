using System;
using System.Threading.Tasks;

namespace EmbedIO.WebApi
{
    /// <summary>Binds a streaming multipart form reader to a controller parameter.</summary>
    /// <remarks>The controller must consume sections sequentially. The reader is disposed when
    /// the request closes; callers may also dispose it sooner. Limits include unread sections
    /// when advancing the reader, and no files are automatically saved.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class MultipartFormAttribute : Attribute, IRequestDataAttribute<WebApiController, MultipartReader>
    {
        /// <summary>Gets or sets the maximum total decoded multipart bytes.</summary>
        public long MaxTotalBytes { get; set; } = 134217728;

        /// <summary>Gets or sets the maximum bytes in each file.</summary>
        public long MaxFileBytes { get; set; } = 67108864;

        /// <summary>Gets or sets the maximum bytes in each non-file field.</summary>
        public long MaxFieldBytes { get; set; } = 65536;

        /// <summary>Gets or sets the maximum number of parts.</summary>
        public int MaxParts { get; set; } = 128;

        /// <summary>Gets or sets the maximum number of file parts.</summary>
        public int MaxFiles { get; set; } = 32;

        /// <summary>Gets or sets the maximum number of non-file fields.</summary>
        public int MaxFields { get; set; } = 128;

        /// <summary>Gets or sets the maximum header bytes in each part.</summary>
        public int MaxHeaderBytes { get; set; } = 16384;

        /// <summary>Gets or sets the maximum headers in each part.</summary>
        public int MaxHeaders { get; set; } = 32;

        /// <inheritdoc />
        public Task<MultipartReader?> GetRequestDataAsync(WebApiController controller, string parameterName)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            MultipartReader reader = controller.HttpContext.OpenMultipartReader(new MultipartOptions
            {
                MaxTotalBytes = MaxTotalBytes,
                MaxFileBytes = MaxFileBytes,
                MaxFieldBytes = MaxFieldBytes,
                MaxParts = MaxParts,
                MaxFiles = MaxFiles,
                MaxFields = MaxFields,
                MaxHeaderBytes = MaxHeaderBytes,
                MaxHeaders = MaxHeaders,
            });
            return Task.FromResult<MultipartReader?>(reader);
        }
    }
}
