using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.Tus
{
    public sealed partial class DiskTusUploadStore
    {
        private const int Format = 0x54555301;

        private string PathFor(string id, string extension) => Path.Combine(Options.Directory, id + extension);

        internal static bool ValidId(string id) =>
            id != null && id.Length == 32 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Upload storage must not contain symbolic links or reparse points.");
        }

        private FileStream OpenData(string id, FileAccess access)
        {
            var path = PathFor(id, ".data");
            RejectLink(path);
            return new FileStream(
                path,
                FileMode.Open,
                access,
                FileShare.Read,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1850:Prefer static HashData method over ComputeHash",
            Justification = "The shared implementation targets netstandard2.0, which has no SHA256.HashData."
        )]
        private static string OwnerHash(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner) || owner.Length > 4096)
                throw new ArgumentException(
                    "A stable owner scope of at most 4096 characters is required.",
                    nameof(owner)
                );

            using var hash = SHA256.Create();
            return Internal.StringOperations.ReplaceOrdinal(
                BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(owner))),
                "-",
                string.Empty
            );
        }

        private void Save(Entry entry, TusUpload upload, bool replace)
        {
            var temporary = PathFor(upload.Id, ".next");

            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
                {
                    writer.Write(Format);
                    writer.Write(entry.Owner);
                    writer.Write(upload.Length);
                    writer.Write(upload.Offset);
                    writer.Write(upload.Expires.UtcTicks);
                    writer.Write(upload.Metadata);
                    writer.Flush();
                }

                file.Flush(true);
            }

            if (replace) File.Replace(temporary, PathFor(upload.Id, ".info"), null);

            else File.Move(temporary, PathFor(upload.Id, ".info"));
        }

        private void Recover()
        {
            // A dedicated directory permits bounded enumeration and deterministic orphan cleanup.
            // Fail on foreign files instead of deleting data belonging to another application.
            var paths = Directory
                .EnumerateFileSystemEntries(Options.Directory)
                .Take(checked(Options.MaximumUploads * 3 + 2))
                .ToArray();

            if (paths.Length > Options.MaximumUploads * 3 + 1)
                throw new IOException("The upload directory exceeds its file budget.");

            foreach (var path in paths)
            {
                RejectLink(path);
                var name = Path.GetFileName(path);

                if (name == "store.lock") continue;

                if (
                    !ValidId(Path.GetFileNameWithoutExtension(name))
                    || (Path.GetExtension(name) != ".info"
                        && Path.GetExtension(name) != ".data"
                        && Path.GetExtension(name) != ".next")
                )
                    throw new IOException("Unexpected entry in the dedicated upload directory.");
            }

            foreach (var path in paths.Where(p => Path.GetExtension(p) == ".info"))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (file.Length > 8192) throw new InvalidDataException("Upload record exceeds its metadata budget.");

                using var reader = new BinaryReader(file, Encoding.UTF8, true);

                if (reader.ReadInt32() != Format) throw new InvalidDataException("Unsupported upload record format.");

                var owner = ReadText(reader, 64);
                var length = reader.ReadInt64();
                var offset = reader.ReadInt64();
                var expires = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var metadata = ReadText(reader, 4096);

                if (
                    owner.Length != 64
                    || owner.Any(c => !Uri.IsHexDigit(c))
                    || length < 0
                    || length > Options.MaximumUploadBytes
                    || offset < 0
                    || offset > length
                    || file.Position != file.Length
                )
                    throw new InvalidDataException("Invalid committed upload record.");

                TusUploadModule.ValidateMetadata(metadata);
                var id = Path.GetFileNameWithoutExtension(path);
                var upload = new TusUpload(id, length, offset, expires, metadata);

                using (var data = OpenData(id, FileAccess.ReadWrite))
                {
                    if (data.Length < offset) throw new InvalidDataException("Committed upload bytes are missing.");

                    if (data.Length != offset)
                    {
                        data.SetLength(offset);
                        data.Flush(true);
                    }
                }

                if (_entries.Count >= Options.MaximumUploads || length > Options.MaximumStorageBytes - _reserved)
                    throw new IOException("Recovered uploads exceed configured storage budgets.");

                _entries.Add(id, new Entry(owner, upload));
                _reserved += length;
            }

            foreach (var owner in _entries.Values.GroupBy(e => e.Owner, StringComparer.Ordinal))
                if (
                    owner.Count() > Options.MaximumUploadsPerOwner
                    || owner.Sum(e => e.Upload.Length) > Options.MaximumStorageBytesPerOwner
                ) throw new IOException("Recovered uploads exceed configured owner budgets.");

            foreach (var path in paths)
            {
                var extension = Path.GetExtension(path);

                if (
                    extension == ".next"
                    || (extension == ".data" && !_entries.ContainsKey(Path.GetFileNameWithoutExtension(path)))
                ) File.Delete(path);
            }

            RemoveExpired();
        }

        private static string ReadText(BinaryReader reader, int maximumBytes)
        {
            // Validate the on-disk length prefix before allocating, even for corrupted records.
            var length = 0;

            for (var shift = 0; shift < 35; shift += 7)
            {
                var part = reader.ReadByte();

                if (shift == 28 && part > 7) throw new InvalidDataException("Invalid upload string length.");

                length |= (part & 127) << shift;

                if (length > maximumBytes) throw new InvalidDataException("Upload string exceeds its metadata budget.");

                if ((part & 128) != 0) continue;

                var bytes = reader.ReadBytes(length);

                if (bytes.Length != length) throw new EndOfStreamException();

                return new UTF8Encoding(false, true).GetString(bytes);
            }

            throw new InvalidDataException("Invalid upload string length.");
        }

        private void Remove(Entry entry)
        {
            var id = entry.Upload.Id;

            try
            {
                File.Delete(PathFor(id, ".info"));
                File.Delete(PathFor(id, ".data"));
            }

            catch
            {
                _faulted = true;
                throw;
            }

            _entries.Remove(id);
            _reserved -= entry.Upload.Length;
        }
    }
}
