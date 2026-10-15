using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Utilities;
using EmbedIO.Internal;

namespace EmbedIO.Files.Internal
{
    internal class HtmlDirectoryLister : IDirectoryLister
    {
        private static readonly Lazy<IDirectoryLister> LazyInstance = new Lazy<IDirectoryLister>(() => new HtmlDirectoryLister());

        private HtmlDirectoryLister()
        {
        }

        public static IDirectoryLister Instance => LazyInstance.Value;

        public string ContentType { get; } = MimeType.Html + "; charset=" + WebServer.DefaultEncoding.WebName;

        public async Task ListDirectoryAsync(
            MappedResourceInfo info,
            string absolutePath,
            IEnumerable<MappedResourceInfo> entries,
            Stream stream,
            CancellationToken cancellationToken)
        {
            const int MaxEntryLength = 50;
            const int SizeIndent = -20; // Negative for right alignment

            if (!info.IsDirectory)
                throw SelfCheck.Failure($"{nameof(HtmlDirectoryLister)}.{nameof(ListDirectoryAsync)} invoked with a file, not a directory.");

            var encodedPath = WebUtility.HtmlEncode(absolutePath);
            using var text = new StreamWriter(stream, WebServer.DefaultEncoding);
            text.Write("<html><head><title>Index of ");
            text.Write(encodedPath);
            text.Write("</title></head><body><h1>Index of ");
            text.Write(encodedPath);
            text.Write("</h1><hr/><pre>");

            // A listing requested without a trailing slash resolves relative links one level up,
            // so name the directory itself. Links stay relative, which keeps them correct behind
            // a proxy that strips a path prefix.
            var slash = absolutePath.LastIndexOf('/');
            var linkBase = slash < 0 || slash == absolutePath.Length - 1
                ? string.Empty
                : "./" + WebUtility.HtmlEncode(absolutePath.Substring(slash + 1)) + "/";

            if (encodedPath.Length > 1)
                text.Write(linkBase.Length == 0 ? "<a href='../'>../</a>\n" : "<a href='./'>../</a>\n");

            entries = entries.ToArray();

            foreach (var directory in entries.Where(m => m.IsDirectory).OrderBy(e => e.Name))
            {
                text.Write($"<a href=\"{linkBase}{Uri.EscapeDataString(directory.Name)}/\">{WebUtility.HtmlEncode(directory.Name)}</a>");
                text.Write(new string(' ', Math.Max(1, MaxEntryLength - directory.Name.Length + 1)));
                text.Write(HttpDate.Format(directory.LastModifiedUtc));
                text.Write('\n');
                await Task.Yield();
            }

            foreach (var file in entries.Where(m => m.IsFile).OrderBy(e => e.Name))
            {
                text.Write($"<a href=\"{linkBase}{Uri.EscapeDataString(file.Name)}\">{WebUtility.HtmlEncode(file.Name)}</a>");
                text.Write(new string(' ', Math.Max(1, MaxEntryLength - file.Name.Length + 1)));
                text.Write(HttpDate.Format(file.LastModifiedUtc));
                text.Write($" {file.Length.ToString("#,###", CultureInfo.InvariantCulture),SizeIndent}\n");
                await Task.Yield();
            }

            text.Write("</pre><hr/></body></html>");
        }
    }
}
