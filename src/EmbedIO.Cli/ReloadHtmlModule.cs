using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Files;

namespace EmbedIO.Cli
{
    // FileModule handles ordinary files, ranges, listings, and MIME types. Only HTML
    // needing a live-reload script is transformed here, from one content snapshot.
    internal sealed class ReloadHtmlModule(IFileProvider provider, int port) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;

        protected override async Task OnRequestAsync(IHttpContext context)
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD")) return;
            var path = context.Request.Url.AbsolutePath;
            var info = provider.MapUrlPath(path, context);
            if (info?.IsDirectory == true)
                info = provider.MapUrlPath(path.TrimEnd('/') + "/index.html", context);
            if (info?.IsFile != true || !string.Equals(info.ContentType, "text/html", StringComparison.OrdinalIgnoreCase)) return;
            using var stream = provider.OpenFile(info.Path);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var html = await reader.ReadToEndAsync(context.CancellationToken);
            var script = $"<script>(()=>{{const u=new URL(location.href);u.protocol='ws:';u.port='{port}';u.pathname='/watcher';u.search='';u.hash='';const ws=new WebSocket(u);ws.onmessage=()=>location.reload();}})();</script>";
            var end = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (end >= 0) html = html.Insert(end, script);
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = bytes.Length;
            context.SetHandled();
            if (context.Request.HttpMethod == "GET")
                await context.Response.OutputStream.WriteAsync(bytes, context.CancellationToken);
        }
    }
}
