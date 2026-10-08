using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EmbedIO.Files;

namespace EmbedIO.Cli
{
    // LiveReload owns the only watcher. Files remain mutable even with --no-watch,
    // so FileModule must reread metadata rather than cache immutable mappings.
    internal sealed class CliFileProvider(string root) : IFileProvider, IDisposable
    {
        private readonly FileSystemProvider _inner = new(root, true);
        public bool IsImmutable => false;
        public event Action<string>? ResourceChanged { add { } remove { } }
        public void Start(CancellationToken cancellationToken) { }
        public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider)
            => _inner.MapUrlPath(requestPath, mimeTypeProvider);
        public Stream OpenFile(string path) => _inner.OpenFile(path);
        public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider)
            => _inner.GetDirectoryEntries(path, mimeTypeProvider);
        public void Dispose() => _inner.Dispose();
    }
}
