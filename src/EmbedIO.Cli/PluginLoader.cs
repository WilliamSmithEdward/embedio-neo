using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

namespace EmbedIO.Cli
{
    internal sealed class PluginLoader : IDisposable
    {
        private readonly List<PluginContext> _contexts = new();

        internal void Register(WebServer server, string path, bool explicitlyRequested)
        {
            path = Path.GetFullPath(path);
            var isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path)) throw new FileNotFoundException("Plugin path does not exist.", path);
            var files = isFile ? new[] { path } : Directory.GetFiles(path, "*.dll").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var api = new PluginApiModule();
            var controllerCount = 0;
            var moduleCount = 0;
            foreach (var file in files)
            {
                // The host's core must stay shared so plugin type identities match.
                if (string.Equals(Path.GetFileName(file), "EmbedIO.dll", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var context = new PluginContext(file);
                    _contexts.Add(context);
                    var assembly = context.LoadFromAssemblyPath(file);
                    foreach (var type in assembly.GetExportedTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters))
                    {
                        if (typeof(WebApiController).IsAssignableFrom(type))
                        {
                            api.RegisterController(type);
                            controllerCount++;
                        }
                        else if (typeof(WebSocketModule).IsAssignableFrom(type))
                        {
                            var module = (WebSocketModule?)Activator.CreateInstance(type)
                                ?? throw new InvalidOperationException($"Cannot create {type.FullName}.");
                            try { server.WithModule(module); }
                            catch { module.Dispose(); throw; }
                            moduleCount++;
                        }
                    }
                }
                catch (BadImageFormatException) when (!isFile) { /* Native DLL in a plugin directory. */ }
            }
            if (controllerCount > 0) server.WithModule(api);
            if (explicitlyRequested && controllerCount + moduleCount == 0)
                throw new InvalidOperationException("No compatible controllers or WebSocket modules found. Rebuild plugins against EmbedIO-Neo; EmbedIO 2.x binaries are not supported.");
        }

        public void Dispose()
        {
            foreach (var context in _contexts) context.Unload();
            _contexts.Clear();
        }

        private sealed class PluginApiModule() : WebApiModule("/")
        {
            protected override System.Threading.Tasks.Task OnPathNotFoundAsync(IHttpContext context)
                => throw RequestHandler.PassThrough();
        }

        private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: true)
        {
            private readonly AssemblyDependencyResolver _resolver = new(path);
            private readonly string _directory = Path.GetDirectoryName(path)!;

            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name == typeof(WebServer).Assembly.GetName().Name) return typeof(WebServer).Assembly;
                var resolved = _resolver.ResolveAssemblyToPath(name);
                var adjacent = Path.Combine(_directory, name.Name + ".dll");
                resolved ??= File.Exists(adjacent) ? adjacent : null;
                return resolved == null ? null : LoadFromAssemblyPath(resolved);
            }

            protected override IntPtr LoadUnmanagedDll(string name)
            {
                var resolved = _resolver.ResolveUnmanagedDllToPath(name);
                return resolved == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(resolved);
            }
        }
    }
}
