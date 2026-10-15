using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files.Internal;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Files
{
    /// <summary>
    /// A module serving files and directory listings from an <see cref="IFileProvider"/>.
    /// </summary>
    /// <seealso cref="WebModuleBase" />
    public class FileModule : WebModuleBase, IDisposable, IMimeTypeCustomizer
    {
        /// <summary>
        /// <para>Default value for <see cref="DefaultDocument"/>.</para>
        /// </summary>
        public const string DefaultDocumentName = "index.html";

        // A successful mapping is remembered so a repeated request skips the provider. The key is
        // the requested path as the client sent it, and a case-insensitive file system maps many
        // spellings of one resource (/index.html, /INDEX.HTML), so an unbounded cache would grow
        // with the spellings clients choose rather than with the resources served. Past this many
        // entries a mapping still succeeds, uncached: the bound costs a provider lookup, never a
        // wrong answer, and it is far above the number of paths an ordinary site serves.
        private const int MappingCacheLimit = 1024;

        private readonly string _cacheSectionName = UniqueIdGenerator.GetNext();
        private readonly MimeTypeCustomizer _mimeTypeCustomizer = new MimeTypeCustomizer();
        private readonly ConcurrentDictionary<string, (MappedResourceInfo Info, string UrlPath)>? _mappingCache;

        private BorrowedResource<FileCache> _cacheReference = new(FileCache.Default);
        private FileCache _cache
        {
            get => _cacheReference.Value;
            set => _cacheReference = new BorrowedResource<FileCache>(value);
        }
        private bool _contentCaching = true;
        private string? _defaultDocument = DefaultDocumentName;
        private string? _defaultExtension;
        private IDirectoryLister? _directoryLister;
        private FileRequestHandlerCallback _onMappingFailed = FileRequestHandler.ThrowNotFound;
        private FileRequestHandlerCallback _onDirectoryNotListable = FileRequestHandler.ThrowUnauthorized;
        private FileRequestHandlerCallback _onMethodNotAllowed = FileRequestHandler.ThrowMethodNotAllowed;
        private Action<IHttpContext, MappedResourceInfo>? _onPrepareResponse;
        private IMimeTypeProvider? _mimeTypeProvider;
        private PrecompressedVariants _precompressedVariants;

        private FileCache.Section? _cacheSection;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileModule"/> class,
        /// using the specified cache.
        /// </summary>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="provider">An <see cref="IFileProvider"/> interface that provides access
        /// to actual files and directories.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
        public FileModule(string baseRoute, IFileProvider provider)
            : base(baseRoute)
        {
            Provider = Validate.NotNull(nameof(provider), provider);
            _mappingCache = Provider.IsImmutable
                ? new ConcurrentDictionary<string, (MappedResourceInfo Info, string UrlPath)>()
                : null;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="FileModule"/> class.
        /// </summary>
        ~FileModule()
        {
            Dispose(false);
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => true;

        /// <summary>
        /// Gets the <see cref="IFileProvider"/>interface that provides access
        /// to actual files and directories served by this module.
        /// </summary>
        public IFileProvider Provider { get; }

        /// <summary>
        /// Gets or sets an optional application-owned MIME type and compression provider.
        /// The default is <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Explicit module overrides take precedence. Unanswered lookups retain the server
        /// and built-in fallbacks. Configure before startup; the provider must be thread-safe
        /// and stable while mappings/content are cached. It is not disposed by this module.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        /// <exception cref="ArgumentException">The provider creates a cycle between FileModule instances.</exception>
        public IMimeTypeProvider? MimeTypeProvider
        {
            get => _mimeTypeProvider;
            set
            {
                EnsureConfigurationNotLocked();
                for (var module = value as FileModule; module != null; module = module._mimeTypeProvider as FileModule)
                {
                    if (ReferenceEquals(module, this))
                        throw new ArgumentException("MIME provider delegation cannot form a FileModule cycle.", nameof(value));
                }
                _mimeTypeProvider = value;
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="FileCache"/> used by this module to store hashes and,
        /// optionally, file contents and rendered directory listings.
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        public FileCache Cache
        {
            get => _cache;
            set
            {
                EnsureConfigurationNotLocked();
                _cache = Validate.NotNull(nameof(value), value);
            }
        }

        /// <summary>
        /// <para>Gets or sets a value indicating whether this module caches the contents of files
        /// and directory listings.</para>
        /// <para>Note that the actual representations of files are stored in <see cref="FileCache"/>;
        /// thus, for example, if a file is always requested with an <c>Accept-Encoding</c> of <c>gzip</c>,
        /// only the gzipped contents of the file will be cached.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public bool ContentCaching
        {
            get => _contentCaching;
            set
            {
                EnsureConfigurationNotLocked();
                _contentCaching = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets the precompressed files this module serves. With <see cref="PrecompressedVariants.Brotli"/>,
        /// a request for <c>app.js</c> from a client that accepts <c>br</c> is answered with <c>app.js.br</c>, if it exists
        /// and is not older than <c>app.js</c>; <see cref="PrecompressedVariants.Gzip"/> does the same with <c>.gz</c>.
        /// The default is <see cref="PrecompressedVariants.None"/>.</para>
        /// <para>A precompressed file is served only where this module would compress the file itself: for MIME types it
        /// prefers to compress, and not for range requests. It is sent with the original file's content type, its own
        /// entity tag, <c>Content-Encoding</c> and <c>Vary: Accept-Encoding</c>. Its content must be the original file,
        /// compressed; nothing checks that.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value contains undefined flags.</exception>
        public PrecompressedVariants PrecompressedVariants
        {
            get => _precompressedVariants;
            set
            {
                EnsureConfigurationNotLocked();
                if ((value & ~PrecompressedVariants.All) != 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined precompressed variants.");

                _precompressedVariants = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets the name of the default document served, if it exists, instead of a directory listing
        /// when the path of a requested URL maps to a directory.</para>
        /// <para>The default value for this property is the <see cref="DefaultDocumentName"/> constant.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public string? DefaultDocument
        {
            get => _defaultDocument;
            set
            {
                EnsureConfigurationNotLocked();
                _defaultDocument = string.IsNullOrEmpty(value) ? null : value;
            }
        }

        /// <summary>
        /// <para>Gets or sets the default extension appended to requested URL paths that do not map
        /// to any file or directory. Defaults to <see langword="null"/>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentException">This property is being set to a non-<see langword="null"/>,
        /// non-empty string that does not start with a period (<c>.</c>).</exception>
        public string? DefaultExtension
        {
            get => _defaultExtension;
            set
            {
                EnsureConfigurationNotLocked();

                if (value == null || value.Length == 0)
                {
                    _defaultExtension = null;
                }
                else if (value[0] != '.')
                {
                    throw new ArgumentException("Default extension does not start with a period.", nameof(value));
                }
                else
                {
                    _defaultExtension = value;
                }
            }
        }

        /// <summary>
        /// <para>Gets or sets the <see cref="IDirectoryLister"/> interface used to generate
        /// directory listing in this module.</para>
        /// <para>A value of <see langword="null"/> (the default) disables the generation
        /// of directory listings.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public IDirectoryLister? DirectoryLister
        {
            get => _directoryLister;
            set
            {
                EnsureConfigurationNotLocked();
                _directoryLister = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets a <see cref="FileRequestHandlerCallback"/> that is called whenever
        /// the requested URL path could not be mapped to any file or directory.</para>
        /// <para>The default is <see cref="FileRequestHandler.ThrowNotFound"/>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        /// <seealso cref="FileRequestHandler"/>
        public FileRequestHandlerCallback OnMappingFailed
        {
            get => _onMappingFailed;
            set
            {
                EnsureConfigurationNotLocked();
                _onMappingFailed = Validate.NotNull(nameof(value), value);
            }
        }

        /// <summary>
        /// <para>Gets or sets a <see cref="FileRequestHandlerCallback"/> that is called whenever
        /// the requested URL path has been mapped to a directory, but directory listing has been
        /// disabled by setting <see cref="DirectoryLister"/> to <see langword="null"/>.</para>
        /// <para>The default is <see cref="FileRequestHandler.ThrowUnauthorized"/>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        /// <seealso cref="FileRequestHandler"/>
        public FileRequestHandlerCallback OnDirectoryNotListable
        {
            get => _onDirectoryNotListable;
            set
            {
                EnsureConfigurationNotLocked();
                _onDirectoryNotListable = Validate.NotNull(nameof(value), value);
            }
        }

        /// <summary>
        /// <para>Gets or sets a <see cref="FileRequestHandlerCallback"/> that is called whenever
        /// the requested URL path has been mapped to a file or directory, but the request's
        /// HTTP method is neither <c>GET</c> nor <c>HEAD</c>.</para>
        /// <para>The default is <see cref="FileRequestHandler.ThrowMethodNotAllowed"/>.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        /// <seealso cref="FileRequestHandler"/>
        public FileRequestHandlerCallback OnMethodNotAllowed
        {
            get => _onMethodNotAllowed;
            set
            {
                EnsureConfigurationNotLocked();
                _onMethodNotAllowed = Validate.NotNull(nameof(value), value);
            }
        }

        /// <summary>
        /// <para>Gets or sets an optional callback invoked after response metadata is prepared,
        /// before headers or file bytes are sent. The default is <see langword="null"/>.</para>
        /// <para>The callback receives the context and actual mapped resource, including default
        /// documents. It runs once per successful GET or HEAD response, including cached content,
        /// directory listings, partial responses and conditional not-modified responses.</para>
        /// <para>Use this callback to customize headers, for example to select a charset.
        /// Changing charset metadata does not transcode the file. Do not write a response body
        /// or change status, content length, range or compression metadata in this callback.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public Action<IHttpContext, MappedResourceInfo>? OnPrepareResponse
        {
            get => _onPrepareResponse;
            set
            {
                EnsureConfigurationNotLocked();
                _onPrepareResponse = value;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public string? GetMimeType(string extension)
            => _mimeTypeCustomizer.GetMimeType(extension) ?? _mimeTypeProvider?.GetMimeType(extension);

        /// <inheritdoc />
        public bool TryDetermineCompression(string mimeType, out bool preferCompression)
        {
            if (_mimeTypeCustomizer.TryDetermineCompression(mimeType, out preferCompression))
                return true;
            return _mimeTypeProvider != null && _mimeTypeProvider.TryDetermineCompression(mimeType, out preferCompression);
        }

        /// <inheritdoc />
        public void AddCustomMimeType(string extension, string mimeType)
            => _mimeTypeCustomizer.AddCustomMimeType(extension, mimeType);

        /// <inheritdoc />
        public void PreferCompression(string mimeType, bool preferCompression)
            => _mimeTypeCustomizer.PreferCompression(mimeType, preferCompression);

        /// <summary>
        /// Clears the part of <see cref="Cache"/> used by this module.
        /// </summary>
        public void ClearCache()
        {
            _mappingCache?.Clear();
            _cacheSection?.Clear();
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources;
        /// <see langword="false"/> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            if (_cacheSection != null)
                Provider.ResourceChanged -= _cacheSection.Remove;

            if (Provider is IDisposable disposableProvider)
                disposableProvider.Dispose();

            if (_cacheSection != null)
                Cache.RemoveSection(_cacheSectionName);
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            base.OnBeforeLockConfiguration();

            _mimeTypeCustomizer.Lock();
        }

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            base.OnStart(cancellationToken);

            _cacheSection = Cache.AddSection(_cacheSectionName);
            Provider.ResourceChanged += _cacheSection.Remove;
            Provider.Start(cancellationToken);
        }

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new System.NullReferenceException();
            MappedResourceInfo? info;
            string urlPath;

            var path = context.RequestedPath;

            // Map the URL path to a mapped resource.
            // DefaultDocument and DefaultExtension are handled here.
            // Use the mapping cache if it exists.
            if (_mappingCache == null)
            {
                info = MapUrlPath(path, context, out urlPath);
            }
            else if (_mappingCache.TryGetValue(path, out var cached))
            {
                (info, urlPath) = cached;
            }
            else
            {
                info = MapUrlPath(path, context, out urlPath);
                if (info != null && _mappingCache.Count < MappingCacheLimit)
                    _mappingCache[path] = (info, urlPath);
            }

            if (info == null)
            {
                // If mapping failed, send a "404 Not Found" response, or whatever OnMappingFailed chooses to do.
                // For example, it may return a default resource (think a folder of images and an imageNotFound.jpg),
                // or redirect the request.
                await OnMappingFailed(context, null).ConfigureAwait(false);
            }
            else if (!IsHttpMethodAllowed(context.Request, out var sendResponseBody))
            {
                // If there is a mapped resource, check that the HTTP method is either GET or HEAD.
                // Otherwise, send a "405 Method Not Allowed" response, or whatever OnMethodNotAllowed chooses to do.
                await OnMethodNotAllowed(context, info).ConfigureAwait(false);
            }
            else if (info.IsDirectory && DirectoryLister == null)
            {
                // If a directory listing was requested, but there is no DirectoryLister,
                // send a "403 Unauthorized" response, or whatever OnDirectoryNotListable chooses to do.
                // For example, one could prefer to send "404 Not Found" instead.
                await OnDirectoryNotListable(context, info).ConfigureAwait(false);
            }
            else
            {
                await HandleResource(context, info, urlPath, sendResponseBody).ConfigureAwait(false);
            }
        }

        // Tells whether a request's HTTP method is suitable for processing by FileModule
        // and, if so, whether a response body must be sent.
        private static bool IsHttpMethodAllowed(IHttpRequest request, out bool sendResponseBody)
        {
            switch (request.HttpVerb)
            {
                case HttpVerbs.Head:
                    sendResponseBody = false;
                    return true;
                case HttpVerbs.Get:
                    sendResponseBody = true;
                    return true;
                default:
                    sendResponseBody = default;
                    return false;
            }
        }

        // Prepares response headers for a "200 OK" or "304 Not Modified" response.
        // RFC7232, Section 4.1
        private static void PreparePositiveResponse(IHttpResponse response, MappedResourceInfo info, string contentType, string entityTag, Action<IHttpResponse> setCompression)
        {
            setCompression(response);
            response.ContentType = contentType;
            response.Headers.Set(HttpHeaderNames.ETag, entityTag);
            response.Headers.Set(HttpHeaderNames.LastModified, HttpDate.Format(info.LastModifiedUtc));
            response.Headers.Set(HttpHeaderNames.CacheControl, "max-age=0, must-revalidate");
            response.Headers.Set(HttpHeaderNames.AcceptRanges, "bytes");
        }

        // Attempts to map a module-relative URL path to a mapped resource,
        // handling DefaultDocument and DefaultExtension.
        // Returns null if not found.
        // Directories mus be returned regardless of directory listing being enabled.
        private MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider, out string resolvedUrlPath)
        {
            resolvedUrlPath = requestPath;
            var result = Provider.MapUrlPath(requestPath, mimeTypeProvider);

            // If requestPath maps to a file, no further searching is needed.
            if (result?.IsFile ?? false)
                return result;

            // Look for a default document.
            // Don't append an additional slash if the URL path is "/".
            // The default document, if found, must be a file, not a directory.
            if (DefaultDocument != null)
            {
                var defaultDocumentPath = requestPath + (requestPath.Length > 1 ? "/" : string.Empty) + DefaultDocument;
                var defaultDocumentResult = Provider.MapUrlPath(defaultDocumentPath, mimeTypeProvider);
                if (defaultDocumentResult?.IsFile ?? false)
                {
                    resolvedUrlPath = defaultDocumentPath;
                    return defaultDocumentResult;
                }
            }

            // Try to apply default extension (but not if the URL path is "/",
            // i.e. the only normalized, non-base URL path that ends in a slash).
            // When the default extension is applied, the result must be a file.
            if (DefaultExtension != null && requestPath.Length > 1)
            {
                var defaultExtensionResult = Provider.MapUrlPath(requestPath + DefaultExtension, mimeTypeProvider);
                if (defaultExtensionResult?.IsFile ?? false)
                {
                    resolvedUrlPath = requestPath + DefaultExtension;
                    return defaultExtensionResult;
                }
            }

            return result;
        }

        // Decides how a mapped resource is answered: with a precompressed variant, a negotiation
        // failure, a "304 Not Modified" reply, or the representation itself.
        private Task HandleResource(IHttpContext context, MappedResourceInfo info, string urlPath, bool sendResponseBody)
        {
            var cacheItem = ResolveCacheItem(info);
            var cachingThreshold = 1024L * Cache.MaxFileSizeKb;

            /*
             * Now we have a cacheItem for the resource.
             * It may have been just created, or it may or may not have a cached content,
             * depending upon the value of the ContentCaching property,
             * the size of the resource, and the value of the
             * MaxFileSizeKb of our Cache.
             */

            var contentType = ResolveContentType(info, out var mimeType);

            // Next we're going to apply proactive negotiation
            // to determine whether we agree with the client upon the compression
            // (or lack of it) to use for the resource.
            var preferCompression = PrefersCompression(context, mimeType);
            if (!context.Request.TryNegotiateContentEncoding(preferCompression, out var compressionMethod, out var setCompressionInResponse))
            {
                // The client refuses every coding this module can produce. A precompressed file may still be one it accepts.
                if (TrySelectPrecompressedVariant(context, info, urlPath, CompressionMethod.None, out var acceptedVariant, out var acceptedMethod))
                    return HandleVariant(context, info, acceptedVariant, acceptedMethod, contentType, sendResponseBody, cachingThreshold);

                // If negotiation failed, the returned callback will do the right thing.
                setCompressionInResponse(context.Response);
                return Task.CompletedTask;
            }

            // A precompressed file replaces compressing the file here, so it is considered only where this
            // module would compress: a MIME type it prefers to compress, and no Range header.
            if (preferCompression && TrySelectPrecompressedVariant(context, info, urlPath, compressionMethod, out var variant, out var variantMethod))
                return HandleVariant(context, info, variant, variantMethod, contentType, sendResponseBody, cachingThreshold);

            var entityTag = info.GetEntityTag(compressionMethod);

            // Send a "304 Not Modified" response if applicable.
            if (IsUnmodified(context.Request, entityTag, info.LastModifiedUtc))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotModified;
                PreparePositiveResponse(context.Response, info, contentType, entityTag, setCompressionInResponse);
                _onPrepareResponse?.Invoke(context, info);
                return Task.CompletedTask;
            }

            return SendRepresentationAsync(context, info, cacheItem, cachingThreshold, compressionMethod, contentType, entityTag, setCompressionInResponse, sendResponseBody);
        }

        // The cache item of a resource. A mutable provider's item is replaced when the resource changed.
        private FileCacheItem ResolveCacheItem(MappedResourceInfo info)
        {
            var section = _cacheSection ?? throw new InvalidOperationException("The file cache section has not been initialized.");
            if (!section.TryGet(info.Path, out var cacheItem))
            {
                // Resource information not yet cached
                cacheItem = new FileCacheItem(section, info.LastModifiedUtc, info.Length);
                section.Add(info.Path, cacheItem);
            }
            else if (!Provider.IsImmutable && (cacheItem.LastModifiedUtc != info.LastModifiedUtc || cacheItem.Length != info.Length))
            {
                // The resource has changed: discard the cache item and create a new one.
                section.Remove(info.Path);
                cacheItem = new FileCacheItem(section, info.LastModifiedUtc, info.Length);
                section.Add(info.Path, cacheItem);
            }

            return cacheItem;
        }

        // The content type sent for a resource, with its parameters; mimeType is the type alone.
        // A resource without a type takes the directory lister's or the default; a type that is
        // not a valid MIME type becomes the default.
        private string ResolveContentType(MappedResourceInfo info, out string mimeType)
        {
            var contentType = info.ContentType ?? DirectoryLister?.ContentType ?? MimeType.Default;
            mimeType = MimeType.StripParameters(contentType);
            if (MimeType.IsMimeType(mimeType, false))
                return contentType;

            mimeType = MimeType.Default;
            return MimeType.Default;
        }

        // Compression is preferred where the MIME type (or the absence of a policy) says so, but
        // never for a range request: the combination of partial responses and entity compression
        // is not really standardized, so a Range header negotiates for no compression and is
        // ignored later if there is compression anyway.
        private static bool PrefersCompression(IHttpContext context, string mimeType)
        {
            if (!context.TryDetermineCompression(mimeType, out var preferCompression))
                preferCompression = true;

            return preferCompression && context.Request.Headers.Get(HttpHeaderNames.Range) == null;
        }

        // A precompressed file is considered only for files, and only when variants are enabled.
        private bool TrySelectPrecompressedVariant(IHttpContext context, MappedResourceInfo info, string urlPath, CompressionMethod negotiated, out MappedResourceInfo variant, out CompressionMethod method)
        {
            if (info.IsFile && _precompressedVariants != PrecompressedVariants.None)
                return TrySelectVariant(context, info, urlPath, negotiated, out variant, out method);

            variant = info;
            method = CompressionMethod.None;
            return false;
        }

        // RFC7232, Section 3.3: "A recipient MUST ignore If-Modified-Since
        //                       if the request contains an If-None-Match header field."
        private static bool IsUnmodified(IHttpRequest request, string entityTag, DateTime lastModifiedUtc)
            => request.CheckIfNoneMatch(entityTag, out var ifNoneMatchExists)
            || (!ifNoneMatchExists && request.CheckIfModifiedSince(lastModifiedUtc, out _));

        // Sends the representation: a directory listing or a file, whole or as one byte range,
        // from the cache when it is cached, cached first when it fits, streamed otherwise. Each
        // path awaits inside one method, so a request still runs through a single state machine.
        private Task SendRepresentationAsync(
            IHttpContext context, MappedResourceInfo info, FileCacheItem cacheItem, long cachingThreshold,
            CompressionMethod compressionMethod, string contentType, string entityTag,
            Action<IHttpResponse> setCompressionInResponse, bool sendResponseBody)
        {
            /*
             * At this point we know the response is "200 OK",
             * unless the request is a range request.
             *
             * RFC7233, Section 3.1: "The Range header field is evaluated after evaluating the precondition
             *                       header fields defined in RFC7232, and only if the result in absence
             *                       of the Range header field would be a 200 (OK) response.  In other
             *                       words, Range is ignored when a conditional GET would result in a 304
             *                       (Not Modified) response."
             */

            // Before evaluating ranges, we must know the content length.
            // This is easy for files, as it is stored in info.Length.
            // Directories always have info.Length == 0; therefore,
            // unless the directory listing is cached, we must generate it now
            // (and cache it while we're there, if applicable).
            // A listing's relative links depend on whether the URL ends in '/', but /dir and /dir/
            // map to the same directory; only the slash form is cached.
            var cacheListing = !info.IsDirectory || context.Request.Url.AbsolutePath.EndsWith("/", StringComparison.Ordinal);
            var content = cacheListing ? cacheItem.GetContent(compressionMethod) : null;
            if (info.IsDirectory && content == null)
                return SendListingAsync(context, info, cacheItem, cachingThreshold, compressionMethod, contentType, entityTag, setCompressionInResponse, sendResponseBody, cacheListing);

            var contentLength = content?.Length ?? info.Length;
            var frame = FrameResponse(context, info, contentLength, compressionMethod, contentType, entityTag, setCompressionInResponse);

            // If it's a HEAD request, we're done.
            if (!sendResponseBody)
            {
                DescribeBodyForHead(context.Response, compressionMethod == CompressionMethod.None || content != null, frame.Length);
                return Task.CompletedTask;
            }

            // Transfer cached content if present.
            if (content != null)
                return WriteContentAsync(context, content, frame);

            // If content must be sent AND cached, first read it and store it.
            // The resource is a file here: a directory would have been listed by now.
            if (ContentCaching && contentLength <= cachingThreshold)
                return CacheAndSendAsync(context, info, cacheItem, compressionMethod, frame);

            // Read and transfer content without caching.
            return StreamAsync(context, info, compressionMethod, frame);
        }

        // Generates a directory listing, caches it when it fits and the URL is the slash form,
        // and sends it like cached content.
        private async Task SendListingAsync(
            IHttpContext context, MappedResourceInfo info, FileCacheItem cacheItem, long cachingThreshold,
            CompressionMethod compressionMethod, string contentType, string entityTag,
            Action<IHttpResponse> setCompressionInResponse, bool sendResponseBody, bool cacheListing)
        {
            var (content, uncompressedLength) = await GenerateDirectoryListingAsync(context, info, compressionMethod)
                .ConfigureAwait(false);
            if (cacheListing && ContentCaching && uncompressedLength <= cachingThreshold)
                _ = cacheItem.SetContent(compressionMethod, content);

            var frame = FrameResponse(context, info, content.Length, compressionMethod, contentType, entityTag, setCompressionInResponse);

            // If it's a HEAD request, we're done; the listing's length is known.
            if (!sendResponseBody)
            {
                DescribeBodyForHead(context.Response, true, frame.Length);
                return;
            }

            await WriteContentAsync(context, content, frame).ConfigureAwait(false);
        }

        // Frames the response around content of a known length: the range selection (ignored when
        // compression is in force), the status and headers, and the prepare callback.
        private ContentFrame FrameResponse(IHttpContext context, MappedResourceInfo info, long contentLength, CompressionMethod compressionMethod, string contentType, string entityTag, Action<IHttpResponse> setCompressionInResponse)
        {
            // Ignore range request is compression is enabled
            // (or should I say forced, since negotiation has tried not to use it).
            var partialStart = 0L;
            var partialUpperBound = contentLength - 1;
            var isPartial = compressionMethod == CompressionMethod.None
                         && context.Request.IsRangeRequest(contentLength, entityTag, info.LastModifiedUtc, out partialStart, out partialUpperBound);
            var responseContentLength = PrepareContentResponse(context.Response, info, contentType, entityTag, setCompressionInResponse, isPartial, partialStart, partialUpperBound, contentLength);

            // Invoke before choosing transport framing: a callback failure can still use
            // the ordinary exception response without retaining a file's content length.
            _onPrepareResponse?.Invoke(context, info);
            return new ContentFrame(isPartial, partialStart, responseContentLength);
        }

        // Prepares a "200 OK" or "206 Partial Content" response and returns the number of body
        // bytes it describes: the whole content, or the selected range.
        private static long PrepareContentResponse(IHttpResponse response, MappedResourceInfo info, string contentType, string entityTag, Action<IHttpResponse> setCompression,
            bool isPartial, long partialStart, long partialUpperBound, long contentLength)
        {
            if (!isPartial)
            {
                PreparePositiveResponse(response, info, contentType, entityTag, setCompression);
                return contentLength;
            }

            response.StatusCode = (int)HttpStatusCode.PartialContent;
            PreparePositiveResponse(response, info, contentType, entityTag, setCompression);
            response.Headers.Set(HttpHeaderNames.ContentRange, $"bytes {partialStart}-{partialUpperBound}/{contentLength}");
            return partialUpperBound - partialStart + 1;
        }

        // A HEAD response declares the body length a GET would send when that length is known
        // (identity content, or content already in the cache); otherwise the GET would be chunked.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DescribeBodyForHead(IHttpResponse response, bool lengthKnown, long length)
        {
            if (lengthKnown)
                response.ContentLength64 = length;
            else
                response.SendChunked = true;
        }

        // Writes the framed part of in-memory content with its exact length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Task WriteContentAsync(IHttpContext context, byte[] content, ContentFrame frame)
        {
            context.Response.ContentLength64 = frame.Length;
            return context.Response.OutputStream.WriteAsync(content, frame.IsPartial ? (int)frame.Start : 0, (int)frame.Length, context.CancellationToken);
        }

        // Reads a file that fits the cache through the negotiated coding into memory, publishes
        // the bytes to the cache and sends the framed part of them.
        private async Task CacheAndSendAsync(IHttpContext context, MappedResourceInfo info, FileCacheItem cacheItem, CompressionMethod compressionMethod, ContentFrame frame)
        {
            byte[] content;
            using (var memoryStream = new MemoryStream())
            {
                using (var compressor = new CompressionStream(memoryStream, compressionMethod))
                {
                    using var source = Provider.OpenFile(info.Path) ?? throw new FileNotFoundException("The mapped file is no longer available.", info.Path);
                    await source.CopyToAsync(compressor, WebServer.StreamCopyBufferSize, context.CancellationToken)
                        .ConfigureAwait(false);
                }

                // The provider sees only CompressionStream; dispose this private buffer before publishing it.
                var buffer = memoryStream.GetBuffer();
                content = buffer.Length == memoryStream.Length ? buffer : memoryStream.ToArray();
            }

            _ = cacheItem.SetContent(compressionMethod, content);

            // Caching reads the entire file; a range response still sends only
            // the selected slice. Compression and ranges are mutually exclusive.
            if (!frame.IsPartial)
                frame = new ContentFrame(false, 0, content.Length);

            await WriteContentAsync(context, content, frame).ConfigureAwait(false);
        }

        // Streams a file from the provider without caching: the whole file through the negotiated
        // coding, or the selected range by seeking (or skipping) to its start.
        private async Task StreamAsync(IHttpContext context, MappedResourceInfo info, CompressionMethod compressionMethod, ContentFrame frame)
        {
            using var source = Provider.OpenFile(info.Path) ?? throw new FileNotFoundException("The mapped file is no longer available.", info.Path);
            context.Response.SendChunked = true;

            if (!frame.IsPartial)
            {
                using var compressor = new CompressionStream(context.Response.OutputStream, compressionMethod);
                await source.CopyToAsync(compressor, WebServer.StreamCopyBufferSize, context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var buffer = new byte[WebServer.StreamCopyBufferSize];
            if (source.CanSeek)
            {
                source.Position = frame.Start;
            }
            else
            {
                var skipLength = frame.Start;
                while (skipLength > 0)
                {
                    var read = await source.ReadAsync(buffer, 0, (int)Math.Min(skipLength, buffer.Length), context.CancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0) throw new EndOfStreamException("The file ended before the requested range.");
                    skipLength -= read;
                }
            }

            var transferSize = frame.Length;
            while (transferSize > 0)
            {
                var read = await source.ReadAsync(buffer, 0, (int)Math.Min(transferSize, buffer.Length), context.CancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The file ended before the requested range was complete.");
                await context.Response.OutputStream.WriteAsync(buffer, 0, read, context.CancellationToken)
                    .ConfigureAwait(false);
                transferSize -= read;
            }
        }

        // The part of the content a response sends: everything, or the selected byte range.
        private readonly struct ContentFrame
        {
            internal ContentFrame(bool isPartial, long start, long length)
            {
                IsPartial = isPartial;
                Start = start;
                Length = length;
            }

            internal bool IsPartial { get; }

            internal long Start { get; }

            internal long Length { get; }
        }

        // Chooses a fresh precompressed file the client accepts at least as much as the negotiated coding.
        // Among equally acceptable files, Brotli is preferred for its size.
        private bool TrySelectVariant(IHttpContext context, MappedResourceInfo info, string urlPath, CompressionMethod negotiated, out MappedResourceInfo variant, out CompressionMethod method)
        {
            variant = info;
            method = CompressionMethod.None;
            var accepted = new QValueList(true, context.Request.Headers.GetValues(HttpHeaderNames.AcceptEncoding));
            var floor = Weight(accepted, negotiated);
            var best = 0;
            foreach (var (flag, candidate, extension) in new[]
            {
                (PrecompressedVariants.Brotli, CompressionMethod.Brotli, ".br"),
                (PrecompressedVariants.Gzip, CompressionMethod.Gzip, ".gz"),
            })
            {
                if ((_precompressedVariants & flag) == 0)
                    continue;

                var weight = Weight(accepted, candidate);
                if (weight <= 0 || weight < floor || weight <= best)
                    continue;

                var found = Provider.MapUrlPath(urlPath + extension, context);
                if (found == null || !found.IsFile || found.LastModifiedUtc < info.LastModifiedUtc)
                    continue;

                variant = found;
                method = candidate;
                best = weight;
            }

            return method != CompressionMethod.None;
        }

        private static int Weight(QValueList accepted, CompressionMethod method)
        {
            var name = method switch
            {
                CompressionMethod.Gzip => CompressionMethodNames.Gzip,
                CompressionMethod.Deflate => CompressionMethodNames.Deflate,
                CompressionMethod.Brotli => CompressionMethodNames.Brotli,
                _ => CompressionMethodNames.None,
            };

            // Without Accept-Encoding, EmbedIO sends the file as is; precompressed files go only to clients
            // that name their coding.
            if (accepted.QValues.Count == 0)
                return method == CompressionMethod.None ? 1000 : 0;

            return accepted.TryGetWeight(name, out var weight) ? weight : 0;
        }

        // Sends a precompressed file: the original's content type, the file's own bytes and an entity tag that
        // identifies both, so it can never match the original's, a dynamically compressed one, or the file's own.
        private async Task HandleVariant(IHttpContext context, MappedResourceInfo info, MappedResourceInfo variant, CompressionMethod method, string contentType, bool sendResponseBody, long cachingThreshold)
        {
            var codingName = method == CompressionMethod.Brotli ? CompressionMethodNames.Brotli : CompressionMethodNames.Gzip;
            var entityTag = EntityTag.ComputeVariant(info.LastModifiedUtc, info.Length, variant.LastModifiedUtc, variant.Length, method);
            void SetCoding(IHttpResponse response)
            {
                response.Headers.Add(HttpHeaderNames.Vary, HttpHeaderNames.AcceptEncoding);
                response.Headers.Set(HttpHeaderNames.ContentEncoding, codingName);
            }

            if (context.Request.CheckIfNoneMatch(entityTag, out var ifNoneMatchExists)
             || (!ifNoneMatchExists && context.Request.CheckIfModifiedSince(variant.LastModifiedUtc, out _)))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotModified;
                PreparePositiveResponse(context.Response, variant, contentType, entityTag, SetCoding);
                _onPrepareResponse?.Invoke(context, info);
                return;
            }

            PreparePositiveResponse(context.Response, variant, contentType, entityTag, SetCoding);
            _onPrepareResponse?.Invoke(context, info);
            context.Response.ContentLength64 = variant.Length;
            if (!sendResponseBody)
                return;

            // The file's bytes, cached like any file. A direct request for the file caches the same bytes.
            // The variant was just mapped, so the cached bytes are checked against it even for an immutable provider:
            // the headers above already describe this file, and the body must match them.
            var section = _cacheSection ?? throw new InvalidOperationException("The file cache section has not been initialized.");
            if (!section.TryGet(variant.Path, out var cacheItem)
             || cacheItem.LastModifiedUtc != variant.LastModifiedUtc || cacheItem.Length != variant.Length)
            {
                section.Remove(variant.Path);
                cacheItem = new FileCacheItem(section, variant.LastModifiedUtc, variant.Length);
                section.Add(variant.Path, cacheItem);
            }

            var content = cacheItem.GetContent(CompressionMethod.None);
            if (content == null)
            {
                using var source = Provider.OpenFile(variant.Path) ?? throw new FileNotFoundException("The mapped file is no longer available.", variant.Path);
                if (ContentCaching && variant.Length <= cachingThreshold)
                {
                    using var memory = new MemoryStream();
                    await source.CopyToAsync(memory, WebServer.StreamCopyBufferSize, context.CancellationToken).ConfigureAwait(false);
                    content = memory.ToArray();
                    if (content.Length != variant.Length)
                        throw new IOException("The precompressed file changed while it was read.");

                    _ = cacheItem.SetContent(CompressionMethod.None, content);
                }
                else
                {
                    await source.CopyToAsync(context.Response.OutputStream, WebServer.StreamCopyBufferSize, context.CancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await context.Response.OutputStream.WriteAsync(content, 0, content.Length, context.CancellationToken).ConfigureAwait(false);
        }

        // Uses DirectoryLister to generate a directory listing asynchronously.
        // Returns a tuple of the generated content and its *uncompressed* length
        // (useful to decide whether it can be cached).
        private async Task<(byte[], long)> GenerateDirectoryListingAsync(
            IHttpContext context,
            MappedResourceInfo info,
            CompressionMethod compressionMethod)
        {
            using var memoryStream = new MemoryStream();
            long uncompressedLength;
            using (var stream = new CompressionStream(memoryStream, compressionMethod))
            {
                await (DirectoryLister ?? throw new InvalidOperationException("No directory lister is configured.")).ListDirectoryAsync(
                    info,
                    context.Request.Url.AbsolutePath,
                    Provider.GetDirectoryEntries(info.Path, context),
                    stream,
                    context.CancellationToken).ConfigureAwait(false);

                uncompressedLength = stream.UncompressedLength;
            }

            // The compressor flushes its final block on disposal; a lister need not close the stream.
            return (memoryStream.ToArray(), uncompressedLength);
        }
    }
}
