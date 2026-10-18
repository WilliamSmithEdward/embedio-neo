using System;
using System.IO;

namespace EmbedIO.Tus
{
    /// <summary>Immutable disk and request budgets for an opt-in tus store.</summary>
    public sealed class TusUploadOptions
    {
        /// <summary>Creates budgets. Storage is reserved for the entire declared length at creation.</summary>
        /// <param name="directory">Dedicated private directory outside static-file mounts, writable only by the host account.</param>
        /// <param name="maximumUploadBytes">Maximum declared size; defaults to 1 GiB.</param>
        /// <param name="maximumStorageBytes">Aggregate reserved file bytes, including complete uploads; defaults to 4 GiB.</param>
        /// <param name="maximumUploads">Maximum resident sessions, including complete uploads; defaults to 128.</param>
        /// <param name="maximumPatchBytes">Maximum bytes in one PATCH; defaults to 16 MiB.</param>
        /// <param name="lifetime">Fixed lifetime, including completed files; defaults to 24 hours, at most 30 days.</param>
        /// <param name="maximumUploadsPerOwner">Per-owner session cap; defaults to the smaller of 16 and the global cap.</param>
        /// <param name="maximumStorageBytesPerOwner">Per-owner reserved bytes; defaults to the smaller of 1 GiB and the global cap.</param>
        /// <param name="maximumConcurrentRequests">Maximum active store operations; excess requests fail immediately. Defaults to four.</param>
        public TusUploadOptions(string directory, long maximumUploadBytes = 1073741824, long maximumStorageBytes = 4294967296,
            int maximumUploads = 128, int maximumPatchBytes = 16777216, TimeSpan? lifetime = null, int maximumConcurrentRequests = 4,
            int? maximumUploadsPerOwner = null, long? maximumStorageBytesPerOwner = null)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A dedicated upload directory is required.", nameof(directory));
            if (maximumUploadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumUploadBytes));
            if (maximumStorageBytes < maximumUploadBytes) throw new ArgumentOutOfRangeException(nameof(maximumStorageBytes));
            if (maximumUploads < 1 || maximumUploads > 100000) throw new ArgumentOutOfRangeException(nameof(maximumUploads));
            if (maximumPatchBytes < 1 || maximumPatchBytes > 67108864) throw new ArgumentOutOfRangeException(nameof(maximumPatchBytes));
            var duration = lifetime ?? TimeSpan.FromHours(24);
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (maximumConcurrentRequests < 1 || maximumConcurrentRequests > 256) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRequests));
            var ownerCount = maximumUploadsPerOwner ?? Math.Min(16, maximumUploads);
            var ownerBytes = maximumStorageBytesPerOwner ?? Math.Min(1073741824, maximumStorageBytes);
            if (ownerCount < 1 || ownerCount > maximumUploads) throw new ArgumentOutOfRangeException(nameof(maximumUploadsPerOwner));
            if (ownerBytes < 0 || ownerBytes > maximumStorageBytes) throw new ArgumentOutOfRangeException(nameof(maximumStorageBytesPerOwner));
            MaximumUploadsPerOwner = ownerCount; MaximumStorageBytesPerOwner = ownerBytes;
            Directory = Path.GetFullPath(directory); MaximumUploadBytes = maximumUploadBytes; MaximumStorageBytes = maximumStorageBytes;
            MaximumUploads = maximumUploads; MaximumPatchBytes = maximumPatchBytes; Lifetime = duration; MaximumConcurrentRequests = maximumConcurrentRequests;
        }
        /// <summary>Gets the dedicated private store directory.</summary>
        public string Directory { get; }
        /// <summary>Gets the per-upload declared length limit.</summary>
        public long MaximumUploadBytes { get; }
        /// <summary>Gets the aggregate reserved byte limit.</summary>
        public long MaximumStorageBytes { get; }
        /// <summary>Gets the session count limit.</summary>
        public int MaximumUploads { get; }
        /// <summary>Gets the per-PATCH byte limit.</summary>
        public int MaximumPatchBytes { get; }
        /// <summary>Gets the fixed session lifetime.</summary>
        public TimeSpan Lifetime { get; }
        /// <summary>Gets the per-owner session cap.</summary>
        public int MaximumUploadsPerOwner { get; }
        /// <summary>Gets the per-owner reserved byte cap.</summary>
        public long MaximumStorageBytesPerOwner { get; }
        /// <summary>Gets the number of simultaneously active store operations.</summary>
        public int MaximumConcurrentRequests { get; }
    }
}
