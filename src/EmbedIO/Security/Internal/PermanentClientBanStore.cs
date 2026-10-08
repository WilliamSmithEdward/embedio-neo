using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmbedIO.Security.Internal
{
    // One cooperating .NET writer per application-owned path. Same-directory replacement.
    internal sealed class PermanentClientBanStore : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _lease;
        private readonly int _capacity;

        internal PermanentClientBanStore(string path, int capacity)
        {
            _path = Path.GetFullPath(path);
            _capacity = capacity;
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new ArgumentException("The store path has no parent directory.", nameof(path)));
            _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        internal string[] Load()
        {
            if (!File.Exists(_path)) return Array.Empty<string>();
            if (new FileInfo(_path).Length > _capacity * 6150L + 1024)
                throw new InvalidDataException("The permanent-ban store exceeds its configured capacity.");
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array
                || keys.GetArrayLength() > _capacity)
                throw new InvalidDataException("Unsupported permanent-ban store format.");
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid permanent client key.");
                var value = key.GetString() ?? throw new InvalidDataException("Invalid permanent client key.");
                try { ClientBanningModule.ValidateKey(value); }
                catch (ArgumentException exception) { throw new InvalidDataException("Invalid permanent client key.", exception); }
                if (!values.Add(value))
                    throw new InvalidDataException("Invalid or duplicate permanent client key.");
            }
            return values.ToArray();
        }

        internal void Save(IEnumerable<string> keys)
        {
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new Utf8JsonWriter(stream);
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteStartArray("keys");
                    foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal)) writer.WriteStringValue(key);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public void Dispose() => _lease.Dispose();
    }
}
