using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Tus
{
    /// <summary>Opt-in tus 1.0 uploads with creation, expiration, termination and checksum extensions.</summary>
    /// <remarks>Register authentication, authorization, browser protection and CORS before this module.
    /// The module borrows its store. No routes or storage are installed unless explicitly configured.
    /// Creates protected tus endpoints at a dedicated base route.</remarks>
    public sealed class TusUploadModule : WebModuleBase
    {
        private readonly DiskTusUploadStore _store;
        private readonly Func<IHttpContext, string, string?> _ownerSelector;
        private Func<IHttpContext, TusUpload, Task>? _onCompleted;
        private int _active;

        /// <summary>Creates protected tus endpoints at a dedicated base route.</summary>
        /// <param name="baseRoute">Upload collection path, for example /uploads/.</param>
        /// <param name="store">Application-owned disk store, shared by modules using the same directory.</param>
        /// <param name="ownerSelector">Application authorization and stable owner selection on every request.
        /// Receives the effective method, including tus method override. Return null to deny. Include tenant and issuer when subjects are not globally unique.
        /// The request must also have an authenticated principal.</param>
        public TusUploadModule(
            string baseRoute,
            DiskTusUploadStore store,
            Func<IHttpContext, string, string?> ownerSelector
        ) : base(baseRoute)
        {
            store = Validate.NotNull(nameof(store), store);
            ownerSelector = Validate.NotNull(nameof(ownerSelector), ownerSelector);

            _store = store;
            _ownerSelector = ownerSelector;
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => true;

        /// <summary>Gets or sets an optional awaited notification after the transition to complete has been durably recorded.
        /// This is not an exactly-once transaction: process interruption can lose notification, and failure does not undo the upload.
        /// Reconcile completed snapshots on startup; keep publication idempotent using the upload ID.</summary>
        public Func<IHttpContext, TusUpload, Task>? OnCompleted
        {
            get => _onCompleted;
            set
            {
                EnsureConfigurationNotLocked();
                _onCompleted = value;
            }
        }

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();
            context.Response.Headers["Tus-Resumable"] = "1.0.0";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = 0;

            if (context.User.Identity?.IsAuthenticated != true)
                throw new HttpException(401, "Authentication is required.");

            var method = Single(context, "X-HTTP-Method-Override", false) ?? context.Request.HttpMethod;
            var owner = _ownerSelector(context, method);

            if (owner == null || string.IsNullOrWhiteSpace(owner) || owner.Length > 4096) throw new HttpException(403);

            if (Interlocked.Increment(ref _active) > _store.Options.MaximumConcurrentRequests)
            {
                Interlocked.Decrement(ref _active);
                throw new HttpException(503);
            }

            try
            {
                if (method != "OPTIONS" && Single(context, "Tus-Resumable", false) != "1.0.0")
                {
                    context.Response.Headers["Tus-Version"] = "1.0.0";
                    throw new HttpException(412);
                }

                var path = context.Request.Url.AbsolutePath;
                var collection = path == BaseRoute || path == BaseRoute.TrimEnd('/');
                var id = path.StartsWith(BaseRoute, StringComparison.Ordinal)
                    ? path.Substring(BaseRoute.Length)
                    : string.Empty;

                if (!collection && !DiskTusUploadStore.ValidId(id)) throw new HttpException(404);

                if (method == "OPTIONS")
                {
                    context.Response.Headers["Tus-Version"] = "1.0.0";
                    context.Response.Headers["Tus-Extension"] = "creation,expiration,termination,checksum";
                    context.Response.Headers["Tus-Checksum-Algorithm"] = "sha1,sha256";
                    context.Response.Headers["Tus-Max-Size"] = Number(_store.Options.MaximumUploadBytes);
                    context.Response.StatusCode = 204;
                    return;
                }

                if (method == "POST" && collection)
                {
                    Reject(context, "Upload-Defer-Length");
                    Reject(context, "Upload-Concat");
                    Reject(context, "Upload-Checksum");
                    Reject(context, "Content-Encoding");
                    var length = ParseNumber(Single(context, "Upload-Length", true));

                    if (length > _store.Options.MaximumUploadBytes) throw new HttpException(413);

                    var metadata = Single(context, "Upload-Metadata", false) ?? string.Empty;
                    ValidateMetadata(metadata);
                    await RequireEmptyBody(context).ConfigureAwait(false);
                    var upload = _store.Create(owner, length, metadata);
                    context.Response.Headers["Location"] = BaseRoute + upload.Id;
                    SetUploadHeaders(context, upload);
                    context.Response.StatusCode = 201;

                    if (upload.IsComplete && _onCompleted != null)
                        await _onCompleted(context, upload).ConfigureAwait(false);

                    return;
                }

                if (!collection && method == "HEAD")
                {
                    SetUploadHeaders(context, _store.Find(id, owner));
                    context.Response.StatusCode = 200;
                    return;
                }

                if (!collection && method == "DELETE")
                {
                    _store.Delete(id, owner);
                    context.Response.StatusCode = 204;
                    return;
                }

                if (!collection && method == "PATCH")
                {
                    // Check ownership before reading any supplied body or exposing offset information.
                    _ = _store.Find(id, owner);

                    if (
                        !string.Equals(
                            Single(context, "Content-Type", true),
                            "application/offset+octet-stream",
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) throw new HttpException(415);

                    Reject(context, "Content-Encoding");
                    Reject(context, "Upload-Length");
                    Reject(context, "Upload-Defer-Length");
                    Reject(context, "Upload-Concat");
                    var offset = ParseNumber(Single(context, "Upload-Offset", true));
                    var checksum = Single(context, "Upload-Checksum", false);
                    string? algorithm = null;
                    byte[]? digest = null;

                    if (checksum != null)
                    {
                        var space = Internal.StringOperations.IndexOfOrdinal(checksum, ' ');

                        if (space < 1) throw new HttpException(400);

                        algorithm = checksum.Substring(0, space);

                        if (algorithm != "sha1" && algorithm != "sha256")
                            throw new HttpException(400, "Unsupported checksum algorithm.");

                        try
                        {
                            digest = Convert.FromBase64String(checksum.Substring(space + 1));
                        }

                        catch (FormatException)
                        {
                            throw new HttpException(400);
                        }

                        if (digest.Length != (algorithm == "sha1" ? 20 : 32)) throw new HttpException(400);
                    }

                    var (Upload, BecameComplete) = await _store
                        .PatchAsync(
                            id,
                            owner,
                            offset,
                            context.Request.InputStream,
                            context.Request.ContentLength64,
                            algorithm,
                            digest,
                            token
                        )
                        .ConfigureAwait(false);
                    SetUploadHeaders(context, Upload);
                    context.Response.StatusCode = 204;

                    if (BecameComplete && _onCompleted != null)
                        await _onCompleted(context, Upload).ConfigureAwait(false);

                    return;
                }

                context.Response.Headers["Allow"] = collection ? "OPTIONS, POST" : "OPTIONS, HEAD, PATCH, DELETE";
                throw new HttpException(405);
            }

            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static long ParseNumber(string? value)
        {
            if (
                value == null
                || value.Length == 0
                || value.Any(c => c < '0' || c > '9')
                || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ) throw new HttpException(400);

            return number;
        }

        private static string? Single(IHttpContext context, string name, bool required)
        {
            var values = context.Request.Headers.GetValues(name);

            if (values == null)
            {
                if (required) throw new HttpException(400, "Missing " + name);

                return null;
            }

            if (values.Length != 1 || values[0].Length > 4096) throw new HttpException(400);

            return values[0];
        }

        private static void Reject(IHttpContext context, string name)
        {
            if (context.Request.Headers.GetValues(name) != null) throw new HttpException(400, "Unsupported " + name);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
            Justification = "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads."
        )]
        private static async Task RequireEmptyBody(IHttpContext context)
        {
            if (context.Request.ContentLength64 > 0)
                throw new HttpException(400, "Creation with upload is not enabled.");

            var probe = new byte[1];

            if (
                await context.Request.InputStream
                    .ReadAsync(probe, 0, 1, context.CancellationToken)
                    .ConfigureAwait(false)
                != 0
            ) throw new HttpException(400);
        }

        private static void SetUploadHeaders(IHttpContext context, TusUpload upload)
        {
            context.Response.Headers["Upload-Offset"] = Number(upload.Offset);
            context.Response.Headers["Upload-Length"] = Number(upload.Length);
            context.Response.Headers["Upload-Expires"] = upload.Expires.ToString("R", CultureInfo.InvariantCulture);

            if (upload.Metadata.Length > 0) context.Response.Headers["Upload-Metadata"] = upload.Metadata;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        internal static void ValidateMetadata(string text)
        {
            if (text.Length == 0) return;

            if (text.Length > 4096 || text.Any(c => c < 32 || c > 126))
                throw new HttpException(400, "Invalid upload metadata.");

            var fields = text.Split(',');
            var keys = new HashSet<string>(StringComparer.Ordinal);

            if (fields.Length > 32) throw new HttpException(400);

            foreach (var field in fields)
            {
                var space = Internal.StringOperations.IndexOfOrdinal(field, ' ');
                var key = space < 0 ? field : field.Substring(0, space);

                if (key.Length < 1 || key.Length > 64 || key.Any(c => c <= 32 || c > 126) || !keys.Add(key))
                    throw new HttpException(400);

                if (space < 0) continue;

                var value = field.Substring(space + 1);

                if (value.Any(char.IsWhiteSpace)) throw new HttpException(400);

                try
                {
                    _ = Convert.FromBase64String(value);
                }

                catch (FormatException)
                {
                    throw new HttpException(400);
                }
            }
        }
    }
}
