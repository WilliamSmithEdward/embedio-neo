using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Tus
{
    /// <summary>A bounded, single-process tus store with exclusive directory ownership and restart recovery.</summary>
    /// <remarks>Dispose after all hosts stop. The directory must be private to the host account on a local filesystem
    /// supporting atomic file replacement. Recovery covers process interruption, not arbitrary filesystem damage or power-loss guarantees.</remarks>
    public sealed partial class DiskTusUploadStore : IDisposable
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0330:Use 'System.Threading.Lock'",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock."
        )]
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly FileStream _directoryLock;
        private long _reserved;
        private int _active;
        private bool _disposed;
        private bool _faulted;

        /// <summary>Exclusively opens and recovers a dedicated private directory. Invalid committed records fail closed.</summary>
        /// <param name="options">Immutable disk and request budgets.</param>
        public DiskTusUploadStore(TusUploadOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Directory.CreateDirectory(options.Directory);
            RejectLink(options.Directory);
            _directoryLock = new FileStream(
                Path.Combine(options.Directory, "store.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );

            try
            {
                Recover();
            }

            catch
            {
                _directoryLock.Dispose();
                throw;
            }
        }

        /// <summary>Gets the immutable budgets.</summary>
        public TusUploadOptions Options { get; }

        /// <summary>Gets current reserved bytes, including complete uploads and expired entries pending cleanup.</summary>
        public long ReservedBytes
        {
            get
            {
                lock (_sync) return _reserved;
            }
        }

        /// <summary>Gets the resident session count.</summary>
        public int Count
        {
            get
            {
                lock (_sync) return _entries.Count;
            }
        }

        /// <summary>Lists live snapshots belonging to a trusted application-selected owner, for restart reconciliation.</summary>
        /// <param name="owner">Stable, authenticated tenant/issuer/subject scope; never a client-provided header.</param>
        /// <returns>At most the configured maximum upload count.</returns>
        public IReadOnlyList<TusUpload> GetUploads(string owner)
        {
            var hash = OwnerHash(owner);

            lock (_sync)
            {
                EnsureOpen();
                return
                [
                    .. _entries.Values
                        .Where(e => e.Owner == hash && e.Upload.Expires > DateTimeOffset.UtcNow)
                        .Select(e => e.Upload)
                ];
            }
        }

        /// <summary>Copies a completed upload to an application-owned destination without closing it.</summary>
        /// <param name="id">Server-generated upload identifier.</param>
        /// <param name="owner">Trusted stable owner scope.</param>
        /// <param name="destination">Application-owned writable stream.</param>
        /// <param name="cancellationToken">Cancellation for the copy.</param>
        /// <returns>The copy operation.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1510:Use ArgumentNullException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ArgumentNullException.ThrowIfNull."
        )]
        public async Task CopyCompletedToAsync(
            string id,
            string owner,
            Stream destination,
            CancellationToken cancellationToken = default
        )
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var entry = Acquire(id, owner);

            try
            {
                if (!entry.Upload.IsComplete) throw new HttpException(409, "The upload is incomplete.");

                using var file = OpenData(id, FileAccess.Read);

                if (file.Length != entry.Upload.Length)
                {
                    lock (_sync) _faulted = true;

                    throw new InvalidDataException("Completed upload length changed.");
                }

                await file
                    .CopyToAsync(destination, 65536, cancellationToken)
                    .ConfigureAwait(false);
            }

            finally
            {
                Release(entry);
            }
        }

        /// <summary>Removes expired idle sessions and reclaims their storage. No background timer is installed.</summary>
        /// <returns>The number removed. Call periodically from the host; creation also performs cleanup.</returns>
        public int RemoveExpired()
        {
            lock (_sync)
            {
                EnsureOpen();
                var expired = _entries.Values
                    .Where(e => !e.Busy && e.Upload.Expires <= DateTimeOffset.UtcNow)
                    .ToArray();

                foreach (var entry in expired) Remove(entry);

                return expired.Length;
            }
        }

        internal TusUpload Create(string owner, long length, string metadata)
        {
            var hash = OwnerHash(owner);

            if (length < 0 || length > Options.MaximumUploadBytes) throw new HttpException(413);

            TusUploadModule.ValidateMetadata(metadata);

            lock (_sync)
            {
                EnsureOpen();
                RemoveExpired();

                if (_active >= Options.MaximumConcurrentRequests) throw new HttpException(503);

                if (_entries.Count >= Options.MaximumUploads || length > Options.MaximumStorageBytes - _reserved)
                    throw new HttpException(507, "Upload storage is full.");

                var owned = _entries.Values
                    .Where(e => e.Owner == hash)
                    .ToArray();

                if (
                    owned.Length >= Options.MaximumUploadsPerOwner
                    || length > Options.MaximumStorageBytesPerOwner - owned.Sum(e => e.Upload.Length)
                ) throw new HttpException(507, "Owner upload storage is full.");

                var upload = new TusUpload(
                    Guid
                        .NewGuid()
                        .ToString("N"),
                    length,
                    0,
                    DateTimeOffset.UtcNow.Add(Options.Lifetime),
                    metadata
                );
                var entry = new Entry(hash, upload);

                try
                {
                    using (var data = new FileStream(
                        PathFor(upload.Id, ".data"),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )) data.Flush(true);

                    Save(entry, upload, false);
                }

                catch
                {
                    _faulted = true;
                    throw;
                }

                _entries.Add(upload.Id, entry);
                _reserved += length;
                return upload;
            }
        }

        internal TusUpload Find(string id, string owner)
        {
            var hash = OwnerHash(owner);

            lock (_sync)
            {
                EnsureOpen();
                return Lookup(id, hash).Upload;
            }
        }

        /// <summary>Terminates an idle upload and reclaims storage. The caller supplies a trusted stable owner scope.</summary>
        /// <param name="id">Server-generated upload identifier.</param>
        /// <param name="owner">Trusted stable owner scope.</param>
        public void Delete(string id, string owner)
        {
            var entry = Acquire(id, owner);

            try
            {
                lock (_sync) Remove(entry);
            }

            finally
            {
                Release(entry);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
            Justification = "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads."
        )]
        internal async Task<(TusUpload Upload, bool BecameComplete)> PatchAsync(
            string id,
            string owner,
            long offset,
            Stream input,
            long contentLength,
            string? algorithm,
            byte[]? checksum,
            CancellationToken token
        )
        {
            var entry = Acquire(id, owner);
            var previous = entry.Upload;
            var committing = false;

            try
            {
                if (offset != previous.Offset) throw new HttpException(409, "The upload offset does not match.");

                var limit = Math.Min(Options.MaximumPatchBytes, previous.Length - previous.Offset);

                if (contentLength > limit) throw new HttpException(413);

                using var hash = algorithm == null ? null : CreateChecksum(algorithm);
                using var file = OpenData(id, FileAccess.ReadWrite);

                if (file.Length < previous.Offset)
                {
                    lock (_sync) _faulted = true;

                    throw new IOException("Committed upload bytes are missing.");
                }

                file.SetLength(previous.Offset);
                file.Position = previous.Offset;

                try
                {
                    var buffer = new byte[65536];
                    long received = 0;

                    while (true)
                    {
                        var count = await input
                            .ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, limit - received + 1), token)
                            .ConfigureAwait(false);

                        if (count == 0) break;

                        if (received + count > limit) throw new HttpException(413);

                        await file
                            .WriteAsync(buffer, 0, count, token)
                            .ConfigureAwait(false);
                        hash?.TransformBlock(buffer, 0, count, buffer, 0);
                        received += count;
                    }

                    if (contentLength >= 0 && received != contentLength)
                        throw new HttpException(400, "Incomplete PATCH body.");

                    if (hash != null)
                    {
                        hash.TransformFinalBlock([], 0, 0);

                        if (
                            checksum == null
                            || !Equal(
                                hash.Hash ?? throw new InvalidOperationException("Missing completed checksum."),
                                checksum
                            )
                        ) throw new HttpException(460, "Checksum mismatch.");
                    }

                    token.ThrowIfCancellationRequested();

                    if (previous.Expires <= DateTimeOffset.UtcNow) throw new HttpException(404);

                    file.Flush(true);
                    var next = new TusUpload(
                        id,
                        previous.Length,
                        previous.Offset + received,
                        previous.Expires,
                        previous.Metadata
                    );
                    committing = true;
                    Save(entry, next, true);

                    lock (_sync) entry.Upload = next;

                    return (next, !previous.IsComplete && next.IsComplete);
                }

                catch
                {
                    // Once replacement has been attempted its disk outcome is authoritative on recovery.
                    // Never truncate bytes beneath a potentially committed offset after a metadata I/O failure.
                    if (committing)
                    {
                        lock (_sync) _faulted = true;
                    }

                    else
                    {
                        try
                        {
                            file.SetLength(previous.Offset);
                            file.Flush(true);
                        }

                        catch
                        {
                            lock (_sync) _faulted = true;

                            throw;
                        }
                    }

                    throw;
                }
            }

            finally
            {
                Release(entry);
            }
        }

        private static HashAlgorithm CreateChecksum(string algorithm)
        {
            // tus requires SHA-1 interoperability. This is a non-security transfer checksum, never authentication.
            if (algorithm == "sha1") return SHA1.Create();

            if (algorithm == "sha256") return SHA256.Create();

            throw new HttpException(400, "Unsupported checksum algorithm.");
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;

            var different = 0;

            for (var i = 0; i < first.Length; i++) different |= first[i] ^ second[i];

            return different == 0;
        }

        private Entry Acquire(string id, string owner)
        {
            var hash = OwnerHash(owner);

            lock (_sync)
            {
                EnsureOpen();
                var entry = Lookup(id, hash);

                if (entry.Busy) throw new HttpException(409, "The upload is busy.");

                if (_active >= Options.MaximumConcurrentRequests) throw new HttpException(503);

                entry.Busy = true;
                _active++;
                return entry;
            }
        }

        private Entry Lookup(string id, string hash)
        {
            if (
                !ValidId(id)
                || !_entries.TryGetValue(id, out var entry)
                || entry.Owner != hash
                || entry.Upload.Expires <= DateTimeOffset.UtcNow
            ) throw new HttpException(404);

            return entry;
        }

        private void Release(Entry entry)
        {
            lock (_sync)
            {
                entry.Busy = false;
                _active--;

                if (_disposed && _active == 0) _directoryLock.Dispose();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        private void EnsureOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskTusUploadStore));

            if (_faulted) throw new IOException("The upload store requires reopening after an I/O failure.");
        }

        /// <summary>Stops new operations and releases directory ownership after outstanding operations finish.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;

                _disposed = true;

                if (_active == 0) _directoryLock.Dispose();
            }
        }

        private sealed class Entry
        {
            internal Entry(string owner, TusUpload upload)
            {
                Owner = owner;
                Upload = upload;
            }

            internal string Owner { get; }
            internal TusUpload Upload { get; set; }
            internal bool Busy { get; set; }
        }
    }
}
